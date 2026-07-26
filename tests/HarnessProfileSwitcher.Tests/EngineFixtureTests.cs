using System;
using System.IO;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal static class EngineFixtureTests
    {
        private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        private const string PackageVersion = "fixture-version";

        private static int Main(string[] arguments)
        {
            if (arguments != null && arguments.Length == 1 &&
                string.Equals(arguments[0], "--non-live-e2e", StringComparison.Ordinal))
            {
                return NonLiveEndToEndTests.Run();
            }
            try
            {
                TestSuccessfulRoundTripPreservesRotatedAuthentication();
                TestRollbackBeforeCommit();
                TestRollForwardAfterCommit();
                TestEverySwitchCheckpointRecoversDeterministically();
                TestPostEffectFileMoveRecovery();
                TestPostEffectJunctionRecovery();
                TestPostLedgerWriteRecovery();
                TestDuplicateAuthenticationIsPreserved();
                TestUnexpectedAuthenticationRequiresManualRecovery();
                TestTamperedSwitchJournalCannotRedirectRecovery();
                TestExclusiveOperationLease();
                TestSwitchSecurityIntegration();
                int processControlTestCount = ProcessControlTests.Run();
                int lifecycleTestCount = LifecycleFixtureTests.Run();
                int profileDeletionTestCount = ProfileDeletionFixtureTests.Run();
                int stateInspectorTestCount = ApplicationStateFixtureTests.Run();
                int runtimeSecurityTestCount = RuntimeSecurityTests.Run();
                Console.WriteLine("PASS: " + (12 + processControlTestCount + lifecycleTestCount + profileDeletionTestCount + stateInspectorTestCount + runtimeSecurityTestCount) + " fixture suites, including checkpoint and post-effect recovery, journal tamper rejection, permanent inactive-profile deletion, shared-state preservation, package-scoped process control, UI state derivation, ACL hardening, and closed-schema diagnostics");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                return 1;
            }
        }

        private static void TestSuccessfulRoundTripPreservesRotatedAuthentication()
        {
            Fixture fixture = Fixture.Create("roundtrip");
            bool succeeded = false;
            try
            {
                SwitchEngine engine = fixture.CreateEngine(new NoFaultInjector());
                SwitchResult toB = engine.SwitchTo(fixture.ProfileB);
                AssertTargetActive(fixture, fixture.ProfileB, fixture.ProfileA, "auth-b-original");
                engine.MarkLaunchObserved(toB.TransactionId);

                File.WriteAllText(fixture.Paths.LiveAuthPath, "auth-b-rotated");
                SwitchResult toA = engine.SwitchTo(fixture.ProfileA);
                AssertTargetActive(fixture, fixture.ProfileA, fixture.ProfileB, "auth-a-original");
                AssertEqual("auth-b-rotated", File.ReadAllText(fixture.Paths.ProfileAuthPath(fixture.ProfileB)), "Outgoing B authentication was not refreshed in its slot.");
                engine.MarkLaunchObserved(toA.TransactionId);

                TransactionJournal completed = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(toA.TransactionId));
                AssertEqual(TransactionPhases.Completed, completed.Phase, "Round-trip transaction was not completed.");
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestRollbackBeforeCommit()
        {
            Fixture fixture = Fixture.Create("rollback");
            bool succeeded = false;
            try
            {
                SwitchEngine crashingEngine = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(TransactionPhases.IncomingBrowserLinkActivated));
                ExpectInjectedFault(delegate { crashingEngine.SwitchTo(fixture.ProfileB); });

                SwitchEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                RecoveryResult recovery = recoveryEngine.RecoverPending();
                AssertEqual("RolledBack", recovery.Result, "Recovery did not roll back before the commit point.");
                AssertSourceActive(fixture, fixture.ProfileA, fixture.ProfileB, "auth-a-original", "auth-b-original");

                TransactionJournal rolledBack = FindJournalByPhase(fixture, TransactionPhases.RolledBack);
                AssertTrue(rolledBack != null, "Rolled-back journal was not preserved.");
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestRollForwardAfterCommit()
        {
            Fixture fixture = Fixture.Create("rollforward");
            bool succeeded = false;
            try
            {
                SwitchEngine crashingEngine = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(TransactionPhases.LedgerCommitted));
                ExpectInjectedFault(delegate { crashingEngine.SwitchTo(fixture.ProfileB); });

                SwitchEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                RecoveryResult recovery = recoveryEngine.RecoverPending();
                AssertEqual("RolledForward", recovery.Result, "Recovery did not roll forward after the commit point.");
                AssertTargetActive(fixture, fixture.ProfileB, fixture.ProfileA, "auth-b-original");

                TransactionJournal pending = FindJournalByPhase(fixture, TransactionPhases.LedgerCommitted);
                AssertTrue(pending != null, "Rolled-forward journal did not remain at the launch boundary.");
                recoveryEngine.MarkLaunchObserved(pending.TransactionId);
                TransactionJournal completed = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(pending.TransactionId));
                AssertEqual(TransactionPhases.Completed, completed.Phase, "Rolled-forward transaction was not completed after launch observation.");
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestEverySwitchCheckpointRecoversDeterministically()
        {
            string[] phases = new string[]
            {
                TransactionPhases.Prepared,
                TransactionPhases.ProcessesClosed,
                TransactionPhases.OutgoingAuthMoveIntent,
                TransactionPhases.OutgoingAuthMoved,
                TransactionPhases.IncomingAuthMoveIntent,
                TransactionPhases.IncomingAuthStaged,
                TransactionPhases.IncomingAuthActivateIntent,
                TransactionPhases.IncomingAuthActivated,
                TransactionPhases.OutgoingBrowserLinkParkIntent,
                TransactionPhases.OutgoingBrowserLinkParked,
                TransactionPhases.IncomingBrowserLinkActivateIntent,
                TransactionPhases.IncomingBrowserLinkActivated,
                TransactionPhases.LedgerCommitIntent,
                TransactionPhases.LedgerCommitted
            };

            foreach (string phase in phases)
            {
                Fixture fixture = Fixture.Create("checkpoint-" + phase);
                bool succeeded = false;
                try
                {
                    SwitchEngine crashingEngine = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(phase));
                    ExpectInjectedFault(delegate { crashingEngine.SwitchTo(fixture.ProfileB); });

                    SwitchEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                    RecoveryResult recovery = recoveryEngine.RecoverPending();
                    if (string.Equals(phase, TransactionPhases.LedgerCommitted, StringComparison.Ordinal))
                    {
                        AssertEqual("RolledForward", recovery.Result, "Commit-point interruption did not roll forward.");
                        AssertTargetActive(fixture, fixture.ProfileB, fixture.ProfileA, "auth-b-original");
                        TransactionJournal pending = FindJournalByPhase(fixture, TransactionPhases.LedgerCommitted);
                        AssertTrue(pending != null, "Commit-point recovery journal is missing.");
                        recoveryEngine.MarkLaunchObserved(pending.TransactionId);
                    }
                    else
                    {
                        AssertEqual("RolledBack", recovery.Result, "Pre-commit interruption did not roll back at " + phase + ".");
                        AssertSourceActive(fixture, fixture.ProfileA, fixture.ProfileB, "auth-a-original", "auth-b-original");
                    }
                    succeeded = true;
                }
                finally
                {
                    if (succeeded) fixture.Archive();
                    else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
                }
            }
        }

        private static void TestSwitchSecurityIntegration()
        {
            Fixture fixture = Fixture.Create("switch-security");
            bool succeeded = false;
            try
            {
                WindowsRuntimeSecurity security = new WindowsRuntimeSecurity(fixture.Paths.RuntimeRoot, fixture.Paths.DeletionRoot);
                SwitchEngine engine = fixture.CreateEngine(new NoFaultInjector(), security);
                SwitchResult result = engine.SwitchTo(fixture.ProfileB);
                engine.MarkLaunchObserved(result.TransactionId);

                AssertAclHealthy(security.AuditDirectory(fixture.Paths.RuntimeRoot), "Switch runtime root ACL was not hardened.");
                AssertAclHealthy(security.AuditDirectory(fixture.Paths.StateDirectory), "Switch state ACL was not hardened.");
                AssertAclHealthy(security.AuditDirectory(fixture.Paths.TransactionsDirectory), "Switch transaction root ACL was not hardened.");
                AssertAclHealthy(security.AuditFile(fixture.Paths.JournalPath(result.TransactionId)), "Switch journal ACL was not hardened.");
                AssertAclHealthy(security.AuditFile(fixture.Paths.ProfileAuthPath(fixture.ProfileA)), "Outgoing authentication ACL was not hardened.");
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestPostEffectFileMoveRecovery()
        {
            for (int moveNumber = 1; moveNumber <= 3; moveNumber++)
            {
                Fixture fixture = Fixture.Create("post-file-move-" + moveNumber);
                bool succeeded = false;
                try
                {
                    SwitchEngine crashing = fixture.CreateEngine(
                        new NoFaultInjector(),
                        new ThrowAfterFileMove(moveNumber),
                        fixture.Junctions,
                        new NoOpRuntimeSecurity());
                    ExpectPostEffectFault(delegate { crashing.SwitchTo(fixture.ProfileB); });

                    RecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                    AssertEqual("RolledBack", recovery.Result, "Post-effect file move did not roll back at move " + moveNumber + ".");
                    AssertSourceActive(fixture, fixture.ProfileA, fixture.ProfileB, "auth-a-original", "auth-b-original");
                    succeeded = true;
                }
                finally
                {
                    if (succeeded) fixture.Archive();
                    else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
                }
            }
        }

        private static void TestPostEffectJunctionRecovery()
        {
            for (int mutationNumber = 1; mutationNumber <= 2; mutationNumber++)
            {
                Fixture fixture = Fixture.Create("post-junction-" + mutationNumber);
                bool succeeded = false;
                try
                {
                    SwitchEngine crashing = fixture.CreateEngine(
                        new NoFaultInjector(),
                        new VerifiedFileMover(),
                        new ThrowAfterJunctionMutation(fixture.Junctions, mutationNumber),
                        new NoOpRuntimeSecurity());
                    ExpectPostEffectFault(delegate { crashing.SwitchTo(fixture.ProfileB); });

                    RecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                    AssertEqual("RolledBack", recovery.Result, "Post-effect junction mutation did not roll back at mutation " + mutationNumber + ".");
                    AssertSourceActive(fixture, fixture.ProfileA, fixture.ProfileB, "auth-a-original", "auth-b-original");
                    succeeded = true;
                }
                finally
                {
                    if (succeeded) fixture.Archive();
                    else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
                }
            }
        }

        private static void TestPostLedgerWriteRecovery()
        {
            Fixture fixture = Fixture.Create("post-ledger-write");
            bool succeeded = false;
            try
            {
                SwitchEngine crashing = fixture.CreateEngine(
                    new NoFaultInjector(),
                    new VerifiedFileMover(),
                    fixture.Junctions,
                    new ThrowAfterLedgerWriteSecurity(fixture.Paths.LedgerPath));
                ExpectPostEffectFault(delegate { crashing.SwitchTo(fixture.ProfileB); });

                Ledger committedLedger = fixture.Store.Read<Ledger>(fixture.Paths.LedgerPath);
                AssertEqual(fixture.ProfileB, committedLedger.ActiveProfileId, "Ledger did not reach disk before the simulated interruption.");
                TransactionJournal intent = FindJournalByPhase(fixture, TransactionPhases.LedgerCommitIntent);
                AssertTrue(intent != null, "Journal unexpectedly advanced beyond the ledger intent boundary.");

                SwitchEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                RecoveryResult recovery = recoveryEngine.RecoverPending();
                AssertEqual("RolledForward", recovery.Result, "A committed ledger with an intent-phase journal did not roll forward.");
                AssertTargetActive(fixture, fixture.ProfileB, fixture.ProfileA, "auth-b-original");
                recoveryEngine.MarkLaunchObserved(recovery.TransactionId);
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestDuplicateAuthenticationIsPreserved()
        {
            Fixture fixture = Fixture.Create("duplicate-auth-preservation");
            bool succeeded = false;
            try
            {
                SwitchEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(TransactionPhases.IncomingAuthActivated));
                ExpectInjectedFault(delegate { crashing.SwitchTo(fixture.ProfileB); });
                TransactionJournal journal = FindJournalByPhase(fixture, TransactionPhases.IncomingAuthActivated);
                AssertTrue(journal != null, "Interrupted journal was not found for duplicate preservation.");
                File.Copy(fixture.Paths.LiveAuthPath, fixture.Paths.ProfileAuthPath(fixture.ProfileB));

                RecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                AssertEqual("RolledBack", recovery.Result, "Duplicate target authentication did not roll back.");
                AssertSourceActive(fixture, fixture.ProfileA, fixture.ProfileB, "auth-a-original", "auth-b-original");

                string quarantine = Path.Combine(fixture.Paths.TransactionDirectory(journal.TransactionId), "quarantine");
                string[] preserved = Directory.GetFiles(quarantine, "*", SearchOption.AllDirectories);
                AssertTrue(preserved.Length == 1, "Duplicate authentication was not preserved exactly once.");
                AssertEqual("auth-b-original", File.ReadAllText(preserved[0]), "Preserved duplicate authentication changed content.");
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestUnexpectedAuthenticationRequiresManualRecovery()
        {
            Fixture fixture = Fixture.Create("unexpected-auth-recovery");
            bool succeeded = false;
            try
            {
                SwitchEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(TransactionPhases.IncomingAuthActivated));
                ExpectInjectedFault(delegate { crashing.SwitchTo(fixture.ProfileB); });
                TransactionJournal journal = FindJournalByPhase(fixture, TransactionPhases.IncomingAuthActivated);
                AssertTrue(journal != null, "Interrupted journal was not found for integrity tampering.");
                File.WriteAllText(fixture.Paths.LiveAuthPath, "unexpected-auth");

                ExpectRecoveryRequired(delegate { fixture.CreateEngine(new NoFaultInjector()).RecoverPending(); });
                TransactionJournal failed = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(journal.TransactionId));
                AssertEqual(TransactionPhases.RecoveryRequired, failed.Phase, "Failed recovery was not recorded as recovery-required.");
                AssertEqual("unexpected-auth", File.ReadAllText(fixture.Paths.LiveAuthPath), "Unexpected authentication was overwritten or removed.");
                AssertEqual("auth-a-original", File.ReadAllText(fixture.Paths.ProfileAuthPath(fixture.ProfileA)), "Recoverable source authentication was not preserved.");
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestTamperedSwitchJournalCannotRedirectRecovery()
        {
            Fixture fixture = Fixture.Create("tampered-switch-journal");
            bool succeeded = false;
            try
            {
                SwitchEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(TransactionPhases.Prepared));
                ExpectInjectedFault(delegate { crashing.SwitchTo(fixture.ProfileB); });
                TransactionJournal journal = FindJournalByPhase(fixture, TransactionPhases.Prepared);
                AssertTrue(journal != null, "Prepared switch journal was not found.");

                string sentinel = Path.Combine(fixture.Root, "unrelated-source-auth.json");
                File.WriteAllText(sentinel, "auth-a-original");
                journal.SourceAuthPath = sentinel;
                fixture.Store.Write(fixture.Paths.JournalPath(journal.TransactionId), journal);

                ExpectInvalidJournal(delegate { fixture.CreateEngine(new NoFaultInjector()).RecoverPending(); });
                AssertTrue(File.Exists(sentinel), "Tampered journal redirected recovery and moved an unrelated file.");
                AssertEqual("auth-a-original", File.ReadAllText(sentinel), "Unrelated file content changed during rejected recovery.");
                AssertSourceActive(fixture, fixture.ProfileA, fixture.ProfileB, "auth-a-original", "auth-b-original");
                TransactionJournal unchanged = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(journal.TransactionId));
                AssertEqual(TransactionPhases.Prepared, unchanged.Phase, "Rejected journal was rewritten despite failing structural validation.");
                ApplicationStateSnapshot state = new ApplicationStateInspector(fixture.Paths, fixture.Store, fixture.Junctions).Inspect();
                AssertTrue(state.Mode == HarnessMode.Broken, "Tampered journal was not surfaced as a broken state.");
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void TestExclusiveOperationLease()
        {
            Fixture fixture = Fixture.Create("exclusive-lease");
            bool succeeded = false;
            try
            {
                using (OperationLease.Acquire(fixture.Paths))
                {
                    bool secondLeaseRejected = false;
                    try
                    {
                        using (OperationLease.Acquire(fixture.Paths))
                        {
                        }
                    }
                    catch (IOException)
                    {
                        secondLeaseRejected = true;
                    }
                    catch (InvalidOperationException)
                    {
                        secondLeaseRejected = true;
                    }
                    AssertTrue(secondLeaseRejected, "A second operation lease was not rejected.");
                }
                succeeded = true;
            }
            finally
            {
                if (succeeded) fixture.Archive();
                else Console.Error.WriteLine("Fixture retained for diagnosis: " + fixture.Root);
            }
        }

        private static void AssertTargetActive(Fixture fixture, string activeId, string inactiveId, string expectedLiveAuth)
        {
            Ledger ledger = fixture.Store.Read<Ledger>(fixture.Paths.LedgerPath);
            AssertEqual(activeId, ledger.ActiveProfileId, "Ledger active profile mismatch.");
            AssertEqual(expectedLiveAuth, File.ReadAllText(fixture.Paths.LiveAuthPath), "Live authentication mismatch.");
            AssertTrue(!File.Exists(fixture.Paths.ProfileAuthPath(activeId)), "Active profile retained a vault authentication file.");
            AssertTrue(File.Exists(fixture.Paths.ProfileAuthPath(inactiveId)), "Inactive profile authentication is missing.");
            AssertLinkTarget(fixture, fixture.Paths.LiveBrowserLinkPath, fixture.Paths.ProfileBrowserPath(activeId));
            AssertLinkTarget(fixture, fixture.Paths.InactiveLinkPath(inactiveId), fixture.Paths.ProfileBrowserPath(inactiveId));
            AssertTrue(!fixture.Junctions.Exists(fixture.Paths.InactiveLinkPath(activeId)), "Active profile retained an inactive junction.");
        }

        private static void AssertSourceActive(Fixture fixture, string activeId, string inactiveId, string expectedLiveAuth, string expectedInactiveAuth)
        {
            AssertTargetActive(fixture, activeId, inactiveId, expectedLiveAuth);
            AssertEqual(expectedInactiveAuth, File.ReadAllText(fixture.Paths.ProfileAuthPath(inactiveId)), "Inactive authentication mismatch after rollback.");
        }

        private static void AssertLinkTarget(Fixture fixture, string linkPath, string expectedTarget)
        {
            AssertTrue(fixture.Junctions.Exists(linkPath), "Expected junction is missing: " + linkPath);
            string target = fixture.Junctions.ResolveTarget(linkPath);
            AssertTrue(WindowsJunctionManager.PathsEqual(target, expectedTarget), "Junction target mismatch for " + linkPath);
        }

        private static TransactionJournal FindJournalByPhase(Fixture fixture, string phase)
        {
            foreach (string directory in Directory.GetDirectories(fixture.Paths.TransactionsDirectory))
            {
                string path = Path.Combine(directory, "journal.json");
                TransactionJournal journal = fixture.Store.Read<TransactionJournal>(path);
                if (string.Equals(journal.Phase, phase, StringComparison.Ordinal))
                {
                    return journal;
                }
            }
            return null;
        }

        private static void ExpectInjectedFault(Action action)
        {
            try
            {
                action();
            }
            catch (InjectedFaultException)
            {
                return;
            }
            throw new Exception("Expected an injected fault.");
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
            throw new Exception("Expected a post-effect fault.");
        }

        private static void ExpectRecoveryRequired(Action action)
        {
            try
            {
                action();
            }
            catch (RecoveryRequiredException)
            {
                return;
            }
            throw new Exception("Expected recovery to stop on unexpected integrity.");
        }

        private static void ExpectInvalidJournal(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new Exception("Expected structural journal validation to reject recovery.");
        }

        private static void AssertAclHealthy(RuntimeAclAudit audit, string message)
        {
            AssertTrue(audit != null && audit.Exists && audit.Protected && audit.Healthy, message);
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

            public static Fixture Create(string label)
            {
                string id = Guid.NewGuid().ToString("N");
                string root = Path.Combine(@"D:\AI\Temp\HarnessProfileSwitcher\EngineTests", label + "-" + id);
                string runtimeRoot = Path.Combine(root, "runtime");
                string liveRoot = Path.Combine(root, "live");
                string liveAuth = Path.Combine(liveRoot, @".codex\auth.json");
                string webRoot = Path.Combine(liveRoot, @"AppData\Codex\web");
                string liveBrowser = Path.Combine(webRoot, "Codex");
                string inactiveLinks = Path.Combine(webRoot, ".hps-links");
                string authStaging = Path.Combine(liveRoot, @".codex\.hps-auth");

                Fixture fixture = new Fixture();
                fixture.Root = root;
                fixture.ProfileA = Guid.NewGuid().ToString("D");
                fixture.ProfileB = Guid.NewGuid().ToString("D");
                fixture.Paths = new EnginePaths(runtimeRoot, liveAuth, liveBrowser, inactiveLinks, authStaging);
                fixture.Store = new AtomicJsonStore();
                fixture.Junctions = new WindowsJunctionManager();

                Directory.CreateDirectory(Path.GetDirectoryName(liveAuth));
                Directory.CreateDirectory(inactiveLinks);
                Directory.CreateDirectory(fixture.Paths.StateDirectory);
                Directory.CreateDirectory(fixture.Paths.TransactionsDirectory);
                Directory.CreateDirectory(fixture.Paths.ProfileBrowserPath(fixture.ProfileA));
                Directory.CreateDirectory(fixture.Paths.ProfileBrowserPath(fixture.ProfileB));
                File.WriteAllText(Path.Combine(fixture.Paths.ProfileBrowserPath(fixture.ProfileA), "browser-a.marker"), "A");
                File.WriteAllText(Path.Combine(fixture.Paths.ProfileBrowserPath(fixture.ProfileB), "browser-b.marker"), "B");
                File.WriteAllText(liveAuth, "auth-a-original");
                File.WriteAllText(fixture.Paths.ProfileAuthPath(fixture.ProfileB), "auth-b-original");

                fixture.Store.Write(fixture.Paths.ProfileMetadataPath(fixture.ProfileA), CreateProfile(fixture.ProfileA, "A"));
                fixture.Store.Write(fixture.Paths.ProfileMetadataPath(fixture.ProfileB), CreateProfile(fixture.ProfileB, "B"));
                fixture.Store.Write(fixture.Paths.LedgerPath, new Ledger
                {
                    SchemaVersion = 1,
                    ActiveProfileId = fixture.ProfileA,
                    UpdatedAtUtc = DateTime.UtcNow.ToString("o")
                });

                fixture.Junctions.Create(fixture.Paths.LiveBrowserLinkPath, fixture.Paths.ProfileBrowserPath(fixture.ProfileA));
                fixture.Junctions.Create(fixture.Paths.InactiveLinkPath(fixture.ProfileB), fixture.Paths.ProfileBrowserPath(fixture.ProfileB));
                return fixture;
            }

            public SwitchEngine CreateEngine(IFaultInjector faultInjector)
            {
                return CreateEngine(faultInjector, new NoOpRuntimeSecurity());
            }

            public SwitchEngine CreateEngine(IFaultInjector faultInjector, IRuntimeSecurity runtimeSecurity)
            {
                return CreateEngine(faultInjector, new VerifiedFileMover(), Junctions, runtimeSecurity);
            }

            public SwitchEngine CreateEngine(
                IFaultInjector faultInjector,
                IVerifiedFileMover fileMover,
                IJunctionManager junctionManager,
                IRuntimeSecurity runtimeSecurity)
            {
                return new SwitchEngine(
                    Paths,
                    Store,
                    fileMover,
                    junctionManager,
                    new AlwaysStoppedGuard(),
                    faultInjector,
                    PackageFamily,
                    PackageVersion,
                    runtimeSecurity);
            }

            public void Archive()
            {
                string archiveRoot = @"D:\fordeletion\HarnessProfileSwitcher-engine-tests";
                Directory.CreateDirectory(archiveRoot);
                string destination = Path.Combine(archiveRoot, Path.GetFileName(Root));
                if (Directory.Exists(destination))
                {
                    throw new IOException("Fixture archive already exists: " + destination);
                }
                Directory.Move(Root, destination);
            }

            private static ProfileMetadata CreateProfile(string id, string name)
            {
                return new ProfileMetadata
                {
                    SchemaVersion = 1,
                    ProfileId = id,
                    DisplayName = name,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                    RegisteredPackageFamily = PackageFamily,
                    RegisteredPackageVersion = PackageVersion
                };
            }
        }

        private sealed class AlwaysStoppedGuard : IProcessGuard
        {
            public void EnsureStopped()
            {
            }
        }

        private sealed class ThrowAtPhaseFaultInjector : IFaultInjector
        {
            private readonly string phase;
            private bool thrown;

            public ThrowAtPhaseFaultInjector(string phase)
            {
                this.phase = phase;
            }

            public void Checkpoint(string currentPhase)
            {
                if (!thrown && string.Equals(phase, currentPhase, StringComparison.Ordinal))
                {
                    thrown = true;
                    throw new InjectedFaultException("Injected interruption after " + currentPhase);
                }
            }
        }

        private sealed class InjectedFaultException : Exception
        {
            public InjectedFaultException(string message)
                : base(message)
            {
            }
        }
    }
}
