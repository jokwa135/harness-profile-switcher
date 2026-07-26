using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HarnessProfileSwitcher.Core
{
    public sealed class ChatGptCompatibilityException : InvalidOperationException
    {
        public ChatGptCompatibilityException(string message)
            : base(message)
        {
        }

        public ChatGptCompatibilityException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class ChatGptInstallation
    {
        public string PackageName { get; set; }
        public string PackageVersion { get; set; }
        public string PackageFamily { get; set; }
        public string InstallLocation { get; set; }
        public string ApplicationId { get; set; }
        public string ManifestExecutable { get; set; }
        public string ExecutablePath { get; set; }
        public string EntryPoint { get; set; }
        public string AppUserModelId { get; set; }
        public bool AppUserModelIdRegistered { get; set; }
    }

    public interface IChatGptInstallationDiscovery
    {
        ChatGptInstallation Discover();
    }

    public sealed class PowerShellPackageDiscovery : IChatGptInstallationDiscovery
    {
        public const string ExpectedPackageName = "OpenAI.Codex";
        public const string ExpectedPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        public const string ExpectedApplicationId = "App";
        public const string ExpectedEntryPoint = "Windows.FullTrustApplication";
        private const int DiscoveryTimeoutMilliseconds = 30000;

        public ChatGptInstallation Discover()
        {
            string output = ExecuteDiscoveryScript();
            PackageDiscoveryPayload payload = DeserializePayload(output);
            ChatGptInstallation installation = new ChatGptInstallation
            {
                PackageName = payload.PackageName,
                PackageVersion = payload.PackageVersion,
                PackageFamily = payload.PackageFamily,
                InstallLocation = CanonicalPath(payload.InstallLocation, "package install location"),
                ApplicationId = payload.ApplicationId,
                ManifestExecutable = payload.ManifestExecutable,
                EntryPoint = payload.EntryPoint,
                AppUserModelId = payload.AppUserModelId,
                AppUserModelIdRegistered = payload.AppUserModelIdRegistered
            };

            ValidateIdentity(installation);
            string normalizedManifestPath = installation.ManifestExecutable.Replace('/', Path.DirectorySeparatorChar);
            installation.ExecutablePath = Path.GetFullPath(Path.Combine(installation.InstallLocation, normalizedManifestPath));
            ValidateExecutable(installation);
            return installation;
        }

        public static void Validate(ChatGptInstallation installation)
        {
            if (installation == null) throw new ArgumentNullException("installation");
            installation.InstallLocation = CanonicalPath(installation.InstallLocation, "package install location");
            installation.ExecutablePath = CanonicalPath(installation.ExecutablePath, "ChatGPT executable path");
            ValidateIdentity(installation);
            ValidateExecutable(installation);
        }

        private static void ValidateIdentity(ChatGptInstallation installation)
        {
            if (!string.Equals(installation.PackageName, ExpectedPackageName, StringComparison.Ordinal))
            {
                throw new ChatGptCompatibilityException("Unexpected Store package name: " + installation.PackageName);
            }
            if (string.IsNullOrWhiteSpace(installation.PackageVersion))
            {
                throw new ChatGptCompatibilityException("The Store package version is missing.");
            }
            if (!string.Equals(installation.PackageFamily, ExpectedPackageFamily, StringComparison.Ordinal))
            {
                throw new ChatGptCompatibilityException("The Store package family changed: " + installation.PackageFamily);
            }
            if (!string.Equals(installation.ApplicationId, ExpectedApplicationId, StringComparison.Ordinal))
            {
                throw new ChatGptCompatibilityException("The manifest application ID changed: " + installation.ApplicationId);
            }
            if (!string.Equals(installation.EntryPoint, ExpectedEntryPoint, StringComparison.Ordinal))
            {
                throw new ChatGptCompatibilityException("The manifest entry point changed: " + installation.EntryPoint);
            }

            string expectedAppUserModelId = installation.PackageFamily + "!" + installation.ApplicationId;
            if (!string.Equals(installation.AppUserModelId, expectedAppUserModelId, StringComparison.Ordinal))
            {
                throw new ChatGptCompatibilityException("The AppUserModelID changed: " + installation.AppUserModelId);
            }
            if (!installation.AppUserModelIdRegistered)
            {
                throw new ChatGptCompatibilityException("The AppUserModelID is not registered: " + installation.AppUserModelId);
            }
            if (string.IsNullOrWhiteSpace(installation.ManifestExecutable) ||
                !string.Equals(Path.GetFileName(installation.ManifestExecutable.Replace('/', Path.DirectorySeparatorChar)), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChatGptCompatibilityException("The manifest executable is no longer ChatGPT.exe: " + installation.ManifestExecutable);
            }
        }

        private static void ValidateExecutable(ChatGptInstallation installation)
        {
            if (!Directory.Exists(installation.InstallLocation))
            {
                throw new ChatGptCompatibilityException("The Store package directory does not exist: " + installation.InstallLocation);
            }
            if (!WindowsPath.IsStrictlyInside(installation.ExecutablePath, installation.InstallLocation))
            {
                throw new ChatGptCompatibilityException("The manifest executable escapes the Store package directory: " + installation.ExecutablePath);
            }
            if (!string.Equals(Path.GetFileName(installation.ExecutablePath), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChatGptCompatibilityException("The resolved package executable is not ChatGPT.exe: " + installation.ExecutablePath);
            }
            if (!File.Exists(installation.ExecutablePath))
            {
                throw new ChatGptCompatibilityException("The Store package executable does not exist: " + installation.ExecutablePath);
            }
        }

        private static string ExecuteDiscoveryScript()
        {
            string script = string.Join("\r\n", new string[]
            {
                "$ErrorActionPreference = 'Stop'",
                "$ProgressPreference = 'SilentlyContinue'",
                "[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)",
                "$packages = @(Get-AppxPackage -Name 'OpenAI.Codex')",
                "if ($packages.Count -ne 1) { throw ('Expected exactly one OpenAI.Codex package; found ' + $packages.Count + '.') }",
                "$package = $packages[0]",
                "$manifest = Get-AppxPackageManifest -Package $package",
                "$apps = @($manifest.Package.Applications.Application | Where-Object { $_.Id -eq 'App' })",
                "if ($apps.Count -ne 1) { throw ('Expected exactly one manifest application named App; found ' + $apps.Count + '.') }",
                "$app = $apps[0]",
                "$aumid = [string]$package.PackageFamilyName + '!' + [string]$app.Id",
                "$registered = @((Get-StartApps) | Where-Object { $_.AppID -eq $aumid }).Count -gt 0",
                "[pscustomobject]@{ PackageName = [string]$package.Name; PackageVersion = [string]$package.Version; PackageFamily = [string]$package.PackageFamilyName; InstallLocation = [string]$package.InstallLocation; ApplicationId = [string]$app.Id; ManifestExecutable = [string]$app.Executable; EntryPoint = [string]$app.EntryPoint; AppUserModelId = $aumid; AppUserModelIdRegistered = $registered } | ConvertTo-Json -Compress"
            });

            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string powerShellPath = Path.Combine(windowsDirectory, @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powerShellPath))
            {
                throw new ChatGptCompatibilityException("Windows PowerShell was not found: " + powerShellPath);
            }

            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new ChatGptCompatibilityException("Windows PowerShell did not start for package discovery.");
                }

                string standardOutput = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(DiscoveryTimeoutMilliseconds))
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                    throw new ChatGptCompatibilityException("Store package discovery timed out.");
                }
                if (process.ExitCode != 0)
                {
                    throw new ChatGptCompatibilityException("Store package discovery failed: " + Condense(standardError));
                }
                return standardOutput.Trim().Trim('\uFEFF');
            }
        }

        private static PackageDiscoveryPayload DeserializePayload(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ChatGptCompatibilityException("Store package discovery returned no data.");
            }

            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(PackageDiscoveryPayload));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (PackageDiscoveryPayload)serializer.ReadObject(stream);
                }
            }
            catch (Exception exception)
            {
                throw new ChatGptCompatibilityException("Store package discovery returned invalid data.", exception);
            }
        }

        private static string CanonicalPath(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ChatGptCompatibilityException("The " + label + " is missing.");
            }
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception exception)
            {
                throw new ChatGptCompatibilityException("The " + label + " is invalid: " + path, exception);
            }
        }

        private static string Condense(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 1000 ? value : value.Substring(0, 1000);
        }

        [DataContract]
        private sealed class PackageDiscoveryPayload
        {
            [DataMember(Name = "PackageName")]
            public string PackageName { get; set; }

            [DataMember(Name = "PackageVersion")]
            public string PackageVersion { get; set; }

            [DataMember(Name = "PackageFamily")]
            public string PackageFamily { get; set; }

            [DataMember(Name = "InstallLocation")]
            public string InstallLocation { get; set; }

            [DataMember(Name = "ApplicationId")]
            public string ApplicationId { get; set; }

            [DataMember(Name = "ManifestExecutable")]
            public string ManifestExecutable { get; set; }

            [DataMember(Name = "EntryPoint")]
            public string EntryPoint { get; set; }

            [DataMember(Name = "AppUserModelId")]
            public string AppUserModelId { get; set; }

            [DataMember(Name = "AppUserModelIdRegistered")]
            public bool AppUserModelIdRegistered { get; set; }
        }
    }

    internal static class WindowsPath
    {
        public static bool IsStrictlyInside(string candidatePath, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
            {
                return false;
            }

            string candidate;
            string root;
            try
            {
                candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return false;
            }

            return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static bool EqualsPath(string firstPath, string secondPath)
        {
            if (string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath))
            {
                return false;
            }
            try
            {
                string first = Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string second = Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
