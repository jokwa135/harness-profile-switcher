using System;
using System.Collections.Generic;
using System.IO;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal static class NonLiveEndToEndTests
    {
        private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        private const string PackageVersion = "fixture-version";

        public static int Run()
        {
            string runId = Guid.NewGuid().ToString("N");
            string root = Path.Combine(FixtureWorkspace.PathFor("NonLiveE2E"), runId);
            string deletionRoot = Path.Combine(FixtureWorkspace.PathFor("Quarantine/nonlive"), runId);
            bool succeeded = false;
            try
            {
                ExecuteJourney(root, deletionRoot);
                Archive(root);
                succeeded = true;
                Console.WriteLine("PASS: isolated non-live journey completed registration, onboarding, rotation-aware switching, cancellation preservation, interruption recovery, permanent inactive-profile deletion, ACL checks, and shared-state invariants");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: non-live E2E fixture retained at " + root);
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                if (!succeeded)
                {
                    Console.Error.WriteLine("Preserved cancellation artifacts remain at " + deletionRoot);
                }
            }
        }

        private static void ExecuteJourney(string root, string deletionRoot)
        {
            string simulatedUser = Path.Combine(root, "simulated-user");
            string codexHome = Path.Combine(simulatedUser, ".codex");
            string webRoot = Path.Combine(simulatedUser, @"AppData\Roaming\Codex\web");
            EnginePaths paths = new EnginePaths(
                Path.Combine(root, "runtime"),
                Path.Combine(codexHome, "auth.json"),
                Path.Combine(webRoot, "Codex"),
                Path.Combine(webRoot, ".hps-links"),
                Path.Combine(codexHome, ".hps-auth"),
                deletionRoot);
            AtomicJsonStore store = new AtomicJsonStore();
            WindowsJunctionManager junctions = new WindowsJunctionManager();
            WindowsRuntimeSecurity security = new WindowsRuntimeSecurity(paths.RuntimeRoot, paths.DeletionRoot);

            Dictionary<string, string> sharedFiles = CreateUnmanagedState(paths, codexHome);
            ApplicationStateInspector inspector = new ApplicationStateInspector(paths, store, junctions);
            AssertMode(HarnessMode.Unmanaged, inspector.Inspect(), "Initial simulated account was not unmanaged.");

            ProfileLifecycleEngine lifecycle = CreateLifecycle(paths, store, junctions, security, new NoFaultInjector());
            LifecycleResult registration = lifecycle.RegisterFirstProfile("Alpha");
            string alpha = registration.ActiveProfileId;
            lifecycle.MarkLaunchObserved(registration.TransactionId);
            AssertMode(HarnessMode.Active, inspector.Inspect(), "First registration did not become active.");
            AssertActive(paths, store, junctions, alpha, "alpha-auth-v1");
            Assert(File.Exists(Path.Combine(paths.ProfileBrowserPath(alpha), @"Default\Cookies")), "Alpha browser tree was not transferred.");
            AssertShared(sharedFiles);

            LifecycleResult beginBeta = lifecycle.BeginOnboarding("Beta");
            string beta = beginBeta.CandidateProfileId;
            lifecycle.MarkLaunchObserved(beginBeta.TransactionId);
            AssertMode(HarnessMode.PendingOnboarding, inspector.Inspect(), "Beta onboarding did not enter pending mode.");
            Assert(!File.Exists(paths.LiveAuthPath), "Clean Beta onboarding retained live authentication.");
            WriteFile(paths.LiveAuthPath, "beta-auth-v1");
            WriteFile(Path.Combine(paths.LiveBrowserLinkPath, @"Default\Cookies"), "beta-cookie-v1");
            WriteFile(Path.Combine(paths.LiveBrowserLinkPath, "Local State"), "beta-local-state-v1");
            Assert(inspector.Inspect().PendingAuthenticationPresent, "Simulated Beta OAuth was not detected.");
            AssertShared(sharedFiles);

            LifecycleResult finalizeBeta = lifecycle.FinalizeOnboarding();
            lifecycle.MarkLaunchObserved(finalizeBeta.TransactionId);
            AssertMode(HarnessMode.Active, inspector.Inspect(), "Finalized Beta account did not become active.");
            AssertActive(paths, store, junctions, beta, "beta-auth-v1");
            AssertEqual("alpha-auth-v1", File.ReadAllText(paths.ProfileAuthPath(alpha)), "Alpha authentication was not parked.");
            AssertShared(sharedFiles);

            File.WriteAllText(paths.LiveAuthPath, "beta-auth-rotated");
            SwitchEngine switching = CreateSwitch(paths, store, junctions, security, new NoFaultInjector());
            SwitchResult switchToAlpha = switching.SwitchTo(alpha);
            switching.MarkLaunchObserved(switchToAlpha.TransactionId);
            AssertActive(paths, store, junctions, alpha, "alpha-auth-v1");
            AssertEqual("beta-auth-rotated", File.ReadAllText(paths.ProfileAuthPath(beta)), "Rotated Beta authentication was not preserved.");

            SwitchResult switchToBeta = switching.SwitchTo(beta);
            switching.MarkLaunchObserved(switchToBeta.TransactionId);
            AssertActive(paths, store, junctions, beta, "beta-auth-rotated");
            AssertShared(sharedFiles);

            LifecycleResult beginCancelled = lifecycle.BeginOnboarding("Cancelled");
            string cancelled = beginCancelled.CandidateProfileId;
            lifecycle.MarkLaunchObserved(beginCancelled.TransactionId);
            WriteFile(paths.LiveAuthPath, "cancelled-auth-partial");
            WriteFile(Path.Combine(paths.LiveBrowserLinkPath, @"Default\Cookies"), "cancelled-cookie-partial");
            LifecycleResult cancellation = lifecycle.CancelOnboarding();
            lifecycle.MarkLaunchObserved(cancellation.TransactionId);
            AssertActive(paths, store, junctions, beta, "beta-auth-rotated");
            Assert(!Directory.Exists(paths.ProfileDirectory(cancelled)), "Cancelled profile remained loadable.");
            TransactionJournal cancellationJournal = store.Read<TransactionJournal>(paths.JournalPath(cancellation.TransactionId));
            AssertEqual("cancelled-auth-partial", File.ReadAllText(cancellationJournal.CandidateAuthQuarantinePath), "Partial cancellation authentication was not preserved.");
            string cancelledBrowser = Path.Combine(cancellationJournal.LifecycleQuarantinePath, @"candidate-profile\browser\Default\Cookies");
            AssertEqual("cancelled-cookie-partial", File.ReadAllText(cancelledBrowser), "Partial cancellation browser state was not preserved.");
            AssertHealthy(security.AuditDirectory(cancellationJournal.LifecycleQuarantinePath), "Cancellation quarantine ACL is unhealthy.");
            AssertHealthy(security.AuditFile(cancellationJournal.CandidateAuthQuarantinePath), "Cancellation authentication ACL is unhealthy.");
            AssertShared(sharedFiles);

            SwitchEngine interrupted = CreateSwitch(
                paths,
                store,
                junctions,
                security,
                new ThrowAtPhase(TransactionPhases.IncomingBrowserLinkActivated));
            ExpectInterruption(delegate { interrupted.SwitchTo(alpha); });
            AssertMode(HarnessMode.RecoveryRequired, inspector.Inspect(), "Interrupted switch was not surfaced for recovery.");
            SwitchEngine recoveryEngine = CreateSwitch(paths, store, junctions, security, new NoFaultInjector());
            RecoveryResult recovered = recoveryEngine.RecoverPending();
            AssertEqual("RolledBack", recovered.Result, "Pre-commit interruption did not roll back.");
            AssertActive(paths, store, junctions, beta, "beta-auth-rotated");
            AssertMode(HarnessMode.Active, inspector.Inspect(), "Recovered switch did not return to active mode.");
            AssertShared(sharedFiles);

            SwitchResult finalSwitch = recoveryEngine.SwitchTo(alpha);
            recoveryEngine.MarkLaunchObserved(finalSwitch.TransactionId);
            AssertActive(paths, store, junctions, alpha, "alpha-auth-v1");
            AssertEqual("beta-auth-rotated", File.ReadAllText(paths.ProfileAuthPath(beta)), "Final inactive Beta authentication is incorrect.");
            AssertHealthy(security.AuditDirectory(paths.RuntimeRoot), "Runtime root ACL is unhealthy.");
            AssertHealthy(security.AuditDirectory(paths.ProfileBrowserPath(alpha)), "Active Alpha browser ACL is unhealthy.");
            AssertHealthy(security.AuditFile(paths.ProfileAuthPath(beta)), "Inactive Beta authentication ACL is unhealthy.");
            AssertShared(sharedFiles);

            ProfileDeletionEngine deletion = CreateDeletion(paths, store, junctions, security, new NoFaultInjector());
            ProfileDeletionResult deleted = deletion.DeleteProfile(beta);
            AssertEqual(beta, deleted.DeletedProfileId, "Non-live deletion returned the wrong profile.");
            Assert(!Directory.Exists(paths.ProfileDirectory(beta)), "Deleted Beta profile directory remains.");
            Assert(!junctions.Exists(paths.InactiveLinkPath(beta)), "Deleted Beta inactive junction remains.");
            AssertActive(paths, store, junctions, alpha, "alpha-auth-v1");
            ApplicationStateSnapshot afterDeletion = inspector.Inspect();
            AssertMode(HarnessMode.Active, afterDeletion, "Profile deletion did not return to active mode.");
            Assert(afterDeletion.Profiles.Count == 1 && afterDeletion.Profiles[0].ProfileId == alpha,
                "Deleted Beta profile remained visible after deletion.");
            AssertHealthy(security.AuditFile(paths.JournalPath(deleted.TransactionId)), "Deletion journal ACL is unhealthy.");
            AssertShared(sharedFiles);
        }

        private static Dictionary<string, string> CreateUnmanagedState(EnginePaths paths, string codexHome)
        {
            Dictionary<string, string> shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { Path.Combine(codexHome, "config.toml"), "model = \"shared-model\"" },
                { Path.Combine(codexHome, @"sessions\session.jsonl"), "shared-session" },
                { Path.Combine(codexHome, @"skills\shared-skill\SKILL.md"), "shared-skill" },
                { Path.Combine(codexHome, @"projects\project.txt"), "shared-project" },
                { Path.Combine(codexHome, "history.jsonl"), "shared-history" }
            };
            foreach (KeyValuePair<string, string> item in shared) WriteFile(item.Key, item.Value);
            WriteFile(paths.LiveAuthPath, "alpha-auth-v1");
            WriteFile(Path.Combine(paths.LiveBrowserLinkPath, @"Default\Cookies"), "alpha-cookie-v1");
            WriteFile(Path.Combine(paths.LiveBrowserLinkPath, "Local State"), "alpha-local-state-v1");
            return shared;
        }

        private static ProfileLifecycleEngine CreateLifecycle(
            EnginePaths paths,
            AtomicJsonStore store,
            IJunctionManager junctions,
            IRuntimeSecurity security,
            IFaultInjector faultInjector)
        {
            return new ProfileLifecycleEngine(
                paths,
                store,
                new VerifiedFileMover(),
                new VerifiedDirectoryMover(),
                junctions,
                new AlwaysStoppedGuard(),
                faultInjector,
                PackageFamily,
                PackageVersion,
                security);
        }

        private static SwitchEngine CreateSwitch(
            EnginePaths paths,
            AtomicJsonStore store,
            IJunctionManager junctions,
            IRuntimeSecurity security,
            IFaultInjector faultInjector)
        {
            return new SwitchEngine(
                paths,
                store,
                new VerifiedFileMover(),
                junctions,
                new AlwaysStoppedGuard(),
                faultInjector,
                PackageFamily,
                PackageVersion,
                security);
        }

        private static ProfileDeletionEngine CreateDeletion(
            EnginePaths paths,
            AtomicJsonStore store,
            IJunctionManager junctions,
            IRuntimeSecurity security,
            IFaultInjector faultInjector)
        {
            return new ProfileDeletionEngine(
                paths,
                store,
                new VerifiedDirectoryMover(),
                junctions,
                faultInjector,
                PackageFamily,
                PackageVersion,
                security);
        }

        private static void AssertActive(
            EnginePaths paths,
            AtomicJsonStore store,
            IJunctionManager junctions,
            string profileId,
            string expectedAuth)
        {
            Ledger ledger = store.Read<Ledger>(paths.LedgerPath);
            AssertEqual(profileId, ledger.ActiveProfileId, "Active profile ID is incorrect.");
            Assert(ledger.PendingOnboarding == null, "Active ledger retained pending onboarding.");
            AssertEqual(expectedAuth, File.ReadAllText(paths.LiveAuthPath), "Live authentication is incorrect.");
            Assert(!File.Exists(paths.ProfileAuthPath(profileId)), "Active profile retained vault authentication.");
            Assert(junctions.Exists(paths.LiveBrowserLinkPath), "Live browser junction is missing.");
            Assert(WindowsJunctionManager.PathsEqual(junctions.ResolveTarget(paths.LiveBrowserLinkPath), paths.ProfileBrowserPath(profileId)), "Live browser junction targets the wrong profile.");
        }

        private static void AssertShared(Dictionary<string, string> sharedFiles)
        {
            foreach (KeyValuePair<string, string> item in sharedFiles)
            {
                Assert(File.Exists(item.Key), "Shared file disappeared: " + item.Key);
                AssertEqual(item.Value, File.ReadAllText(item.Key), "Shared file changed: " + item.Key);
            }
        }

        private static void AssertMode(HarnessMode expected, ApplicationStateSnapshot snapshot, string message)
        {
            Assert(snapshot.Mode == expected, message + " Found " + snapshot.Mode + ".");
        }

        private static void AssertHealthy(RuntimeAclAudit audit, string message)
        {
            Assert(audit != null && audit.Exists && audit.Protected && audit.Healthy, message);
        }

        private static void ExpectInterruption(Action action)
        {
            try
            {
                action();
            }
            catch (JourneyInterruptionException)
            {
                return;
            }
            throw new InvalidOperationException("Expected an injected journey interruption.");
        }

        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private static void Archive(string root)
        {
            string archiveRoot = FixtureWorkspace.PathFor("Archive/nonlive");
            Directory.CreateDirectory(archiveRoot);
            Directory.Move(root, Path.Combine(archiveRoot, Path.GetFileName(root)));
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message + " Expected '" + expected + "', found '" + actual + "'.");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class AlwaysStoppedGuard : IProcessGuard
        {
            public void EnsureStopped()
            {
            }
        }

        private sealed class ThrowAtPhase : IFaultInjector
        {
            private readonly string phase;
            private bool thrown;

            public ThrowAtPhase(string phase)
            {
                this.phase = phase;
            }

            public void Checkpoint(string currentPhase)
            {
                if (!thrown && string.Equals(currentPhase, phase, StringComparison.Ordinal))
                {
                    thrown = true;
                    throw new JourneyInterruptionException("Injected interruption after " + phase + ".");
                }
            }
        }

        private sealed class JourneyInterruptionException : Exception
        {
            public JourneyInterruptionException(string message)
                : base(message)
            {
            }
        }
    }
}
