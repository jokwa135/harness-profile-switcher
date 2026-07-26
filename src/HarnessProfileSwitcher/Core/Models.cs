using System;
using System.Runtime.Serialization;

namespace HarnessProfileSwitcher.Core
{
    [DataContract]
    public sealed class Ledger
    {
        [DataMember(Name = "schemaVersion", Order = 1)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "activeProfileId", Order = 2, EmitDefaultValue = false)]
        public string ActiveProfileId { get; set; }

        [DataMember(Name = "pendingOnboarding", Order = 3, EmitDefaultValue = false)]
        public PendingOnboarding PendingOnboarding { get; set; }

        [DataMember(Name = "lastCommittedTransactionId", Order = 4, EmitDefaultValue = false)]
        public string LastCommittedTransactionId { get; set; }

        [DataMember(Name = "updatedAtUtc", Order = 5)]
        public string UpdatedAtUtc { get; set; }
    }

    [DataContract]
    public sealed class PendingOnboarding
    {
        [DataMember(Name = "transactionId", Order = 1)]
        public string TransactionId { get; set; }

        [DataMember(Name = "previousProfileId", Order = 2)]
        public string PreviousProfileId { get; set; }

        [DataMember(Name = "candidateProfileId", Order = 3)]
        public string CandidateProfileId { get; set; }
    }

    [DataContract]
    public sealed class ProfileMetadata
    {
        [DataMember(Name = "schemaVersion", Order = 1)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "profileId", Order = 2)]
        public string ProfileId { get; set; }

        [DataMember(Name = "displayName", Order = 3)]
        public string DisplayName { get; set; }

        [DataMember(Name = "createdAtUtc", Order = 4)]
        public string CreatedAtUtc { get; set; }

        [DataMember(Name = "lastActivatedAtUtc", Order = 5, EmitDefaultValue = false)]
        public string LastActivatedAtUtc { get; set; }

        [DataMember(Name = "registeredPackageFamily", Order = 6)]
        public string RegisteredPackageFamily { get; set; }

        [DataMember(Name = "registeredPackageVersion", Order = 7)]
        public string RegisteredPackageVersion { get; set; }
    }

    [DataContract]
    public sealed class TransactionJournal
    {
        [DataMember(Name = "schemaVersion", Order = 1)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "transactionId", Order = 2)]
        public string TransactionId { get; set; }

        [DataMember(Name = "operation", Order = 3)]
        public string Operation { get; set; }

        [DataMember(Name = "phase", Order = 4)]
        public string Phase { get; set; }

        [DataMember(Name = "sourceProfileId", Order = 5)]
        public string SourceProfileId { get; set; }

        [DataMember(Name = "targetProfileId", Order = 6)]
        public string TargetProfileId { get; set; }

        [DataMember(Name = "packageFamily", Order = 7)]
        public string PackageFamily { get; set; }

        [DataMember(Name = "packageVersion", Order = 8)]
        public string PackageVersion { get; set; }

        [DataMember(Name = "liveAuthPath", Order = 9)]
        public string LiveAuthPath { get; set; }

        [DataMember(Name = "sourceAuthPath", Order = 10)]
        public string SourceAuthPath { get; set; }

        [DataMember(Name = "targetAuthPath", Order = 11)]
        public string TargetAuthPath { get; set; }

        [DataMember(Name = "stagingAuthPath", Order = 12)]
        public string StagingAuthPath { get; set; }

        [DataMember(Name = "liveBrowserLinkPath", Order = 13)]
        public string LiveBrowserLinkPath { get; set; }

        [DataMember(Name = "sourceInactiveLinkPath", Order = 14)]
        public string SourceInactiveLinkPath { get; set; }

        [DataMember(Name = "targetInactiveLinkPath", Order = 15)]
        public string TargetInactiveLinkPath { get; set; }

        [DataMember(Name = "sourceBrowserPath", Order = 16)]
        public string SourceBrowserPath { get; set; }

        [DataMember(Name = "targetBrowserPath", Order = 17)]
        public string TargetBrowserPath { get; set; }

        [DataMember(Name = "sourceAuthSha256", Order = 18)]
        public string SourceAuthSha256 { get; set; }

        [DataMember(Name = "targetAuthSha256", Order = 19)]
        public string TargetAuthSha256 { get; set; }

        [DataMember(Name = "sourceAuthLength", Order = 20)]
        public long SourceAuthLength { get; set; }

        [DataMember(Name = "targetAuthLength", Order = 21)]
        public long TargetAuthLength { get; set; }

        [DataMember(Name = "createdAtUtc", Order = 22)]
        public string CreatedAtUtc { get; set; }

        [DataMember(Name = "updatedAtUtc", Order = 23)]
        public string UpdatedAtUtc { get; set; }

        [DataMember(Name = "previousProfileId", Order = 24, EmitDefaultValue = false)]
        public string PreviousProfileId { get; set; }

        [DataMember(Name = "candidateProfileId", Order = 25, EmitDefaultValue = false)]
        public string CandidateProfileId { get; set; }

        [DataMember(Name = "targetDisplayName", Order = 26, EmitDefaultValue = false)]
        public string TargetDisplayName { get; set; }

        [DataMember(Name = "candidateProfilePath", Order = 27, EmitDefaultValue = false)]
        public string CandidateProfilePath { get; set; }

        [DataMember(Name = "lifecycleQuarantinePath", Order = 28, EmitDefaultValue = false)]
        public string LifecycleQuarantinePath { get; set; }

        [DataMember(Name = "cancelledCandidateLinkPath", Order = 29, EmitDefaultValue = false)]
        public string CancelledCandidateLinkPath { get; set; }

        [DataMember(Name = "candidateAuthQuarantinePath", Order = 30, EmitDefaultValue = false)]
        public string CandidateAuthQuarantinePath { get; set; }

        [DataMember(Name = "candidateAuthWasPresent", Order = 31)]
        public bool CandidateAuthWasPresent { get; set; }

        [DataMember(Name = "directoryTreeSha256", Order = 32, EmitDefaultValue = false)]
        public string DirectoryTreeSha256 { get; set; }

        [DataMember(Name = "directoryFileCount", Order = 33)]
        public long DirectoryFileCount { get; set; }

        [DataMember(Name = "directoryCount", Order = 34)]
        public long DirectoryCount { get; set; }

        [DataMember(Name = "directoryTotalLength", Order = 35)]
        public long DirectoryTotalLength { get; set; }

        [DataMember(Name = "deletionStagingPath", Order = 36, EmitDefaultValue = false)]
        public string DeletionStagingPath { get; set; }
    }

    public static class TransactionPhases
    {
        public const string Prepared = "Prepared";
        public const string ProcessesClosed = "ProcessesClosed";
        public const string OutgoingAuthMoveIntent = "OutgoingAuthMoveIntent";
        public const string OutgoingAuthMoved = "OutgoingAuthMoved";
        public const string IncomingAuthMoveIntent = "IncomingAuthMoveIntent";
        public const string IncomingAuthStaged = "IncomingAuthStaged";
        public const string IncomingAuthActivateIntent = "IncomingAuthActivateIntent";
        public const string IncomingAuthActivated = "IncomingAuthActivated";
        public const string OutgoingBrowserLinkParkIntent = "OutgoingBrowserLinkParkIntent";
        public const string OutgoingBrowserLinkParked = "OutgoingBrowserLinkParked";
        public const string IncomingBrowserLinkActivateIntent = "IncomingBrowserLinkActivateIntent";
        public const string IncomingBrowserLinkActivated = "IncomingBrowserLinkActivated";
        public const string LedgerCommitIntent = "LedgerCommitIntent";
        public const string LedgerCommitted = "LedgerCommitted";
        public const string LaunchObserved = "LaunchObserved";
        public const string Completed = "Completed";
        public const string RollbackStarted = "RollbackStarted";
        public const string RolledBack = "RolledBack";
        public const string RecoveryRequired = "RecoveryRequired";
        public const string ProfileCreateIntent = "ProfileCreateIntent";
        public const string ProfileCreated = "ProfileCreated";
        public const string BrowserTransferIntent = "BrowserTransferIntent";
        public const string BrowserTransferred = "BrowserTransferred";
        public const string LiveJunctionCreateIntent = "LiveJunctionCreateIntent";
        public const string LiveJunctionCreated = "LiveJunctionCreated";
        public const string CandidateBrowserActivateIntent = "CandidateBrowserActivateIntent";
        public const string CandidateBrowserActivated = "CandidateBrowserActivated";
        public const string PendingLedgerCommitIntent = "PendingLedgerCommitIntent";
        public const string PendingLedgerCommitted = "PendingLedgerCommitted";
        public const string CandidateValidationCompleted = "CandidateValidationCompleted";
        public const string CandidateAuthQuarantineIntent = "CandidateAuthQuarantineIntent";
        public const string CandidateAuthQuarantined = "CandidateAuthQuarantined";
        public const string CandidateBrowserLinkParkIntent = "CandidateBrowserLinkParkIntent";
        public const string CandidateBrowserLinkParked = "CandidateBrowserLinkParked";
        public const string PreviousBrowserLinkActivateIntent = "PreviousBrowserLinkActivateIntent";
        public const string PreviousBrowserLinkActivated = "PreviousBrowserLinkActivated";
        public const string CandidateProfileQuarantineIntent = "CandidateProfileQuarantineIntent";
        public const string CandidateProfileQuarantined = "CandidateProfileQuarantined";
        public const string PreviousAuthActivateIntent = "PreviousAuthActivateIntent";
        public const string PreviousAuthActivated = "PreviousAuthActivated";
        public const string ProfileDeleteLinkIntent = "ProfileDeleteLinkIntent";
        public const string ProfileDeleteLinkDeleted = "ProfileDeleteLinkDeleted";
        public const string ProfileDeleteStageIntent = "ProfileDeleteStageIntent";
        public const string ProfileDeleteStaged = "ProfileDeleteStaged";
        public const string ProfileDeleteContentIntent = "ProfileDeleteContentIntent";
        public const string ProfileDeleteContentDeleted = "ProfileDeleteContentDeleted";

        public static bool IsTerminal(string phase)
        {
            return string.Equals(phase, Completed, StringComparison.Ordinal) ||
                   string.Equals(phase, RolledBack, StringComparison.Ordinal);
        }
    }

    public sealed class SwitchResult
    {
        public string TransactionId { get; set; }
        public string SourceProfileId { get; set; }
        public string TargetProfileId { get; set; }
    }

    public sealed class RecoveryResult
    {
        public string TransactionId { get; set; }
        public string Result { get; set; }
        public string ActiveProfileId { get; set; }
    }

    public sealed class LifecycleResult
    {
        public string Operation { get; set; }
        public string TransactionId { get; set; }
        public string ActiveProfileId { get; set; }
        public string PreviousProfileId { get; set; }
        public string CandidateProfileId { get; set; }
    }

    public sealed class LifecycleRecoveryResult
    {
        public string TransactionId { get; set; }
        public string Operation { get; set; }
        public string Result { get; set; }
        public string ActiveProfileId { get; set; }
        public string CandidateProfileId { get; set; }
    }

    public sealed class ProfileDeletionResult
    {
        public string TransactionId { get; set; }
        public string ActiveProfileId { get; set; }
        public string DeletedProfileId { get; set; }
    }

    public sealed class ProfileDeletionRecoveryResult
    {
        public string TransactionId { get; set; }
        public string Result { get; set; }
        public string ActiveProfileId { get; set; }
        public string DeletedProfileId { get; set; }
    }

    public interface IProcessGuard
    {
        void EnsureStopped();
    }

    public interface IFaultInjector
    {
        void Checkpoint(string phase);
    }

    public sealed class NoFaultInjector : IFaultInjector
    {
        public void Checkpoint(string phase)
        {
        }
    }
}
