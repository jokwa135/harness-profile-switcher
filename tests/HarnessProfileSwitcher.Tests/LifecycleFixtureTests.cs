using System;
using System.Collections.Generic;
using System.IO;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal static class LifecycleFixtureTests
    {
        private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        private const string PackageVersion = "fixture-version";

        public static int Run()
        {
            TestSuccessfulRegistrationFinalizeAndCancel();
            TestCancelBeforeOAuthProducesNoCredentialArtifact();
            TestEveryFirstRegistrationCheckpointRecovers();
            TestEveryBeginOnboardingCheckpointRecovers();
            TestEveryFinalizeOnboardingCheckpointRecovers();
            TestEveryCancelOnboardingCheckpointRecovers();
            TestLifecycleSecurityIntegration();
            TestPostEffectFirstRegistrationRecovery();
            TestPostEffectBeginOnboardingRecovery();
            TestPostEffectCancelOnboardingRecovery();
            TestLifecyclePostLedgerWriteRecovery();
            TestTamperedLifecycleJournalCannotRedirectRecovery();
            TestMixedOperationHistoryRemainsUsable();
            return 13;
        }

        private static void TestLifecycleSecurityIntegration()
        {
            LifecycleFixture fixture = LifecycleFixture.CreateUnmanaged("lifecycle-security");
            bool succeeded = false;
            try
            {
                WindowsRuntimeSecurity security = new WindowsRuntimeSecurity(fixture.Paths.RuntimeRoot, fixture.Paths.DeletionRoot);
                ProfileLifecycleEngine engine = fixture.CreateEngine(new NoFaultInjector(), security);
                LifecycleResult registration = engine.RegisterFirstProfile("Primary");
                fixture.ProfileA = registration.ActiveProfileId;
                engine.MarkLaunchObserved(registration.TransactionId);

                AssertHealthy(security.AuditDirectory(fixture.Paths.RuntimeRoot), "Lifecycle runtime root ACL was not hardened.");
                AssertHealthy(security.AuditDirectory(fixture.Paths.ProfileDirectory(fixture.ProfileA)), "Registered profile ACL was not hardened.");
                AssertHealthy(security.AuditDirectory(fixture.Paths.ProfileBrowserPath(fixture.ProfileA)), "Registered browser ACL was not hardened.");
                AssertHealthy(security.AuditFile(fixture.Paths.ProfileMetadataPath(fixture.ProfileA)), "Registered metadata ACL was not hardened.");
                AssertHealthy(security.AuditFile(fixture.Paths.JournalPath(registration.TransactionId)), "Registration journal ACL was not hardened.");

                LifecycleResult begin = engine.BeginOnboarding("Cancelled");
                fixture.ProfileB = begin.CandidateProfileId;
                engine.MarkLaunchObserved(begin.TransactionId);
                AssertHealthy(security.AuditFile(fixture.Paths.ProfileAuthPath(fixture.ProfileA)), "Vault authentication ACL was not hardened.");
                fixture.CompleteOAuth("auth-b-partial", fixture.ProfileB);

                LifecycleResult cancellation = engine.CancelOnboarding();
                TransactionJournal journal = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(cancellation.TransactionId));
                AssertHealthy(security.AuditDirectory(journal.LifecycleQuarantinePath), "Lifecycle quarantine ACL was not hardened.");
                AssertHealthy(security.AuditFile(journal.CandidateAuthQuarantinePath), "Quarantined authentication ACL was not hardened.");
                AssertHealthy(security.AuditDirectory(Path.Combine(journal.LifecycleQuarantinePath, "candidate-profile")), "Quarantined profile ACL was not hardened.");
                engine.MarkLaunchObserved(cancellation.TransactionId);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestPostEffectFirstRegistrationRecovery()
        {
            for (int scenario = 1; scenario <= 2; scenario++)
            {
                LifecycleFixture fixture = LifecycleFixture.CreateUnmanaged("register-post-effect-" + scenario);
                bool succeeded = false;
                try
                {
                    IVerifiedDirectoryMover directoryMover = scenario == 1
                        ? (IVerifiedDirectoryMover)new ThrowAfterDirectoryMove(1)
                        : new VerifiedDirectoryMover();
                    IJunctionManager junctionManager = scenario == 2
                        ? (IJunctionManager)new ThrowAfterJunctionMutation(fixture.Junctions, 1)
                        : fixture.Junctions;
                    ProfileLifecycleEngine crashing = fixture.CreateEngine(
                        new NoFaultInjector(),
                        new VerifiedFileMover(),
                        directoryMover,
                        junctionManager,
                        new NoOpRuntimeSecurity());
                    ExpectPostEffectFault(delegate { crashing.RegisterFirstProfile("Primary"); });

                    ProfileLifecycleEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                    LifecycleRecoveryResult recovery = recoveryEngine.RecoverPending();
                    AssertEqual("RolledBack", recovery.Result, "First registration post-effect failure did not roll back.");
                    AssertTrue(!File.Exists(fixture.Paths.LedgerPath), "Rolled-back registration created a ledger.");
                    AssertEqual("auth-a", File.ReadAllText(fixture.Paths.LiveAuthPath), "Registration recovery changed live authentication.");
                    AssertTrue(Directory.Exists(fixture.Paths.LiveBrowserLinkPath), "Registration recovery did not restore the unmanaged browser directory.");
                    AssertTrue(!fixture.Junctions.Exists(fixture.Paths.LiveBrowserLinkPath), "Registration recovery left a live junction.");
                    AssertEqual("A", File.ReadAllText(Path.Combine(fixture.Paths.LiveBrowserLinkPath, "browser-a.marker")), "Registration recovery changed browser state.");
                    TransactionJournal journal = FindJournal(fixture, TransactionPhases.RolledBack);
                    AssertTrue(Directory.Exists(Path.Combine(journal.LifecycleQuarantinePath, "uncommitted-profile")), "Uncommitted registration profile was not preserved for deletion.");
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestPostEffectBeginOnboardingRecovery()
        {
            for (int scenario = 1; scenario <= 3; scenario++)
            {
                LifecycleFixture fixture = LifecycleFixture.CreateManaged("begin-post-effect-" + scenario);
                bool succeeded = false;
                try
                {
                    IVerifiedFileMover fileMover = scenario == 1
                        ? (IVerifiedFileMover)new ThrowAfterFileMove(1)
                        : new VerifiedFileMover();
                    IJunctionManager junctionManager = scenario > 1
                        ? (IJunctionManager)new ThrowAfterJunctionMutation(fixture.Junctions, scenario - 1)
                        : fixture.Junctions;
                    ProfileLifecycleEngine crashing = fixture.CreateEngine(
                        new NoFaultInjector(),
                        fileMover,
                        new VerifiedDirectoryMover(),
                        junctionManager,
                        new NoOpRuntimeSecurity());
                    ExpectPostEffectFault(delegate { crashing.BeginOnboarding("Candidate"); });

                    LifecycleRecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                    AssertEqual("RolledBack", recovery.Result, "Begin-onboarding post-effect failure did not roll back.");
                    AssertActive(fixture, fixture.ProfileA, "auth-a");
                    TransactionJournal journal = FindJournal(fixture, TransactionPhases.RolledBack);
                    AssertTrue(Directory.Exists(Path.Combine(journal.LifecycleQuarantinePath, "uncommitted-candidate-profile")), "Uncommitted onboarding profile was not preserved for deletion.");
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestPostEffectCancelOnboardingRecovery()
        {
            for (int scenario = 1; scenario <= 5; scenario++)
            {
                LifecycleFixture fixture = LifecycleFixture.CreatePending("cancel-post-effect-" + scenario, true);
                bool succeeded = false;
                try
                {
                    IVerifiedFileMover fileMover = scenario <= 2
                        ? (IVerifiedFileMover)new ThrowAfterFileMove(scenario)
                        : new VerifiedFileMover();
                    IVerifiedDirectoryMover directoryMover = scenario == 3
                        ? (IVerifiedDirectoryMover)new ThrowAfterDirectoryMove(1)
                        : new VerifiedDirectoryMover();
                    IJunctionManager junctionManager = scenario >= 4
                        ? (IJunctionManager)new ThrowAfterJunctionMutation(fixture.Junctions, scenario - 3)
                        : fixture.Junctions;
                    ProfileLifecycleEngine crashing = fixture.CreateEngine(
                        new NoFaultInjector(),
                        fileMover,
                        directoryMover,
                        junctionManager,
                        new NoOpRuntimeSecurity());
                    ExpectPostEffectFault(delegate { crashing.CancelOnboarding(); });

                    LifecycleRecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                    AssertEqual("PendingOnboardingRestored", recovery.Result, "Cancel-onboarding post-effect failure did not restore pending state.");
                    AssertPending(fixture, fixture.ProfileA, fixture.ProfileB, true);
                    AssertEqual("auth-b", File.ReadAllText(fixture.Paths.LiveAuthPath), "Candidate authentication was not restored after cancellation interruption.");
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestLifecyclePostLedgerWriteRecovery()
        {
            TestRegistrationPostLedgerWriteRecovery();
            TestBeginPostLedgerWriteRecovery();
            TestFinalizePostLedgerWriteRecovery();
            TestCancelPostLedgerWriteRecovery();
        }

        private static void TestTamperedLifecycleJournalCannotRedirectRecovery()
        {
            LifecycleFixture fixture = LifecycleFixture.CreateManaged("tampered-lifecycle-journal");
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(TransactionPhases.Prepared));
                ExpectInjectedFault(delegate { crashing.BeginOnboarding("Candidate"); });
                TransactionJournal journal = FindJournal(fixture, TransactionPhases.Prepared);

                string sentinelDirectory = Path.Combine(fixture.Root, "unrelated-directory");
                Directory.CreateDirectory(sentinelDirectory);
                string sentinelFile = Path.Combine(sentinelDirectory, "keep.txt");
                File.WriteAllText(sentinelFile, "keep-me");
                journal.CandidateProfilePath = sentinelDirectory;
                fixture.Store.Write(fixture.Paths.JournalPath(journal.TransactionId), journal);

                ExpectInvalidJournal(delegate { fixture.CreateEngine(new NoFaultInjector()).RecoverPending(); });
                AssertTrue(Directory.Exists(sentinelDirectory) && File.Exists(sentinelFile), "Tampered lifecycle journal moved an unrelated directory.");
                AssertEqual("keep-me", File.ReadAllText(sentinelFile), "Unrelated directory content changed during rejected recovery.");
                AssertActive(fixture, fixture.ProfileA, "auth-a");
                TransactionJournal unchanged = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(journal.TransactionId));
                AssertEqual(TransactionPhases.Prepared, unchanged.Phase, "Rejected lifecycle journal was rewritten.");
                ApplicationStateSnapshot state = new ApplicationStateInspector(fixture.Paths, fixture.Store, fixture.Junctions).Inspect();
                AssertTrue(state.Mode == HarnessMode.Broken, "Tampered lifecycle journal was not surfaced as broken.");
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestMixedOperationHistoryRemainsUsable()
        {
            LifecycleFixture fixture = LifecycleFixture.CreatePending("mixed-operation-history", true);
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine lifecycle = fixture.CreateEngine(new NoFaultInjector());
                LifecycleResult finalized = lifecycle.FinalizeOnboarding();
                lifecycle.MarkLaunchObserved(finalized.TransactionId);
                AssertActive(fixture, fixture.ProfileB, "auth-b");

                SwitchEngine switchEngine = fixture.CreateSwitchEngine();
                SwitchResult switched = switchEngine.SwitchTo(fixture.ProfileA);
                switchEngine.MarkLaunchObserved(switched.TransactionId);
                AssertActive(fixture, fixture.ProfileA, "auth-a");

                LifecycleResult begin = lifecycle.BeginOnboarding("Temporary");
                fixture.ProfileC = begin.CandidateProfileId;
                lifecycle.MarkLaunchObserved(begin.TransactionId);
                AssertPending(fixture, fixture.ProfileA, fixture.ProfileC, false);
                LifecycleResult cancelled = lifecycle.CancelOnboarding();
                lifecycle.MarkLaunchObserved(cancelled.TransactionId);
                AssertActive(fixture, fixture.ProfileA, "auth-a");
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestRegistrationPostLedgerWriteRecovery()
        {
            LifecycleFixture fixture = LifecycleFixture.CreateUnmanaged("register-post-ledger");
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine crashing = fixture.CreateEngine(new NoFaultInjector(), new ThrowAfterLedgerWriteSecurity(fixture.Paths.LedgerPath));
                ExpectPostEffectFault(delegate { crashing.RegisterFirstProfile("Primary"); });
                LifecycleRecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                AssertEqual("RolledForward", recovery.Result, "Committed first-registration ledger did not roll forward.");
                fixture.ProfileA = recovery.ActiveProfileId;
                AssertActive(fixture, fixture.ProfileA, "auth-a");
                fixture.CreateEngine(new NoFaultInjector()).MarkLaunchObserved(recovery.TransactionId);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestBeginPostLedgerWriteRecovery()
        {
            LifecycleFixture fixture = LifecycleFixture.CreateManaged("begin-post-ledger");
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine crashing = fixture.CreateEngine(new NoFaultInjector(), new ThrowAfterLedgerWriteSecurity(fixture.Paths.LedgerPath));
                ExpectPostEffectFault(delegate { crashing.BeginOnboarding("Candidate"); });
                LifecycleRecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                AssertEqual("RolledForwardPendingOnboarding", recovery.Result, "Committed pending ledger did not roll forward.");
                fixture.ProfileB = recovery.CandidateProfileId;
                AssertPending(fixture, fixture.ProfileA, fixture.ProfileB, false);
                fixture.CreateEngine(new NoFaultInjector()).MarkLaunchObserved(recovery.TransactionId);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestFinalizePostLedgerWriteRecovery()
        {
            LifecycleFixture fixture = LifecycleFixture.CreatePending("finalize-post-ledger", true);
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine crashing = fixture.CreateEngine(new NoFaultInjector(), new ThrowAfterLedgerWriteSecurity(fixture.Paths.LedgerPath));
                ExpectPostEffectFault(delegate { crashing.FinalizeOnboarding(); });
                LifecycleRecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                AssertEqual("RolledForward", recovery.Result, "Committed finalize ledger did not roll forward.");
                AssertActive(fixture, fixture.ProfileB, "auth-b");
                fixture.CreateEngine(new NoFaultInjector()).MarkLaunchObserved(recovery.TransactionId);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestCancelPostLedgerWriteRecovery()
        {
            LifecycleFixture fixture = LifecycleFixture.CreatePending("cancel-post-ledger", true);
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine crashing = fixture.CreateEngine(new NoFaultInjector(), new ThrowAfterLedgerWriteSecurity(fixture.Paths.LedgerPath));
                ExpectPostEffectFault(delegate { crashing.CancelOnboarding(); });
                LifecycleRecoveryResult recovery = fixture.CreateEngine(new NoFaultInjector()).RecoverPending();
                AssertEqual("RolledForward", recovery.Result, "Committed cancellation ledger did not roll forward.");
                AssertActive(fixture, fixture.ProfileA, "auth-a");
                fixture.CreateEngine(new NoFaultInjector()).MarkLaunchObserved(recovery.TransactionId);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestSuccessfulRegistrationFinalizeAndCancel()
        {
            LifecycleFixture fixture = LifecycleFixture.CreateUnmanaged("lifecycle-success");
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine engine = fixture.CreateEngine(new NoFaultInjector());
                LifecycleResult registration = engine.RegisterFirstProfile("Primary");
                fixture.ProfileA = registration.ActiveProfileId;
                AssertActive(fixture, fixture.ProfileA, "auth-a");
                engine.MarkLaunchObserved(registration.TransactionId);

                LifecycleResult beginB = engine.BeginOnboarding("Secondary");
                fixture.ProfileB = beginB.CandidateProfileId;
                AssertPending(fixture, fixture.ProfileA, fixture.ProfileB, false);
                engine.MarkLaunchObserved(beginB.TransactionId);

                fixture.CompleteOAuth("auth-b", fixture.ProfileB);
                LifecycleResult finalizeB = engine.FinalizeOnboarding();
                AssertActive(fixture, fixture.ProfileB, "auth-b");
                AssertReady(fixture, fixture.ProfileA, "auth-a");
                engine.MarkLaunchObserved(finalizeB.TransactionId);

                LifecycleResult beginC = engine.BeginOnboarding("Cancelled");
                fixture.ProfileC = beginC.CandidateProfileId;
                engine.MarkLaunchObserved(beginC.TransactionId);
                fixture.CompleteOAuth("auth-c-partial", fixture.ProfileC);
                LifecycleResult cancelC = engine.CancelOnboarding();
                AssertActive(fixture, fixture.ProfileB, "auth-b");
                AssertTrue(!Directory.Exists(fixture.Paths.ProfileDirectory(fixture.ProfileC)), "Cancelled candidate profile remained loadable.");
                TransactionJournal cancellation = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(cancelC.TransactionId));
                AssertTrue(Directory.Exists(Path.Combine(cancellation.LifecycleQuarantinePath, "candidate-profile")), "Cancelled candidate profile was not preserved for deletion.");
                AssertEqual("auth-c-partial", File.ReadAllText(cancellation.CandidateAuthQuarantinePath), "Partial candidate authentication was not preserved.");
                engine.MarkLaunchObserved(cancelC.TransactionId);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestCancelBeforeOAuthProducesNoCredentialArtifact()
        {
            LifecycleFixture fixture = LifecycleFixture.CreatePending("cancel-before-oauth", false);
            bool succeeded = false;
            try
            {
                ProfileLifecycleEngine engine = fixture.CreateEngine(new NoFaultInjector());
                AssertThrows<InvalidOperationException>(delegate { engine.FinalizeOnboarding(); }, "Finalize accepted a candidate without OAuth authentication.");
                LifecycleResult cancellation = engine.CancelOnboarding();
                TransactionJournal journal = fixture.Store.Read<TransactionJournal>(fixture.Paths.JournalPath(cancellation.TransactionId));
                AssertActive(fixture, fixture.ProfileA, "auth-a");
                AssertTrue(!journal.CandidateAuthWasPresent, "Cancellation recorded nonexistent candidate authentication.");
                AssertTrue(!File.Exists(journal.CandidateAuthQuarantinePath), "Cancellation created a credential artifact when no candidate authentication existed.");
                engine.MarkLaunchObserved(cancellation.TransactionId);
                succeeded = true;
            }
            finally
            {
                fixture.Finish(succeeded);
            }
        }

        private static void TestEveryFirstRegistrationCheckpointRecovers()
        {
            string[] phases = new string[]
            {
                TransactionPhases.Prepared,
                TransactionPhases.ProcessesClosed,
                TransactionPhases.ProfileCreateIntent,
                TransactionPhases.ProfileCreated,
                TransactionPhases.BrowserTransferIntent,
                TransactionPhases.BrowserTransferred,
                TransactionPhases.LiveJunctionCreateIntent,
                TransactionPhases.LiveJunctionCreated,
                TransactionPhases.LedgerCommitIntent,
                TransactionPhases.LedgerCommitted
            };

            foreach (string phase in phases)
            {
                LifecycleFixture fixture = LifecycleFixture.CreateUnmanaged("register-" + phase);
                bool succeeded = false;
                try
                {
                    ProfileLifecycleEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(phase));
                    ExpectInjectedFault(delegate { crashing.RegisterFirstProfile("Primary"); });
                    ProfileLifecycleEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                    LifecycleRecoveryResult recovery = recoveryEngine.RecoverPending();
                    if (string.Equals(phase, TransactionPhases.LedgerCommitted, StringComparison.Ordinal))
                    {
                        AssertEqual("RolledForward", recovery.Result, "Committed registration did not roll forward.");
                        fixture.ProfileA = recovery.ActiveProfileId;
                        AssertActive(fixture, fixture.ProfileA, "auth-a");
                        TransactionJournal journal = FindJournal(fixture, TransactionPhases.LedgerCommitted);
                        recoveryEngine.MarkLaunchObserved(journal.TransactionId);
                    }
                    else
                    {
                        AssertEqual("RolledBack", recovery.Result, "Pre-commit registration did not roll back at " + phase + ".");
                        AssertTrue(!File.Exists(fixture.Paths.LedgerPath), "Rolled-back first registration created a ledger.");
                        AssertEqual("auth-a", File.ReadAllText(fixture.Paths.LiveAuthPath), "Registration rollback changed authentication.");
                        AssertTrue(Directory.Exists(fixture.Paths.LiveBrowserLinkPath), "Registration rollback did not restore the browser directory.");
                        AssertTrue(!fixture.Junctions.Exists(fixture.Paths.LiveBrowserLinkPath), "Registration rollback left a managed junction live.");
                    }
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestEveryBeginOnboardingCheckpointRecovers()
        {
            string[] phases = new string[]
            {
                TransactionPhases.Prepared,
                TransactionPhases.ProcessesClosed,
                TransactionPhases.ProfileCreateIntent,
                TransactionPhases.ProfileCreated,
                TransactionPhases.OutgoingAuthMoveIntent,
                TransactionPhases.OutgoingAuthMoved,
                TransactionPhases.OutgoingBrowserLinkParkIntent,
                TransactionPhases.OutgoingBrowserLinkParked,
                TransactionPhases.CandidateBrowserActivateIntent,
                TransactionPhases.CandidateBrowserActivated,
                TransactionPhases.PendingLedgerCommitIntent,
                TransactionPhases.PendingLedgerCommitted
            };

            foreach (string phase in phases)
            {
                LifecycleFixture fixture = LifecycleFixture.CreateManaged("begin-" + phase);
                bool succeeded = false;
                try
                {
                    ProfileLifecycleEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(phase));
                    ExpectInjectedFault(delegate { crashing.BeginOnboarding("Candidate"); });
                    ProfileLifecycleEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                    LifecycleRecoveryResult recovery = recoveryEngine.RecoverPending();
                    if (string.Equals(phase, TransactionPhases.PendingLedgerCommitted, StringComparison.Ordinal))
                    {
                        fixture.ProfileB = recovery.CandidateProfileId;
                        AssertEqual("RolledForwardPendingOnboarding", recovery.Result, "Committed begin-onboarding did not roll forward.");
                        AssertPending(fixture, fixture.ProfileA, fixture.ProfileB, false);
                        TransactionJournal journal = FindJournal(fixture, TransactionPhases.PendingLedgerCommitted);
                        recoveryEngine.MarkLaunchObserved(journal.TransactionId);
                    }
                    else
                    {
                        AssertEqual("RolledBack", recovery.Result, "Pre-commit begin-onboarding did not roll back at " + phase + ".");
                        AssertActive(fixture, fixture.ProfileA, "auth-a");
                    }
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestEveryFinalizeOnboardingCheckpointRecovers()
        {
            string[] phases = new string[]
            {
                TransactionPhases.Prepared,
                TransactionPhases.ProcessesClosed,
                TransactionPhases.CandidateValidationCompleted,
                TransactionPhases.LedgerCommitIntent,
                TransactionPhases.LedgerCommitted
            };

            foreach (string phase in phases)
            {
                LifecycleFixture fixture = LifecycleFixture.CreatePending("finalize-" + phase, true);
                bool succeeded = false;
                try
                {
                    ProfileLifecycleEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(phase));
                    ExpectInjectedFault(delegate { crashing.FinalizeOnboarding(); });
                    ProfileLifecycleEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                    LifecycleRecoveryResult recovery = recoveryEngine.RecoverPending();
                    if (string.Equals(phase, TransactionPhases.LedgerCommitted, StringComparison.Ordinal))
                    {
                        AssertEqual("RolledForward", recovery.Result, "Committed finalize-onboarding did not roll forward.");
                        AssertActive(fixture, fixture.ProfileB, "auth-b");
                        TransactionJournal journal = FindJournal(fixture, TransactionPhases.LedgerCommitted);
                        recoveryEngine.MarkLaunchObserved(journal.TransactionId);
                    }
                    else
                    {
                        AssertEqual("PendingOnboardingRestored", recovery.Result, "Pre-commit finalize-onboarding did not preserve pending state at " + phase + ".");
                        AssertPending(fixture, fixture.ProfileA, fixture.ProfileB, true);
                    }
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void TestEveryCancelOnboardingCheckpointRecovers()
        {
            string[] phases = new string[]
            {
                TransactionPhases.Prepared,
                TransactionPhases.ProcessesClosed,
                TransactionPhases.CandidateAuthQuarantineIntent,
                TransactionPhases.CandidateAuthQuarantined,
                TransactionPhases.CandidateBrowserLinkParkIntent,
                TransactionPhases.CandidateBrowserLinkParked,
                TransactionPhases.PreviousBrowserLinkActivateIntent,
                TransactionPhases.PreviousBrowserLinkActivated,
                TransactionPhases.CandidateProfileQuarantineIntent,
                TransactionPhases.CandidateProfileQuarantined,
                TransactionPhases.PreviousAuthActivateIntent,
                TransactionPhases.PreviousAuthActivated,
                TransactionPhases.LedgerCommitIntent,
                TransactionPhases.LedgerCommitted
            };

            foreach (string phase in phases)
            {
                LifecycleFixture fixture = LifecycleFixture.CreatePending("cancel-" + phase, true);
                bool succeeded = false;
                try
                {
                    ProfileLifecycleEngine crashing = fixture.CreateEngine(new ThrowAtPhaseFaultInjector(phase));
                    ExpectInjectedFault(delegate { crashing.CancelOnboarding(); });
                    ProfileLifecycleEngine recoveryEngine = fixture.CreateEngine(new NoFaultInjector());
                    LifecycleRecoveryResult recovery = recoveryEngine.RecoverPending();
                    if (string.Equals(phase, TransactionPhases.LedgerCommitted, StringComparison.Ordinal))
                    {
                        AssertEqual("RolledForward", recovery.Result, "Committed cancel-onboarding did not roll forward.");
                        AssertActive(fixture, fixture.ProfileA, "auth-a");
                        TransactionJournal journal = FindJournal(fixture, TransactionPhases.LedgerCommitted);
                        recoveryEngine.MarkLaunchObserved(journal.TransactionId);
                    }
                    else
                    {
                        AssertEqual("PendingOnboardingRestored", recovery.Result, "Pre-commit cancel-onboarding did not restore pending state at " + phase + ".");
                        AssertPending(fixture, fixture.ProfileA, fixture.ProfileB, true);
                    }
                    succeeded = true;
                }
                finally
                {
                    fixture.Finish(succeeded);
                }
            }
        }

        private static void AssertActive(LifecycleFixture fixture, string profileId, string expectedAuth)
        {
            Ledger ledger = fixture.Store.Read<Ledger>(fixture.Paths.LedgerPath);
            AssertEqual(profileId, ledger.ActiveProfileId, "Active profile mismatch.");
            AssertTrue(ledger.PendingOnboarding == null, "Active ledger retained pending onboarding.");
            AssertEqual(expectedAuth, File.ReadAllText(fixture.Paths.LiveAuthPath), "Live authentication mismatch.");
            AssertTrue(!File.Exists(fixture.Paths.ProfileAuthPath(profileId)), "Active profile retained vault authentication.");
            AssertLink(fixture, fixture.Paths.LiveBrowserLinkPath, fixture.Paths.ProfileBrowserPath(profileId));
        }

        private static void AssertReady(LifecycleFixture fixture, string profileId, string expectedAuth)
        {
            AssertEqual(expectedAuth, File.ReadAllText(fixture.Paths.ProfileAuthPath(profileId)), "Ready profile authentication mismatch.");
            AssertLink(fixture, fixture.Paths.InactiveLinkPath(profileId), fixture.Paths.ProfileBrowserPath(profileId));
        }

        private static void AssertPending(LifecycleFixture fixture, string previousProfileId, string candidateProfileId, bool expectLiveAuth)
        {
            Ledger ledger = fixture.Store.Read<Ledger>(fixture.Paths.LedgerPath);
            AssertTrue(string.IsNullOrWhiteSpace(ledger.ActiveProfileId), "Pending ledger names an active profile.");
            AssertTrue(ledger.PendingOnboarding != null, "Pending onboarding metadata is missing.");
            AssertEqual(previousProfileId, ledger.PendingOnboarding.PreviousProfileId, "Pending previous profile mismatch.");
            AssertEqual(candidateProfileId, ledger.PendingOnboarding.CandidateProfileId, "Pending candidate profile mismatch.");
            AssertReady(fixture, previousProfileId, "auth-a");
            AssertLink(fixture, fixture.Paths.LiveBrowserLinkPath, fixture.Paths.ProfileBrowserPath(candidateProfileId));
            AssertEqual(expectLiveAuth, File.Exists(fixture.Paths.LiveAuthPath), "Pending live-auth presence mismatch.");
        }

        private static void AssertLink(LifecycleFixture fixture, string linkPath, string targetPath)
        {
            AssertTrue(fixture.Junctions.Exists(linkPath), "Expected junction is missing: " + linkPath);
            AssertTrue(WindowsJunctionManager.PathsEqual(fixture.Junctions.ResolveTarget(linkPath), targetPath), "Junction target mismatch.");
        }

        private static TransactionJournal FindJournal(LifecycleFixture fixture, string phase)
        {
            foreach (string directory in Directory.GetDirectories(fixture.Paths.TransactionsDirectory))
            {
                string path = Path.Combine(directory, "journal.json");
                TransactionJournal journal = fixture.Store.Read<TransactionJournal>(path);
                if (string.Equals(journal.Phase, phase, StringComparison.Ordinal)) return journal;
            }
            throw new Exception("Journal was not found at phase " + phase + ".");
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
            throw new Exception("Expected an injected lifecycle fault.");
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
            throw new Exception("Expected a post-effect lifecycle fault.");
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
            throw new Exception("Expected structural lifecycle journal validation to reject recovery.");
        }

        private static void AssertHealthy(RuntimeAclAudit audit, string message)
        {
            AssertTrue(audit != null && audit.Exists && audit.Protected && audit.Healthy, message);
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertEqual(bool expected, bool actual, string message)
        {
            if (expected != actual) throw new Exception(message + " Expected " + expected + ", found " + actual + ".");
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new Exception(message + " Expected '" + expected + "', found '" + actual + "'.");
            }
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

        private sealed class LifecycleFixture
        {
            private LifecycleFixture()
            {
            }

            public string Root { get; private set; }
            public EnginePaths Paths { get; private set; }
            public AtomicJsonStore Store { get; private set; }
            public WindowsJunctionManager Junctions { get; private set; }
            public string ProfileA { get; set; }
            public string ProfileB { get; set; }
            public string ProfileC { get; set; }

            public static LifecycleFixture CreateUnmanaged(string label)
            {
                string root = Path.Combine(@"D:\AI\Temp\HarnessProfileSwitcher\LifecycleTests", label + "-" + Guid.NewGuid().ToString("N"));
                string liveRoot = Path.Combine(root, "live");
                string webRoot = Path.Combine(liveRoot, @"AppData\Codex\web");
                LifecycleFixture fixture = new LifecycleFixture();
                fixture.Root = root;
                fixture.Paths = new EnginePaths(
                    Path.Combine(root, "runtime"),
                    Path.Combine(liveRoot, @".codex\auth.json"),
                    Path.Combine(webRoot, "Codex"),
                    Path.Combine(webRoot, ".hps-links"),
                    Path.Combine(liveRoot, @".codex\.hps-auth"),
                    Path.Combine(root, "fordeletion"));
                fixture.Store = new AtomicJsonStore();
                fixture.Junctions = new WindowsJunctionManager();
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.LiveAuthPath));
                Directory.CreateDirectory(fixture.Paths.LiveBrowserLinkPath);
                File.WriteAllText(fixture.Paths.LiveAuthPath, "auth-a");
                File.WriteAllText(Path.Combine(fixture.Paths.LiveBrowserLinkPath, "browser-a.marker"), "A");
                return fixture;
            }

            public static LifecycleFixture CreateManaged(string label)
            {
                LifecycleFixture fixture = CreateUnmanaged(label);
                ProfileLifecycleEngine engine = fixture.CreateEngine(new NoFaultInjector());
                LifecycleResult registration = engine.RegisterFirstProfile("Primary");
                fixture.ProfileA = registration.ActiveProfileId;
                engine.MarkLaunchObserved(registration.TransactionId);
                return fixture;
            }

            public static LifecycleFixture CreatePending(string label, bool completeOAuth)
            {
                LifecycleFixture fixture = CreateManaged(label);
                ProfileLifecycleEngine engine = fixture.CreateEngine(new NoFaultInjector());
                LifecycleResult begin = engine.BeginOnboarding("Candidate");
                fixture.ProfileB = begin.CandidateProfileId;
                engine.MarkLaunchObserved(begin.TransactionId);
                if (completeOAuth) fixture.CompleteOAuth("auth-b", fixture.ProfileB);
                return fixture;
            }

            public void CompleteOAuth(string authentication, string profileId)
            {
                File.WriteAllText(Paths.LiveAuthPath, authentication);
                File.WriteAllText(Path.Combine(Paths.ProfileBrowserPath(profileId), "oauth-state.marker"), "signed-in");
            }

            public ProfileLifecycleEngine CreateEngine(IFaultInjector faultInjector)
            {
                return CreateEngine(faultInjector, new NoOpRuntimeSecurity());
            }

            public ProfileLifecycleEngine CreateEngine(IFaultInjector faultInjector, IRuntimeSecurity runtimeSecurity)
            {
                return CreateEngine(
                    faultInjector,
                    new VerifiedFileMover(),
                    new VerifiedDirectoryMover(),
                    Junctions,
                    runtimeSecurity);
            }

            public ProfileLifecycleEngine CreateEngine(
                IFaultInjector faultInjector,
                IVerifiedFileMover fileMover,
                IVerifiedDirectoryMover directoryMover,
                IJunctionManager junctionManager,
                IRuntimeSecurity runtimeSecurity)
            {
                return new ProfileLifecycleEngine(
                    Paths,
                    Store,
                    fileMover,
                    directoryMover,
                    junctionManager,
                    new AlwaysStoppedGuard(),
                    faultInjector,
                    PackageFamily,
                    PackageVersion,
                    runtimeSecurity);
            }

            public SwitchEngine CreateSwitchEngine()
            {
                return new SwitchEngine(
                    Paths,
                    Store,
                    new VerifiedFileMover(),
                    Junctions,
                    new AlwaysStoppedGuard(),
                    new NoFaultInjector(),
                    PackageFamily,
                    PackageVersion);
            }

            public void Finish(bool succeeded)
            {
                if (!succeeded)
                {
                    Console.Error.WriteLine("Lifecycle fixture retained for diagnosis: " + Root);
                    return;
                }
                string archiveRoot = @"D:\fordeletion\HarnessProfileSwitcher-lifecycle-tests";
                Directory.CreateDirectory(archiveRoot);
                string destination = Path.Combine(archiveRoot, Path.GetFileName(Root));
                Directory.Move(Root, destination);
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
                    throw new InjectedFaultException("Injected lifecycle interruption after " + currentPhase);
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
