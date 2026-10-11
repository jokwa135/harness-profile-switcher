using System;
using System.IO;
using System.Linq;

namespace HarnessProfileSwitcher.Core
{
    // Deliberately has no process controller, launcher, credential reader or file mover.
    public sealed class ProfileMetadataEditor
    {
        private readonly EnginePaths paths;
        private readonly AtomicJsonStore store;
        private readonly IJunctionManager junctions;
        private readonly IRuntimeSecurity security;
        private readonly IFaultInjector faults;

        public ProfileMetadataEditor(EnginePaths paths, AtomicJsonStore store, IJunctionManager junctions,
            IRuntimeSecurity security, IFaultInjector faults)
        {
            this.paths = paths;
            this.store = store;
            this.junctions = junctions;
            this.security = security;
            this.faults = faults;
        }

        public static string ValidateName(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            name = name.Trim();
            if (name.Length == 0) throw new ArgumentException("Enter a profile name.", "name");
            if (name.Any(char.IsControl)) throw new ArgumentException("Names cannot contain control characters.", "name");
            return name;
        }

        public bool Rename(string profileId, string displayName)
        {
            profileId = EnginePaths.NormalizeProfileId(profileId);
            displayName = ValidateName(displayName);
            RejectReparseChain(paths.StateDirectory);
            using (OperationLease.Acquire(paths))
            {
                ApplicationStateSnapshot state = new ApplicationStateInspector(paths, store, junctions).Inspect();
                ProfileSummary profile = state.Profiles == null ? null : state.Profiles.FirstOrDefault(
                    delegate(ProfileSummary item) { return item.ProfileId == profileId; });
                if (state.Mode != HarnessMode.Active || profile == null || !profile.CanRename)
                    throw new InvalidOperationException("Renaming requires healthy saved profiles with no pending operation.");

                string metadataPath = paths.ProfileMetadataPath(profileId);
                RejectReparseChain(paths.ProfileDirectory(profileId));
                if ((File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Profile metadata must be a regular file.");
                RequireHealthy(security.AuditDirectory(paths.ProfileDirectory(profileId)));
                RequireHealthy(security.AuditFile(metadataPath));
                ProfileMetadata metadata = store.Read<ProfileMetadata>(metadataPath);
                if (metadata.SchemaVersion != 1 || !string.Equals(metadata.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Profile metadata does not match its slot.");
                if (string.Equals(metadata.DisplayName, displayName, StringComparison.Ordinal)) return false;
                metadata.DisplayName = displayName;
                faults.Checkpoint("RenameBeforeAtomicWrite");
                store.Write(metadataPath, metadata);
                security.ProtectFile(metadataPath);
                return true;
            }
        }

        private void RejectReparseChain(string directory)
        {
            string root = Path.GetFullPath(paths.RuntimeRoot).TrimEnd('\\');
            string current = Path.GetFullPath(directory).TrimEnd('\\');
            while (current.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Metadata directories must exist and cannot be reparse points.");
                RequireHealthy(security.AuditDirectory(current));
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return;
                current = Path.GetDirectoryName(current);
            }
            throw new IOException("Metadata path is outside the runtime root.");
        }

        private static void RequireHealthy(RuntimeAclAudit audit)
        {
            if (audit == null || !audit.Exists || !audit.Healthy || !audit.Protected)
                throw new UnauthorizedAccessException("Profile metadata security needs attention.");
        }
    }
}
