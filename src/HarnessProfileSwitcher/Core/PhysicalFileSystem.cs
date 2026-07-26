using System;
using System.IO;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisualBasicFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;

namespace HarnessProfileSwitcher.Core
{
    public sealed class FileFingerprint
    {
        public long Length { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class DirectoryFingerprint
    {
        public long FileCount { get; set; }
        public long DirectoryCount { get; set; }
        public long TotalLength { get; set; }
        public string TreeSha256 { get; set; }

        public bool EqualsFingerprint(DirectoryFingerprint other)
        {
            return other != null &&
                   FileCount == other.FileCount &&
                   DirectoryCount == other.DirectoryCount &&
                   TotalLength == other.TotalLength &&
                   string.Equals(TreeSha256, other.TreeSha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    public interface IVerifiedDirectoryMover
    {
        DirectoryFingerprint Fingerprint(string path);
        void MoveDirectory(string sourcePath, string destinationPath);
        void DeleteDirectory(string path);
    }

    public sealed class VerifiedDirectoryMover : IVerifiedDirectoryMover
    {
        public DirectoryFingerprint Fingerprint(string path)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException("Directory required for integrity verification is missing: " + path);
            }
            RejectReparsePoint(path);

            List<DirectoryManifestEntry> entries = new List<DirectoryManifestEntry>();
            Stack<string> pending = new Stack<string>();
            pending.Push(path);
            long directoryCount = 0;
            long fileCount = 0;
            long totalLength = 0;

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                RejectReparsePoint(current);
                directoryCount++;
                if (!WindowsPath.EqualsPath(current, path))
                {
                    entries.Add(new DirectoryManifestEntry
                    {
                        Record = "D|" + RelativePath(path, current)
                    });
                }

                foreach (string childDirectory in Directory.GetDirectories(current))
                {
                    RejectReparsePoint(childDirectory);
                    pending.Push(childDirectory);
                }
                foreach (string file in Directory.GetFiles(current))
                {
                    RejectReparsePoint(file);
                    FileInfo info = new FileInfo(file);
                    string fileHash;
                    using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (SHA256 algorithm = SHA256.Create())
                    {
                        fileHash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
                    }
                    fileCount++;
                    totalLength += info.Length;
                    entries.Add(new DirectoryManifestEntry
                    {
                        Record = "F|" + RelativePath(path, file) + "|" + info.Length + "|" + fileHash
                    });
                }
            }

            entries.Sort(delegate(DirectoryManifestEntry left, DirectoryManifestEntry right)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(left.Record, right.Record);
            });
            string treeHash;
            using (SHA256 algorithm = SHA256.Create())
            using (MemoryStream manifest = new MemoryStream())
            {
                foreach (DirectoryManifestEntry entry in entries)
                {
                    byte[] record = Encoding.UTF8.GetBytes(entry.Record + "\n");
                    manifest.Write(record, 0, record.Length);
                }
                manifest.Position = 0;
                treeHash = BitConverter.ToString(algorithm.ComputeHash(manifest)).Replace("-", string.Empty);
            }

            return new DirectoryFingerprint
            {
                FileCount = fileCount,
                DirectoryCount = directoryCount,
                TotalLength = totalLength,
                TreeSha256 = treeHash
            };
        }

        public void MoveDirectory(string sourcePath, string destinationPath)
        {
            sourcePath = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
            destinationPath = Path.GetFullPath(destinationPath).TrimEnd(Path.DirectorySeparatorChar);
            if (WindowsPath.EqualsPath(sourcePath, destinationPath))
            {
                throw new InvalidOperationException("Directory move source and destination are identical.");
            }
            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException("Directory move source is missing: " + sourcePath);
            }
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                throw new IOException("Directory move destination already exists: " + destinationPath);
            }

            DirectoryFingerprint before = Fingerprint(sourcePath);
            RequireCrossVolumeCapacity(sourcePath, destinationPath, before.TotalLength);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            VisualBasicFileSystem.MoveDirectory(sourcePath, destinationPath);
            if (Directory.Exists(sourcePath))
            {
                throw new IOException("Verified directory move left the source in place: " + sourcePath);
            }
            if (!Directory.Exists(destinationPath))
            {
                throw new IOException("Verified directory move did not create the destination: " + destinationPath);
            }
            DirectoryFingerprint after = Fingerprint(destinationPath);
            if (!before.EqualsFingerprint(after))
            {
                throw new IOException("Directory integrity changed during move to: " + destinationPath);
            }
        }

        public void DeleteDirectory(string path)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(path))
            {
                return;
            }

            Fingerprint(path);
            Directory.Delete(path, true);
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new IOException("Verified directory deletion left the path in place: " + path);
            }
        }

        private static void RejectReparsePoint(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Directory moves do not follow reparse points: " + path);
            }
        }

        private static void RequireCrossVolumeCapacity(string sourcePath, string destinationPath, long contentLength)
        {
            string sourceRoot = Path.GetPathRoot(sourcePath);
            string destinationRoot = Path.GetPathRoot(destinationPath);
            if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase)) return;

            DriveInfo destinationDrive = new DriveInfo(destinationRoot);
            if (!destinationDrive.IsReady)
            {
                throw new IOException("Destination drive is not ready: " + destinationRoot);
            }
            const long safetyReserve = 64L * 1024L * 1024L;
            long required;
            try
            {
                required = checked(contentLength + safetyReserve);
            }
            catch (OverflowException)
            {
                throw new IOException("Directory size exceeds the supported capacity check.");
            }
            if (destinationDrive.AvailableFreeSpace < required)
            {
                throw new IOException("Destination drive does not have enough free space for the verified directory transfer.");
            }
        }

        private static string RelativePath(string root, string path)
        {
            return path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private sealed class DirectoryManifestEntry
        {
            public string Record { get; set; }
        }
    }

    public interface IVerifiedFileMover
    {
        FileFingerprint Fingerprint(string path);
        void MoveFile(string sourcePath, string destinationPath);
    }

    public sealed class VerifiedFileMover : IVerifiedFileMover
    {
        public FileFingerprint Fingerprint(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("File required for integrity verification is missing.", path);
            }

            FileInfo info = new FileInfo(path);
            string hash;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 algorithm = SHA256.Create())
            {
                hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }

            return new FileFingerprint
            {
                Length = info.Length,
                Sha256 = hash
            };
        }

        public void MoveFile(string sourcePath, string destinationPath)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Source and destination paths are identical.");
            }
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Move source is missing.", sourcePath);
            }
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                throw new IOException("Move destination already exists: " + destinationPath);
            }

            string parent = Path.GetDirectoryName(destinationPath);
            Directory.CreateDirectory(parent);
            FileFingerprint before = Fingerprint(sourcePath);

            VisualBasicFileSystem.MoveFile(sourcePath, destinationPath);

            if (File.Exists(sourcePath))
            {
                throw new IOException("Verified move left the source file in place: " + sourcePath);
            }
            if (!File.Exists(destinationPath))
            {
                throw new IOException("Verified move did not create the destination file: " + destinationPath);
            }

            FileFingerprint after = Fingerprint(destinationPath);
            if (before.Length != after.Length || !string.Equals(before.Sha256, after.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("File integrity changed during move to: " + destinationPath);
            }
        }
    }
}
