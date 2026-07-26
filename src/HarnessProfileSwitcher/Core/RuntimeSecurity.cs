using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HarnessProfileSwitcher.Core
{
    public sealed class RuntimeAclAudit
    {
        public bool Exists { get; set; }
        public bool Protected { get; set; }
        public bool Healthy { get; set; }
        public IList<string> Issues { get; set; }
    }

    public interface IRuntimeSecurity
    {
        void EnsureProtectedDirectory(string path);
        void ProtectFile(string path);
        void ProtectTree(string path);
        RuntimeAclAudit AuditDirectory(string path);
        RuntimeAclAudit AuditFile(string path);
    }

    public sealed class NoOpRuntimeSecurity : IRuntimeSecurity
    {
        public void EnsureProtectedDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public void ProtectFile(string path)
        {
        }

        public void ProtectTree(string path)
        {
        }

        public RuntimeAclAudit AuditDirectory(string path)
        {
            return new RuntimeAclAudit
            {
                Exists = Directory.Exists(path),
                Protected = true,
                Healthy = true,
                Issues = new string[0]
            };
        }

        public RuntimeAclAudit AuditFile(string path)
        {
            return new RuntimeAclAudit
            {
                Exists = File.Exists(path),
                Protected = true,
                Healthy = true,
                Issues = new string[0]
            };
        }
    }

    public sealed class WindowsRuntimeSecurity : IRuntimeSecurity
    {
        private readonly string runtimeRoot;
        private readonly string deletionRoot;
        private readonly SecurityIdentifier currentUser;
        private readonly SecurityIdentifier localSystem;
        private readonly SecurityIdentifier administrators;

        public WindowsRuntimeSecurity(string runtimeRoot, string deletionRoot)
        {
            this.runtimeRoot = Canonical(runtimeRoot);
            this.deletionRoot = Canonical(deletionRoot);
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
            {
                if (identity.User == null) throw new InvalidOperationException("The current Windows user SID could not be resolved.");
                currentUser = identity.User;
            }
            localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        }

        public void EnsureProtectedDirectory(string path)
        {
            path = RequireAllowedPath(path);
            string allowedRoot = AllowedRootFor(path);
            foreach (string directory in DirectoryHierarchy(allowedRoot, path))
            {
                if (File.Exists(directory)) throw new IOException("A file blocks the protected directory path: " + directory);
                Directory.CreateDirectory(directory);
                RejectReparsePoint(directory);
                ApplyDirectoryAcl(directory);
            }
        }

        public void ProtectFile(string path)
        {
            path = RequireAllowedPath(path);
            EnsureNoReparseDirectoryChain(AllowedRootFor(path), Path.GetDirectoryName(path));
            if (!File.Exists(path)) throw new FileNotFoundException("File required for ACL protection is missing.", path);
            RejectReparsePoint(path);
            ApplyFileAcl(path);
        }

        private void ApplyFileAcl(string path)
        {
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(currentUser);
            security.AddAccessRule(FileRule(currentUser));
            security.AddAccessRule(FileRule(localSystem));
            security.AddAccessRule(FileRule(administrators));
            File.SetAccessControl(path, security);
            RequireHealthyFileAcl(path);
        }

        public void ProtectTree(string path)
        {
            path = RequireAllowedPath(path);
            EnsureNoReparseDirectoryChain(AllowedRootFor(path), path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Directory required for ACL protection is missing: " + path);
            RejectReparsePoint(path);

            List<string> directories = new List<string>();
            List<string> files = new List<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                RejectReparsePoint(current);
                directories.Add(current);
                foreach (string file in Directory.GetFiles(current))
                {
                    RejectReparsePoint(file);
                    files.Add(file);
                }
                foreach (string child in Directory.GetDirectories(current))
                {
                    RejectReparsePoint(child);
                    pending.Push(child);
                }
            }

            foreach (string directory in directories)
            {
                ApplyDirectoryAcl(directory);
            }
            foreach (string file in files)
            {
                ApplyFileAcl(file);
            }
        }

        public RuntimeAclAudit AuditDirectory(string path)
        {
            path = RequireAllowedPath(path);
            EnsureNoReparseDirectoryChain(AllowedRootFor(path), path);
            if (!Directory.Exists(path))
            {
                return new RuntimeAclAudit
                {
                    Exists = false,
                    Protected = false,
                    Healthy = false,
                    Issues = new string[] { "DirectoryMissing" }
                };
            }

            DirectorySecurity security = Directory.GetAccessControl(path, AccessControlSections.Access);
            return AuditRules(security.AreAccessRulesProtected, security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        }

        public RuntimeAclAudit AuditFile(string path)
        {
            path = RequireAllowedPath(path);
            EnsureNoReparseDirectoryChain(AllowedRootFor(path), Path.GetDirectoryName(path));
            if (!File.Exists(path))
            {
                return new RuntimeAclAudit
                {
                    Exists = false,
                    Protected = false,
                    Healthy = false,
                    Issues = new string[] { "FileMissing" }
                };
            }

            RejectReparsePoint(path);
            FileSecurity security = File.GetAccessControl(path, AccessControlSections.Access);
            return AuditRules(security.AreAccessRulesProtected, security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        }

        private void ApplyDirectoryAcl(string path)
        {
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(currentUser);
            security.AddAccessRule(DirectoryRule(currentUser));
            security.AddAccessRule(DirectoryRule(localSystem));
            security.AddAccessRule(DirectoryRule(administrators));
            Directory.SetAccessControl(path, security);
            DirectorySecurity applied = Directory.GetAccessControl(path, AccessControlSections.Access);
            RuntimeAclAudit audit = AuditRules(applied.AreAccessRulesProtected, applied.GetAccessRules(true, true, typeof(SecurityIdentifier)));
            if (!audit.Healthy)
            {
                throw new UnauthorizedAccessException("Runtime directory ACL verification failed.");
            }
        }

        private void RequireHealthyFileAcl(string path)
        {
            FileSecurity security = File.GetAccessControl(path, AccessControlSections.Access);
            RuntimeAclAudit audit = AuditRules(security.AreAccessRulesProtected, security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
            if (!audit.Healthy)
            {
                throw new UnauthorizedAccessException("Runtime file ACL verification failed.");
            }
        }

        private RuntimeAclAudit AuditRules(bool rulesProtected, AuthorizationRuleCollection rules)
        {
            List<string> issues = new List<string>();
            if (!rulesProtected) issues.Add("InheritanceEnabled");
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                currentUser.Value,
                localSystem.Value,
                administrators.Value
            };
            HashSet<string> fullControl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileSystemAccessRule rule in rules)
            {
                SecurityIdentifier sid = rule.IdentityReference as SecurityIdentifier;
                if (sid == null)
                {
                    issues.Add("UnresolvedPrincipal");
                    continue;
                }
                if (rule.IsInherited) issues.Add("InheritedRule");
                if (rule.AccessControlType != AccessControlType.Allow) issues.Add("NonAllowRule");
                if (!allowed.Contains(sid.Value)) issues.Add("UnexpectedPrincipal");
                if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                {
                    fullControl.Add(sid.Value);
                }
            }
            foreach (string sid in allowed)
            {
                if (!fullControl.Contains(sid)) issues.Add("RequiredPrincipalMissingFullControl");
            }
            issues = issues.Distinct(StringComparer.Ordinal).ToList();
            return new RuntimeAclAudit
            {
                Exists = true,
                Protected = rulesProtected,
                Healthy = issues.Count == 0,
                Issues = issues
            };
        }

        private string RequireAllowedPath(string path)
        {
            string candidate = Canonical(path);
            if (AllowedRootForOrNull(candidate) != null)
            {
                return candidate;
            }
            throw new UnauthorizedAccessException("ACL changes are restricted to Harness Profile Switcher storage roots.");
        }

        private string AllowedRootFor(string path)
        {
            string root = AllowedRootForOrNull(path);
            if (root == null) throw new UnauthorizedAccessException("ACL changes are restricted to Harness Profile Switcher storage roots.");
            return root;
        }

        private string AllowedRootForOrNull(string path)
        {
            bool inRuntime = WindowsPath.EqualsPath(path, runtimeRoot) || WindowsPath.IsStrictlyInside(path, runtimeRoot);
            bool inDeletion = WindowsPath.EqualsPath(path, deletionRoot) || WindowsPath.IsStrictlyInside(path, deletionRoot);
            if (inRuntime && inDeletion) return runtimeRoot.Length >= deletionRoot.Length ? runtimeRoot : deletionRoot;
            if (inRuntime) return runtimeRoot;
            if (inDeletion) return deletionRoot;
            return null;
        }

        private static IList<string> DirectoryHierarchy(string root, string path)
        {
            List<string> hierarchy = new List<string>();
            string current = root;
            hierarchy.Add(current);
            if (WindowsPath.EqualsPath(root, path)) return hierarchy;
            string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string part in relative.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                hierarchy.Add(current);
            }
            return hierarchy;
        }

        private static void EnsureNoReparseDirectoryChain(string root, string path)
        {
            foreach (string directory in DirectoryHierarchy(root, path))
            {
                if (File.Exists(directory) && !Directory.Exists(directory))
                {
                    throw new IOException("A file blocks the protected directory path: " + directory);
                }
                if (!Directory.Exists(directory)) return;
                RejectReparsePoint(directory);
            }
        }

        private static FileSystemAccessRule DirectoryRule(SecurityIdentifier sid)
        {
            return new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);
        }

        private static FileSystemAccessRule FileRule(SecurityIdentifier sid)
        {
            return new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow);
        }

        private static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A security root path is required.", "path");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("ACL hardening does not follow reparse points: " + path);
            }
        }
    }
}
