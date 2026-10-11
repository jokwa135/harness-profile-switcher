using System;
using System.IO;
using System.Linq;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal static class ProfileDeletionFixtureTests
    {
        private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        private const string PackageVersion = "fixture-version";

        public static int Run()
        {
            TestSuccessfulDeletionPreservesActiveAndSharedState();
            TestUnsafeDeletionRequestsAreRejected();
            TestEveryDeletionCheckpointRecovers();
            TestPostEffectDeletionFailuresRecover();
            TestTamperedDeletionJournalCannotRedirectRecovery();
            TestDeletionSecurityIntegration();
            return 6;
        }

        private static void TestSuccessfulDeletionPreservesActiveAndSharedState()
        {
            Fixture fixture = Fixture.Create("delete-success");
            bool succeeded = false;
            try
            {
                ApplicationStateSnapshot before = fixture.Inspect();
                ProfileSummary inactive = before.Profiles.Single(
                    delegate(ProfileSummary profile) { return profile.ProfileId == fixture.ProfileA; });
                AssertTrue(inactive.CanDelete, "Healthy inactive profile was not exposed as deletable.");

                ProfileDeletionResult result = fixture.CreateDeletionEngine(new NoFaultInjector()).DeleteProfile(fixture.ProfileA);
                AssertEqual(fixture.ProfileA, result.DeletedProfileId, "Deletion returned the wrong target profile.");
                AssertDeleted(fixture, fixture.ProfileA);
                AssertActive(fixture);
                AssertShared(fixture);

                TransactionJournal journal = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(result.TransactionId));
                AssertEqual(TransactionPhases.Completed, journal.Phase, "Profile deletion journal was not completed.");
                AssertTrue(!Directory.Exists(journal.DeletionStagingPath), "Completed deletion retained staged profile content.");

                ApplicationStateSnapshot after = fixture.Inspect();
                AssertTrue(after.Mode == HarnessMode.Active, "Deletion did not return to active mode.");
                AssertTrue(after.Profiles.Count == 1 && after.Profiles[0].ProfileId == fixture.ProfileB,
                    "Deleted profile remained discoverable.");
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestUnsafeDeletionRequestsAreRejected()
        {
            Fixture activeFixture = Fixture.Create("delete-reject-active");
            bool activeSucceeded = false;
            try
            {
                AssertThrows<InvalidOperationException>(
                    delegate { activeFixture.CreateDeletionEngine(new NoFaultInjector()).DeleteProfile(activeFixture.ProfileB); },
                    "Deletion accepted the active profile.");
                AssertActive(activeFixture);
                AssertReady(activeFixture, activeFixture.ProfileA);
                AssertShared(activeFixture);
                activeSucceeded = true;
            }
            finally
            {
                activeFixture.Finish(activeSucceeded);
            }

            Fixture pendingFixture = Fixture.Create("delete-reject-pending");
            bool pendingSucceeded = false;
            try
            {
                ProfileLifecycleEngine lifecycle = pendingFixture.CreateLifecycleEngine(new NoFaultInjector());
                LifecycleResult begin = lifecycle.BeginOnboarding("Pending");
                lifecycle.MarkLaunchObserved(begin.TransactionId);
                AssertThrows<InvalidOperationException>(
                    delegate { pendingFixture.CreateDeletionEngine(new NoFaultInjector()).DeleteProfile(pendingFixture.ProfileA); },
                    "Deletion was accepted during onboarding.");
                AssertTrue(Directory.Exists(pendingFixture.Paths.ProfileDirectory(pendingFixture.ProfileA)),
                    "Rejected onboarding deletion removed the target profile.");
                AssertShared(pendingFixture);
                pendingSucceeded = true;
            }
            finally
            {
                pendingFixture.Finish(pendingSucceeded);
            }

            Fixture brokenFixture = Fixture.Create("delete-reject-broken");
            bool brokenSucceeded = false;
            try
            {
                File.Delete(brokenFixture.Paths.ProfileAuthPath(brokenFixture.ProfileA));
                AssertThrows<InvalidOperationException>(
                    delegate { brokenFixture.CreateDeletionEngine(new NoFaultInjector()).DeleteProfile(brokenFixture.ProfileA); },
                    "Deletion accepted a structurally broken inactive profile.");
                AssertTrue(Directory.Exists(brokenFixture.Paths.ProfileDirectory(brokenFixture.ProfileA)),
                    "Rejected broken-profile deletion removed the profile directory.");
                AssertShared(brokenFixture);
                brokenSucceeded = true;
            }
            finally
            {
                brokenFixture.Finish(brokenSucceeded);
            }
        }

        private static void TestEveryDeletionCheckpointRecovers()
        {
            string[] phases =
            {
                TransactionPhases.Prepared,
                TransactionPhases.ProfileDeleteLinkIntent,
                TransactionPhases.ProfileDeleteLinkDeleted,
                TransactionPhases.ProfileDeleteStageIntent,
                TransactionPhases.ProfileDeleteStaged,
                TransactionPhases.ProfileDeleteContentIntent,
                TransactionPhases.ProfileDeleteContentDeleted
            };

            foreach (string phase in phases)
            {
                Fixture fixture = Fixture.Create("delete-checkpoint-" + phase);
                bool succeeded = false;
                try
                {
                    ProfileDeletionEngine crashing = fixture.CreateDeletionEngine(new ThrowAtPhase(phase));
                    ExpectInjectedFault(delegate { crashing.DeleteProfile(fixture.ProfileA); });
                    ApplicationStateSnapshot interrupted = fixture.Inspect();
                    AssertTrue(interrupted.Mode == HarnessMode.RecoveryRequired,
                        "Interrupted deletion was not surfaced for recovery at " + phase + ".");
                    AssertTrue(interrupted.CanRecoverAutomatically,
                        "Interrupted deletion was not marked automatically recoverable at " + phase + ".");

                    ProfileDeletionRecoveryResult recovery =
                        fixture.CreateDeletionEngine(new NoFaultInjector()).RecoverPending();
                    AssertEqual("DeletedProfile", recovery.Result, "Deletion recovery did not finish at " + phase + ".");
                    AssertDeleted(fixture, fixture.ProfileA);
                    AssertActive(fixture);
                    AssertShared(fixture);
                    AssertTrue(fixture.Inspect().Mode == HarnessMode.Active,
                        "Recovered deletion did not return to active mode at " + phase + ".");
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestPostEffectDeletionFailuresRecover()
        {
            for (int scenario = 1; scenario <= 3; scenario++)
            {
                Fixture fixture = Fixture.Create("delete-post-effect-" + scenario);
                bool succeeded = false;
                try
                {
                    IVerifiedDirectoryMover directoryMover = scenario == 2
                        ? (IVerifiedDirectoryMover)new ThrowAfterDirectoryMove(1)
                        : scenario == 3
                            ? (IVerifiedDirectoryMover)new ThrowAfterPartialDirectoryDelete()
                            : new VerifiedDirectoryMover();
                    IJunctionManager junctionManager = scenario == 1
                        ? (IJunctionManager)new ThrowAfterJunctionMutation(fixture.Junctions, 1)
                        : fixture.Junctions;
                    ProfileDeletionEngine crashing = fixture.CreateDeletionEngine(
                        new NoFaultInjector(),
                        directoryMover,
                        junctionManager,
                        new NoOpRuntimeSecurity());
                    ExpectPostEffectFault(delegate { crashing.DeleteProfile(fixture.ProfileA); });

                    ProfileDeletionRecoveryResult recovery =
                        fixture.CreateDeletionEngine(new NoFaultInjector()).RecoverPending();
                    AssertEqual("DeletedProfile", recovery.Result, "Post-effect deletion did not recover in scenario " + scenario + ".");
                    AssertDeleted(fixture, fixture.ProfileA);
                    AssertActive(fixture);
                    AssertShared(fixture);
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestTamperedDeletionJournalCannotRedirectRecovery()
        {
            Fixture fixture = Fixture.Create("delete-tampered-journal");
            bool succeeded = false;
            try
            {
                ExpectInjectedFault(delegate
                {
                    fixture.CreateDeletionEngine(new ThrowAtPhase(TransactionPhases.Prepared))
                        .DeleteProfile(fixture.ProfileA);
                });
                TransactionJournal journal = fixture.PendingJournal();
                journal.DeletionStagingPath = fixture.SharedSentinelDirectory;
                fixture.Store.Write(fixture.Paths.JournalPath(journal.TransactionId), journal);

                AssertThrows<InvalidDataException>(
                    delegate { fixture.CreateDeletionEngine(new NoFaultInjector()).RecoverPending(); },
                    "Tampered deletion journal redirected recovery.");
                AssertShared(fixture);
                AssertReady(fixture, fixture.ProfileA);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestDeletionSecurityIntegration()
        {
            Fixture fixture = Fixture.Create("delete-security");
            bool succeeded = false;
            try
            {
                WindowsRuntimeSecurity security =
                    new WindowsRuntimeSecurity(fixture.Paths.RuntimeRoot, fixture.Paths.DeletionRoot);
                ProfileDeletionResult result = fixture.CreateDeletionEngine(
                    new NoFaultInjector(),
                    new VerifiedDirectoryMover(),
                    fixture.Junctions,
                    security).DeleteProfile(fixture.ProfileA);
                AssertHealthy(security.AuditDirectory(fixture.Paths.RuntimeRoot),
                    "Deletion did not preserve a healthy runtime ACL.");
                AssertHealthy(security.AuditFile(fixture.Paths.JournalPath(result.TransactionId)),
                    "Deletion journal ACL is unhealthy.");
                AssertDeleted(fixture, fixture.ProfileA);
                AssertActive(fixture);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void AssertActive(Fixture fixture)
        {
            Ledger ledger = fixture.Store.Read<Ledger>(fixture.Paths.LedgerPath);
            AssertEqual(fixture.ProfileB, ledger.ActiveProfileId, "Active profile changed during deletion.");
            AssertTrue(ledger.PendingOnboarding == null, "Deletion introduced pending onboarding.");
            AssertEqual("auth-b", File.ReadAllText(fixture.Paths.LiveAuthPath), "Live authentication changed during deletion.");
            AssertTrue(!File.Exists(fixture.Paths.ProfileAuthPath(fixture.ProfileB)),
                "Active profile gained a vault authentication copy.");
            AssertLink(fixture, fixture.Paths.LiveBrowserLinkPath, fixture.Paths.ProfileBrowserPath(fixture.ProfileB));
        }

        private static void AssertReady(Fixture fixture, string profileId)
        {
            AssertTrue(File.Exists(fixture.Paths.ProfileAuthPath(profileId)), "Ready profile authentication is missing.");
            AssertTrue(Directory.Exists(fixture.Paths.ProfileBrowserPath(profileId)), "Ready profile browser state is missing.");
            AssertLink(fixture, fixture.Paths.InactiveLinkPath(profileId), fixture.Paths.ProfileBrowserPath(profileId));
        }

        private static void AssertDeleted(Fixture fixture, string profileId)
        {
            AssertTrue(!Directory.Exists(fixture.Paths.ProfileDirectory(profileId)), "Deleted profile directory remains.");
            AssertTrue(!File.Exists(fixture.Paths.ProfileAuthPath(profileId)), "Deleted profile authentication remains.");
            AssertTrue(!fixture.Junctions.Exists(fixture.Paths.InactiveLinkPath(profileId)), "Deleted profile junction remains.");
        }

        private static void AssertShared(Fixture fixture)
        {
            string sentinel = Path.Combine(fixture.SharedSentinelDirectory, "shared.txt");
            AssertTrue(File.Exists(sentinel), "Shared-state sentinel disappeared.");
            AssertEqual("shared-state", File.ReadAllText(sentinel), "Shared-state sentinel changed.");
        }

        private static void AssertLink(Fixture fixture, string linkPath, string targetPath)
        {
            AssertTrue(fixture.Junctions.Exists(linkPath), "Expected junction is missing: " + linkPath);
            AssertTrue(
                WindowsJunctionManager.PathsEqual(fixture.Junctions.ResolveTarget(linkPath), targetPath),
                "Junction target mismatch.");
        }

        private static void ExpectInjectedFault(Action action)
        {
            try
            {
                action();
            }
            catch (InjectedDeletionFaultException)
            {
                return;
            }
            throw new Exception("Expected an injected profile deletion interruption.");
        }

        private static void ExpectPostEffectFault(Action action)
        {
            try
            {
                action();
            }
            catch (PostEffectFaultException)
            {
                return;
            }
            throw new Exception("Expected a post-effect profile deletion failure.");
        }

        private static void AssertHealthy(RuntimeAclAudit audit, string message)
        {
            AssertTrue(audit != null && audit.Exists && audit.Protected && audit.Healthy, message);
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new Exception(message);
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new Exception(message + " Expected '" + expected + "', found '" + actual + "'.");
            }
        }

        private sealed class Fixture
        {
            private Fixture()
            {
            }

            public string Root { get; private set; }
            public EnginePaths Paths { get; private set; }
            public AtomicJsonStore Store { get; private set; }
            public WindowsJunctionManager Junctions { get; private set; }
            public string ProfileA { get; private set; }
            public string ProfileB { get; private set; }
            public string SharedSentinelDirectory { get; private set; }

            public static Fixture Create(string label)
            {
                string root = Path.Combine(
                    FixtureWorkspace.PathFor("ProfileDeletionTests"),
                    label.Substring(0, Math.Min(12, label.Length)) + "-" + Guid.NewGuid().ToString("N").Substring(0,8));
                string liveRoot = Path.Combine(root, "live");
                string webRoot = Path.Combine(liveRoot, @"AppData\Codex\web");
                Fixture fixture = new Fixture
                {
                    Root = root,
                    Paths = new EnginePaths(
                        Path.Combine(root, "runtime"),
                        Path.Combine(liveRoot, @".codex\auth.json"),
                        Path.Combine(webRoot, "Codex"),
                        Path.Combine(webRoot, ".hps-links"),
                        Path.Combine(liveRoot, @".codex\.hps-auth"),
                        Path.Combine(root, "fordeletion")),
                    Store = new AtomicJsonStore(),
                    Junctions = new WindowsJunctionManager(),
                    SharedSentinelDirectory = Path.Combine(liveRoot, @".codex\shared")
                };

                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.LiveAuthPath));
                Directory.CreateDirectory(fixture.Paths.LiveBrowserLinkPath);
                Directory.CreateDirectory(fixture.SharedSentinelDirectory);
                File.WriteAllText(fixture.Paths.LiveAuthPath, "auth-a");
                File.WriteAllText(Path.Combine(fixture.Paths.LiveBrowserLinkPath, "browser-a.marker"), "A");
                File.WriteAllText(Path.Combine(fixture.SharedSentinelDirectory, "shared.txt"), "shared-state");

                ProfileLifecycleEngine lifecycle = fixture.CreateLifecycleEngine(new NoFaultInjector());
                LifecycleResult registration = lifecycle.RegisterFirstProfile("Alpha");
                fixture.ProfileA = registration.ActiveProfileId;
                lifecycle.MarkLaunchObserved(registration.TransactionId);

                LifecycleResult begin = lifecycle.BeginOnboarding("Beta");
                fixture.ProfileB = begin.CandidateProfileId;
                lifecycle.MarkLaunchObserved(begin.TransactionId);
                File.WriteAllText(fixture.Paths.LiveAuthPath, "auth-b");
                File.WriteAllText(Path.Combine(fixture.Paths.ProfileBrowserPath(fixture.ProfileB), "oauth.marker"), "signed-in");
                LifecycleResult finalize = lifecycle.FinalizeOnboarding();
                lifecycle.MarkLaunchObserved(finalize.TransactionId);
                return fixture;
            }

            public ApplicationStateSnapshot Inspect()
            {
                return new ApplicationStateInspector(Paths, Store, Junctions).Inspect();
            }

            public ProfileLifecycleEngine CreateLifecycleEngine(IFaultInjector faultInjector)
            {
                return new ProfileLifecycleEngine(
                    Paths,
                    Store,
                    new VerifiedFileMover(),
                    new VerifiedDirectoryMover(),
                    Junctions,
                    new AlwaysStoppedGuard(),
                    faultInjector,
                    PackageFamily,
                    PackageVersion);
            }

            public ProfileDeletionEngine CreateDeletionEngine(IFaultInjector faultInjector)
            {
                return CreateDeletionEngine(
                    faultInjector,
                    new VerifiedDirectoryMover(),
                    Junctions,
                    new NoOpRuntimeSecurity());
            }

            public ProfileDeletionEngine CreateDeletionEngine(
                IFaultInjector faultInjector,
                IVerifiedDirectoryMover directoryMover,
                IJunctionManager junctionManager,
                IRuntimeSecurity runtimeSecurity)
            {
                return new ProfileDeletionEngine(
                    Paths,
                    Store,
                    directoryMover,
                    junctionManager,
                    faultInjector,
                    PackageFamily,
                    PackageVersion,
                    runtimeSecurity);
            }

            public TransactionJournal PendingJournal()
            {
                foreach (string directory in Directory.GetDirectories(Paths.TransactionsDirectory))
                {
                    string journalPath = Path.Combine(directory, "journal.json");
                    if (!File.Exists(journalPath)) continue;
                    TransactionJournal journal = Store.Read<TransactionJournal>(journalPath);
                    if (!TransactionPhases.IsTerminal(journal.Phase)) return journal;
                }
                throw new InvalidOperationException("No pending journal was found.");
            }

            public void Finish(bool succeeded)
            {
                if (!succeeded)
                {
                    Console.Error.WriteLine("Profile deletion fixture retained for diagnosis: " + Root);
                    return;
                }
                string archiveRoot = FixtureWorkspace.PathFor("Archive/deletion");
                Directory.CreateDirectory(archiveRoot);
                Directory.Move(Root, Path.Combine(archiveRoot, Path.GetFileName(Root)));
            }
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
                    throw new InjectedDeletionFaultException("Injected deletion interruption after " + phase + ".");
                }
            }
        }

        private sealed class ThrowAfterPartialDirectoryDelete : IVerifiedDirectoryMover
        {
            private readonly IVerifiedDirectoryMover inner = new VerifiedDirectoryMover();
            private bool thrown;

            public DirectoryFingerprint Fingerprint(string path)
            {
                return inner.Fingerprint(path);
            }

            public void MoveDirectory(string sourcePath, string destinationPath)
            {
                inner.MoveDirectory(sourcePath, destinationPath);
            }

            public void DeleteDirectory(string path)
            {
                if (!thrown)
                {
                    string file = Directory.GetFiles(path, "*", SearchOption.AllDirectories).First();
                    File.Delete(file);
                    thrown = true;
                    throw new PostEffectFaultException("Injected failure after partial profile content deletion.");
                }
                inner.DeleteDirectory(path);
            }
        }

        private sealed class InjectedDeletionFaultException : Exception
        {
            public InjectedDeletionFaultException(string message)
                : base(message)
            {
            }
        }
    }
}
