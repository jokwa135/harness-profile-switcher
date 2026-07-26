using System;
using System.IO;

namespace HarnessProfileSwitcher.Core
{
    public static class ProductionEnvironment
    {
        public const string RuntimeRoot = @"D:\AI\Data\HarnessProfileSwitcher";
        public const string DeletionRoot = @"D:\fordeletion\HarnessProfileSwitcher";

        public static EnginePaths CreatePaths()
        {
            string configuredCodexHome = ReadCodexHome();
            if (!string.IsNullOrWhiteSpace(configuredCodexHome))
            {
                throw new ChatGptCompatibilityException("CODEX_HOME is configured. This current-PC build supports only the default shared .codex location.");
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(userProfile) || string.IsNullOrWhiteSpace(appData))
            {
                throw new ChatGptCompatibilityException("Windows user profile paths could not be resolved.");
            }

            string codexHome = Path.Combine(userProfile, ".codex");
            string webRoot = Path.Combine(appData, @"Codex\web");
            return new EnginePaths(
                RuntimeRoot,
                Path.Combine(codexHome, "auth.json"),
                Path.Combine(webRoot, "Codex"),
                Path.Combine(webRoot, ".hps-links"),
                Path.Combine(codexHome, ".hps-auth"),
                DeletionRoot);
        }

        private static string ReadCodexHome()
        {
            string value = Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            value = Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            return Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.Machine);
        }
    }
}
