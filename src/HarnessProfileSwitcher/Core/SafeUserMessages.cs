using System;
using System.IO;
using System.Runtime.Serialization;

namespace HarnessProfileSwitcher.Core
{
    public static class SafeUserMessages
    {
        public static string ForOperation(Exception exception)
        {
            if (exception is PostOperationDiagnosticException)
            {
                return "The account operation completed, but its protected diagnostic record could not be updated. Refresh to confirm the active account before continuing.";
            }
            if (exception is ChatGptCompatibilityException)
            {
                return "The installed ChatGPT runtime does not match the supported single-profile layout. Close extra ChatGPT windows, refresh, and try again.";
            }
            if (exception is RecoveryRequiredException)
            {
                return "The saved account state needs recovery. Use Recover safely before another account action.";
            }
            if (exception is UnauthorizedAccessException)
            {
                return "Windows denied access required for this operation. No further account changes were attempted.";
            }
            if (exception is IOException)
            {
                return "A verified storage operation did not complete. Refresh the account state before trying again.";
            }
            if (exception is TimeoutException)
            {
                return "ChatGPT did not reach the expected state in time. Refresh before trying another account action.";
            }
            if (exception is SerializationException)
            {
                return "Saved Harness Profile Switcher state could not be validated. Do not retry until recovery is available.";
            }
            if (exception is ArgumentException)
            {
                return "The requested account action was invalid and no change was made.";
            }
            if (exception is InvalidOperationException)
            {
                return "The current account state does not allow that action. Refresh before trying again.";
            }
            return "The operation did not complete. Refresh the account state before trying again.";
        }

        public static string ForInspection(Exception exception)
        {
            if (exception is UnauthorizedAccessException) return "Windows denied access while reading profile state.";
            if (exception is IOException) return "Profile state could not be read consistently from disk.";
            if (exception is SerializationException) return "Saved profile state could not be validated.";
            return "Profile state could not be inspected safely.";
        }

        public static string ForStartup(Exception exception)
        {
            if (exception is UnauthorizedAccessException)
            {
                return "Harness Profile Switcher could not start because Windows denied access to required local state.";
            }
            if (exception is ChatGptCompatibilityException)
            {
                return "Harness Profile Switcher could not validate the installed ChatGPT runtime.";
            }
            return "Harness Profile Switcher could not start safely.";
        }
    }
}
