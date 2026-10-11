using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal static class RuntimeSecurityTests
    {
        public static int Run()
        {
            string fixtureRoot = Path.Combine(FixtureWorkspace.PathFor("SecurityTests"), Guid.NewGuid().ToString("N"));
            bool succeeded = false;
            Directory.CreateDirectory(fixtureRoot);
            try
            {
                TestAclBoundary(fixtureRoot);
                TestClosedDiagnosticSchema(fixtureRoot);
                succeeded = true;
                return 2;
            }
            finally
            {
                if (succeeded)
                {
                    ArchiveFixture(fixtureRoot);
                }
                else
                {
                    Console.Error.WriteLine("Security fixture retained for diagnosis: " + fixtureRoot);
                }
            }
        }

        private static void TestAclBoundary(string fixtureRoot)
        {
            string runtimeRoot = Path.Combine(fixtureRoot, "runtime");
            string deletionRoot = Path.Combine(fixtureRoot, "deletion");
            string child = Path.Combine(runtimeRoot, "vault", "browser");
            string protectedFile = Path.Combine(child, "state.bin");
            string outsideFile = Path.Combine(fixtureRoot, "outside", "untouched.txt");
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(Path.GetDirectoryName(outsideFile));
            File.WriteAllText(protectedFile, "browser-state", Encoding.UTF8);
            File.WriteAllText(outsideFile, "outside", Encoding.UTF8);

            WindowsRuntimeSecurity security = new WindowsRuntimeSecurity(runtimeRoot, deletionRoot);
            security.ProtectTree(runtimeRoot);

            AssertHealthy(security.AuditDirectory(runtimeRoot), "Runtime root ACL was not hardened.");
            AssertHealthy(security.AuditDirectory(child), "Nested browser ACL was not hardened.");
            AssertHealthy(security.AuditFile(protectedFile), "Nested browser file ACL was not hardened.");
            ExpectUnauthorized(delegate { security.ProtectFile(outsideFile); });

            string escapeLink = Path.Combine(runtimeRoot, "escape");
            new WindowsJunctionManager().Create(escapeLink, Path.GetDirectoryName(outsideFile));
            ExpectIo(delegate { security.ProtectFile(Path.Combine(escapeLink, Path.GetFileName(outsideFile))); });
        }

        private static void TestClosedDiagnosticSchema(string fixtureRoot)
        {
            string runtimeRoot = Path.Combine(fixtureRoot, "diagnostic-runtime");
            string deletionRoot = Path.Combine(fixtureRoot, "diagnostic-deletion");
            EnginePaths paths = new EnginePaths(
                runtimeRoot,
                Path.Combine(fixtureRoot, "live", "auth.json"),
                Path.Combine(fixtureRoot, "live", "browser"),
                Path.Combine(fixtureRoot, "live", "links"),
                Path.Combine(fixtureRoot, "live", "staging"),
                deletionRoot);
            WindowsRuntimeSecurity security = new WindowsRuntimeSecurity(runtimeRoot, deletionRoot);
            SecureDiagnosticLogger logger = new SecureDiagnosticLogger(paths, security);
            string transactionId = Guid.NewGuid().ToString("D");

            logger.OperationStarted(DiagnosticOperations.Switch, "1.2.3.4");
            logger.OperationSucceeded(DiagnosticOperations.Switch, "1.2.3.4", transactionId);
            logger.OperationFailed(
                DiagnosticOperations.Recover,
                "1.2.3.4",
                null,
                new InvalidOperationException("fake-access-token at D:\\private\\auth.json for Personal Account"));

            ExpectArgument(delegate { logger.OperationStarted("switch\nfake-access-token", "1.2.3.4"); });
            ExpectArgument(delegate { logger.OperationStarted(DiagnosticOperations.Switch, "1.2.3.4-secret"); });

            string[] logFiles = Directory.GetFiles(paths.LogsDirectory, "events-*.jsonl");
            Assert(logFiles.Length == 1, "Expected one protected diagnostic log file.");
            string raw = File.ReadAllText(logFiles[0], Encoding.UTF8);
            Assert(!raw.Contains("fake-access-token"), "Diagnostic output leaked exception text.");
            Assert(!raw.Contains("Personal Account"), "Diagnostic output leaked a profile display name.");
            Assert(!raw.Contains("D:\\\\private"), "Diagnostic output leaked an input path.");
            Assert(!raw.Contains("\"message\""), "Diagnostic schema unexpectedly contains a message field.");
            Assert(!raw.Contains("\"path\""), "Diagnostic schema unexpectedly contains a path field.");
            Assert(!raw.Contains("\"hash\""), "Diagnostic schema unexpectedly contains a hash field.");

            string[] lines = raw.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert(lines.Length == 3, "Expected exactly three diagnostic events.");
            DiagnosticLogEvent[] events = lines.Select(Deserialize).ToArray();
            Assert(events.All(value => value.SchemaVersion == 1), "Diagnostic schema version was not fixed.");
            Assert(events[0].EventCode == "operation_started", "Start event code is incorrect.");
            Assert(events[1].TransactionId == transactionId, "Success event lost its transaction ID.");
            Assert(events[2].ErrorCategory == DiagnosticErrorCategories.StateConflict, "Failure was not reduced to a controlled category.");
            AssertHealthy(security.AuditDirectory(paths.LogsDirectory), "Diagnostic directory ACL was not hardened.");
            AssertHealthy(security.AuditFile(logFiles[0]), "Diagnostic file ACL was not hardened.");

            Exception secretBearingFailure = new Exception("fake-access-token at D:\\private\\auth.json for Personal Account");
            Assert(!SafeUserMessages.ForOperation(secretBearingFailure).Contains("fake-access-token"), "Operation UI leaked arbitrary exception text.");
            Assert(!SafeUserMessages.ForInspection(secretBearingFailure).Contains("D:\\private"), "Inspection UI leaked an arbitrary path.");
            Assert(!SafeUserMessages.ForStartup(secretBearingFailure).Contains("Personal Account"), "Startup UI leaked a profile name.");
        }

        private static DiagnosticLogEvent Deserialize(string line)
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
            {
                return (DiagnosticLogEvent)new DataContractJsonSerializer(typeof(DiagnosticLogEvent)).ReadObject(stream);
            }
        }

        private static void AssertHealthy(RuntimeAclAudit audit, string message)
        {
            Assert(audit != null && audit.Exists && audit.Protected && audit.Healthy, message);
            Assert(audit.Issues != null && audit.Issues.Count == 0, message + " Issues: " + string.Join(",", audit.Issues ?? new string[0]));
        }

        private static void ExpectUnauthorized(Action action)
        {
            try
            {
                action();
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            throw new InvalidOperationException("Expected ACL scope enforcement to reject an outside path.");
        }

        private static void ExpectArgument(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                return;
            }
            throw new InvalidOperationException("Expected the closed diagnostic schema to reject arbitrary text.");
        }

        private static void ExpectIo(Action action)
        {
            try
            {
                action();
            }
            catch (IOException)
            {
                return;
            }
            throw new InvalidOperationException("Expected ACL hardening to reject a reparse-point escape.");
        }

        private static void ArchiveFixture(string fixtureRoot)
        {
            string archiveParent = FixtureWorkspace.PathFor("Archive/security");
            Directory.CreateDirectory(archiveParent);
            string destination = Path.Combine(archiveParent, Path.GetFileName(fixtureRoot));
            Directory.Move(fixtureRoot, destination);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
