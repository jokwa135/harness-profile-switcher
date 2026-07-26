using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HarnessProfileSwitcher.Core
{
    public sealed class ProfileLifecycleEngine
    {
        public const string RegisterFirstOperation = "register-first";
        public const string BeginOnboardingOperation = "begin-onboarding";
        public const string FinalizeOnboardingOperation = "finalize-onboarding";
        public const string CancelOnboardingOperation = "cancel-onboarding";

        private const string MissingIdentity = "Missing";
        private const string ExpectedIdentity = "Expected";
        private const string OtherIdentity = "Other";

        private readonly EnginePaths paths;
        private readonly AtomicJsonStore store;
        private readonly IVerifiedFileMover fileMover;
        private readonly IVerifiedDirectoryMover directoryMover;
        private readonly IJunctionManager junctions;
        private readonly IProcessGuard processGuard;
        private readonly IFaultInjector faultInjector;
        private readonly IRuntimeSecurity runtimeSecurity;
        private readonly string packageFamily;
        private readonly string packageVersion;

        public ProfileLifecycleEngine(
            EnginePaths paths,
            AtomicJsonStore store,
            IVerifiedFileMover fileMover,
            IVerifiedDirectoryMover directoryMover,
            IJunctionManager junctions,
            IProcessGuard processGuard,
            IFaultInjector faultInjector,
            string packageFamily,
            string packageVersion)
            : this(
                paths,
                store,
                fileMover,
                directoryMover,
                junctions,
                processGuard,
                faultInjector,
                packageFamily,
                packageVersion,
                new NoOpRuntimeSecurity())
        {
        }

        public ProfileLifecycleEngine(
            EnginePaths paths,
            AtomicJsonStore store,
            IVerifiedFileMover fileMover,
            IVerifiedDirectoryMover directoryMover,
            IJunctionManager junctions,
            IProcessGuard processGuard,
            IFaultInjector faultInjector,
            string packageFamily,
            string packageVersion,
            IRuntimeSecurity runtimeSecurity)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (store == null) throw new ArgumentNullException("store");
            if (fileMover == null) throw new ArgumentNullException("fileMover");
            if (directoryMover == null) throw new ArgumentNullException("directoryMover");
            if (junctions == null) throw new ArgumentNullException("junctions");
            if (processGuard == null) throw new ArgumentNullException("processGuard");
            if (faultInjector == null) throw new ArgumentNullException("faultInjector");
            if (runtimeSecurity == null) throw new ArgumentNullException("runtimeSecurity");
            if (string.IsNullOrWhiteSpace(packageFamily)) throw new ArgumentException("Package family is required.", "packageFamily");
            if (string.IsNullOrWhiteSpace(packageVersion)) throw new ArgumentException("Package version is required.", "packageVersion");

            this.paths = paths;
            this.store = store;
            this.fileMover = fileMover;
            this.directoryMover = directoryMover;
            this.junctions = junctions;
            this.processGuard = processGuard;
            this.faultInjector = faultInjector;
            this.runtimeSecurity = runtimeSecurity;
            this.packageFamily = packageFamily;
            this.packageVersion = packageVersion;
        }

        public LifecycleResult RegisterFirstProfile(string displayName)
        {
            displayName = NormalizeDisplayName(displayName);
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                RequireNoPendingJournal();
                if (File.Exists(paths.LedgerPath))
                {
                    throw new InvalidOperationException("A profile ledger already exists; first registration is not available.");
                }

                processGuard.EnsureStopped();
                if (!File.Exists(paths.LiveAuthPath))
                {
                    throw new FileNotFoundException("Live authentication is required for first registration.", paths.LiveAuthPath);
                }
                RequireRealDirectory(paths.LiveBrowserLinkPath, "The unmanaged browser-state path");
                DirectoryFingerprint browserFingerprint = directoryMover.Fingerprint(paths.LiveBrowserLinkPath);

                string transactionId = Guid.NewGuid().ToString("D");
                string profileId = Guid.NewGuid().ToString("D");
                TransactionJournal journal = NewLifecycleJournal(RegisterFirstOperation, transactionId, null, profileId, null, profileId, displayName);
                journal.CandidateProfilePath = paths.ProfileDirectory(profileId);
                journal.TargetBrowserPath = paths.ProfileBrowserPath(profileId);
                journal.LiveBrowserLinkPath = paths.LiveBrowserLinkPath;
                journal.CancelledCandidateLinkPath = paths.CancelledLinkPath(profileId, transactionId);
                ApplyDirectoryFingerprint(journal, browserFingerprint);
                SavePhase(journal, TransactionPhases.Prepared);
                SavePhase(journal, TransactionPhases.ProcessesClosed);

                SavePhase(journal, TransactionPhases.ProfileCreateIntent);
                CreateProfile(journal, false);
                SavePhase(journal, TransactionPhases.ProfileCreated);

                SavePhase(journal, TransactionPhases.BrowserTransferIntent);
                RequireDirectoryIdentity(journal.LiveBrowserLinkPath, journal, ExpectedIdentity, "unmanaged browser state");
                directoryMover.MoveDirectory(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                runtimeSecurity.ProtectTree(journal.TargetBrowserPath);
                RequireDirectoryIdentity(journal.TargetBrowserPath, journal, ExpectedIdentity, "registered browser state");
                SavePhase(journal, TransactionPhases.BrowserTransferred);

                SavePhase(journal, TransactionPhases.LiveJunctionCreateIntent);
                junctions.Create(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                RequireLink(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                SavePhase(journal, TransactionPhases.LiveJunctionCreated);

                ValidateActiveArrangement(profileId);
                SavePhase(journal, TransactionPhases.LedgerCommitIntent);
                WriteActiveLedger(profileId, transactionId);
                SavePhase(journal, TransactionPhases.LedgerCommitted);
                return ResultFor(journal, profileId);
            }
        }

        public LifecycleResult BeginOnboarding(string displayName)
        {
            displayName = NormalizeDisplayName(displayName);
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                RequireNoPendingJournal();
                Ledger ledger = ReadLedger();
                if (ledger.PendingOnboarding != null)
                {
                    throw new InvalidOperationException("An account onboarding operation is already pending.");
                }
                string previousProfileId = EnginePaths.NormalizeProfileId(ledger.ActiveProfileId);
                ReadAndValidateProfile(previousProfileId);
                ValidateActiveArrangement(previousProfileId);

                processGuard.EnsureStopped();
                FileFingerprint previousAuth = fileMover.Fingerprint(paths.LiveAuthPath);
                string transactionId = Guid.NewGuid().ToString("D");
                string candidateProfileId = Guid.NewGuid().ToString("D");
                TransactionJournal journal = NewLifecycleJournal(
                    BeginOnboardingOperation,
                    transactionId,
                    previousProfileId,
                    candidateProfileId,
                    previousProfileId,
                    candidateProfileId,
                    displayName);
                journal.SourceAuthPath = paths.ProfileAuthPath(previousProfileId);
                journal.SourceInactiveLinkPath = paths.InactiveLinkPath(previousProfileId);
                journal.SourceBrowserPath = paths.ProfileBrowserPath(previousProfileId);
                journal.TargetBrowserPath = paths.ProfileBrowserPath(candidateProfileId);
                journal.CandidateProfilePath = paths.ProfileDirectory(candidateProfileId);
                journal.CancelledCandidateLinkPath = paths.CancelledLinkPath(candidateProfileId, transactionId);
                journal.SourceAuthSha256 = previousAuth.Sha256;
                journal.SourceAuthLength = previousAuth.Length;
                SavePhase(journal, TransactionPhases.Prepared);
                SavePhase(journal, TransactionPhases.ProcessesClosed);

                SavePhase(journal, TransactionPhases.ProfileCreateIntent);
                CreateProfile(journal, true);
                SavePhase(journal, TransactionPhases.ProfileCreated);

                SavePhase(journal, TransactionPhases.OutgoingAuthMoveIntent);
                processGuard.EnsureStopped();
                fileMover.MoveFile(journal.LiveAuthPath, journal.SourceAuthPath);
                runtimeSecurity.ProtectFile(journal.SourceAuthPath);
                RequireFileIdentity(journal.SourceAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength, ExpectedIdentity, "previous profile authentication");
                SavePhase(journal, TransactionPhases.OutgoingAuthMoved);

                SavePhase(journal, TransactionPhases.OutgoingBrowserLinkParkIntent);
                processGuard.EnsureStopped();
                junctions.Move(journal.LiveBrowserLinkPath, journal.SourceInactiveLinkPath);
                RequireLink(journal.SourceInactiveLinkPath, journal.SourceBrowserPath);
                SavePhase(journal, TransactionPhases.OutgoingBrowserLinkParked);

                SavePhase(journal, TransactionPhases.CandidateBrowserActivateIntent);
                processGuard.EnsureStopped();
                junctions.Create(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                RequireLink(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                SavePhase(journal, TransactionPhases.CandidateBrowserActivated);

                ValidatePendingArrangement(journal, true);
                SavePhase(journal, TransactionPhases.PendingLedgerCommitIntent);
                WritePendingLedger(journal);
                SavePhase(journal, TransactionPhases.PendingLedgerCommitted);
                return ResultFor(journal, null);
            }
        }

        public LifecycleResult FinalizeOnboarding()
        {
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                RequireNoPendingJournal();
                Ledger ledger = ReadLedger();
                PendingOnboarding pending = RequirePendingOnboarding(ledger);
                string previousProfileId = EnginePaths.NormalizeProfileId(pending.PreviousProfileId);
                string candidateProfileId = EnginePaths.NormalizeProfileId(pending.CandidateProfileId);

                processGuard.EnsureStopped();
                ValidatePendingLedgerIdentity(pending);
                ReadAndValidateProfile(previousProfileId);
                ReadAndValidateProfile(candidateProfileId);
                ValidateReadyArrangement(previousProfileId);
                RequireLink(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(candidateProfileId));
                if (!File.Exists(paths.LiveAuthPath))
                {
                    throw new InvalidOperationException("OAuth has not produced live authentication for the candidate profile.");
                }
                DirectoryFingerprint browserFingerprint = directoryMover.Fingerprint(paths.ProfileBrowserPath(candidateProfileId));
                if (browserFingerprint.FileCount == 0)
                {
                    throw new InvalidOperationException("The candidate browser slot contains no sign-in state.");
                }
                FileFingerprint candidateAuth = fileMover.Fingerprint(paths.LiveAuthPath);
                FileFingerprint previousAuth = fileMover.Fingerprint(paths.ProfileAuthPath(previousProfileId));

                string transactionId = Guid.NewGuid().ToString("D");
                TransactionJournal journal = NewLifecycleJournal(
                    FinalizeOnboardingOperation,
                    transactionId,
                    previousProfileId,
                    candidateProfileId,
                    previousProfileId,
                    candidateProfileId,
                    null);
                journal.SourceAuthPath = paths.ProfileAuthPath(previousProfileId);
                journal.SourceAuthSha256 = previousAuth.Sha256;
                journal.SourceAuthLength = previousAuth.Length;
                journal.TargetAuthSha256 = candidateAuth.Sha256;
                journal.TargetAuthLength = candidateAuth.Length;
                journal.SourceInactiveLinkPath = paths.InactiveLinkPath(previousProfileId);
                journal.SourceBrowserPath = paths.ProfileBrowserPath(previousProfileId);
                journal.TargetBrowserPath = paths.ProfileBrowserPath(candidateProfileId);
                journal.CandidateProfilePath = paths.ProfileDirectory(candidateProfileId);
                ApplyDirectoryFingerprint(journal, browserFingerprint);
                SavePhase(journal, TransactionPhases.Prepared);
                SavePhase(journal, TransactionPhases.ProcessesClosed);

                ValidatePendingArrangement(journal, false);
                SavePhase(journal, TransactionPhases.CandidateValidationCompleted);
                TouchProfileActivated(candidateProfileId);
                SavePhase(journal, TransactionPhases.LedgerCommitIntent);
                WriteActiveLedger(candidateProfileId, transactionId);
                SavePhase(journal, TransactionPhases.LedgerCommitted);
                return ResultFor(journal, candidateProfileId);
            }
        }

        public LifecycleResult CancelOnboarding()
        {
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                RequireNoPendingJournal();
                Ledger ledger = ReadLedger();
                PendingOnboarding pending = RequirePendingOnboarding(ledger);
                string previousProfileId = EnginePaths.NormalizeProfileId(pending.PreviousProfileId);
                string candidateProfileId = EnginePaths.NormalizeProfileId(pending.CandidateProfileId);

                processGuard.EnsureStopped();
                ValidatePendingLedgerIdentity(pending);
                ReadAndValidateProfile(previousProfileId);
                ReadAndValidateProfile(candidateProfileId);
                ValidateReadyArrangement(previousProfileId);
                RequireLink(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(candidateProfileId));
                DirectoryFingerprint candidateProfileFingerprint = directoryMover.Fingerprint(paths.ProfileDirectory(candidateProfileId));
                FileFingerprint previousAuth = fileMover.Fingerprint(paths.ProfileAuthPath(previousProfileId));
                bool candidateAuthPresent = File.Exists(paths.LiveAuthPath);
                FileFingerprint candidateAuth = candidateAuthPresent ? fileMover.Fingerprint(paths.LiveAuthPath) : null;

                string transactionId = Guid.NewGuid().ToString("D");
                TransactionJournal journal = NewLifecycleJournal(
                    CancelOnboardingOperation,
                    transactionId,
                    candidateProfileId,
                    previousProfileId,
                    previousProfileId,
                    candidateProfileId,
                    null);
                journal.SourceAuthSha256 = candidateAuth == null ? null : candidateAuth.Sha256;
                journal.SourceAuthLength = candidateAuth == null ? 0 : candidateAuth.Length;
                journal.TargetAuthPath = paths.ProfileAuthPath(previousProfileId);
                journal.TargetAuthSha256 = previousAuth.Sha256;
                journal.TargetAuthLength = previousAuth.Length;
                journal.SourceBrowserPath = paths.ProfileBrowserPath(candidateProfileId);
                journal.TargetBrowserPath = paths.ProfileBrowserPath(previousProfileId);
                journal.SourceInactiveLinkPath = paths.InactiveLinkPath(previousProfileId);
                journal.CandidateProfilePath = paths.ProfileDirectory(candidateProfileId);
                journal.CancelledCandidateLinkPath = paths.CancelledLinkPath(candidateProfileId, transactionId);
                journal.CandidateAuthWasPresent = candidateAuthPresent;
                journal.CandidateAuthQuarantinePath = Path.Combine(journal.LifecycleQuarantinePath, "candidate-auth.json");
                ApplyDirectoryFingerprint(journal, candidateProfileFingerprint);
                SavePhase(journal, TransactionPhases.Prepared);
                SavePhase(journal, TransactionPhases.ProcessesClosed);

                SavePhase(journal, TransactionPhases.CandidateAuthQuarantineIntent);
                if (candidateAuthPresent)
                {
                    runtimeSecurity.EnsureProtectedDirectory(journal.LifecycleQuarantinePath);
                    fileMover.MoveFile(journal.LiveAuthPath, journal.CandidateAuthQuarantinePath);
                    runtimeSecurity.ProtectFile(journal.CandidateAuthQuarantinePath);
                    RequireFileIdentity(journal.CandidateAuthQuarantinePath, journal.SourceAuthSha256, journal.SourceAuthLength, ExpectedIdentity, "candidate authentication quarantine");
                }
                SavePhase(journal, TransactionPhases.CandidateAuthQuarantined);

                SavePhase(journal, TransactionPhases.CandidateBrowserLinkParkIntent);
                junctions.Move(journal.LiveBrowserLinkPath, journal.CancelledCandidateLinkPath);
                RequireLink(journal.CancelledCandidateLinkPath, journal.SourceBrowserPath);
                SavePhase(journal, TransactionPhases.CandidateBrowserLinkParked);

                SavePhase(journal, TransactionPhases.PreviousBrowserLinkActivateIntent);
                junctions.Move(journal.SourceInactiveLinkPath, journal.LiveBrowserLinkPath);
                RequireLink(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                SavePhase(journal, TransactionPhases.PreviousBrowserLinkActivated);

                SavePhase(journal, TransactionPhases.CandidateProfileQuarantineIntent);
                runtimeSecurity.EnsureProtectedDirectory(journal.LifecycleQuarantinePath);
                directoryMover.MoveDirectory(journal.CandidateProfilePath, CandidateProfileQuarantinePath(journal));
                runtimeSecurity.ProtectTree(CandidateProfileQuarantinePath(journal));
                RequireDirectoryIdentity(CandidateProfileQuarantinePath(journal), journal, ExpectedIdentity, "candidate profile quarantine");
                SavePhase(journal, TransactionPhases.CandidateProfileQuarantined);

                SavePhase(journal, TransactionPhases.PreviousAuthActivateIntent);
                fileMover.MoveFile(journal.TargetAuthPath, journal.LiveAuthPath);
                RequireFileIdentity(journal.LiveAuthPath, journal.TargetAuthSha256, journal.TargetAuthLength, ExpectedIdentity, "restored previous authentication");
                SavePhase(journal, TransactionPhases.PreviousAuthActivated);

                ValidateActiveArrangement(previousProfileId);
                TouchProfileActivated(previousProfileId);
                SavePhase(journal, TransactionPhases.LedgerCommitIntent);
                WriteActiveLedger(previousProfileId, transactionId);
                SavePhase(journal, TransactionPhases.LedgerCommitted);
                return ResultFor(journal, previousProfileId);
            }
        }

        public void MarkLaunchObserved(string transactionId)
        {
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                TransactionJournal journal = store.Read<TransactionJournal>(paths.JournalPath(transactionId));
                TransactionJournalValidator.ValidateLifecycle(paths, journal, packageFamily, transactionId);
                RequireLifecycleOperation(journal);
                if (string.Equals(journal.Phase, TransactionPhases.Completed, StringComparison.Ordinal)) return;

                bool begin = string.Equals(journal.Operation, BeginOnboardingOperation, StringComparison.Ordinal);
                string expectedCommit = begin ? TransactionPhases.PendingLedgerCommitted : TransactionPhases.LedgerCommitted;
                if (!string.Equals(journal.Phase, expectedCommit, StringComparison.Ordinal) &&
                    !string.Equals(journal.Phase, TransactionPhases.LaunchObserved, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Lifecycle transaction has not reached its commit point: " + journal.Phase);
                }

                Ledger ledger = ReadLedger();
                if (begin)
                {
                    RequireMatchingPendingLedger(ledger, journal);
                    ValidatePendingArrangement(journal, false);
                }
                else
                {
                    if (ledger.PendingOnboarding != null ||
                        !string.Equals(ledger.ActiveProfileId, journal.TargetProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RecoveryRequiredException("Ledger no longer names the committed lifecycle target.");
                    }
                    ValidateActiveArrangement(journal.TargetProfileId);
                }

                SavePhase(journal, TransactionPhases.LaunchObserved);
                SavePhase(journal, TransactionPhases.Completed);
            }
        }

        public LifecycleRecoveryResult RecoverPending()
        {
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                TransactionJournal journal = FindPendingJournal();
                if (journal == null)
                {
                    return new LifecycleRecoveryResult { Result = "NoPendingTransaction" };
                }
                RequireLifecycleOperation(journal);
                processGuard.EnsureStopped();
                try
                {
                    if (string.Equals(journal.Operation, RegisterFirstOperation, StringComparison.Ordinal))
                    {
                        return RecoverFirstRegistration(journal);
                    }
                    if (string.Equals(journal.Operation, BeginOnboardingOperation, StringComparison.Ordinal))
                    {
                        return RecoverBeginOnboarding(journal);
                    }
                    if (string.Equals(journal.Operation, FinalizeOnboardingOperation, StringComparison.Ordinal))
                    {
                        return RecoverFinalizeOnboarding(journal);
                    }
                    return RecoverCancelOnboarding(journal);
                }
                catch (Exception exception)
                {
                    try
                    {
                        journal.Phase = TransactionPhases.RecoveryRequired;
                        journal.UpdatedAtUtc = UtcNow();
                        WriteProtectedJson(paths.JournalPath(journal.TransactionId), journal);
                    }
                    catch
                    {
                    }
                    throw new RecoveryRequiredException("Automatic lifecycle recovery could not establish a safe account state.", exception);
                }
            }
        }

        private LifecycleRecoveryResult RecoverFirstRegistration(TransactionJournal journal)
        {
            Ledger ledger = TryReadLedger();
            bool committed = ledger != null && ledger.PendingOnboarding == null &&
                             string.Equals(ledger.ActiveProfileId, journal.TargetProfileId, StringComparison.OrdinalIgnoreCase);
            if (committed)
            {
                EnsureProfileMetadata(journal, false);
                EnsureRegistrationBrowserCommitted(journal);
                ValidateActiveArrangement(journal.TargetProfileId);
                WriteActiveLedger(journal.TargetProfileId, journal.TransactionId);
                SetPhaseWithoutFault(journal, TransactionPhases.LedgerCommitted);
                return Recovery(journal, "RolledForward", journal.TargetProfileId, null);
            }
            if (ledger != null)
            {
                throw new InvalidOperationException("An unexpected ledger exists during first-registration recovery.");
            }

            SetPhaseWithoutFault(journal, TransactionPhases.RollbackStarted);
            ParkExpectedLiveJunction(journal, journal.TargetBrowserPath);
            RestoreOriginalRegistrationBrowser(journal);
            PreserveDirectoryIfPresent(journal.CandidateProfilePath, Path.Combine(journal.LifecycleQuarantinePath, "uncommitted-profile"));
            SetPhaseWithoutFault(journal, TransactionPhases.RolledBack);
            return Recovery(journal, "RolledBack", null, null);
        }

        private LifecycleRecoveryResult RecoverBeginOnboarding(TransactionJournal journal)
        {
            Ledger ledger = ReadLedger();
            bool committed = HasMatchingPendingLedger(ledger, journal);
            if (committed)
            {
                EnsureBeginAuthenticationCommitted(journal);
                EnsureBeginLinksCommitted(journal);
                ValidatePendingArrangement(journal, false);
                WritePendingLedger(journal);
                SetPhaseWithoutFault(journal, TransactionPhases.PendingLedgerCommitted);
                return Recovery(journal, "RolledForwardPendingOnboarding", null, journal.CandidateProfileId);
            }
            if (ledger.PendingOnboarding != null ||
                !string.Equals(ledger.ActiveProfileId, journal.PreviousProfileId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Ledger does not match either side of begin-onboarding recovery.");
            }

            SetPhaseWithoutFault(journal, TransactionPhases.RollbackStarted);
            RestoreBeginLinksToPrevious(journal);
            RestoreBeginAuthenticationToPrevious(journal);
            PreserveDirectoryIfPresent(journal.CandidateProfilePath, Path.Combine(journal.LifecycleQuarantinePath, "uncommitted-candidate-profile"));
            ValidateActiveArrangement(journal.PreviousProfileId);
            WriteActiveLedger(journal.PreviousProfileId, null);
            SetPhaseWithoutFault(journal, TransactionPhases.RolledBack);
            return Recovery(journal, "RolledBack", journal.PreviousProfileId, null);
        }

        private LifecycleRecoveryResult RecoverFinalizeOnboarding(TransactionJournal journal)
        {
            Ledger ledger = ReadLedger();
            if (ledger.PendingOnboarding == null &&
                string.Equals(ledger.ActiveProfileId, journal.CandidateProfileId, StringComparison.OrdinalIgnoreCase))
            {
                ValidateActiveArrangement(journal.CandidateProfileId);
                ValidateReadyArrangement(journal.PreviousProfileId);
                WriteActiveLedger(journal.CandidateProfileId, journal.TransactionId);
                SetPhaseWithoutFault(journal, TransactionPhases.LedgerCommitted);
                return Recovery(journal, "RolledForward", journal.CandidateProfileId, null);
            }
            if (HasPendingProfiles(ledger, journal.PreviousProfileId, journal.CandidateProfileId))
            {
                ValidatePendingArrangement(journal, false);
                SetPhaseWithoutFault(journal, TransactionPhases.RolledBack);
                return Recovery(journal, "PendingOnboardingRestored", null, journal.CandidateProfileId);
            }
            throw new InvalidOperationException("Ledger does not match either side of finalize-onboarding recovery.");
        }

        private LifecycleRecoveryResult RecoverCancelOnboarding(TransactionJournal journal)
        {
            Ledger ledger = ReadLedger();
            bool committed = ledger.PendingOnboarding == null &&
                             string.Equals(ledger.ActiveProfileId, journal.PreviousProfileId, StringComparison.OrdinalIgnoreCase);
            if (committed)
            {
                EnsureCancelAuthenticationCommitted(journal);
                EnsureCancelProfileCommitted(journal);
                EnsureCancelLinksCommitted(journal);
                ValidateActiveArrangement(journal.PreviousProfileId);
                WriteActiveLedger(journal.PreviousProfileId, journal.TransactionId);
                SetPhaseWithoutFault(journal, TransactionPhases.LedgerCommitted);
                return Recovery(journal, "RolledForward", journal.PreviousProfileId, null);
            }
            if (!HasPendingProfiles(ledger, journal.PreviousProfileId, journal.CandidateProfileId))
            {
                throw new InvalidOperationException("Ledger does not match either side of cancel-onboarding recovery.");
            }

            SetPhaseWithoutFault(journal, TransactionPhases.RollbackStarted);
            RestoreCancelProfileToPending(journal);
            RestoreCancelLinksToPending(journal);
            RestoreCancelAuthenticationToPending(journal);
            ValidatePendingArrangement(journal, false);
            SetPhaseWithoutFault(journal, TransactionPhases.RolledBack);
            return Recovery(journal, "PendingOnboardingRestored", null, journal.CandidateProfileId);
        }

        private void EnsureRegistrationBrowserCommitted(TransactionJournal journal)
        {
            string targetIdentity = IdentifyDirectory(journal.TargetBrowserPath, journal);
            bool liveJunction = junctions.Exists(journal.LiveBrowserLinkPath);
            if (liveJunction)
            {
                RequireLink(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                if (!string.Equals(targetIdentity, ExpectedIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Committed browser target is incomplete.");
                }
                return;
            }

            string liveIdentity = IdentifyDirectory(journal.LiveBrowserLinkPath, journal);
            if (string.Equals(targetIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(liveIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(journal.LiveBrowserLinkPath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-live-browser"));
                }
            }
            else if (string.Equals(liveIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(targetIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(journal.TargetBrowserPath, Path.Combine(journal.LifecycleQuarantinePath, "partial-target-browser"));
                }
                directoryMover.MoveDirectory(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
                runtimeSecurity.ProtectTree(journal.TargetBrowserPath);
            }
            else
            {
                throw new InvalidOperationException("No complete browser tree exists for committed registration.");
            }
            junctions.Create(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
        }

        private void RestoreOriginalRegistrationBrowser(TransactionJournal journal)
        {
            string liveIdentity = junctions.Exists(journal.LiveBrowserLinkPath)
                ? MissingIdentity
                : IdentifyDirectory(journal.LiveBrowserLinkPath, journal);
            string targetIdentity = IdentifyDirectory(journal.TargetBrowserPath, journal);
            if (string.Equals(liveIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(targetIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(journal.TargetBrowserPath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-target-browser"));
                }
                return;
            }
            if (string.Equals(targetIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(liveIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(journal.LiveBrowserLinkPath, Path.Combine(journal.LifecycleQuarantinePath, "partial-live-browser"));
                }
                directoryMover.MoveDirectory(journal.TargetBrowserPath, journal.LiveBrowserLinkPath);
                return;
            }
            throw new InvalidOperationException("No complete browser tree exists for first-registration rollback.");
        }

        private void EnsureBeginAuthenticationCommitted(TransactionJournal journal)
        {
            string vault = IdentifyFile(journal.SourceAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength);
            string live = IdentifyFile(journal.LiveAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength);
            if (string.Equals(vault, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (string.Equals(live, ExpectedIdentity, StringComparison.Ordinal))
                {
                    PreserveFile(journal.LiveAuthPath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-previous-auth.json"));
                }
                return;
            }
            if (string.Equals(vault, MissingIdentity, StringComparison.Ordinal) &&
                string.Equals(live, ExpectedIdentity, StringComparison.Ordinal))
            {
                fileMover.MoveFile(journal.LiveAuthPath, journal.SourceAuthPath);
                runtimeSecurity.ProtectFile(journal.SourceAuthPath);
                return;
            }
            throw new InvalidOperationException("Previous authentication cannot be established in its vault slot.");
        }

        private void RestoreBeginAuthenticationToPrevious(TransactionJournal journal)
        {
            string live = IdentifyFile(journal.LiveAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength);
            string vault = IdentifyFile(journal.SourceAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength);
            if (string.Equals(live, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (string.Equals(vault, ExpectedIdentity, StringComparison.Ordinal))
                {
                    PreserveFile(journal.SourceAuthPath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-vault-auth.json"));
                }
                else if (!string.Equals(vault, MissingIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Previous vault contains unexpected authentication.");
                }
                return;
            }
            if (string.Equals(live, MissingIdentity, StringComparison.Ordinal) &&
                string.Equals(vault, ExpectedIdentity, StringComparison.Ordinal))
            {
                fileMover.MoveFile(journal.SourceAuthPath, journal.LiveAuthPath);
                return;
            }
            throw new InvalidOperationException("Previous authentication cannot be restored to the live slot.");
        }

        private void EnsureBeginLinksCommitted(TransactionJournal journal)
        {
            EnsureProfileMetadata(journal, true);
            if (junctions.Exists(journal.LiveBrowserLinkPath))
            {
                string liveTarget = junctions.ResolveTarget(journal.LiveBrowserLinkPath);
                if (WindowsJunctionManager.PathsEqual(liveTarget, journal.TargetBrowserPath))
                {
                    RequireLink(journal.SourceInactiveLinkPath, journal.SourceBrowserPath);
                    return;
                }
                if (!WindowsJunctionManager.PathsEqual(liveTarget, journal.SourceBrowserPath))
                {
                    throw new InvalidOperationException("Live browser junction targets an unknown profile.");
                }
                if (junctions.Exists(journal.SourceInactiveLinkPath))
                {
                    throw new InvalidOperationException("Previous browser junction exists in both active and inactive locations.");
                }
                junctions.Move(journal.LiveBrowserLinkPath, journal.SourceInactiveLinkPath);
            }
            else if (!junctions.Exists(journal.SourceInactiveLinkPath))
            {
                throw new InvalidOperationException("Previous browser junction is missing.");
            }
            junctions.Create(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
        }

        private void RestoreBeginLinksToPrevious(TransactionJournal journal)
        {
            if (junctions.Exists(journal.LiveBrowserLinkPath))
            {
                string target = junctions.ResolveTarget(journal.LiveBrowserLinkPath);
                if (WindowsJunctionManager.PathsEqual(target, journal.TargetBrowserPath))
                {
                    ParkExpectedLiveJunction(journal, journal.TargetBrowserPath);
                }
                else if (!WindowsJunctionManager.PathsEqual(target, journal.SourceBrowserPath))
                {
                    throw new InvalidOperationException("Live browser junction targets an unknown profile.");
                }
            }
            if (!junctions.Exists(journal.LiveBrowserLinkPath))
            {
                junctions.Move(journal.SourceInactiveLinkPath, journal.LiveBrowserLinkPath);
            }
            RequireLink(journal.LiveBrowserLinkPath, journal.SourceBrowserPath);
        }

        private void RestoreCancelProfileToPending(TransactionJournal journal)
        {
            string source = journal.CandidateProfilePath;
            string quarantine = CandidateProfileQuarantinePath(journal);
            string sourceIdentity = IdentifyDirectory(source, journal);
            string quarantineIdentity = IdentifyDirectory(quarantine, journal);
            if (string.Equals(sourceIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(quarantineIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(quarantine, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-candidate-profile"));
                }
                return;
            }
            if (string.Equals(quarantineIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(sourceIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(source, Path.Combine(journal.LifecycleQuarantinePath, "partial-candidate-profile"));
                }
                directoryMover.MoveDirectory(quarantine, source);
                runtimeSecurity.ProtectTree(source);
                return;
            }
            throw new InvalidOperationException("Candidate profile cannot be restored from cancellation quarantine.");
        }

        private void EnsureCancelProfileCommitted(TransactionJournal journal)
        {
            string sourceIdentity = IdentifyDirectory(journal.CandidateProfilePath, journal);
            string quarantineIdentity = IdentifyDirectory(CandidateProfileQuarantinePath(journal), journal);
            if (string.Equals(quarantineIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(sourceIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(journal.CandidateProfilePath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-source-candidate-profile"));
                }
                return;
            }
            if (string.Equals(sourceIdentity, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (!string.Equals(quarantineIdentity, MissingIdentity, StringComparison.Ordinal))
                {
                    PreserveDirectoryIfPresent(CandidateProfileQuarantinePath(journal), Path.Combine(journal.LifecycleQuarantinePath, "partial-quarantined-candidate-profile"));
                }
                runtimeSecurity.EnsureProtectedDirectory(journal.LifecycleQuarantinePath);
                directoryMover.MoveDirectory(journal.CandidateProfilePath, CandidateProfileQuarantinePath(journal));
                runtimeSecurity.ProtectTree(CandidateProfileQuarantinePath(journal));
                return;
            }
            throw new InvalidOperationException("Candidate profile cannot be established in cancellation quarantine.");
        }

        private void RestoreCancelLinksToPending(TransactionJournal journal)
        {
            if (junctions.Exists(journal.LiveBrowserLinkPath))
            {
                string target = junctions.ResolveTarget(journal.LiveBrowserLinkPath);
                if (WindowsJunctionManager.PathsEqual(target, journal.TargetBrowserPath))
                {
                    if (junctions.Exists(journal.SourceInactiveLinkPath))
                    {
                        throw new InvalidOperationException("Previous browser junction is duplicated.");
                    }
                    junctions.Move(journal.LiveBrowserLinkPath, journal.SourceInactiveLinkPath);
                }
                else if (!WindowsJunctionManager.PathsEqual(target, journal.SourceBrowserPath))
                {
                    throw new InvalidOperationException("Live browser junction targets an unknown profile.");
                }
            }
            if (!junctions.Exists(journal.LiveBrowserLinkPath))
            {
                junctions.Move(journal.CancelledCandidateLinkPath, journal.LiveBrowserLinkPath);
            }
            RequireLink(journal.LiveBrowserLinkPath, journal.SourceBrowserPath);
            RequireLink(journal.SourceInactiveLinkPath, journal.TargetBrowserPath);
        }

        private void EnsureCancelLinksCommitted(TransactionJournal journal)
        {
            if (junctions.Exists(journal.LiveBrowserLinkPath))
            {
                string target = junctions.ResolveTarget(journal.LiveBrowserLinkPath);
                if (WindowsJunctionManager.PathsEqual(target, journal.SourceBrowserPath))
                {
                    ParkExpectedLiveJunction(journal, journal.SourceBrowserPath);
                }
                else if (!WindowsJunctionManager.PathsEqual(target, journal.TargetBrowserPath))
                {
                    throw new InvalidOperationException("Live browser junction targets an unknown profile.");
                }
            }
            if (!junctions.Exists(journal.LiveBrowserLinkPath))
            {
                junctions.Move(journal.SourceInactiveLinkPath, journal.LiveBrowserLinkPath);
            }
            RequireLink(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
            if (!junctions.Exists(journal.CancelledCandidateLinkPath))
            {
                throw new InvalidOperationException("Cancelled candidate junction metadata is missing.");
            }
        }

        private void RestoreCancelAuthenticationToPending(TransactionJournal journal)
        {
            string livePrevious = IdentifyFile(journal.LiveAuthPath, journal.TargetAuthSha256, journal.TargetAuthLength);
            string vaultPrevious = IdentifyFile(journal.TargetAuthPath, journal.TargetAuthSha256, journal.TargetAuthLength);
            if (string.Equals(livePrevious, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (string.Equals(vaultPrevious, MissingIdentity, StringComparison.Ordinal))
                {
                    fileMover.MoveFile(journal.LiveAuthPath, journal.TargetAuthPath);
                    runtimeSecurity.ProtectFile(journal.TargetAuthPath);
                }
                else if (string.Equals(vaultPrevious, ExpectedIdentity, StringComparison.Ordinal))
                {
                    PreserveFile(journal.LiveAuthPath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-live-previous-auth.json"));
                }
                else
                {
                    throw new InvalidOperationException("Previous vault authentication is unexpected.");
                }
            }
            else if (!string.Equals(vaultPrevious, ExpectedIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Previous authentication cannot be restored to its vault slot.");
            }

            if (!journal.CandidateAuthWasPresent)
            {
                if (File.Exists(journal.CandidateAuthQuarantinePath) || File.Exists(journal.LiveAuthPath))
                {
                    throw new InvalidOperationException("Unexpected candidate authentication exists during cancellation rollback.");
                }
                return;
            }

            string liveCandidate = IdentifyFile(journal.LiveAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength);
            string quarantinedCandidate = IdentifyFile(journal.CandidateAuthQuarantinePath, journal.SourceAuthSha256, journal.SourceAuthLength);
            if (string.Equals(liveCandidate, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (string.Equals(quarantinedCandidate, ExpectedIdentity, StringComparison.Ordinal))
                {
                    PreserveFile(journal.CandidateAuthQuarantinePath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-candidate-auth.json"));
                }
                return;
            }
            if (string.Equals(liveCandidate, MissingIdentity, StringComparison.Ordinal) &&
                string.Equals(quarantinedCandidate, ExpectedIdentity, StringComparison.Ordinal))
            {
                fileMover.MoveFile(journal.CandidateAuthQuarantinePath, journal.LiveAuthPath);
                return;
            }
            throw new InvalidOperationException("Candidate authentication cannot be restored to the pending live slot.");
        }

        private void EnsureCancelAuthenticationCommitted(TransactionJournal journal)
        {
            if (journal.CandidateAuthWasPresent)
            {
                string liveCandidate = IdentifyFile(journal.LiveAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength);
                string quarantinedCandidate = IdentifyFile(journal.CandidateAuthQuarantinePath, journal.SourceAuthSha256, journal.SourceAuthLength);
                if (string.Equals(liveCandidate, ExpectedIdentity, StringComparison.Ordinal) &&
                    string.Equals(quarantinedCandidate, MissingIdentity, StringComparison.Ordinal))
                {
                    runtimeSecurity.EnsureProtectedDirectory(journal.LifecycleQuarantinePath);
                    fileMover.MoveFile(journal.LiveAuthPath, journal.CandidateAuthQuarantinePath);
                    runtimeSecurity.ProtectFile(journal.CandidateAuthQuarantinePath);
                }
                else if (string.Equals(liveCandidate, ExpectedIdentity, StringComparison.Ordinal) &&
                         string.Equals(quarantinedCandidate, ExpectedIdentity, StringComparison.Ordinal))
                {
                    PreserveFile(journal.LiveAuthPath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-live-candidate-auth.json"));
                }
                else if (!string.Equals(quarantinedCandidate, ExpectedIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Candidate authentication is not preserved in cancellation quarantine.");
                }
            }

            string livePrevious = IdentifyFile(journal.LiveAuthPath, journal.TargetAuthSha256, journal.TargetAuthLength);
            string vaultPrevious = IdentifyFile(journal.TargetAuthPath, journal.TargetAuthSha256, journal.TargetAuthLength);
            if (string.Equals(livePrevious, ExpectedIdentity, StringComparison.Ordinal))
            {
                if (string.Equals(vaultPrevious, ExpectedIdentity, StringComparison.Ordinal))
                {
                    PreserveFile(journal.TargetAuthPath, Path.Combine(journal.LifecycleQuarantinePath, "duplicate-vault-previous-auth.json"));
                }
                return;
            }
            if (string.Equals(livePrevious, MissingIdentity, StringComparison.Ordinal) &&
                string.Equals(vaultPrevious, ExpectedIdentity, StringComparison.Ordinal))
            {
                fileMover.MoveFile(journal.TargetAuthPath, journal.LiveAuthPath);
                return;
            }
            throw new InvalidOperationException("Previous authentication cannot be established in the active live slot.");
        }

        private void ValidateActiveArrangement(string profileId)
        {
            profileId = EnginePaths.NormalizeProfileId(profileId);
            ReadAndValidateProfile(profileId);
            if (!File.Exists(paths.LiveAuthPath))
            {
                throw new InvalidOperationException("Live authentication is missing for the active profile.");
            }
            if (File.Exists(paths.ProfileAuthPath(profileId)))
            {
                throw new InvalidOperationException("The active profile retains a loadable vault authentication file.");
            }
            RequireLink(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(profileId));
        }

        private void ValidateReadyArrangement(string profileId)
        {
            profileId = EnginePaths.NormalizeProfileId(profileId);
            ReadAndValidateProfile(profileId);
            if (!File.Exists(paths.ProfileAuthPath(profileId)))
            {
                throw new InvalidOperationException("Inactive profile authentication is missing.");
            }
            RequireLink(paths.InactiveLinkPath(profileId), paths.ProfileBrowserPath(profileId));
        }

        private void ValidatePendingArrangement(TransactionJournal journal, bool requireAuthAbsent)
        {
            ReadAndValidateProfile(journal.PreviousProfileId);
            ReadAndValidateProfile(journal.CandidateProfileId);
            ValidateReadyArrangement(journal.PreviousProfileId);
            RequireLink(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(journal.CandidateProfileId));
            if (!Directory.Exists(paths.ProfileBrowserPath(journal.CandidateProfileId)))
            {
                throw new DirectoryNotFoundException("Candidate browser slot is missing.");
            }
            if (File.Exists(paths.ProfileAuthPath(journal.CandidateProfileId)))
            {
                throw new InvalidOperationException("Pending candidate has a loadable vault authentication file.");
            }
            if (requireAuthAbsent && File.Exists(paths.LiveAuthPath))
            {
                throw new InvalidOperationException("Live authentication must be absent before candidate sign-in.");
            }
        }

        private ProfileMetadata ReadAndValidateProfile(string profileId)
        {
            profileId = EnginePaths.NormalizeProfileId(profileId);
            ProfileMetadata profile = store.Read<ProfileMetadata>(paths.ProfileMetadataPath(profileId));
            if (profile.SchemaVersion != 1 ||
                !string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                throw new InvalidOperationException("Profile metadata is invalid: " + profileId);
            }
            if (!string.Equals(profile.RegisteredPackageFamily, packageFamily, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Profile was registered for a different package family.");
            }
            return profile;
        }

        private void CreateProfile(TransactionJournal journal, bool createBrowserDirectory)
        {
            if (Directory.Exists(journal.CandidateProfilePath))
            {
                throw new IOException("Candidate profile path already exists: " + journal.CandidateProfilePath);
            }
            runtimeSecurity.EnsureProtectedDirectory(journal.CandidateProfilePath);
            if (createBrowserDirectory) runtimeSecurity.EnsureProtectedDirectory(journal.TargetBrowserPath);
            WriteProtectedJson(paths.ProfileMetadataPath(journal.CandidateProfileId), NewProfile(journal.CandidateProfileId, journal.TargetDisplayName));
        }

        private void EnsureProfileMetadata(TransactionJournal journal, bool requireBrowserDirectory)
        {
            if (!Directory.Exists(journal.CandidateProfilePath)) runtimeSecurity.EnsureProtectedDirectory(journal.CandidateProfilePath);
            if (requireBrowserDirectory && !Directory.Exists(journal.TargetBrowserPath))
            {
                throw new DirectoryNotFoundException("Candidate browser directory is missing: " + journal.TargetBrowserPath);
            }
            string metadataPath = paths.ProfileMetadataPath(journal.CandidateProfileId);
            if (!File.Exists(metadataPath))
            {
                WriteProtectedJson(metadataPath, NewProfile(journal.CandidateProfileId, journal.TargetDisplayName));
            }
            runtimeSecurity.ProtectFile(metadataPath);
            ReadAndValidateProfile(journal.CandidateProfileId);
        }

        private ProfileMetadata NewProfile(string profileId, string displayName)
        {
            return new ProfileMetadata
            {
                SchemaVersion = 1,
                ProfileId = EnginePaths.NormalizeProfileId(profileId),
                DisplayName = NormalizeDisplayName(displayName),
                CreatedAtUtc = UtcNow(),
                RegisteredPackageFamily = packageFamily,
                RegisteredPackageVersion = packageVersion
            };
        }

        private void TouchProfileActivated(string profileId)
        {
            ProfileMetadata profile = ReadAndValidateProfile(profileId);
            profile.LastActivatedAtUtc = UtcNow();
            WriteProtectedJson(paths.ProfileMetadataPath(profileId), profile);
        }

        private TransactionJournal NewLifecycleJournal(
            string operation,
            string transactionId,
            string sourceProfileId,
            string targetProfileId,
            string previousProfileId,
            string candidateProfileId,
            string displayName)
        {
            return new TransactionJournal
            {
                SchemaVersion = 1,
                TransactionId = transactionId,
                Operation = operation,
                SourceProfileId = sourceProfileId,
                TargetProfileId = targetProfileId,
                PreviousProfileId = previousProfileId,
                CandidateProfileId = candidateProfileId,
                TargetDisplayName = displayName,
                PackageFamily = packageFamily,
                PackageVersion = packageVersion,
                LiveAuthPath = paths.LiveAuthPath,
                LiveBrowserLinkPath = paths.LiveBrowserLinkPath,
                LifecycleQuarantinePath = paths.LifecycleQuarantineDirectory(transactionId),
                CreatedAtUtc = UtcNow(),
                UpdatedAtUtc = UtcNow()
            };
        }

        private void WriteActiveLedger(string profileId, string transactionId)
        {
            WriteProtectedJson(paths.LedgerPath, new Ledger
            {
                SchemaVersion = 1,
                ActiveProfileId = EnginePaths.NormalizeProfileId(profileId),
                PendingOnboarding = null,
                LastCommittedTransactionId = transactionId,
                UpdatedAtUtc = UtcNow()
            });
        }

        private void WritePendingLedger(TransactionJournal journal)
        {
            WriteProtectedJson(paths.LedgerPath, new Ledger
            {
                SchemaVersion = 1,
                ActiveProfileId = null,
                PendingOnboarding = new PendingOnboarding
                {
                    TransactionId = journal.TransactionId,
                    PreviousProfileId = journal.PreviousProfileId,
                    CandidateProfileId = journal.CandidateProfileId
                },
                LastCommittedTransactionId = journal.TransactionId,
                UpdatedAtUtc = UtcNow()
            });
        }

        private Ledger ReadLedger()
        {
            Ledger ledger = store.Read<Ledger>(paths.LedgerPath);
            if (ledger.SchemaVersion != 1)
            {
                throw new InvalidOperationException("Unsupported ledger schema version.");
            }
            return ledger;
        }

        private Ledger TryReadLedger()
        {
            return File.Exists(paths.LedgerPath) ? ReadLedger() : null;
        }

        private PendingOnboarding RequirePendingOnboarding(Ledger ledger)
        {
            if (ledger.PendingOnboarding == null || !string.IsNullOrWhiteSpace(ledger.ActiveProfileId))
            {
                throw new InvalidOperationException("No account onboarding operation is pending.");
            }
            EnginePaths.NormalizeProfileId(ledger.PendingOnboarding.PreviousProfileId);
            EnginePaths.NormalizeProfileId(ledger.PendingOnboarding.CandidateProfileId);
            Guid transactionId;
            if (!Guid.TryParse(ledger.PendingOnboarding.TransactionId, out transactionId))
            {
                throw new InvalidOperationException("Pending onboarding transaction ID is invalid.");
            }
            return ledger.PendingOnboarding;
        }

        private void ValidatePendingLedgerIdentity(PendingOnboarding pending)
        {
            string beginJournalPath = paths.JournalPath(pending.TransactionId);
            TransactionJournal beginJournal = store.Read<TransactionJournal>(beginJournalPath);
            TransactionJournalValidator.ValidateLifecycle(paths, beginJournal, packageFamily, pending.TransactionId);
            if (!string.Equals(beginJournal.Operation, BeginOnboardingOperation, StringComparison.Ordinal) ||
                !TransactionPhases.IsTerminal(beginJournal.Phase) ||
                !string.Equals(beginJournal.PreviousProfileId, pending.PreviousProfileId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(beginJournal.CandidateProfileId, pending.CandidateProfileId, StringComparison.OrdinalIgnoreCase))
            {
                throw new RecoveryRequiredException("Pending onboarding does not match a completed begin-onboarding transaction.");
            }
        }

        private bool HasMatchingPendingLedger(Ledger ledger, TransactionJournal journal)
        {
            return ledger != null && ledger.PendingOnboarding != null &&
                   string.IsNullOrWhiteSpace(ledger.ActiveProfileId) &&
                   string.Equals(ledger.PendingOnboarding.TransactionId, journal.TransactionId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ledger.PendingOnboarding.PreviousProfileId, journal.PreviousProfileId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ledger.PendingOnboarding.CandidateProfileId, journal.CandidateProfileId, StringComparison.OrdinalIgnoreCase);
        }

        private void RequireMatchingPendingLedger(Ledger ledger, TransactionJournal journal)
        {
            if (!HasMatchingPendingLedger(ledger, journal))
            {
                throw new RecoveryRequiredException("Ledger no longer matches the committed pending onboarding.");
            }
        }

        private static bool HasPendingProfiles(Ledger ledger, string previousProfileId, string candidateProfileId)
        {
            return ledger != null && ledger.PendingOnboarding != null && string.IsNullOrWhiteSpace(ledger.ActiveProfileId) &&
                   string.Equals(ledger.PendingOnboarding.PreviousProfileId, previousProfileId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ledger.PendingOnboarding.CandidateProfileId, candidateProfileId, StringComparison.OrdinalIgnoreCase);
        }

        private void ParkExpectedLiveJunction(TransactionJournal journal, string expectedTarget)
        {
            if (!junctions.Exists(journal.LiveBrowserLinkPath)) return;
            RequireLink(journal.LiveBrowserLinkPath, expectedTarget);
            string destination = AvailableJunctionPath(journal.CancelledCandidateLinkPath);
            junctions.Move(journal.LiveBrowserLinkPath, destination);
        }

        private string AvailableJunctionPath(string preferredPath)
        {
            if (!junctions.Exists(preferredPath) && !Directory.Exists(preferredPath) && !File.Exists(preferredPath)) return preferredPath;
            return preferredPath + ".preserved-" + Guid.NewGuid().ToString("N");
        }

        private void PreserveDirectoryIfPresent(string sourcePath, string preferredDestination)
        {
            if (!Directory.Exists(sourcePath) || junctions.Exists(sourcePath)) return;
            string destination = preferredDestination;
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                destination += ".preserved-" + Guid.NewGuid().ToString("N");
            }
            runtimeSecurity.EnsureProtectedDirectory(Path.GetDirectoryName(destination));
            directoryMover.MoveDirectory(sourcePath, destination);
            runtimeSecurity.ProtectTree(destination);
        }

        private void PreserveFile(string sourcePath, string preferredDestination)
        {
            if (!File.Exists(sourcePath)) return;
            string destination = preferredDestination;
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                destination += ".preserved-" + Guid.NewGuid().ToString("N");
            }
            runtimeSecurity.EnsureProtectedDirectory(Path.GetDirectoryName(destination));
            fileMover.MoveFile(sourcePath, destination);
            runtimeSecurity.ProtectFile(destination);
        }

        private string IdentifyFile(string path, string expectedHash, long expectedLength)
        {
            if (!File.Exists(path)) return MissingIdentity;
            FileFingerprint fingerprint = fileMover.Fingerprint(path);
            return fingerprint.Length == expectedLength &&
                   string.Equals(fingerprint.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase)
                ? ExpectedIdentity
                : OtherIdentity;
        }

        private string IdentifyDirectory(string path, TransactionJournal journal)
        {
            if (!Directory.Exists(path) || junctions.Exists(path)) return MissingIdentity;
            DirectoryFingerprint fingerprint = directoryMover.Fingerprint(path);
            return FingerprintMatchesJournal(fingerprint, journal) ? ExpectedIdentity : OtherIdentity;
        }

        private void RequireFileIdentity(string path, string hash, long length, string expectedIdentity, string label)
        {
            string identity = IdentifyFile(path, hash, length);
            if (!string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected file state for " + label + ".");
            }
        }

        private void RequireDirectoryIdentity(string path, TransactionJournal journal, string expectedIdentity, string label)
        {
            string identity = IdentifyDirectory(path, journal);
            if (!string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected directory state for " + label + ".");
            }
        }

        private static void ApplyDirectoryFingerprint(TransactionJournal journal, DirectoryFingerprint fingerprint)
        {
            journal.DirectoryTreeSha256 = fingerprint.TreeSha256;
            journal.DirectoryFileCount = fingerprint.FileCount;
            journal.DirectoryCount = fingerprint.DirectoryCount;
            journal.DirectoryTotalLength = fingerprint.TotalLength;
        }

        private static bool FingerprintMatchesJournal(DirectoryFingerprint fingerprint, TransactionJournal journal)
        {
            return fingerprint != null &&
                   fingerprint.FileCount == journal.DirectoryFileCount &&
                   fingerprint.DirectoryCount == journal.DirectoryCount &&
                   fingerprint.TotalLength == journal.DirectoryTotalLength &&
                   string.Equals(fingerprint.TreeSha256, journal.DirectoryTreeSha256, StringComparison.OrdinalIgnoreCase);
        }

        private void RequireLink(string linkPath, string targetPath)
        {
            if (!junctions.Exists(linkPath))
            {
                throw new InvalidOperationException("Expected browser junction is missing: " + linkPath);
            }
            string actual = junctions.ResolveTarget(linkPath);
            if (!WindowsJunctionManager.PathsEqual(actual, targetPath))
            {
                throw new InvalidOperationException("Browser junction targets an unexpected profile directory.");
            }
        }

        private void RequireRealDirectory(string path, string label)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(label + " is missing: " + path);
            if (junctions.Exists(path)) throw new InvalidOperationException(label + " is already a managed junction.");
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(label + " is an unsupported reparse point.");
            }
        }

        private void SavePhase(TransactionJournal journal, string phase)
        {
            journal.Phase = phase;
            journal.UpdatedAtUtc = UtcNow();
            WriteProtectedJson(paths.JournalPath(journal.TransactionId), journal);
            faultInjector.Checkpoint(phase);
        }

        private void SetPhaseWithoutFault(TransactionJournal journal, string phase)
        {
            journal.Phase = phase;
            journal.UpdatedAtUtc = UtcNow();
            WriteProtectedJson(paths.JournalPath(journal.TransactionId), journal);
        }

        private TransactionJournal FindPendingJournal()
        {
            if (!Directory.Exists(paths.TransactionsDirectory)) return null;
            List<TransactionJournal> pending = new List<TransactionJournal>();
            foreach (string directory in Directory.GetDirectories(paths.TransactionsDirectory))
            {
                string journalPath = Path.Combine(directory, "journal.json");
                if (!File.Exists(journalPath)) continue;
                TransactionJournal journal = store.Read<TransactionJournal>(journalPath);
                TransactionJournalValidator.ValidateAny(paths, journal, packageFamily, Path.GetFileName(directory));
                if (!TransactionPhases.IsTerminal(journal.Phase)) pending.Add(journal);
            }
            if (pending.Count > 1)
            {
                throw new RecoveryRequiredException("More than one non-terminal transaction journal exists.");
            }
            return pending.Count == 0 ? null : pending[0];
        }

        private void RequireNoPendingJournal()
        {
            TransactionJournal pending = FindPendingJournal();
            if (pending != null)
            {
                throw new RecoveryRequiredException("Transaction " + pending.TransactionId + " must be recovered before another lifecycle action.");
            }
        }

        private void RequireLifecycleOperation(TransactionJournal journal)
        {
            if (journal == null ||
                (!string.Equals(journal.Operation, RegisterFirstOperation, StringComparison.Ordinal) &&
                 !string.Equals(journal.Operation, BeginOnboardingOperation, StringComparison.Ordinal) &&
                 !string.Equals(journal.Operation, FinalizeOnboardingOperation, StringComparison.Ordinal) &&
                 !string.Equals(journal.Operation, CancelOnboardingOperation, StringComparison.Ordinal)))
            {
                throw new RecoveryRequiredException("The pending transaction is not a profile lifecycle operation.");
            }
        }

        private void EnsureRuntimeDirectories()
        {
            runtimeSecurity.EnsureProtectedDirectory(paths.RuntimeRoot);
            runtimeSecurity.EnsureProtectedDirectory(paths.StateDirectory);
            runtimeSecurity.EnsureProtectedDirectory(paths.ProfilesDirectory);
            runtimeSecurity.EnsureProtectedDirectory(paths.TransactionsDirectory);
            Directory.CreateDirectory(paths.InactiveLinksDirectory);
        }

        private void WriteProtectedJson<T>(string path, T value)
        {
            runtimeSecurity.EnsureProtectedDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            store.Write(path, value);
            runtimeSecurity.ProtectFile(path);
        }

        private static string CandidateProfileQuarantinePath(TransactionJournal journal)
        {
            return Path.Combine(journal.LifecycleQuarantinePath, "candidate-profile");
        }

        private static string NormalizeDisplayName(string displayName)
        {
            if (displayName == null) throw new ArgumentNullException("displayName");
            displayName = displayName.Trim();
            if (displayName.Length == 0) throw new ArgumentException("Profile display name is required.", "displayName");
            if (displayName.Any(char.IsControl)) throw new ArgumentException("Profile display name contains control characters.", "displayName");
            return displayName;
        }

        private static LifecycleResult ResultFor(TransactionJournal journal, string activeProfileId)
        {
            return new LifecycleResult
            {
                Operation = journal.Operation,
                TransactionId = journal.TransactionId,
                ActiveProfileId = activeProfileId,
                PreviousProfileId = journal.PreviousProfileId,
                CandidateProfileId = journal.CandidateProfileId
            };
        }

        private static LifecycleRecoveryResult Recovery(
            TransactionJournal journal,
            string result,
            string activeProfileId,
            string candidateProfileId)
        {
            return new LifecycleRecoveryResult
            {
                TransactionId = journal.TransactionId,
                Operation = journal.Operation,
                Result = result,
                ActiveProfileId = activeProfileId,
                CandidateProfileId = candidateProfileId
            };
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o");
        }
    }
}
