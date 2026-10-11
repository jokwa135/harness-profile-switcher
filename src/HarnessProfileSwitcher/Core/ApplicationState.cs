using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HarnessProfileSwitcher.Core
{
    public enum HarnessMode
    {
        Unmanaged,
        Active,
        PendingOnboarding,
        RecoveryRequired,
        Broken
    }

    public sealed class ProfileSummary
    {
        public string ProfileId { get; set; }
        public string DisplayName { get; set; }
        public string Status { get; set; }
        public bool IsActive { get; set; }
        public bool CanSwitch { get; set; }
        public bool CanDelete { get; set; }
        public bool CanRename { get; set; }
    }

    public sealed class PendingTransactionSummary
    {
        public string TransactionId { get; set; }
        public string Operation { get; set; }
        public string Phase { get; set; }
    }

    public sealed class ApplicationStateSnapshot
    {
        public HarnessMode Mode { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string ActiveProfileId { get; set; }
        public string PreviousProfileId { get; set; }
        public string CandidateProfileId { get; set; }
        public bool PendingAuthenticationPresent { get; set; }
        public bool CanRecoverAutomatically { get; set; }
        public PendingTransactionSummary PendingTransaction { get; set; }
        public IList<ProfileSummary> Profiles { get; set; }
    }

    public sealed class ApplicationStateInspector
    {
        private readonly EnginePaths paths;
        private readonly AtomicJsonStore store;
        private readonly IJunctionManager junctions;

        public ApplicationStateInspector(EnginePaths paths, AtomicJsonStore store, IJunctionManager junctions)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (store == null) throw new ArgumentNullException("store");
            if (junctions == null) throw new ArgumentNullException("junctions");
            this.paths = paths;
            this.store = store;
            this.junctions = junctions;
        }

        public ApplicationStateSnapshot Inspect()
        {
            try
            {
                return InspectCore();
            }
            catch (Exception exception)
            {
                return new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.Broken,
                    Title = "Account state needs attention",
                    Description = SafeMessage(exception),
                    Profiles = new List<ProfileSummary>()
                };
            }
        }

        private ApplicationStateSnapshot InspectCore()
        {
            List<ProfileRecord> profiles = LoadProfiles();
            List<TransactionJournal> pendingJournals = LoadPendingJournals();
            Ledger ledger = File.Exists(paths.LedgerPath) ? store.Read<Ledger>(paths.LedgerPath) : null;
            if (ledger != null && ledger.SchemaVersion != 1)
            {
                throw new InvalidOperationException("The profile ledger uses an unsupported schema version.");
            }

            if (pendingJournals.Count > 0)
            {
                TransactionJournal first = pendingJournals[0];
                return new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.RecoveryRequired,
                    Title = pendingJournals.Count == 1 ? "A previous operation needs recovery" : "Multiple operations need recovery",
                    Description = pendingJournals.Count == 1
                        ? "No account changes are available until the interrupted operation is resolved."
                        : "More than one unfinished journal was found. Automatic mutation is disabled.",
                    ActiveProfileId = ledger == null ? null : ledger.ActiveProfileId,
                    PendingTransaction = new PendingTransactionSummary
                    {
                        TransactionId = first.TransactionId,
                        Operation = first.Operation,
                        Phase = first.Phase
                    },
                    CanRecoverAutomatically = pendingJournals.Count == 1 && IsSupportedOperation(first.Operation),
                    Profiles = BuildProfileSummaries(profiles, ledger)
                };
            }

            if (ledger == null)
            {
                bool liveAuth = File.Exists(paths.LiveAuthPath);
                bool realBrowser = Directory.Exists(paths.LiveBrowserLinkPath) && !junctions.Exists(paths.LiveBrowserLinkPath);
                if (liveAuth && realBrowser)
                {
                    return new ApplicationStateSnapshot
                    {
                        Mode = HarnessMode.Unmanaged,
                        Title = "Register the account already signed in",
                        Description = "This one-time setup moves only the browser session to its durable D: slot. Shared Codex projects, history, skills, and settings stay where they are.",
                        Profiles = new List<ProfileSummary>()
                    };
                }
                return new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.Broken,
                    Title = "No registrable signed-in account was found",
                    Description = "Harness Profile Switcher expected both live authentication and a real ChatGPT browser-state directory.",
                    Profiles = new List<ProfileSummary>()
                };
            }

            if (ledger.PendingOnboarding != null)
            {
                if (!string.IsNullOrWhiteSpace(ledger.ActiveProfileId))
                {
                    throw new InvalidOperationException("The ledger names both an active profile and pending onboarding.");
                }
                string previousId = EnginePaths.NormalizeProfileId(ledger.PendingOnboarding.PreviousProfileId);
                string candidateId = EnginePaths.NormalizeProfileId(ledger.PendingOnboarding.CandidateProfileId);
                return new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.PendingOnboarding,
                    Title = File.Exists(paths.LiveAuthPath) ? "New account sign-in detected" : "Finish signing in to the new account",
                    Description = File.Exists(paths.LiveAuthPath)
                        ? "Return here after the sign-in window is ready, then finish setup."
                        : "Complete the OAuth flow in ChatGPT. You can cancel safely and restore the previous account at any time.",
                    PreviousProfileId = previousId,
                    CandidateProfileId = candidateId,
                    PendingAuthenticationPresent = File.Exists(paths.LiveAuthPath),
                    Profiles = BuildProfileSummaries(profiles, ledger)
                };
            }

            if (string.IsNullOrWhiteSpace(ledger.ActiveProfileId))
            {
                throw new InvalidOperationException("The ledger has neither an active profile nor pending onboarding.");
            }
            string activeId = EnginePaths.NormalizeProfileId(ledger.ActiveProfileId);
            IList<ProfileSummary> summaries = BuildProfileSummaries(profiles, ledger);
            ProfileSummary active = summaries.FirstOrDefault(delegate(ProfileSummary item)
            {
                return string.Equals(item.ProfileId, activeId, StringComparison.OrdinalIgnoreCase);
            });
            if (active == null || !string.Equals(active.Status, "Active", StringComparison.Ordinal))
            {
                return new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.Broken,
                    Title = "The active profile is incomplete",
                    Description = "Authentication, browser state, or profile metadata does not match the ledger. Switching is disabled.",
                    ActiveProfileId = activeId,
                    Profiles = summaries
                };
            }

            return new ApplicationStateSnapshot
            {
                Mode = HarnessMode.Active,
                Title = active.DisplayName + " is active",
                Description = "Switching preserves the newest authentication state while keeping local Codex work shared.",
                ActiveProfileId = activeId,
                Profiles = summaries
            };
        }

        private IList<ProfileSummary> BuildProfileSummaries(List<ProfileRecord> profiles, Ledger ledger)
        {
            string activeId = ledger == null ? null : ledger.ActiveProfileId;
            string previousId = ledger != null && ledger.PendingOnboarding != null ? ledger.PendingOnboarding.PreviousProfileId : null;
            string candidateId = ledger != null && ledger.PendingOnboarding != null ? ledger.PendingOnboarding.CandidateProfileId : null;
            List<ProfileSummary> summaries = new List<ProfileSummary>();
            foreach (ProfileRecord record in profiles)
            {
                ProfileSummary summary = new ProfileSummary
                {
                    ProfileId = record.ProfileId,
                    DisplayName = record.DisplayName,
                    Status = "Broken"
                };
                if (!record.Valid)
                {
                    summaries.Add(summary);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(activeId) &&
                    string.Equals(record.ProfileId, activeId, StringComparison.OrdinalIgnoreCase))
                {
                    bool healthy = File.Exists(paths.LiveAuthPath) &&
                                   !File.Exists(paths.ProfileAuthPath(record.ProfileId)) &&
                                   LinkTargets(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(record.ProfileId));
                    summary.Status = healthy ? "Active" : "Broken";
                    summary.IsActive = healthy;
                }
                else if (!string.IsNullOrWhiteSpace(candidateId) &&
                         string.Equals(record.ProfileId, candidateId, StringComparison.OrdinalIgnoreCase))
                {
                    bool healthy = Directory.Exists(paths.ProfileBrowserPath(record.ProfileId)) &&
                                   !File.Exists(paths.ProfileAuthPath(record.ProfileId)) &&
                                   LinkTargets(paths.LiveBrowserLinkPath, paths.ProfileBrowserPath(record.ProfileId));
                    summary.Status = healthy ? "Signing in" : "Broken";
                }
                else
                {
                    bool healthy = File.Exists(paths.ProfileAuthPath(record.ProfileId)) &&
                                   Directory.Exists(paths.ProfileBrowserPath(record.ProfileId)) &&
                                   LinkTargets(paths.InactiveLinkPath(record.ProfileId), paths.ProfileBrowserPath(record.ProfileId));
                    summary.Status = healthy ? "Ready" : "Broken";
                    summary.CanSwitch = healthy && string.IsNullOrWhiteSpace(candidateId);
                    summary.CanDelete = healthy && string.IsNullOrWhiteSpace(candidateId);
                }

                if (!string.IsNullOrWhiteSpace(previousId) &&
                    string.Equals(record.ProfileId, previousId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(summary.Status, "Ready", StringComparison.Ordinal))
                {
                    summary.Status = "Broken";
                    summary.CanSwitch = false;
                }
                summaries.Add(summary);
            }

            foreach (ProfileSummary summary in summaries)
                summary.CanRename = ledger != null && ledger.PendingOnboarding == null &&
                    (summary.Status == "Active" || summary.Status == "Ready");

            return summaries
                .OrderByDescending(delegate(ProfileSummary item) { return item.IsActive; })
                // Eligibility is finalized by the inspection's mode in the UI/editor.
                .ThenBy(delegate(ProfileSummary item) { return item.Status == "Signing in" ? 0 : 1; })
                .ThenBy(delegate(ProfileSummary item) { return item.DisplayName; }, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private List<ProfileRecord> LoadProfiles()
        {
            List<ProfileRecord> profiles = new List<ProfileRecord>();
            if (!Directory.Exists(paths.ProfilesDirectory)) return profiles;
            foreach (string directory in Directory.GetDirectories(paths.ProfilesDirectory))
            {
                string directoryName = Path.GetFileName(directory);
                Guid profileGuid;
                if (!Guid.TryParse(directoryName, out profileGuid)) continue;
                string profileId = profileGuid.ToString("D");
                try
                {
                    ProfileMetadata metadata = store.Read<ProfileMetadata>(paths.ProfileMetadataPath(profileId));
                    bool valid = metadata.SchemaVersion == 1 &&
                                 string.Equals(metadata.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(metadata.DisplayName);
                    profiles.Add(new ProfileRecord
                    {
                        ProfileId = profileId,
                        DisplayName = valid ? metadata.DisplayName : "Unreadable profile",
                        Valid = valid
                    });
                }
                catch
                {
                    profiles.Add(new ProfileRecord
                    {
                        ProfileId = profileId,
                        DisplayName = "Unreadable profile",
                        Valid = false
                    });
                }
            }
            return profiles;
        }

        private List<TransactionJournal> LoadPendingJournals()
        {
            List<TransactionJournal> journals = new List<TransactionJournal>();
            if (!Directory.Exists(paths.TransactionsDirectory)) return journals;
            foreach (string directory in Directory.GetDirectories(paths.TransactionsDirectory))
            {
                string journalPath = Path.Combine(directory, "journal.json");
                if (!File.Exists(journalPath)) continue;
                TransactionJournal journal = store.Read<TransactionJournal>(journalPath);
                TransactionJournalValidator.ValidateForInspection(paths, journal, Path.GetFileName(directory));
                if (!TransactionPhases.IsTerminal(journal.Phase)) journals.Add(journal);
            }
            return journals.OrderBy(delegate(TransactionJournal item) { return item.CreatedAtUtc; }, StringComparer.Ordinal).ToList();
        }

        private bool LinkTargets(string linkPath, string targetPath)
        {
            try
            {
                return junctions.Exists(linkPath) &&
                       WindowsJunctionManager.PathsEqual(junctions.ResolveTarget(linkPath), targetPath);
            }
            catch
            {
                return false;
            }
        }

        private static string SafeMessage(Exception exception)
        {
            return SafeUserMessages.ForInspection(exception);
        }

        private static bool IsSupportedOperation(string operation)
        {
            return string.Equals(operation, "switch", StringComparison.Ordinal) ||
                   string.Equals(operation, ProfileLifecycleEngine.RegisterFirstOperation, StringComparison.Ordinal) ||
                   string.Equals(operation, ProfileLifecycleEngine.BeginOnboardingOperation, StringComparison.Ordinal) ||
                   string.Equals(operation, ProfileLifecycleEngine.FinalizeOnboardingOperation, StringComparison.Ordinal) ||
                   string.Equals(operation, ProfileLifecycleEngine.CancelOnboardingOperation, StringComparison.Ordinal) ||
                   string.Equals(operation, ProfileDeletionEngine.DeleteProfileOperation, StringComparison.Ordinal);
        }

        private sealed class ProfileRecord
        {
            public string ProfileId { get; set; }
            public string DisplayName { get; set; }
            public bool Valid { get; set; }
        }
    }
}
