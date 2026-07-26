using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HarnessProfileSwitcher.Core
{
    public interface IJunctionManager
    {
        bool Exists(string path);
        string ResolveTarget(string path);
        void Create(string junctionPath, string targetPath);
        void Move(string sourcePath, string destinationPath);
        void Delete(string path);
    }

    public sealed class WindowsJunctionManager : IJunctionManager
    {
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

        public bool Exists(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Directory) != 0 &&
                       (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        public string ResolveTarget(string path)
        {
            path = Path.GetFullPath(path);
            if (!Exists(path))
            {
                throw new IOException("Expected directory junction is missing: " + path);
            }

            using (SafeFileHandle handle = CreateFile(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open junction: " + path);
                }

                StringBuilder buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve junction: " + path);
                }
                if (length >= buffer.Capacity)
                {
                    buffer = new StringBuilder((int)length + 1);
                    length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                    if (length == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve junction: " + path);
                    }
                }

                return NormalizeFinalPath(buffer.ToString());
            }
        }

        public void Create(string junctionPath, string targetPath)
        {
            junctionPath = Path.GetFullPath(junctionPath);
            targetPath = Path.GetFullPath(targetPath);
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException("Junction target is missing: " + targetPath);
            }
            if (File.Exists(junctionPath) || Directory.Exists(junctionPath) || Exists(junctionPath))
            {
                throw new IOException("Junction path already exists: " + junctionPath);
            }

            ValidateCmdPath(junctionPath);
            ValidateCmdPath(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(junctionPath));

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec");
            startInfo.Arguments = "/d /c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            string output;
            string error;
            int exitCode;
            using (Process process = Process.Start(startInfo))
            {
                output = process.StandardOutput.ReadToEnd();
                error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            if (exitCode != 0)
            {
                throw new IOException("Junction creation failed with exit code " + exitCode + ": " + error + output);
            }
            VerifyTarget(junctionPath, targetPath);
        }

        public void Move(string sourcePath, string destinationPath)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);
            if (!Exists(sourcePath))
            {
                throw new IOException("Junction move source is missing: " + sourcePath);
            }
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath) || Exists(destinationPath))
            {
                throw new IOException("Junction move destination exists: " + destinationPath);
            }

            string expectedTarget = ResolveTarget(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            Directory.Move(sourcePath, destinationPath);
            VerifyTarget(destinationPath, expectedTarget);
        }

        public void Delete(string path)
        {
            path = Path.GetFullPath(path);
            if (!Exists(path))
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new IOException("Expected junction path contains a non-junction entry: " + path);
                }
                return;
            }

            Directory.Delete(path, false);
            if (Exists(path) || File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException("Junction deletion left the path in place: " + path);
            }
        }

        private void VerifyTarget(string junctionPath, string expectedTarget)
        {
            string actualTarget = ResolveTarget(junctionPath);
            if (!PathsEqual(actualTarget, expectedTarget))
            {
                throw new IOException("Junction target mismatch. Expected " + expectedTarget + ", found " + actualTarget + ".");
            }
        }

        public static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string localPrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                path = @"\\" + path.Substring(uncPrefix.Length);
            }
            else if (path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(localPrefix.Length);
            }
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }

        private static void ValidateCmdPath(string path)
        {
            if (path.IndexOf('"') >= 0 || path.IndexOf('%') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0)
            {
                throw new ArgumentException("Path contains characters that are unsafe for junction creation: " + path);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);
    }
}
