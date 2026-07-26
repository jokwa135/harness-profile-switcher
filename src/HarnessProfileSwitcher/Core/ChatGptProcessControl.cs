using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace HarnessProfileSwitcher.Core
{
    public sealed class PackageProcessInfo
    {
        public int ProcessId { get; set; }
        public int ParentProcessId { get; set; }
        public string Name { get; set; }
        public string ExecutablePath { get; set; }
        public string CommandLine { get; set; }
        public bool IsMainDesktopProcess { get; set; }
        public bool IsCodexHelper { get; set; }
    }

    public sealed class PackageProcessSnapshot
    {
        private readonly ReadOnlyCollection<PackageProcessInfo> processes;
        private readonly ReadOnlyCollection<PackageProcessInfo> mainProcesses;

        public PackageProcessSnapshot(IEnumerable<PackageProcessInfo> processes)
        {
            List<PackageProcessInfo> all = new List<PackageProcessInfo>(processes ?? Enumerable.Empty<PackageProcessInfo>());
            all.Sort(delegate(PackageProcessInfo left, PackageProcessInfo right)
            {
                return left.ProcessId.CompareTo(right.ProcessId);
            });
            this.processes = new ReadOnlyCollection<PackageProcessInfo>(all);
            this.mainProcesses = new ReadOnlyCollection<PackageProcessInfo>(all.Where(delegate(PackageProcessInfo item)
            {
                return item.IsMainDesktopProcess;
            }).ToList());
        }

        public ReadOnlyCollection<PackageProcessInfo> Processes
        {
            get { return processes; }
        }

        public ReadOnlyCollection<PackageProcessInfo> MainProcesses
        {
            get { return mainProcesses; }
        }

        public bool IsStopped
        {
            get { return processes.Count == 0; }
        }
    }

    public interface IPackageProcessInventory
    {
        PackageProcessSnapshot Capture();
    }

    public static class ChatGptRuntimeCompatibility
    {
        private static readonly Regex UserDataDirectoryPattern = new Regex(
            "--user-data-dir=(?:\"(?<quoted>[^\"]+)\"|(?<plain>[^\\s]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static void Validate(PackageProcessSnapshot snapshot, string expectedBrowserPath)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (string.IsNullOrWhiteSpace(expectedBrowserPath)) throw new ArgumentException("Expected browser path is required.", "expectedBrowserPath");
            if (snapshot.MainProcesses.Count > 1)
            {
                throw new ChatGptCompatibilityException("Expected at most one main ChatGPT Desktop process; found " + snapshot.MainProcesses.Count + ".");
            }

            foreach (PackageProcessInfo process in snapshot.Processes)
            {
                Match match = UserDataDirectoryPattern.Match(process.CommandLine ?? string.Empty);
                if (!match.Success) continue;
                string observed = match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["plain"].Value;
                if (!WindowsPath.EqualsPath(observed, expectedBrowserPath))
                {
                    throw new ChatGptCompatibilityException("A running package process uses a different browser-state directory.");
                }
            }
        }
    }

    public sealed class WmiPackageProcessInventory : IPackageProcessInventory
    {
        private readonly ChatGptInstallation installation;

        public WmiPackageProcessInventory(ChatGptInstallation installation)
        {
            PowerShellPackageDiscovery.Validate(installation);
            this.installation = installation;
        }

        public PackageProcessSnapshot Capture()
        {
            List<PackageProcessInfo> processes = new List<PackageProcessInfo>();
            const string query = "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process";
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    using (item)
                    {
                        string executablePath = ReadString(item, "ExecutablePath");
                        if (!WindowsPath.IsStrictlyInside(executablePath, installation.InstallLocation))
                        {
                            continue;
                        }

                        string name = ReadString(item, "Name");
                        string commandLine = ReadString(item, "CommandLine");
                        bool isChatGpt = string.Equals(name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
                        processes.Add(new PackageProcessInfo
                        {
                            ProcessId = ReadInt32(item, "ProcessId"),
                            ParentProcessId = ReadInt32(item, "ParentProcessId"),
                            Name = name,
                            ExecutablePath = Path.GetFullPath(executablePath),
                            CommandLine = commandLine,
                            IsMainDesktopProcess = isChatGpt && commandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0,
                            IsCodexHelper = string.Equals(name, "codex.exe", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }
            catch (ManagementException exception)
            {
                throw new ChatGptCompatibilityException("Windows process inventory failed.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new ChatGptCompatibilityException("Windows denied access to the process inventory.", exception);
            }

            return new PackageProcessSnapshot(processes);
        }

        private static string ReadString(ManagementBaseObject item, string propertyName)
        {
            object value = item[propertyName];
            return value == null ? string.Empty : Convert.ToString(value);
        }

        private static int ReadInt32(ManagementBaseObject item, string propertyName)
        {
            object value = item[propertyName];
            return value == null ? 0 : Convert.ToInt32(value);
        }
    }

    public sealed class ChatGptProcessesRunningException : InvalidOperationException
    {
        public ChatGptProcessesRunningException(PackageProcessSnapshot snapshot)
            : base("ChatGPT Desktop is still running with " + snapshot.Processes.Count + " package-scoped process(es).")
        {
            Snapshot = snapshot;
        }

        public PackageProcessSnapshot Snapshot { get; private set; }
    }

    public sealed class CloseAttempt
    {
        public bool Stopped { get; set; }
        public PackageProcessSnapshot RemainingProcesses { get; set; }
        public string ForceConfirmationToken { get; set; }
        public DateTime? ConfirmationExpiresAtUtc { get; set; }
    }

    public sealed class ChatGptProcessController : IProcessGuard
    {
        private const int PollMilliseconds = 100;
        private readonly ChatGptInstallation installation;
        private readonly IPackageProcessInventory inventory;
        private readonly TimeSpan confirmationLifetime;
        private readonly Func<int, Process> openProcessById;
        private readonly Func<Process, string> readExecutablePath;
        private readonly Action<Process> terminateProcess;
        private readonly object confirmationSync = new object();
        private string pendingConfirmationToken;
        private DateTime pendingConfirmationExpiryUtc;

        public ChatGptProcessController(
            ChatGptInstallation installation,
            IPackageProcessInventory inventory,
            TimeSpan confirmationLifetime)
            : this(
                installation,
                inventory,
                confirmationLifetime,
                Process.GetProcessById,
                delegate(Process process) { return process.MainModule.FileName; },
                delegate(Process process) { process.Kill(); })
        {
        }

        internal ChatGptProcessController(
            ChatGptInstallation installation,
            IPackageProcessInventory inventory,
            TimeSpan confirmationLifetime,
            Func<int, Process> openProcessById,
            Func<Process, string> readExecutablePath,
            Action<Process> terminateProcess)
        {
            PowerShellPackageDiscovery.Validate(installation);
            if (inventory == null) throw new ArgumentNullException("inventory");
            if (confirmationLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("confirmationLifetime");
            if (openProcessById == null) throw new ArgumentNullException("openProcessById");
            if (readExecutablePath == null) throw new ArgumentNullException("readExecutablePath");
            if (terminateProcess == null) throw new ArgumentNullException("terminateProcess");
            this.installation = installation;
            this.inventory = inventory;
            this.confirmationLifetime = confirmationLifetime;
            this.openProcessById = openProcessById;
            this.readExecutablePath = readExecutablePath;
            this.terminateProcess = terminateProcess;
        }

        public ChatGptProcessController(ChatGptInstallation installation, IPackageProcessInventory inventory)
            : this(installation, inventory, TimeSpan.FromMinutes(1))
        {
        }

        public void EnsureStopped()
        {
            PackageProcessSnapshot snapshot = inventory.Capture();
            if (!snapshot.IsStopped)
            {
                throw new ChatGptProcessesRunningException(snapshot);
            }
        }

        public CloseAttempt TryCloseGracefully(TimeSpan timeout)
        {
            ValidateTimeout(timeout, "timeout");
            PackageProcessSnapshot snapshot = inventory.Capture();
            if (snapshot.IsStopped)
            {
                ClearConfirmation();
                return StoppedAttempt(snapshot);
            }
            if (snapshot.MainProcesses.Count > 1)
            {
                throw new ChatGptCompatibilityException("Expected at most one main ChatGPT Desktop process; found " + snapshot.MainProcesses.Count + ".");
            }

            if (snapshot.MainProcesses.Count == 1)
            {
                RequestGracefulClose(snapshot.MainProcesses[0]);
            }

            PackageProcessSnapshot remaining = WaitForStopped(timeout);
            if (remaining.IsStopped)
            {
                ClearConfirmation();
                return StoppedAttempt(remaining);
            }

            string token = CreateConfirmationToken();
            DateTime expiresAt = DateTime.UtcNow.Add(confirmationLifetime);
            lock (confirmationSync)
            {
                pendingConfirmationToken = token;
                pendingConfirmationExpiryUtc = expiresAt;
            }
            return new CloseAttempt
            {
                Stopped = false,
                RemainingProcesses = remaining,
                ForceConfirmationToken = token,
                ConfirmationExpiresAtUtc = expiresAt
            };
        }

        public PackageProcessSnapshot ForceCloseConfirmed(string confirmationToken, TimeSpan timeout)
        {
            ValidateTimeout(timeout, "timeout");
            ConsumeConfirmation(confirmationToken);
            DateTime deadline = DateTime.UtcNow.Add(timeout);

            while (true)
            {
                PackageProcessSnapshot snapshot = inventory.Capture();
                if (snapshot.IsStopped)
                {
                    return snapshot;
                }

                IEnumerable<PackageProcessInfo> ordered = snapshot.Processes
                    .OrderBy(delegate(PackageProcessInfo item) { return item.IsMainDesktopProcess ? 0 : 1; })
                    .ThenBy(delegate(PackageProcessInfo item) { return item.ProcessId; });
                foreach (PackageProcessInfo processInfo in ordered)
                {
                    KillVerified(processInfo);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }
                SleepUntil(deadline);
            }

            PackageProcessSnapshot remaining = inventory.Capture();
            if (!remaining.IsStopped)
            {
                throw new TimeoutException("Package-scoped ChatGPT processes did not stop after confirmed force-close.");
            }
            return remaining;
        }

        private void RequestGracefulClose(PackageProcessInfo processInfo)
        {
            using (Process process = OpenVerifiedProcess(processInfo))
            {
                if (process == null) return;
                try
                {
                    process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private void KillVerified(PackageProcessInfo processInfo)
        {
            using (Process process = OpenVerifiedProcess(processInfo))
            {
                if (process == null) return;
                try
                {
                    terminateProcess(process);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception exception)
                {
                    if (ExitedDuringVerification(process)) return;
                    throw new ChatGptCompatibilityException("Windows refused to terminate package process " + processInfo.ProcessId + ".", exception);
                }
            }
        }

        private Process OpenVerifiedProcess(PackageProcessInfo processInfo)
        {
            Process process;
            try
            {
                process = openProcessById(processInfo.ProcessId);
            }
            catch (ArgumentException)
            {
                return null;
            }

            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    return null;
                }

                string currentPath = readExecutablePath(process);
                if (!WindowsPath.IsStrictlyInside(currentPath, installation.InstallLocation) ||
                    !WindowsPath.EqualsPath(currentPath, processInfo.ExecutablePath))
                {
                    process.Dispose();
                    throw new ChatGptCompatibilityException("Process identity changed before process control action; refusing PID " + processInfo.ProcessId + ".");
                }
                return process;
            }
            catch (Win32Exception exception)
            {
                if (ExitedDuringVerification(process))
                {
                    process.Dispose();
                    return null;
                }
                process.Dispose();
                throw new ChatGptCompatibilityException("Windows denied executable verification for process " + processInfo.ProcessId + ".", exception);
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
                return null;
            }
        }

        private static bool ExitedDuringVerification(Process process)
        {
            try
            {
                process.Refresh();
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        private PackageProcessSnapshot WaitForStopped(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (true)
            {
                PackageProcessSnapshot snapshot = inventory.Capture();
                if (snapshot.IsStopped || DateTime.UtcNow >= deadline)
                {
                    return snapshot;
                }
                SleepUntil(deadline);
            }
        }

        private static void SleepUntil(DateTime deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            int milliseconds = (int)Math.Min(PollMilliseconds, Math.Ceiling(remaining.TotalMilliseconds));
            if (milliseconds > 0) Thread.Sleep(milliseconds);
        }

        private void ConsumeConfirmation(string confirmationToken)
        {
            if (string.IsNullOrWhiteSpace(confirmationToken))
            {
                throw new UnauthorizedAccessException("A force-close confirmation token is required.");
            }

            lock (confirmationSync)
            {
                if (pendingConfirmationToken == null)
                {
                    throw new UnauthorizedAccessException("No force-close confirmation is pending.");
                }
                if (DateTime.UtcNow > pendingConfirmationExpiryUtc)
                {
                    pendingConfirmationToken = null;
                    throw new UnauthorizedAccessException("The force-close confirmation expired.");
                }
                if (!string.Equals(pendingConfirmationToken, confirmationToken, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("The force-close confirmation token is invalid.");
                }
                pendingConfirmationToken = null;
            }
        }

        private void ClearConfirmation()
        {
            lock (confirmationSync)
            {
                pendingConfirmationToken = null;
                pendingConfirmationExpiryUtc = DateTime.MinValue;
            }
        }

        private static CloseAttempt StoppedAttempt(PackageProcessSnapshot snapshot)
        {
            return new CloseAttempt
            {
                Stopped = true,
                RemainingProcesses = snapshot
            };
        }

        private static string CreateConfirmationToken()
        {
            byte[] bytes = new byte[18];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static void ValidateTimeout(TimeSpan timeout, string parameterName)
        {
            if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    public interface IAppActivator
    {
        void Activate(ChatGptInstallation installation);
    }

    public sealed class ExplorerAppActivator : IAppActivator
    {
        public void Activate(ChatGptInstallation installation)
        {
            PowerShellPackageDiscovery.Validate(installation);
            string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!File.Exists(explorerPath))
            {
                throw new FileNotFoundException("Windows Explorer was not found.", explorerPath);
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = explorerPath,
                Arguments = "shell:AppsFolder\\" + installation.AppUserModelId,
                UseShellExecute = true
            };
            Process process = Process.Start(startInfo);
            if (process != null) process.Dispose();
        }
    }

    public sealed class ChatGptLauncher
    {
        private const int PollMilliseconds = 100;
        private readonly ChatGptInstallation installation;
        private readonly IPackageProcessInventory inventory;
        private readonly IAppActivator activator;

        public ChatGptLauncher(
            ChatGptInstallation installation,
            IPackageProcessInventory inventory,
            IAppActivator activator)
        {
            PowerShellPackageDiscovery.Validate(installation);
            if (inventory == null) throw new ArgumentNullException("inventory");
            if (activator == null) throw new ArgumentNullException("activator");
            this.installation = installation;
            this.inventory = inventory;
            this.activator = activator;
        }

        public PackageProcessSnapshot LaunchAndWait(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException("timeout");
            }

            PackageProcessSnapshot before = inventory.Capture();
            if (!before.IsStopped)
            {
                throw new ChatGptProcessesRunningException(before);
            }

            activator.Activate(installation);
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            PackageProcessSnapshot latest = before;
            while (DateTime.UtcNow < deadline)
            {
                latest = inventory.Capture();
                if (latest.MainProcesses.Count > 1)
                {
                    throw new ChatGptCompatibilityException("Launch created more than one main ChatGPT Desktop process.");
                }
                if (latest.MainProcesses.Count == 1)
                {
                    return latest;
                }
                Thread.Sleep(PollMilliseconds);
            }

            latest = inventory.Capture();
            if (latest.MainProcesses.Count == 1)
            {
                return latest;
            }
            if (latest.MainProcesses.Count > 1)
            {
                throw new ChatGptCompatibilityException("Launch created more than one main ChatGPT Desktop process.");
            }
            throw new TimeoutException("ChatGPT Desktop launch was not observed before the timeout.");
        }
    }
}
