using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HarnessProfileSwitcher.Core
{
    public static class DiagnosticOperations
    {
        public const string RegisterFirst = "register_first";
        public const string BeginOnboarding = "begin_onboarding";
        public const string FinalizeOnboarding = "finalize_onboarding";
        public const string CancelOnboarding = "cancel_onboarding";
        public const string Switch = "switch";
        public const string Recover = "recover";

        internal static readonly ISet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            RegisterFirst,
            BeginOnboarding,
            FinalizeOnboarding,
            CancelOnboarding,
            Switch,
            Recover
        };
    }

    public static class DiagnosticErrorCategories
    {
        public const string Compatibility = "compatibility";
        public const string RecoveryRequired = "recovery_required";
        public const string AccessDenied = "access_denied";
        public const string StorageIo = "storage_io";
        public const string Timeout = "timeout";
        public const string Cancelled = "cancelled";
        public const string InvalidRequest = "invalid_request";
        public const string StateConflict = "state_conflict";
        public const string InvalidStateData = "invalid_state_data";
        public const string Unexpected = "unexpected";

        internal static readonly ISet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            Compatibility,
            RecoveryRequired,
            AccessDenied,
            StorageIo,
            Timeout,
            Cancelled,
            InvalidRequest,
            StateConflict,
            InvalidStateData,
            Unexpected
        };
    }

    [DataContract]
    public sealed class DiagnosticLogEvent
    {
        [DataMember(Name = "schemaVersion", Order = 1)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "timestampUtc", Order = 2)]
        public string TimestampUtc { get; set; }

        [DataMember(Name = "level", Order = 3)]
        public string Level { get; set; }

        [DataMember(Name = "eventCode", Order = 4)]
        public string EventCode { get; set; }

        [DataMember(Name = "operation", Order = 5)]
        public string Operation { get; set; }

        [DataMember(Name = "outcome", Order = 6)]
        public string Outcome { get; set; }

        [DataMember(Name = "transactionId", Order = 7, EmitDefaultValue = false)]
        public string TransactionId { get; set; }

        [DataMember(Name = "packageVersion", Order = 8)]
        public string PackageVersion { get; set; }

        [DataMember(Name = "errorCategory", Order = 9, EmitDefaultValue = false)]
        public string ErrorCategory { get; set; }
    }

    public interface ISecureDiagnosticLogger
    {
        void OperationStarted(string operation, string packageVersion);
        void OperationSucceeded(string operation, string packageVersion, string transactionId);
        void OperationNoop(string operation, string packageVersion);
        void OperationFailed(string operation, string packageVersion, string transactionId, Exception exception);
    }

    public sealed class NoOpSecureDiagnosticLogger : ISecureDiagnosticLogger
    {
        public void OperationStarted(string operation, string packageVersion) { }
        public void OperationSucceeded(string operation, string packageVersion, string transactionId) { }
        public void OperationNoop(string operation, string packageVersion) { }
        public void OperationFailed(string operation, string packageVersion, string transactionId, Exception exception) { }
    }

    public sealed class SecureDiagnosticLogger : ISecureDiagnosticLogger
    {
        private readonly EnginePaths paths;
        private readonly IRuntimeSecurity runtimeSecurity;
        private readonly object writeGate = new object();

        public SecureDiagnosticLogger(EnginePaths paths, IRuntimeSecurity runtimeSecurity)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (runtimeSecurity == null) throw new ArgumentNullException("runtimeSecurity");
            this.paths = paths;
            this.runtimeSecurity = runtimeSecurity;
        }

        public void OperationStarted(string operation, string packageVersion)
        {
            WriteEvent("info", "operation_started", operation, "started", packageVersion, null, null);
        }

        public void OperationSucceeded(string operation, string packageVersion, string transactionId)
        {
            WriteEvent("info", "operation_succeeded", operation, "succeeded", packageVersion, NormalizeTransactionId(transactionId, true), null);
        }

        public void OperationNoop(string operation, string packageVersion)
        {
            WriteEvent("info", "operation_noop", operation, "noop", packageVersion, null, null);
        }

        public void OperationFailed(string operation, string packageVersion, string transactionId, Exception exception)
        {
            if (exception == null) throw new ArgumentNullException("exception");
            WriteEvent(
                "error",
                "operation_failed",
                operation,
                "failed",
                packageVersion,
                NormalizeTransactionId(transactionId, false),
                DiagnosticErrorClassifier.Classify(exception));
        }

        private void WriteEvent(
            string level,
            string eventCode,
            string operation,
            string outcome,
            string packageVersion,
            string transactionId,
            string errorCategory)
        {
            ValidateOperation(operation);
            ValidatePackageVersion(packageVersion);
            ValidateClosedFields(level, eventCode, outcome, transactionId, errorCategory);

            DiagnosticLogEvent record = new DiagnosticLogEvent
            {
                SchemaVersion = 1,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Level = level,
                EventCode = eventCode,
                Operation = operation,
                Outcome = outcome,
                TransactionId = transactionId,
                PackageVersion = packageVersion,
                ErrorCategory = errorCategory
            };

            lock (writeGate)
            {
                runtimeSecurity.EnsureProtectedDirectory(paths.RuntimeRoot);
                runtimeSecurity.EnsureProtectedDirectory(paths.LogsDirectory);
                string logPath = Path.Combine(paths.LogsDirectory, "events-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");
                byte[] payload;
                using (MemoryStream buffer = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(DiagnosticLogEvent)).WriteObject(buffer, record);
                    byte[] serialized = buffer.ToArray();
                    payload = new byte[serialized.Length + 1];
                    Buffer.BlockCopy(serialized, 0, payload, 0, serialized.Length);
                    payload[payload.Length - 1] = (byte)'\n';
                }
                using (FileStream stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                runtimeSecurity.ProtectFile(logPath);
            }
        }

        private static void ValidateClosedFields(string level, string eventCode, string outcome, string transactionId, string errorCategory)
        {
            bool started = string.Equals(eventCode, "operation_started", StringComparison.Ordinal) &&
                           string.Equals(level, "info", StringComparison.Ordinal) &&
                           string.Equals(outcome, "started", StringComparison.Ordinal) &&
                           transactionId == null && errorCategory == null;
            bool succeeded = string.Equals(eventCode, "operation_succeeded", StringComparison.Ordinal) &&
                             string.Equals(level, "info", StringComparison.Ordinal) &&
                             string.Equals(outcome, "succeeded", StringComparison.Ordinal) &&
                             transactionId != null && errorCategory == null;
            bool noop = string.Equals(eventCode, "operation_noop", StringComparison.Ordinal) &&
                        string.Equals(level, "info", StringComparison.Ordinal) &&
                        string.Equals(outcome, "noop", StringComparison.Ordinal) &&
                        transactionId == null && errorCategory == null;
            bool failed = string.Equals(eventCode, "operation_failed", StringComparison.Ordinal) &&
                          string.Equals(level, "error", StringComparison.Ordinal) &&
                          string.Equals(outcome, "failed", StringComparison.Ordinal) &&
                          errorCategory != null && DiagnosticErrorCategories.Allowed.Contains(errorCategory);
            if (!started && !succeeded && !noop && !failed)
            {
                throw new ArgumentException("Diagnostic event fields do not match the closed event schema.");
            }
        }

        private static void ValidateOperation(string operation)
        {
            if (operation == null || !DiagnosticOperations.Allowed.Contains(operation))
            {
                throw new ArgumentException("Diagnostic operation is not recognized.", "operation");
            }
        }

        private static void ValidatePackageVersion(string packageVersion)
        {
            if (string.IsNullOrWhiteSpace(packageVersion) || packageVersion.Length > 32)
            {
                throw new ArgumentException("Package version is invalid.", "packageVersion");
            }
            for (int index = 0; index < packageVersion.Length; index++)
            {
                char value = packageVersion[index];
                if (!(value >= '0' && value <= '9') && value != '.')
                {
                    throw new ArgumentException("Package version is invalid.", "packageVersion");
                }
            }
        }

        private static string NormalizeTransactionId(string transactionId, bool required)
        {
            if (string.IsNullOrWhiteSpace(transactionId))
            {
                if (required) throw new ArgumentException("Transaction ID is required.", "transactionId");
                return null;
            }
            Guid parsed;
            if (!Guid.TryParseExact(transactionId, "D", out parsed))
            {
                throw new ArgumentException("Transaction ID is invalid.", "transactionId");
            }
            return parsed.ToString("D");
        }
    }

    public static class DiagnosticErrorClassifier
    {
        public static string Classify(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException("exception");
            if (exception is ChatGptCompatibilityException) return DiagnosticErrorCategories.Compatibility;
            if (exception is RecoveryRequiredException) return DiagnosticErrorCategories.RecoveryRequired;
            if (exception is UnauthorizedAccessException) return DiagnosticErrorCategories.AccessDenied;
            if (exception is IOException) return DiagnosticErrorCategories.StorageIo;
            if (exception is TimeoutException) return DiagnosticErrorCategories.Timeout;
            if (exception is OperationCanceledException) return DiagnosticErrorCategories.Cancelled;
            if (exception is SerializationException) return DiagnosticErrorCategories.InvalidStateData;
            if (exception is ArgumentException) return DiagnosticErrorCategories.InvalidRequest;
            if (exception is InvalidOperationException) return DiagnosticErrorCategories.StateConflict;
            return DiagnosticErrorCategories.Unexpected;
        }
    }

    public sealed class PostOperationDiagnosticException : IOException
    {
        public PostOperationDiagnosticException(Exception innerException)
            : base("The account operation completed, but its diagnostic completion event could not be secured.", innerException)
        {
        }
    }
}
