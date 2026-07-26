using System;
using System.IO;

namespace HarnessProfileSwitcher.Core
{
    public sealed class EnginePaths
    {
        public EnginePaths(
            string runtimeRoot,
            string liveAuthPath,
            string liveBrowserLinkPath,
            string inactiveLinksDirectory,
            string authStagingRoot)
            : this(
                runtimeRoot,
                liveAuthPath,
                liveBrowserLinkPath,
                inactiveLinksDirectory,
                authStagingRoot,
                @"D:\fordeletion\HarnessProfileSwitcher")
        {
        }

        public EnginePaths(
            string runtimeRoot,
            string liveAuthPath,
            string liveBrowserLinkPath,
            string inactiveLinksDirectory,
            string authStagingRoot,
            string deletionRoot)
        {
            RuntimeRoot = Canonical(runtimeRoot);
            LiveAuthPath = Canonical(liveAuthPath);
            LiveBrowserLinkPath = Canonical(liveBrowserLinkPath);
            InactiveLinksDirectory = Canonical(inactiveLinksDirectory);
            AuthStagingRoot = Canonical(authStagingRoot);
            DeletionRoot = Canonical(deletionRoot);

            StateDirectory = Path.Combine(RuntimeRoot, "state");
            ProfilesDirectory = Path.Combine(RuntimeRoot, "profiles");
            TransactionsDirectory = Path.Combine(RuntimeRoot, "transactions");
            LogsDirectory = Path.Combine(RuntimeRoot, "logs");
            LedgerPath = Path.Combine(StateDirectory, "ledger.json");
            OperationLockPath = Path.Combine(StateDirectory, "operation.lock");
        }

        public string RuntimeRoot { get; private set; }
        public string StateDirectory { get; private set; }
        public string ProfilesDirectory { get; private set; }
        public string TransactionsDirectory { get; private set; }
        public string LogsDirectory { get; private set; }
        public string LedgerPath { get; private set; }
        public string OperationLockPath { get; private set; }
        public string LiveAuthPath { get; private set; }
        public string LiveBrowserLinkPath { get; private set; }
        public string InactiveLinksDirectory { get; private set; }
        public string AuthStagingRoot { get; private set; }
        public string DeletionRoot { get; private set; }

        public string ProfileDirectory(string profileId)
        {
            return Path.Combine(ProfilesDirectory, NormalizeProfileId(profileId));
        }

        public string ProfileMetadataPath(string profileId)
        {
            return Path.Combine(ProfileDirectory(profileId), "profile.json");
        }

        public string ProfileAuthPath(string profileId)
        {
            return Path.Combine(ProfileDirectory(profileId), "auth.json");
        }

        public string ProfileBrowserPath(string profileId)
        {
            return Path.Combine(ProfileDirectory(profileId), "browser");
        }

        public string InactiveLinkPath(string profileId)
        {
            return Path.Combine(InactiveLinksDirectory, NormalizeProfileId(profileId));
        }

        public string TransactionDirectory(string transactionId)
        {
            return Path.Combine(TransactionsDirectory, NormalizeTransactionId(transactionId));
        }

        public string JournalPath(string transactionId)
        {
            return Path.Combine(TransactionDirectory(transactionId), "journal.json");
        }

        public string ProfileDeletionStagingPath(string transactionId)
        {
            return Path.Combine(TransactionDirectory(transactionId), "profile-delete-staging");
        }

        public string StagingAuthPath(string transactionId)
        {
            return Path.Combine(AuthStagingRoot, NormalizeTransactionId(transactionId), "incoming-auth.json");
        }

        public string QuarantinePath(string transactionId, string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Invalid quarantine label.", "label");
            }
            return Path.Combine(TransactionDirectory(transactionId), Path.Combine("quarantine", label + "-" + Guid.NewGuid().ToString("N")));
        }

        public string LifecycleQuarantineDirectory(string transactionId)
        {
            return Path.Combine(DeletionRoot, Path.Combine("lifecycle", NormalizeTransactionId(transactionId)));
        }

        public string CancelledLinkPath(string profileId, string transactionId)
        {
            return Path.Combine(
                InactiveLinksDirectory,
                NormalizeProfileId(profileId) + ".cancelled-" + NormalizeTransactionId(transactionId));
        }

        public static string NormalizeProfileId(string profileId)
        {
            Guid value;
            if (!Guid.TryParse(profileId, out value))
            {
                throw new ArgumentException("Profile ID must be a GUID.", "profileId");
            }
            return value.ToString("D");
        }

        private static string NormalizeTransactionId(string transactionId)
        {
            Guid value;
            if (!Guid.TryParse(transactionId, out value))
            {
                throw new ArgumentException("Transaction ID must be a GUID.", "transactionId");
            }
            return value.ToString("D");
        }

        private static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A required path is empty.", "path");
            }
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
    }
}
