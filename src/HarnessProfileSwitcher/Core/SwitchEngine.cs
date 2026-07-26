using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HarnessProfileSwitcher.Core
{
    public sealed class RecoveryRequiredException : InvalidOperationException
    {
        public RecoveryRequiredException(string message)
            : base(message)
        {
        }

        public RecoveryRequiredException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class SwitchEngine
    {
        private readonly EnginePaths paths;
        private readonly AtomicJsonStore store;
        private readonly IVerifiedFileMover fileMover;
        private readonly IJunctionManager junctions;
        private readonly IProcessGuard processGuard;
        private readonly IFaultInjector faultInjector;
        private readonly IRuntimeSecurity runtimeSecurity;
        private readonly string packageFamily;
        private readonly string packageVersion;

        public SwitchEngine(
            EnginePaths paths,
            AtomicJsonStore store,
            IVerifiedFileMover fileMover,
            IJunctionManager junctions,
            IProcessGuard processGuard,
            IFaultInjector faultInjector,
            string packageFamily,
            string packageVersion)
            : this(
                paths,
                store,
                fileMover,
                junctions,
                processGuard,
                faultInjector,
                packageFamily,
                packageVersion,
                new NoOpRuntimeSecurity())
        {
        }

        public SwitchEngine(
            EnginePaths paths,
            AtomicJsonStore store,
            IVerifiedFileMover fileMover,
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
            if (junctions == null) throw new ArgumentNullException("junctions");
            if (processGuard == null) throw new ArgumentNullException("processGuard");
            if (faultInjector == null) throw new ArgumentNullException("faultInjector");
            if (runtimeSecurity == null) throw new ArgumentNullException("runtimeSecurity");
            if (string.IsNullOrWhiteSpace(packageFamily)) throw new ArgumentException("Package family is required.", "packageFamily");
            if (string.IsNullOrWhiteSpace(packageVersion)) throw new ArgumentException("Package version is required.", "packageVersion");

            this.paths = paths;
            this.store = store;
            this.fileMover = fileMover;
            this.junctions = junctions;
            this.processGuard = processGuard;
            this.faultInjector = faultInjector;
            this.runtimeSecurity = runtimeSecurity;
            this.packageFamily = packageFamily;
            this.packageVersion = packageVersion;
        }

        public SwitchResult SwitchTo(string targetProfileId)
        {
            targetProfileId = EnginePaths.NormalizeProfileId(targetProfileId);
            EnsureRuntimeDirectories();

            using (OperationLease.Acquire(paths))
            {
                TransactionJournal pending = FindPendingJournal();
                if (pending != null)
                {
                    throw new RecoveryRequiredException("Transaction " + pending.TransactionId + " must be recovered before another switch.");
                }

                Ledger ledger = ReadLedger();
                if (ledger.PendingOnboarding != null)
                {
                    throw new InvalidOperationException("Account onboarding must be finalized or cancelled before switching.");
                }
                string sourceProfileId = EnginePaths.NormalizeProfileId(ledger.ActiveProfileId);
                if (string.Equals(sourceProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The requested profile is already active.");
                }

                ProfileMetadata sourceProfile = ReadAndValidateProfile(sourceProfileId);
                ProfileMetadata targetProfile = ReadAndValidateProfile(targetProfileId);
                ValidatePackageFamily(sourceProfile);
                ValidatePackageFamily(targetProfile);
                ValidateActiveProfile(sourceProfileId);
                ValidateReadyProfile(targetProfileId);

                FileFingerprint sourceAuth = fileMover.Fingerprint(paths.LiveAuthPath);
                FileFingerprint targetAuth = fileMover.Fingerprint(paths.ProfileAuthPath(targetProfileId));
                if (string.Equals(sourceAuth.Sha256, targetAuth.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Source and target authentication files are identical; refusing an ambiguous switch.");
                }

                string transactionId = Guid.NewGuid().ToString("D");
                TransactionJournal journal = CreateSwitchJournal(
                    transactionId,
                    sourceProfileId,
                    targetProfileId,
                    sourceAuth,
                    targetAuth);
                SavePhase(journal, TransactionPhases.Prepared);

                processGuard.EnsureStopped();
                SavePhase(journal, TransactionPhases.ProcessesClosed);

                SavePhase(journal, TransactionPhases.OutgoingAuthMoveIntent);
                processGuard.EnsureStopped();
                fileMover.MoveFile(journal.LiveAuthPath, journal.SourceAuthPath);
                runtimeSecurity.ProtectFile(journal.SourceAuthPath);
                SavePhase(journal, TransactionPhases.OutgoingAuthMoved);

                SavePhase(journal, TransactionPhases.IncomingAuthMoveIntent);
                processGuard.EnsureStopped();
                fileMover.MoveFile(journal.TargetAuthPath, journal.StagingAuthPath);
                SavePhase(journal, TransactionPhases.IncomingAuthStaged);

                SavePhase(journal, TransactionPhases.IncomingAuthActivateIntent);
                processGuard.EnsureStopped();
                fileMover.MoveFile(journal.StagingAuthPath, journal.LiveAuthPath);
                SavePhase(journal, TransactionPhases.IncomingAuthActivated);

                SavePhase(journal, TransactionPhases.OutgoingBrowserLinkParkIntent);
                processGuard.EnsureStopped();
                junctions.Move(journal.LiveBrowserLinkPath, journal.SourceInactiveLinkPath);
                SavePhase(journal, TransactionPhases.OutgoingBrowserLinkParked);

                SavePhase(journal, TransactionPhases.IncomingBrowserLinkActivateIntent);
                processGuard.EnsureStopped();
                junctions.Move(journal.TargetInactiveLinkPath, journal.LiveBrowserLinkPath);
                SavePhase(journal, TransactionPhases.IncomingBrowserLinkActivated);

                ValidateTargetArrangement(journal, true);
                SavePhase(journal, TransactionPhases.LedgerCommitIntent);
                WriteActiveLedger(targetProfileId, transactionId);
                SavePhase(journal, TransactionPhases.LedgerCommitted);

                return new SwitchResult
                {
                    TransactionId = transactionId,
                    SourceProfileId = sourceProfileId,
                    TargetProfileId = targetProfileId
                };
            }
        }

        public void MarkLaunchObserved(string transactionId)
        {
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                TransactionJournal journal = store.Read<TransactionJournal>(paths.JournalPath(transactionId));
                TransactionJournalValidator.ValidateSwitch(paths, journal, packageFamily, transactionId);
                if (!string.Equals(journal.Operation, "switch", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Transaction is not a normal profile switch.");
                }
                if (string.Equals(journal.Phase, TransactionPhases.Completed, StringComparison.Ordinal))
                {
                    return;
                }
                if (!string.Equals(journal.Phase, TransactionPhases.LedgerCommitted, StringComparison.Ordinal) &&
                    !string.Equals(journal.Phase, TransactionPhases.LaunchObserved, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Transaction has not reached the ledger commit point: " + journal.Phase);
                }

                Ledger ledger = ReadLedger();
                if (!string.Equals(ledger.ActiveProfileId, journal.TargetProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RecoveryRequiredException("Ledger no longer names the committed target profile.");
                }
                if (!File.Exists(journal.LiveAuthPath))
                {
                    throw new RecoveryRequiredException("Live authentication is missing after launch.");
                }
                RequireLinkTarget(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);

                SavePhase(journal, TransactionPhases.LaunchObserved);
                SavePhase(journal, TransactionPhases.Completed);
            }
        }

        public RecoveryResult RecoverPending()
        {
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                TransactionJournal journal = FindPendingJournal();
                if (journal == null)
                {
                    return new RecoveryResult
                    {
                        Result = "NoPendingTransaction"
                    };
                }

                if (!string.Equals(journal.Operation, "switch", StringComparison.Ordinal))
                {
                    throw new RecoveryRequiredException("The pending transaction is a profile lifecycle operation and must use lifecycle recovery.");
                }

                processGuard.EnsureStopped();
                try
                {
                    Ledger ledger = ReadLedger();
                    if (string.Equals(ledger.ActiveProfileId, journal.TargetProfileId, StringComparison.OrdinalIgnoreCase) &&
                        ledger.PendingOnboarding == null)
                    {
                        CompleteTarget(journal);
                        return new RecoveryResult
                        {
                            TransactionId = journal.TransactionId,
                            Result = "RolledForward",
                            ActiveProfileId = journal.TargetProfileId
                        };
                    }

                    RestoreSource(journal);
                    return new RecoveryResult
                    {
                        TransactionId = journal.TransactionId,
                        Result = "RolledBack",
                        ActiveProfileId = journal.SourceProfileId
                    };
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
                    throw new RecoveryRequiredException("Automatic recovery could not establish a safe account state.", exception);
                }
            }
        }

        private void RestoreSource(TransactionJournal journal)
        {
            journal.Phase = TransactionPhases.RollbackStarted;
            journal.UpdatedAtUtc = UtcNow();
            WriteProtectedJson(paths.JournalPath(journal.TransactionId), journal);

            RestoreTargetAuthenticationToVault(journal);
            RestoreSourceAuthenticationToLive(journal);
            RestoreSourceBrowserLink(journal);
            ValidateSourceArrangement(journal);
            WriteActiveLedger(journal.SourceProfileId, null);

            journal.Phase = TransactionPhases.RolledBack;
            journal.UpdatedAtUtc = UtcNow();
            WriteProtectedJson(paths.JournalPath(journal.TransactionId), journal);
        }

        private void CompleteTarget(TransactionJournal journal)
        {
            EnsureSourceAuthenticationInVault(journal);
            EnsureTargetAuthenticationLive(journal);
            EnsureTargetBrowserLinkLive(journal);
            ValidateTargetArrangement(journal, false);
            WriteActiveLedger(journal.TargetProfileId, journal.TransactionId);

            journal.Phase = TransactionPhases.LedgerCommitted;
            journal.UpdatedAtUtc = UtcNow();
            WriteProtectedJson(paths.JournalPath(journal.TransactionId), journal);
        }

        private void RestoreTargetAuthenticationToVault(TransactionJournal journal)
        {
            string targetVaultIdentity = IdentifyAuth(journal.TargetAuthPath, journal);
            string stagingIdentity = IdentifyAuth(journal.StagingAuthPath, journal);
            string liveIdentity = IdentifyAuth(journal.LiveAuthPath, journal);

            if (targetVaultIdentity != "missing" && targetVaultIdentity != "target")
            {
                throw new RecoveryRequiredException("Target vault authentication has unexpected integrity.");
            }

            if (liveIdentity == "target")
            {
                if (targetVaultIdentity == "missing")
                {
                    fileMover.MoveFile(journal.LiveAuthPath, journal.TargetAuthPath);
                    runtimeSecurity.ProtectFile(journal.TargetAuthPath);
                    targetVaultIdentity = "target";
                }
                else
                {
                    QuarantineAuth(journal.LiveAuthPath, journal, "duplicate-live-target");
                }
            }
            else if (liveIdentity != "missing" && liveIdentity != "source")
            {
                throw new RecoveryRequiredException("Live authentication has unexpected integrity during rollback.");
            }

            if (stagingIdentity == "target")
            {
                if (targetVaultIdentity == "missing")
                {
                    fileMover.MoveFile(journal.StagingAuthPath, journal.TargetAuthPath);
                    runtimeSecurity.ProtectFile(journal.TargetAuthPath);
                }
                else
                {
                    QuarantineAuth(journal.StagingAuthPath, journal, "duplicate-staged-target");
                }
            }
            else if (stagingIdentity != "missing")
            {
                throw new RecoveryRequiredException("Staged authentication has unexpected integrity during rollback.");
            }
        }

        private void RestoreSourceAuthenticationToLive(TransactionJournal journal)
        {
            string liveIdentity = IdentifyAuth(journal.LiveAuthPath, journal);
            string sourceVaultIdentity = IdentifyAuth(journal.SourceAuthPath, journal);
            if (sourceVaultIdentity != "missing" && sourceVaultIdentity != "source")
            {
                throw new RecoveryRequiredException("Source vault authentication has unexpected integrity.");
            }

            if (liveIdentity == "missing")
            {
                if (sourceVaultIdentity != "source")
                {
                    throw new RecoveryRequiredException("Source authentication is unavailable for rollback.");
                }
                fileMover.MoveFile(journal.SourceAuthPath, journal.LiveAuthPath);
            }
            else if (liveIdentity == "source")
            {
                if (sourceVaultIdentity == "source")
                {
                    QuarantineAuth(journal.SourceAuthPath, journal, "duplicate-source-vault");
                }
            }
            else
            {
                throw new RecoveryRequiredException("A non-source authentication file remains live during rollback.");
            }
        }

        private void RestoreSourceBrowserLink(TransactionJournal journal)
        {
            string liveIdentity = IdentifyLink(journal.LiveBrowserLinkPath, journal);
            string sourceInactiveIdentity = IdentifyLink(journal.SourceInactiveLinkPath, journal);
            string targetInactiveIdentity = IdentifyLink(journal.TargetInactiveLinkPath, journal);

            if (liveIdentity == "target")
            {
                if (targetInactiveIdentity != "missing")
                {
                    throw new RecoveryRequiredException("Both live and inactive target browser junctions exist.");
                }
                junctions.Move(journal.LiveBrowserLinkPath, journal.TargetInactiveLinkPath);
                liveIdentity = "missing";
            }
            else if (liveIdentity != "missing" && liveIdentity != "source")
            {
                throw new RecoveryRequiredException("Live browser junction has an unexpected target during rollback.");
            }

            if (liveIdentity == "missing")
            {
                if (sourceInactiveIdentity != "source")
                {
                    throw new RecoveryRequiredException("Source browser junction is unavailable for rollback.");
                }
                junctions.Move(journal.SourceInactiveLinkPath, journal.LiveBrowserLinkPath);
            }
            else if (sourceInactiveIdentity != "missing")
            {
                throw new RecoveryRequiredException("Both live and inactive source browser junctions exist.");
            }
        }

        private void EnsureSourceAuthenticationInVault(TransactionJournal journal)
        {
            string sourceVaultIdentity = IdentifyAuth(journal.SourceAuthPath, journal);
            string liveIdentity = IdentifyAuth(journal.LiveAuthPath, journal);
            if (sourceVaultIdentity == "source")
            {
                return;
            }
            if (sourceVaultIdentity != "missing")
            {
                throw new RecoveryRequiredException("Source vault authentication has unexpected integrity.");
            }
            if (liveIdentity != "source")
            {
                throw new RecoveryRequiredException("Source authentication is unavailable while completing target state.");
            }
            fileMover.MoveFile(journal.LiveAuthPath, journal.SourceAuthPath);
            runtimeSecurity.ProtectFile(journal.SourceAuthPath);
        }

        private void EnsureTargetAuthenticationLive(TransactionJournal journal)
        {
            if (File.Exists(journal.LiveAuthPath))
            {
                string liveIdentity = IdentifyAuth(journal.LiveAuthPath, journal);
                if (liveIdentity == "source")
                {
                    throw new RecoveryRequiredException("Source authentication remains live after target commit.");
                }

                if (File.Exists(journal.TargetAuthPath))
                {
                    QuarantineAuth(journal.TargetAuthPath, journal, "consumed-target-vault");
                }
                if (File.Exists(journal.StagingAuthPath))
                {
                    QuarantineAuth(journal.StagingAuthPath, journal, "consumed-target-staging");
                }
                return;
            }

            string stagingIdentity = IdentifyAuth(journal.StagingAuthPath, journal);
            if (stagingIdentity == "target")
            {
                fileMover.MoveFile(journal.StagingAuthPath, journal.LiveAuthPath);
                return;
            }

            string targetVaultIdentity = IdentifyAuth(journal.TargetAuthPath, journal);
            if (targetVaultIdentity == "target")
            {
                fileMover.MoveFile(journal.TargetAuthPath, journal.StagingAuthPath);
                fileMover.MoveFile(journal.StagingAuthPath, journal.LiveAuthPath);
                return;
            }

            throw new RecoveryRequiredException("Target authentication is unavailable while completing target state.");
        }

        private void EnsureTargetBrowserLinkLive(TransactionJournal journal)
        {
            string liveIdentity = IdentifyLink(journal.LiveBrowserLinkPath, journal);
            string sourceInactiveIdentity = IdentifyLink(journal.SourceInactiveLinkPath, journal);
            string targetInactiveIdentity = IdentifyLink(journal.TargetInactiveLinkPath, journal);

            if (liveIdentity == "source")
            {
                if (sourceInactiveIdentity != "missing")
                {
                    throw new RecoveryRequiredException("Both live and inactive source browser junctions exist.");
                }
                junctions.Move(journal.LiveBrowserLinkPath, journal.SourceInactiveLinkPath);
                liveIdentity = "missing";
            }
            else if (liveIdentity != "missing" && liveIdentity != "target")
            {
                throw new RecoveryRequiredException("Live browser junction has an unexpected target while completing target state.");
            }

            if (liveIdentity == "missing")
            {
                if (targetInactiveIdentity != "target")
                {
                    throw new RecoveryRequiredException("Target browser junction is unavailable while completing target state.");
                }
                junctions.Move(journal.TargetInactiveLinkPath, journal.LiveBrowserLinkPath);
            }
            else if (targetInactiveIdentity != "missing")
            {
                throw new RecoveryRequiredException("Both live and inactive target browser junctions exist.");
            }
        }

        private void ValidateActiveProfile(string profileId)
        {
            if (!File.Exists(paths.LiveAuthPath))
            {
                throw new FileNotFoundException("Live authentication is missing.", paths.LiveAuthPath);
            }
            if (File.Exists(paths.ProfileAuthPath(profileId)))
            {
                throw new InvalidOperationException("Active profile also has a loadable vault authentication file.");
            }
            if (!Directory.Exists(paths.ProfileBrowserPath(profileId)))
            {
                throw new DirectoryNotFoundException("Active profile browser directory is missing: " + paths.ProfileBrowserPath(profileId));
            }
            RequireLinkTarget(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(profileId));
            if (junctions.Exists(paths.InactiveLinkPath(profileId)))
            {
                throw new InvalidOperationException("Active profile also has an inactive browser junction.");
            }
        }

        private void ValidateReadyProfile(string profileId)
        {
            if (!File.Exists(paths.ProfileAuthPath(profileId)))
            {
                throw new FileNotFoundException("Inactive profile authentication is missing.", paths.ProfileAuthPath(profileId));
            }
            if (!Directory.Exists(paths.ProfileBrowserPath(profileId)))
            {
                throw new DirectoryNotFoundException("Inactive profile browser directory is missing: " + paths.ProfileBrowserPath(profileId));
            }
            RequireLinkTarget(paths.InactiveLinkPath(profileId), paths.ProfileBrowserPath(profileId));
        }

        private void ValidateSourceArrangement(TransactionJournal journal)
        {
            RequireAuthIdentity(journal.LiveAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength, "source live authentication");
            if (File.Exists(journal.SourceAuthPath))
            {
                throw new RecoveryRequiredException("Source vault authentication still exists after rollback.");
            }
            RequireAuthIdentity(journal.TargetAuthPath, journal.TargetAuthSha256, journal.TargetAuthLength, "target vault authentication");
            RequireLinkTarget(journal.LiveBrowserLinkPath, journal.SourceBrowserPath);
            RequireLinkTarget(journal.TargetInactiveLinkPath, journal.TargetBrowserPath);
            if (junctions.Exists(journal.SourceInactiveLinkPath))
            {
                throw new RecoveryRequiredException("Source inactive junction still exists after rollback.");
            }
        }

        private void ValidateTargetArrangement(TransactionJournal journal, bool requireOriginalTargetHash)
        {
            if (!File.Exists(journal.LiveAuthPath))
            {
                throw new RecoveryRequiredException("Target live authentication is missing.");
            }
            if (requireOriginalTargetHash)
            {
                RequireAuthIdentity(journal.LiveAuthPath, journal.TargetAuthSha256, journal.TargetAuthLength, "target live authentication");
            }
            RequireAuthIdentity(journal.SourceAuthPath, journal.SourceAuthSha256, journal.SourceAuthLength, "source vault authentication");
            if (File.Exists(journal.TargetAuthPath) || File.Exists(journal.StagingAuthPath))
            {
                throw new RecoveryRequiredException("Target retains an extra loadable authentication copy.");
            }
            RequireLinkTarget(journal.LiveBrowserLinkPath, journal.TargetBrowserPath);
            RequireLinkTarget(journal.SourceInactiveLinkPath, journal.SourceBrowserPath);
            if (junctions.Exists(journal.TargetInactiveLinkPath))
            {
                throw new RecoveryRequiredException("Target inactive junction still exists after activation.");
            }
        }

        private ProfileMetadata ReadAndValidateProfile(string profileId)
        {
            ProfileMetadata metadata = store.Read<ProfileMetadata>(paths.ProfileMetadataPath(profileId));
            if (metadata.SchemaVersion != 1)
            {
                throw new InvalidOperationException("Unsupported profile schema version: " + metadata.SchemaVersion);
            }
            if (!string.Equals(EnginePaths.NormalizeProfileId(metadata.ProfileId), profileId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Profile metadata ID does not match its directory.");
            }
            return metadata;
        }

        private void ValidatePackageFamily(ProfileMetadata profile)
        {
            if (!string.Equals(profile.RegisteredPackageFamily, packageFamily, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Profile was registered for a different package family.");
            }
        }

        private Ledger ReadLedger()
        {
            Ledger ledger = store.Read<Ledger>(paths.LedgerPath);
            if (ledger.SchemaVersion != 1)
            {
                throw new InvalidOperationException("Unsupported ledger schema version: " + ledger.SchemaVersion);
            }
            if (string.IsNullOrWhiteSpace(ledger.ActiveProfileId) && ledger.PendingOnboarding == null)
            {
                throw new InvalidOperationException("Ledger has neither an active profile nor pending onboarding.");
            }
            return ledger;
        }

        private void WriteActiveLedger(string profileId, string transactionId)
        {
            Ledger ledger = new Ledger
            {
                SchemaVersion = 1,
                ActiveProfileId = EnginePaths.NormalizeProfileId(profileId),
                PendingOnboarding = null,
                LastCommittedTransactionId = transactionId,
                UpdatedAtUtc = UtcNow()
            };
            WriteProtectedJson(paths.LedgerPath, ledger);
        }

        private TransactionJournal CreateSwitchJournal(
            string transactionId,
            string sourceProfileId,
            string targetProfileId,
            FileFingerprint sourceAuth,
            FileFingerprint targetAuth)
        {
            string now = UtcNow();
            return new TransactionJournal
            {
                SchemaVersion = 1,
                TransactionId = transactionId,
                Operation = "switch",
                SourceProfileId = sourceProfileId,
                TargetProfileId = targetProfileId,
                PackageFamily = packageFamily,
                PackageVersion = packageVersion,
                LiveAuthPath = paths.LiveAuthPath,
                SourceAuthPath = paths.ProfileAuthPath(sourceProfileId),
                TargetAuthPath = paths.ProfileAuthPath(targetProfileId),
                StagingAuthPath = paths.StagingAuthPath(transactionId),
                LiveBrowserLinkPath = paths.LiveBrowserLinkPath,
                SourceInactiveLinkPath = paths.InactiveLinkPath(sourceProfileId),
                TargetInactiveLinkPath = paths.InactiveLinkPath(targetProfileId),
                SourceBrowserPath = paths.ProfileBrowserPath(sourceProfileId),
                TargetBrowserPath = paths.ProfileBrowserPath(targetProfileId),
                SourceAuthSha256 = sourceAuth.Sha256,
                TargetAuthSha256 = targetAuth.Sha256,
                SourceAuthLength = sourceAuth.Length,
                TargetAuthLength = targetAuth.Length,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        private void SavePhase(TransactionJournal journal, string phase)
        {
            journal.Phase = phase;
            journal.UpdatedAtUtc = UtcNow();
            WriteProtectedJson(paths.JournalPath(journal.TransactionId), journal);
            faultInjector.Checkpoint(phase);
        }

        private TransactionJournal FindPendingJournal()
        {
            if (!Directory.Exists(paths.TransactionsDirectory))
            {
                return null;
            }

            List<TransactionJournal> pending = new List<TransactionJournal>();
            foreach (string directory in Directory.GetDirectories(paths.TransactionsDirectory))
            {
                string journalPath = Path.Combine(directory, "journal.json");
                if (!File.Exists(journalPath))
                {
                    throw new RecoveryRequiredException("Transaction directory has no journal: " + directory);
                }
                TransactionJournal journal = store.Read<TransactionJournal>(journalPath);
                TransactionJournalValidator.ValidateAny(paths, journal, packageFamily, Path.GetFileName(directory));
                if (!TransactionPhases.IsTerminal(journal.Phase))
                {
                    pending.Add(journal);
                }
            }

            if (pending.Count > 1)
            {
                throw new RecoveryRequiredException("Multiple unfinished transactions were found.");
            }
            return pending.Count == 1 ? pending[0] : null;
        }

        private string IdentifyAuth(string path, TransactionJournal journal)
        {
            if (!File.Exists(path))
            {
                return "missing";
            }
            FileFingerprint fingerprint = fileMover.Fingerprint(path);
            if (fingerprint.Length == journal.SourceAuthLength &&
                string.Equals(fingerprint.Sha256, journal.SourceAuthSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "source";
            }
            if (fingerprint.Length == journal.TargetAuthLength &&
                string.Equals(fingerprint.Sha256, journal.TargetAuthSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "target";
            }
            return "other";
        }

        private string IdentifyLink(string path, TransactionJournal journal)
        {
            if (!junctions.Exists(path))
            {
                return "missing";
            }
            string target = junctions.ResolveTarget(path);
            if (WindowsJunctionManager.PathsEqual(target, journal.SourceBrowserPath))
            {
                return "source";
            }
            if (WindowsJunctionManager.PathsEqual(target, journal.TargetBrowserPath))
            {
                return "target";
            }
            return "other";
        }

        private void RequireAuthIdentity(string path, string expectedHash, long expectedLength, string label)
        {
            if (!File.Exists(path))
            {
                throw new RecoveryRequiredException("Missing " + label + ": " + path);
            }
            FileFingerprint fingerprint = fileMover.Fingerprint(path);
            if (fingerprint.Length != expectedLength || !string.Equals(fingerprint.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new RecoveryRequiredException("Integrity mismatch for " + label + ".");
            }
        }

        private void RequireLinkTarget(string linkPath, string expectedTarget)
        {
            if (!junctions.Exists(linkPath))
            {
                throw new RecoveryRequiredException("Required browser junction is missing: " + linkPath);
            }
            string actualTarget = junctions.ResolveTarget(linkPath);
            if (!WindowsJunctionManager.PathsEqual(actualTarget, expectedTarget))
            {
                throw new RecoveryRequiredException("Browser junction points to an unexpected target: " + linkPath);
            }
        }

        private void QuarantineAuth(string sourcePath, TransactionJournal journal, string label)
        {
            string destination = paths.QuarantinePath(journal.TransactionId, label);
            runtimeSecurity.EnsureProtectedDirectory(Path.GetDirectoryName(destination));
            fileMover.MoveFile(sourcePath, destination);
            runtimeSecurity.ProtectFile(destination);
        }

        private void EnsureRuntimeDirectories()
        {
            runtimeSecurity.EnsureProtectedDirectory(paths.RuntimeRoot);
            runtimeSecurity.EnsureProtectedDirectory(paths.StateDirectory);
            runtimeSecurity.EnsureProtectedDirectory(paths.ProfilesDirectory);
            runtimeSecurity.EnsureProtectedDirectory(paths.TransactionsDirectory);
            Directory.CreateDirectory(paths.InactiveLinksDirectory);
            Directory.CreateDirectory(paths.AuthStagingRoot);
        }

        private void WriteProtectedJson<T>(string path, T value)
        {
            runtimeSecurity.EnsureProtectedDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            store.Write(path, value);
            runtimeSecurity.ProtectFile(path);
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o");
        }
    }
}
