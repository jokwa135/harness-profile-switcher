using System;
using System.Collections.Generic;
using System.IO;

namespace HarnessProfileSwitcher.Core
{
    public sealed class ProfileDeletionEngine
    {
        public const string DeleteProfileOperation = "delete-profile";

        private readonly EnginePaths paths;
        private readonly AtomicJsonStore store;
        private readonly IVerifiedDirectoryMover directoryMover;
        private readonly IJunctionManager junctions;
        private readonly IFaultInjector faultInjector;
        private readonly IRuntimeSecurity runtimeSecurity;
        private readonly string packageFamily;
        private readonly string packageVersion;

        public ProfileDeletionEngine(
            EnginePaths paths,
            AtomicJsonStore store,
            IVerifiedDirectoryMover directoryMover,
            IJunctionManager junctions,
            IFaultInjector faultInjector,
            string packageFamily,
            string packageVersion)
            : this(
                paths,
                store,
                directoryMover,
                junctions,
                faultInjector,
                packageFamily,
                packageVersion,
                new NoOpRuntimeSecurity())
        {
        }

        public ProfileDeletionEngine(
            EnginePaths paths,
            AtomicJsonStore store,
            IVerifiedDirectoryMover directoryMover,
            IJunctionManager junctions,
            IFaultInjector faultInjector,
            string packageFamily,
            string packageVersion,
            IRuntimeSecurity runtimeSecurity)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (store == null) throw new ArgumentNullException("store");
            if (directoryMover == null) throw new ArgumentNullException("directoryMover");
            if (junctions == null) throw new ArgumentNullException("junctions");
            if (faultInjector == null) throw new ArgumentNullException("faultInjector");
            if (runtimeSecurity == null) throw new ArgumentNullException("runtimeSecurity");
            if (string.IsNullOrWhiteSpace(packageFamily)) throw new ArgumentException("Package family is required.", "packageFamily");
            if (string.IsNullOrWhiteSpace(packageVersion)) throw new ArgumentException("Package version is required.", "packageVersion");

            this.paths = paths;
            this.store = store;
            this.directoryMover = directoryMover;
            this.junctions = junctions;
            this.faultInjector = faultInjector;
            this.runtimeSecurity = runtimeSecurity;
            this.packageFamily = packageFamily;
            this.packageVersion = packageVersion;
        }

        public ProfileDeletionResult DeleteProfile(string targetProfileId)
        {
            targetProfileId = EnginePaths.NormalizeProfileId(targetProfileId);
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                RequireNoPendingJournal();
                Ledger ledger = ReadActiveLedger();
                string activeProfileId = EnginePaths.NormalizeProfileId(ledger.ActiveProfileId);
                if (string.Equals(activeProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The active profile cannot be deleted. Switch to another profile first.");
                }

                ReadAndValidateProfile(activeProfileId);
                ProfileMetadata targetProfile = ReadAndValidateProfile(targetProfileId);
                ValidateActiveArrangement(activeProfileId);
                ValidateReadyArrangement(targetProfileId);

                DirectoryFingerprint targetFingerprint = directoryMover.Fingerprint(paths.ProfileDirectory(targetProfileId));
                string transactionId = Guid.NewGuid().ToString("D");
                TransactionJournal journal = NewJournal(
                    transactionId,
                    activeProfileId,
                    targetProfileId,
                    targetProfile.DisplayName,
                    targetFingerprint);
                SavePhase(journal, TransactionPhases.Prepared);

                SavePhase(journal, TransactionPhases.ProfileDeleteLinkIntent);
                DeleteExpectedInactiveLink(journal);
                SavePhase(journal, TransactionPhases.ProfileDeleteLinkDeleted);

                SavePhase(journal, TransactionPhases.ProfileDeleteStageIntent);
                directoryMover.MoveDirectory(journal.CandidateProfilePath, journal.DeletionStagingPath);
                runtimeSecurity.ProtectTree(journal.DeletionStagingPath);
                RequireFullDirectoryIdentity(journal.DeletionStagingPath, journal, "staged profile");
                SavePhase(journal, TransactionPhases.ProfileDeleteStaged);

                SavePhase(journal, TransactionPhases.ProfileDeleteContentIntent);
                directoryMover.DeleteDirectory(journal.DeletionStagingPath);
                RequirePathAbsent(journal.DeletionStagingPath, "profile deletion staging path");
                SavePhase(journal, TransactionPhases.ProfileDeleteContentDeleted);

                ValidateActiveArrangement(activeProfileId);
                RequireDeletedArrangement(journal);
                SavePhase(journal, TransactionPhases.Completed);
                return new ProfileDeletionResult
                {
                    TransactionId = transactionId,
                    ActiveProfileId = activeProfileId,
                    DeletedProfileId = targetProfileId
                };
            }
        }

        public ProfileDeletionRecoveryResult RecoverPending()
        {
            EnsureRuntimeDirectories();
            using (OperationLease.Acquire(paths))
            {
                TransactionJournal journal = FindPendingJournal();
                if (journal == null)
                {
                    return new ProfileDeletionRecoveryResult { Result = "NoPendingTransaction" };
                }
                RequireDeletionOperation(journal);
                try
                {
                    Ledger ledger = ReadActiveLedger();
                    if (!string.Equals(
                        EnginePaths.NormalizeProfileId(ledger.ActiveProfileId),
                        journal.SourceProfileId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The active ledger changed during profile deletion recovery.");
                    }

                    ValidateActiveArrangement(journal.SourceProfileId);
                    DeleteExpectedInactiveLink(journal);
                    EnsureProfileStagedOrDeleted(journal);
                    if (Directory.Exists(journal.DeletionStagingPath))
                    {
                        directoryMover.DeleteDirectory(journal.DeletionStagingPath);
                    }
                    RequireDeletedArrangement(journal);
                    ValidateActiveArrangement(journal.SourceProfileId);
                    SetPhaseWithoutFault(journal, TransactionPhases.ProfileDeleteContentDeleted);
                    SetPhaseWithoutFault(journal, TransactionPhases.Completed);
                    return new ProfileDeletionRecoveryResult
                    {
                        TransactionId = journal.TransactionId,
                        Result = "DeletedProfile",
                        ActiveProfileId = journal.SourceProfileId,
                        DeletedProfileId = journal.TargetProfileId
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
                    throw new RecoveryRequiredException("Automatic profile deletion recovery could not finish safely.", exception);
                }
            }
        }

        private void EnsureProfileStagedOrDeleted(TransactionJournal journal)
        {
            bool sourceExists = Directory.Exists(journal.CandidateProfilePath);
            bool stagingExists = Directory.Exists(journal.DeletionStagingPath);
            if (sourceExists && stagingExists)
            {
                throw new InvalidOperationException("Both the registered and staged profile directories exist.");
            }

            if (sourceExists)
            {
                RequireFullDirectoryIdentity(journal.CandidateProfilePath, journal, "registered profile");
                directoryMover.MoveDirectory(journal.CandidateProfilePath, journal.DeletionStagingPath);
                runtimeSecurity.ProtectTree(journal.DeletionStagingPath);
                RequireFullDirectoryIdentity(journal.DeletionStagingPath, journal, "staged profile");
                return;
            }

            if (stagingExists)
            {
                if (!ContentDeletionMayHaveStarted(journal.Phase))
                {
                    RequireFullDirectoryIdentity(journal.DeletionStagingPath, journal, "staged profile");
                }
                else
                {
                    directoryMover.Fingerprint(journal.DeletionStagingPath);
                }
                return;
            }

            if (!ContentDeletionMayHaveStarted(journal.Phase))
            {
                throw new InvalidOperationException("The profile disappeared before its permanent deletion phase.");
            }
        }

        private void DeleteExpectedInactiveLink(TransactionJournal journal)
        {
            if (junctions.Exists(journal.TargetInactiveLinkPath))
            {
                string actualTarget = junctions.ResolveTarget(journal.TargetInactiveLinkPath);
                if (!WindowsJunctionManager.PathsEqual(actualTarget, journal.TargetBrowserPath))
                {
                    throw new InvalidOperationException("The inactive profile junction points to an unexpected target.");
                }
                junctions.Delete(journal.TargetInactiveLinkPath);
            }
            else if (File.Exists(journal.TargetInactiveLinkPath) || Directory.Exists(journal.TargetInactiveLinkPath))
            {
                throw new InvalidOperationException("The inactive profile link path contains a non-junction entry.");
            }
            RequirePathAbsent(journal.TargetInactiveLinkPath, "inactive profile junction");
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
            if (!Directory.Exists(paths.ProfileBrowserPath(profileId)))
            {
                throw new DirectoryNotFoundException("The active profile browser directory is missing.");
            }
            RequireLink(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(profileId));
            if (junctions.Exists(paths.InactiveLinkPath(profileId)))
            {
                throw new InvalidOperationException("The active profile also has an inactive browser junction.");
            }
        }

        private void ValidateReadyArrangement(string profileId)
        {
            profileId = EnginePaths.NormalizeProfileId(profileId);
            ReadAndValidateProfile(profileId);
            if (!File.Exists(paths.ProfileAuthPath(profileId)))
            {
                throw new InvalidOperationException("Inactive profile authentication is missing.");
            }
            if (!Directory.Exists(paths.ProfileBrowserPath(profileId)))
            {
                throw new DirectoryNotFoundException("The inactive profile browser directory is missing.");
            }
            RequireLink(paths.InactiveLinkPath(profileId), paths.ProfileBrowserPath(profileId));
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

        private Ledger ReadActiveLedger()
        {
            Ledger ledger = store.Read<Ledger>(paths.LedgerPath);
            if (ledger.SchemaVersion != 1)
            {
                throw new InvalidOperationException("Unsupported ledger schema version.");
            }
            if (ledger.PendingOnboarding != null || string.IsNullOrWhiteSpace(ledger.ActiveProfileId))
            {
                throw new InvalidOperationException("Profile deletion is unavailable during account onboarding.");
            }
            EnginePaths.NormalizeProfileId(ledger.ActiveProfileId);
            return ledger;
        }

        private TransactionJournal NewJournal(
            string transactionId,
            string activeProfileId,
            string targetProfileId,
            string targetDisplayName,
            DirectoryFingerprint fingerprint)
        {
            return new TransactionJournal
            {
                SchemaVersion = 1,
                TransactionId = transactionId,
                Operation = DeleteProfileOperation,
                Phase = TransactionPhases.Prepared,
                SourceProfileId = activeProfileId,
                TargetProfileId = targetProfileId,
                TargetDisplayName = targetDisplayName,
                PackageFamily = packageFamily,
                PackageVersion = packageVersion,
                LiveAuthPath = paths.LiveAuthPath,
                LiveBrowserLinkPath = paths.LiveBrowserLinkPath,
                TargetAuthPath = paths.ProfileAuthPath(targetProfileId),
                TargetInactiveLinkPath = paths.InactiveLinkPath(targetProfileId),
                SourceBrowserPath = paths.ProfileBrowserPath(activeProfileId),
                TargetBrowserPath = paths.ProfileBrowserPath(targetProfileId),
                CandidateProfilePath = paths.ProfileDirectory(targetProfileId),
                DeletionStagingPath = paths.ProfileDeletionStagingPath(transactionId),
                DirectoryTreeSha256 = fingerprint.TreeSha256,
                DirectoryFileCount = fingerprint.FileCount,
                DirectoryCount = fingerprint.DirectoryCount,
                DirectoryTotalLength = fingerprint.TotalLength,
                CreatedAtUtc = UtcNow(),
                UpdatedAtUtc = UtcNow()
            };
        }

        private void RequireFullDirectoryIdentity(string path, TransactionJournal journal, string label)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException("Expected " + label + " is missing.");
            }
            DirectoryFingerprint fingerprint = directoryMover.Fingerprint(path);
            if (fingerprint.FileCount != journal.DirectoryFileCount ||
                fingerprint.DirectoryCount != journal.DirectoryCount ||
                fingerprint.TotalLength != journal.DirectoryTotalLength ||
                !string.Equals(fingerprint.TreeSha256, journal.DirectoryTreeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Integrity mismatch for " + label + ".");
            }
        }

        private void RequireDeletedArrangement(TransactionJournal journal)
        {
            RequirePathAbsent(journal.TargetInactiveLinkPath, "inactive profile junction");
            RequirePathAbsent(journal.CandidateProfilePath, "registered profile directory");
            RequirePathAbsent(journal.DeletionStagingPath, "profile deletion staging path");
        }

        private void RequireLink(string linkPath, string expectedTarget)
        {
            if (!junctions.Exists(linkPath))
            {
                throw new InvalidOperationException("Required browser junction is missing.");
            }
            if (!WindowsJunctionManager.PathsEqual(junctions.ResolveTarget(linkPath), expectedTarget))
            {
                throw new InvalidOperationException("Browser junction points to an unexpected target.");
            }
        }

        private void RequirePathAbsent(string path, string label)
        {
            if (junctions.Exists(path) || File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException("Deletion left the " + label + " in place.");
            }
        }

        private static bool ContentDeletionMayHaveStarted(string phase)
        {
            return string.Equals(phase, TransactionPhases.ProfileDeleteContentIntent, StringComparison.Ordinal) ||
                   string.Equals(phase, TransactionPhases.ProfileDeleteContentDeleted, StringComparison.Ordinal) ||
                   string.Equals(phase, TransactionPhases.RecoveryRequired, StringComparison.Ordinal) ||
                   string.Equals(phase, TransactionPhases.Completed, StringComparison.Ordinal);
        }

        private void RequireDeletionOperation(TransactionJournal journal)
        {
            TransactionJournalValidator.ValidateProfileDeletion(paths, journal, packageFamily, journal.TransactionId);
            if (!string.Equals(journal.Operation, DeleteProfileOperation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The pending transaction is not a profile deletion.");
            }
        }

        private TransactionJournal FindPendingJournal()
        {
            if (!Directory.Exists(paths.TransactionsDirectory)) return null;
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
                if (!TransactionPhases.IsTerminal(journal.Phase)) pending.Add(journal);
            }
            if (pending.Count > 1)
            {
                throw new RecoveryRequiredException("Multiple unfinished transactions were found.");
            }
            return pending.Count == 1 ? pending[0] : null;
        }

        private void RequireNoPendingJournal()
        {
            if (FindPendingJournal() != null)
            {
                throw new RecoveryRequiredException("An unfinished transaction must be recovered before deleting a profile.");
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

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o");
        }
    }
}
