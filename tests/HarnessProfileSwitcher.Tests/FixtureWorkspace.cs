using System;
using System.IO;

namespace HarnessProfileSwitcher.Tests
{
    internal static class FixtureWorkspace
    {
        public static string PathFor(string name)
        {
            string root = Environment.GetEnvironmentVariable("HPS_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("HPS_TEST_ROOT must explicitly name a fresh isolated fixture directory.");
            root = Path.GetFullPath(root);
            string prefix = @"D:\AI\Tools\codex-profile-switcher\artifacts\";
            if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixtures must stay inside this project's D: artifacts boundary.");
            return Path.Combine(root, name);
        }

        public static string HelperExecutable(string variable)
        {
            string fixtureRoot = PathFor(".");
            string session = Path.GetFullPath(fixtureRoot).TrimEnd('\\');
            string relative = variable == "HPS_TEST_FAKE_PACKAGE_EXE" ? @"FakePackage\ChatGPT.exe"
                : variable == "HPS_TEST_OUTSIDE_CODEX_EXE" ? @"OutsidePackage\codex.exe" : null;
            if (relative == null) throw new ArgumentException("Unknown fixture helper.");
            string expected = Path.GetFullPath(Path.Combine(session, relative));
            string actual = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(actual) || !string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected))
                throw new InvalidOperationException("Process fixtures require the current isolated session's synthetic helper.");
            return expected;
        }
    }
}
