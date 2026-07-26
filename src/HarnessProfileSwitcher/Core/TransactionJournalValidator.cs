using System;
using System.Collections.Generic;
using System.IO;

namespace HarnessProfileSwitcher.Core
{
    public static class TransactionJournalValidator
    {
        private static readonly ISet<string> SwitchPhases = new HashSet<string>(StringComparer.Ordinal)
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
            TransactionPhases.LedgerCommitted,
            TransactionPhases.LaunchObserved,
            TransactionPhases.Completed,
            TransactionPhases.RollbackStarted,
            TransactionPhases.RolledBack,
            TransactionPhases.RecoveryRequired
        };

        private static readonly ISet<string> RegisterPhases = LifecyclePhases(
            TransactionPhases.ProfileCreateIntent,
            TransactionPhases.ProfileCreated,
            TransactionPhases.BrowserTransferIntent,
            TransactionPhases.BrowserTransferred,
            TransactionPhases.LiveJunctionCreateIntent,
            TransactionPhases.LiveJunctionCreated);

        private static readonly ISet<string> BeginPhases = LifecyclePhases(
            TransactionPhases.ProfileCreateIntent,
            TransactionPhases.ProfileCreated,
            TransactionPhases.OutgoingAuthMoveIntent,
            TransactionPhases.OutgoingAuthMoved,
            TransactionPhases.OutgoingBrowserLinkParkIntent,
            TransactionPhases.OutgoingBrowserLinkParked,
            TransactionPhases.CandidateBrowserActivateIntent,
            TransactionPhases.CandidateBrowserActivated,
            TransactionPhases.PendingLedgerCommitIntent,
            TransactionPhases.PendingLedgerCommitted);

        private static readonly ISet<string> FinalizePhases = LifecyclePhases(
            TransactionPhases.CandidateValidationCompleted);

        private static readonly ISet<string> CancelPhases = LifecyclePhases(
            TransactionPhases.CandidateAuthQuarantineIntent,
            TransactionPhases.CandidateAuthQuarantined,
            TransactionPhases.CandidateBrowserLinkParkIntent,
            TransactionPhases.CandidateBrowserLinkParked,
            TransactionPhases.PreviousBrowserLinkActivateIntent,
            TransactionPhases.PreviousBrowserLinkActivated,
            TransactionPhases.CandidateProfileQuarantineIntent,
            TransactionPhases.CandidateProfileQuarantined,
            TransactionPhases.PreviousAuthActivateIntent,
            TransactionPhases.PreviousAuthActivated);

        public static void ValidateForInspection(EnginePaths paths, TransactionJournal journal, string expectedTransactionId)
        {
            ValidateAny(paths, journal, null, expectedTransactionId);
        }

        public static void ValidateAny(
            EnginePaths paths,
            TransactionJournal journal,
            string expectedPackageFamily,
            string expectedTransactionId)
        {
            if (journal == null) throw new InvalidDataException("Transaction journal is empty.");
            if (string.Equals(journal.Operation, "switch", StringComparison.Ordinal))
            {
                ValidateSwitch(paths, journal, expectedPackageFamily, expectedTransactionId);
                return;
            }
            ValidateLifecycle(paths, journal, expectedPackageFamily, expectedTransactionId);
        }

        public static void ValidateSwitch(
            EnginePaths paths,
            TransactionJournal journal,
            string expectedPackageFamily,
            string expectedTransactionId)
        {
            ValidateCommon(paths, journal, expectedPackageFamily, expectedTransactionId, "switch", SwitchPhases);
            string source = NormalizeProfileId(journal.SourceProfileId, "source profile");
            string target = NormalizeProfileId(journal.TargetProfileId, "target profile");
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Switch journal names the same source and target profile.");
            }

            RequirePath(journal.SourceAuthPath, paths.ProfileAuthPath(source), "source authentication");
            RequirePath(journal.TargetAuthPath, paths.ProfileAuthPath(target), "target authentication");
            RequirePath(journal.StagingAuthPath, paths.StagingAuthPath(journal.TransactionId), "staged authentication");
            RequirePath(journal.SourceInactiveLinkPath, paths.InactiveLinkPath(source), "source inactive junction");
            RequirePath(journal.TargetInactiveLinkPath, paths.InactiveLinkPath(target), "target inactive junction");
            RequirePath(journal.SourceBrowserPath, paths.ProfileBrowserPath(source), "source browser slot");
            RequirePath(journal.TargetBrowserPath, paths.ProfileBrowserPath(target), "target browser slot");
            RequireFingerprint(journal.SourceAuthSha256, journal.SourceAuthLength, "source authentication");
            RequireFingerprint(journal.TargetAuthSha256, journal.TargetAuthLength, "target authentication");
        }

        public static void ValidateLifecycle(
            EnginePaths paths,
            TransactionJournal journal,
            string expectedPackageFamily,
            string expectedTransactionId)
        {
            if (journal == null) throw new InvalidDataException("Transaction journal is empty.");
            ISet<string> phases = LifecyclePhaseSet(journal.Operation);
            ValidateCommon(paths, journal, expectedPackageFamily, expectedTransactionId, journal.Operation, phases);

            string candidate = NormalizeProfileId(journal.CandidateProfileId, "candidate profile");
            RequirePath(journal.CandidateProfilePath, paths.ProfileDirectory(candidate), "candidate profile directory");
            RequirePath(journal.LifecycleQuarantinePath, paths.LifecycleQuarantineDirectory(journal.TransactionId), "lifecycle quarantine");

            if (string.Equals(journal.Operation, ProfileLifecycleEngine.RegisterFirstOperation, StringComparison.Ordinal))
            {
                RequireProfileId(journal.TargetProfileId, candidate, "registration target profile");
                RequireEmpty(journal.SourceProfileId, "registration source profile");
                RequireEmpty(journal.PreviousProfileId, "registration previous profile");
                RequireDisplayName(journal.TargetDisplayName);
                RequirePath(journal.TargetBrowserPath, paths.ProfileBrowserPath(candidate), "registration browser slot");
                RequirePath(journal.CancelledCandidateLinkPath, paths.CancelledLinkPath(candidate, journal.TransactionId), "registration parked junction");
                RequireDirectoryFingerprint(journal);
                return;
            }

            string previous = NormalizeProfileId(journal.PreviousProfileId, "previous profile");
            if (string.Equals(previous, candidate, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Lifecycle journal names the same previous and candidate profile.");
            }

            if (string.Equals(journal.Operation, ProfileLifecycleEngine.BeginOnboardingOperation, StringComparison.Ordinal))
            {
                RequireProfileId(journal.SourceProfileId, previous, "begin source profile");
                RequireProfileId(journal.TargetProfileId, candidate, "begin target profile");
                RequireDisplayName(journal.TargetDisplayName);
                RequirePath(journal.SourceAuthPath, paths.ProfileAuthPath(previous), "previous authentication vault");
                RequirePath(journal.SourceInactiveLinkPath, paths.InactiveLinkPath(previous), "previous inactive junction");
                RequirePath(journal.SourceBrowserPath, paths.ProfileBrowserPath(previous), "previous browser slot");
                RequirePath(journal.TargetBrowserPath, paths.ProfileBrowserPath(candidate), "candidate browser slot");
                RequirePath(journal.CancelledCandidateLinkPath, paths.CancelledLinkPath(candidate, journal.TransactionId), "candidate parked junction");
                RequireFingerprint(journal.SourceAuthSha256, journal.SourceAuthLength, "previous authentication");
                return;
            }

            if (string.Equals(journal.Operation, ProfileLifecycleEngine.FinalizeOnboardingOperation, StringComparison.Ordinal))
            {
                RequireProfileId(journal.SourceProfileId, previous, "finalize source profile");
                RequireProfileId(journal.TargetProfileId, candidate, "finalize target profile");
                RequirePath(journal.SourceAuthPath, paths.ProfileAuthPath(previous), "previous authentication vault");
                RequirePath(journal.SourceInactiveLinkPath, paths.InactiveLinkPath(previous), "previous inactive junction");
                RequirePath(journal.SourceBrowserPath, paths.ProfileBrowserPath(previous), "previous browser slot");
                RequirePath(journal.TargetBrowserPath, paths.ProfileBrowserPath(candidate), "candidate browser slot");
                RequireFingerprint(journal.SourceAuthSha256, journal.SourceAuthLength, "previous authentication");
                RequireFingerprint(journal.TargetAuthSha256, journal.TargetAuthLength, "candidate authentication");
                RequireDirectoryFingerprint(journal);
                return;
            }

            RequireProfileId(journal.SourceProfileId, candidate, "cancel source profile");
            RequireProfileId(journal.TargetProfileId, previous, "cancel target profile");
            RequirePath(journal.TargetAuthPath, paths.ProfileAuthPath(previous), "previous authentication vault");
            RequirePath(journal.SourceInactiveLinkPath, paths.InactiveLinkPath(previous), "previous inactive junction");
            RequirePath(journal.SourceBrowserPath, paths.ProfileBrowserPath(candidate), "candidate browser slot");
            RequirePath(journal.TargetBrowserPath, paths.ProfileBrowserPath(previous), "previous browser slot");
            RequirePath(journal.CancelledCandidateLinkPath, paths.CancelledLinkPath(candidate, journal.TransactionId), "cancelled candidate junction");
            RequirePath(journal.CandidateAuthQuarantinePath, Path.Combine(journal.LifecycleQuarantinePath, "candidate-auth.json"), "candidate authentication quarantine");
            RequireFingerprint(journal.TargetAuthSha256, journal.TargetAuthLength, "previous authentication");
            if (journal.CandidateAuthWasPresent)
            {
                RequireFingerprint(journal.SourceAuthSha256, journal.SourceAuthLength, "candidate authentication");
            }
            RequireDirectoryFingerprint(journal);
        }

        private static void ValidateCommon(
            EnginePaths paths,
            TransactionJournal journal,
            string expectedPackageFamily,
            string expectedTransactionId,
            string expectedOperation,
            ISet<string> allowedPhases)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (journal == null) throw new InvalidDataException("Transaction journal is empty.");
            if (journal.SchemaVersion != 1) throw new InvalidDataException("Transaction journal schema is unsupported.");
            string transactionId = NormalizeTransactionId(journal.TransactionId, "journal transaction ID");
            string expected = NormalizeTransactionId(expectedTransactionId, "transaction directory ID");
            if (!string.Equals(transactionId, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Transaction journal ID does not match its directory.");
            }
            if (!string.Equals(journal.Operation, expectedOperation, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Transaction journal operation is invalid.");
            }
            if (string.IsNullOrWhiteSpace(journal.Phase) || !allowedPhases.Contains(journal.Phase))
            {
                throw new InvalidDataException("Transaction journal phase is invalid for its operation.");
            }
            if (string.IsNullOrWhiteSpace(journal.PackageFamily) || string.IsNullOrWhiteSpace(journal.PackageVersion))
            {
                throw new InvalidDataException("Transaction journal package identity is incomplete.");
            }
            if (!string.IsNullOrWhiteSpace(expectedPackageFamily) &&
                !string.Equals(journal.PackageFamily, expectedPackageFamily, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Transaction journal belongs to a different package family.");
            }
            RequirePath(journal.LiveAuthPath, paths.LiveAuthPath, "live authentication");
            RequirePath(journal.LiveBrowserLinkPath, paths.LiveBrowserLinkPath, "live browser junction");
        }

        private static ISet<string> LifecyclePhaseSet(string operation)
        {
            if (string.Equals(operation, ProfileLifecycleEngine.RegisterFirstOperation, StringComparison.Ordinal)) return RegisterPhases;
            if (string.Equals(operation, ProfileLifecycleEngine.BeginOnboardingOperation, StringComparison.Ordinal)) return BeginPhases;
            if (string.Equals(operation, ProfileLifecycleEngine.FinalizeOnboardingOperation, StringComparison.Ordinal)) return FinalizePhases;
            if (string.Equals(operation, ProfileLifecycleEngine.CancelOnboardingOperation, StringComparison.Ordinal)) return CancelPhases;
            throw new InvalidDataException("Transaction journal lifecycle operation is invalid.");
        }

        private static ISet<string> LifecyclePhases(params string[] operationPhases)
        {
            HashSet<string> values = new HashSet<string>(StringComparer.Ordinal)
            {
                TransactionPhases.Prepared,
                TransactionPhases.ProcessesClosed,
                TransactionPhases.LedgerCommitIntent,
                TransactionPhases.LedgerCommitted,
                TransactionPhases.LaunchObserved,
                TransactionPhases.Completed,
                TransactionPhases.RollbackStarted,
                TransactionPhases.RolledBack,
                TransactionPhases.RecoveryRequired
            };
            foreach (string phase in operationPhases) values.Add(phase);
            return values;
        }

        private static string NormalizeTransactionId(string value, string label)
        {
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed)) throw new InvalidDataException(label + " is not a canonical GUID.");
            return parsed.ToString("D");
        }

        private static string NormalizeProfileId(string value, string label)
        {
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed)) throw new InvalidDataException(label + " is not a canonical GUID.");
            return parsed.ToString("D");
        }

        private static void RequireProfileId(string actual, string expected, string label)
        {
            string normalized = NormalizeProfileId(actual, label);
            if (!string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(label + " does not match its deterministic profile slot.");
            }
        }

        private static void RequirePath(string actual, string expected, string label)
        {
            if (string.IsNullOrWhiteSpace(actual) || !WindowsJunctionManager.PathsEqual(actual, expected))
            {
                throw new InvalidDataException("Transaction journal " + label + " path is invalid.");
            }
        }

        private static void RequireFingerprint(string sha256, long length, string label)
        {
            if (length < 0 || !IsSha256(sha256))
            {
                throw new InvalidDataException("Transaction journal " + label + " fingerprint is invalid.");
            }
        }

        private static void RequireDirectoryFingerprint(TransactionJournal journal)
        {
            if (!IsSha256(journal.DirectoryTreeSha256) || journal.DirectoryCount < 1 ||
                journal.DirectoryFileCount < 0 || journal.DirectoryTotalLength < 0)
            {
                throw new InvalidDataException("Transaction journal directory fingerprint is invalid.");
            }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool hexadecimal = (character >= '0' && character <= '9') ||
                                   (character >= 'a' && character <= 'f') ||
                                   (character >= 'A' && character <= 'F');
                if (!hexadecimal) return false;
            }
            return true;
        }

        private static void RequireDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new InvalidDataException("Lifecycle journal display name is missing.");
            foreach (char character in displayName)
            {
                if (char.IsControl(character)) throw new InvalidDataException("Lifecycle journal display name is invalid.");
            }
        }

        private static void RequireEmpty(string value, string label)
        {
            if (!string.IsNullOrEmpty(value)) throw new InvalidDataException(label + " must be empty.");
        }
    }
}
