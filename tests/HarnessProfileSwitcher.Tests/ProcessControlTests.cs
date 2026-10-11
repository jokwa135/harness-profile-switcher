using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal static class ProcessControlTests
    {
        public static int Run()
        {
            TestInstalledPackageDiscoveryIsReadOnly();
            TestRuntimeCompatibilityGuardsBrowserPathAndMainCount();
            TestForceCloseIsConfirmationGatedAndPackageScoped();
            TestForceCloseToleratesExitDuringIdentityVerification();
            TestLauncherUsesStableAppIdentityContract();
            return 5;
        }

        private static void TestRuntimeCompatibilityGuardsBrowserPathAndMainCount()
        {
            string expected = @"D:\fixtures\browser";
            PackageProcessInfo main = new PackageProcessInfo
            {
                ProcessId = 10,
                Name = "ChatGPT.exe",
                ExecutablePath = @"D:\fixtures\package\ChatGPT.exe",
                CommandLine = "ChatGPT.exe --user-data-dir=\"" + expected + "\"",
                IsMainDesktopProcess = true
            };
            ChatGptRuntimeCompatibility.Validate(new PackageProcessSnapshot(new PackageProcessInfo[] { main }), expected);

            PackageProcessInfo wrongBrowser = new PackageProcessInfo
            {
                ProcessId = 11,
                Name = "ChatGPT.exe",
                ExecutablePath = main.ExecutablePath,
                CommandLine = @"ChatGPT.exe --user-data-dir=D:\fixtures\other",
                IsMainDesktopProcess = true
            };
            AssertThrows<ChatGptCompatibilityException>(delegate
            {
                ChatGptRuntimeCompatibility.Validate(new PackageProcessSnapshot(new PackageProcessInfo[] { wrongBrowser }), expected);
            }, "A mismatched browser-state directory was accepted.");

            PackageProcessInfo secondMain = new PackageProcessInfo
            {
                ProcessId = 12,
                Name = "ChatGPT.exe",
                ExecutablePath = main.ExecutablePath,
                CommandLine = main.CommandLine,
                IsMainDesktopProcess = true
            };
            AssertThrows<ChatGptCompatibilityException>(delegate
            {
                ChatGptRuntimeCompatibility.Validate(new PackageProcessSnapshot(new PackageProcessInfo[] { main, secondMain }), expected);
            }, "More than one main ChatGPT process was accepted.");
        }

        private static void TestInstalledPackageDiscoveryIsReadOnly()
        {
            ChatGptInstallation installation = new PowerShellPackageDiscovery().Discover();
            AssertEqual(PowerShellPackageDiscovery.ExpectedPackageName, installation.PackageName, "Package discovery returned the wrong package.");
            AssertEqual(PowerShellPackageDiscovery.ExpectedPackageFamily, installation.PackageFamily, "Package discovery returned the wrong family.");
            AssertEqual(PowerShellPackageDiscovery.ExpectedApplicationId, installation.ApplicationId, "Package discovery returned the wrong application ID.");
            AssertEqual(installation.PackageFamily + "!" + installation.ApplicationId, installation.AppUserModelId, "Package discovery returned the wrong AppUserModelID.");
            AssertTrue(installation.AppUserModelIdRegistered, "The discovered AppUserModelID is not registered.");
            AssertTrue(File.Exists(installation.ExecutablePath), "The discovered ChatGPT executable does not exist.");

            PackageProcessSnapshot snapshot = new WmiPackageProcessInventory(installation).Capture();
            AssertTrue(snapshot.MainProcesses.Count <= 1, "The live package has more than one main ChatGPT process.");
            foreach (PackageProcessInfo process in snapshot.Processes)
            {
                string packagePrefix = installation.InstallLocation.TrimEnd('\\') + "\\";
                AssertTrue(process.ExecutablePath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase), "Live inventory included a process outside the package directory.");
            }
        }

        private static void TestForceCloseIsConfirmationGatedAndPackageScoped()
        {
            string fakePackagePath = RequiredEnvironmentPath("HPS_TEST_FAKE_PACKAGE_EXE");
            string outsideCodexPath = RequiredEnvironmentPath("HPS_TEST_OUTSIDE_CODEX_EXE");
            ChatGptInstallation installation = CreateTestInstallation(fakePackagePath);
            WmiPackageProcessInventory inventory = new WmiPackageProcessInventory(installation);
            ChatGptProcessController controller = new ChatGptProcessController(installation, inventory, TimeSpan.FromSeconds(30));
            Process packageProcess = null;
            Process outsideProcess = null;

            try
            {
                packageProcess = StartHidden(fakePackagePath);
                outsideProcess = StartHidden(outsideCodexPath);
                WaitUntil(delegate
                {
                    PackageProcessSnapshot current = inventory.Capture();
                    return current.Processes.Count == 1 && current.Processes[0].ProcessId == packageProcess.Id;
                }, TimeSpan.FromSeconds(5), "The fake package process was not observed by WMI.");

                PackageProcessSnapshot initial = inventory.Capture();
                AssertEqual(1, initial.Processes.Count, "Package inventory included an out-of-package process.");
                AssertEqual(packageProcess.Id, initial.Processes[0].ProcessId, "Package inventory identified the wrong process.");
                AssertEqual(1, initial.MainProcesses.Count, "The fake ChatGPT process was not classified as the main process.");
                AssertThrows<ChatGptProcessesRunningException>(delegate { controller.EnsureStopped(); }, "EnsureStopped accepted a running package process.");

                CloseAttempt attempt = controller.TryCloseGracefully(TimeSpan.FromMilliseconds(300));
                AssertTrue(!attempt.Stopped, "A windowless fake process unexpectedly closed gracefully.");
                AssertTrue(!string.IsNullOrWhiteSpace(attempt.ForceConfirmationToken), "Force-close confirmation token was not issued.");
                AssertTrue(attempt.ConfirmationExpiresAtUtc.HasValue, "Force-close confirmation expiry was not issued.");

                AssertThrows<UnauthorizedAccessException>(delegate
                {
                    controller.ForceCloseConfirmed("wrong-token", TimeSpan.FromSeconds(5));
                }, "An invalid force-close confirmation token was accepted.");
                AssertProcessRunning(packageProcess, "An invalid token terminated the package process.");
                AssertProcessRunning(outsideProcess, "An invalid token terminated the outside codex process.");

                PackageProcessSnapshot stopped = controller.ForceCloseConfirmed(attempt.ForceConfirmationToken, TimeSpan.FromSeconds(5));
                AssertTrue(stopped.IsStopped, "Confirmed force-close left a package process running.");
                AssertTrue(packageProcess.WaitForExit(5000), "The fake package process did not exit.");
                AssertProcessRunning(outsideProcess, "Package-scoped force-close terminated an outside codex process.");
                controller.EnsureStopped();

                AssertThrows<UnauthorizedAccessException>(delegate
                {
                    controller.ForceCloseConfirmed(attempt.ForceConfirmationToken, TimeSpan.FromSeconds(1));
                }, "A force-close confirmation token was reusable.");
            }
            finally
            {
                StopTrackedProcess(packageProcess);
                StopTrackedProcess(outsideProcess);
            }
        }

        private static void TestLauncherUsesStableAppIdentityContract()
        {
            string fakePackagePath = RequiredEnvironmentPath("HPS_TEST_FAKE_PACKAGE_EXE");
            ChatGptInstallation installation = CreateTestInstallation(fakePackagePath);
            FakeActivator activator = new FakeActivator();
            ActivationInventory inventory = new ActivationInventory(activator, fakePackagePath);
            ChatGptLauncher launcher = new ChatGptLauncher(installation, inventory, activator);

            PackageProcessSnapshot observed = launcher.LaunchAndWait(TimeSpan.FromSeconds(1));
            AssertEqual(1, activator.ActivationCount, "The launcher did not invoke the AppUserModelID activator exactly once.");
            AssertEqual(1, observed.MainProcesses.Count, "The launcher did not observe exactly one main process.");
            AssertEqual(installation.AppUserModelId, activator.LastAppUserModelId, "The launcher did not pass the stable AppUserModelID.");
        }

        private static void TestForceCloseToleratesExitDuringIdentityVerification()
        {
            string fakePackagePath = RequiredEnvironmentPath("HPS_TEST_FAKE_PACKAGE_EXE");
            ChatGptInstallation installation = CreateTestInstallation(fakePackagePath);
            WmiPackageProcessInventory inventory = new WmiPackageProcessInventory(installation);
            Process packageProcess = null;
            bool simulateExitRace = false;
            try
            {
                packageProcess = StartHidden(fakePackagePath);
                WaitUntil(delegate
                {
                    PackageProcessSnapshot current = inventory.Capture();
                    return current.Processes.Count == 1 && current.Processes[0].ProcessId == packageProcess.Id;
                }, TimeSpan.FromSeconds(5), "The race fixture process was not observed by WMI.");

                ChatGptProcessController controller = new ChatGptProcessController(
                    installation,
                    inventory,
                    TimeSpan.FromSeconds(30),
                    Process.GetProcessById,
                    delegate(Process process)
                    {
                        if (simulateExitRace && process.Id == packageProcess.Id)
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                            throw new Win32Exception(5, "Simulated process-exit race during executable verification.");
                        }
                        return process.MainModule.FileName;
                    },
                    delegate(Process process) { process.Kill(); });

                CloseAttempt attempt = controller.TryCloseGracefully(TimeSpan.FromMilliseconds(300));
                AssertTrue(!attempt.Stopped, "The race fixture unexpectedly closed gracefully.");
                simulateExitRace = true;
                PackageProcessSnapshot stopped = controller.ForceCloseConfirmed(attempt.ForceConfirmationToken, TimeSpan.FromSeconds(5));
                AssertTrue(stopped.IsStopped, "A process exit during identity verification was treated as a compatibility failure.");
            }
            finally
            {
                StopTrackedProcess(packageProcess);
            }
        }

        private static ChatGptInstallation CreateTestInstallation(string executablePath)
        {
            string installLocation = Path.GetDirectoryName(executablePath);
            return new ChatGptInstallation
            {
                PackageName = PowerShellPackageDiscovery.ExpectedPackageName,
                PackageVersion = "fixture-version",
                PackageFamily = PowerShellPackageDiscovery.ExpectedPackageFamily,
                InstallLocation = installLocation,
                ApplicationId = PowerShellPackageDiscovery.ExpectedApplicationId,
                ManifestExecutable = Path.GetFileName(executablePath),
                ExecutablePath = executablePath,
                EntryPoint = PowerShellPackageDiscovery.ExpectedEntryPoint,
                AppUserModelId = PowerShellPackageDiscovery.ExpectedPackageFamily + "!" + PowerShellPackageDiscovery.ExpectedApplicationId,
                AppUserModelIdRegistered = true
            };
        }

        private static Process StartHidden(string executablePath)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process process = Process.Start(startInfo);
            if (process == null) throw new Exception("Failed to start fake process: " + executablePath);
            return process;
        }

        private static string RequiredEnvironmentPath(string variableName)
        {
            return FixtureWorkspace.HelperExecutable(variableName);
        }

        private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string failureMessage)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                Thread.Sleep(100);
            }
            if (!condition()) throw new Exception(failureMessage);
        }

        private static void AssertProcessRunning(Process process, string message)
        {
            process.Refresh();
            if (process.HasExited) throw new Exception(message);
        }

        private static void StopTrackedProcess(Process process)
        {
            if (process == null) return;
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new Exception(message);
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new Exception(message + " Expected '" + expected + "', found '" + actual + "'.");
            }
        }

        private static void AssertEqual(int expected, int actual, string message)
        {
            if (expected != actual)
            {
                throw new Exception(message + " Expected " + expected + ", found " + actual + ".");
            }
        }

        private sealed class FakeActivator : IAppActivator
        {
            public int ActivationCount { get; private set; }
            public string LastAppUserModelId { get; private set; }

            public void Activate(ChatGptInstallation installation)
            {
                ActivationCount++;
                LastAppUserModelId = installation.AppUserModelId;
            }
        }

        private sealed class ActivationInventory : IPackageProcessInventory
        {
            private readonly FakeActivator activator;
            private readonly string executablePath;

            public ActivationInventory(FakeActivator activator, string executablePath)
            {
                this.activator = activator;
                this.executablePath = executablePath;
            }

            public PackageProcessSnapshot Capture()
            {
                if (activator.ActivationCount == 0)
                {
                    return new PackageProcessSnapshot(null);
                }
                return new PackageProcessSnapshot(new PackageProcessInfo[]
                {
                    new PackageProcessInfo
                    {
                        ProcessId = 12345,
                        ParentProcessId = 0,
                        Name = "ChatGPT.exe",
                        ExecutablePath = executablePath,
                        CommandLine = "ChatGPT.exe",
                        IsMainDesktopProcess = true
                    }
                });
            }
        }
    }
}
