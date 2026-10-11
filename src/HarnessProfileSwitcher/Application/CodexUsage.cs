using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Application
{
    public sealed class CodexUsageWindow
    {
        public string Bucket { get; internal set; }
        public double UsedPercent { get; internal set; }
        public int DurationMinutes { get; internal set; }
        public DateTimeOffset? ResetsAt { get; internal set; }
    }

    public sealed class CodexUsageSnapshot
    {
        public string Status { get; internal set; }
        public string Explanation { get; internal set; }
        public DateTimeOffset? ObservedAt { get; internal set; }
        public IList<CodexUsageWindow> Windows { get; internal set; }
        public bool SharedAccountVerified { get; internal set; }

        public static CodexUsageSnapshot Unavailable(string reason)
        {
            return new CodexUsageSnapshot { Status = "Unavailable", Explanation = reason, Windows = new CodexUsageWindow[0] };
        }

        public string Detail(DateTimeOffset now)
        {
            if (ObservedAt == null || Windows == null || Windows.Count == 0) return Explanation;
            return string.Join(Environment.NewLine, Windows.Select(delegate(CodexUsageWindow window)
            {
                string duration = window.DurationMinutes % 1440 == 0 ? (window.DurationMinutes / 1440) + "d window"
                    : window.DurationMinutes % 60 == 0 ? (window.DurationMinutes / 60) + "h window"
                    : window.DurationMinutes + "m window";
                string reset = !window.ResetsAt.HasValue ? "reset unknown"
                    : window.ResetsAt.Value <= now ? "reset passed; refresh needed"
                    : "resets " + window.ResetsAt.Value.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
                return window.Bucket + ": " + duration + " · " + (100 - window.UsedPercent).ToString("0.#", CultureInfo.CurrentCulture) + "% remaining · " + reset;
            }).ToArray());
        }

        public string Freshness(DateTimeOffset now)
        {
            if (!ObservedAt.HasValue) return "No verified snapshot · no account was activated";
            bool stale = now - ObservedAt.Value > TimeSpan.FromMinutes(5) ||
                Windows.Any(delegate(CodexUsageWindow window) { return window.ResetsAt.HasValue && window.ResetsAt.Value <= now; });
            return (stale ? "Stale snapshot" : "Cached snapshot") + " · last checked " +
                ObservedAt.Value.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture) +
                (SharedAccountVerified ? " · shared account/context verified" : "");
        }
    }

    // No production network/process/auth access. A future safe transport must supply verified
    // identity and generation; the protocol's presence alone is not permission to start a server.
    public static class CodexUsageReader
    {
        public const string SafetyReason = "Live lookup disabled: no auth-write-free retrieval path verified.";

        public static IDictionary<string, CodexUsageSnapshot> Inspect(ApplicationStateSnapshot state)
        {
            Dictionary<string, CodexUsageSnapshot> result = new Dictionary<string, CodexUsageSnapshot>();
            if (state.Profiles != null)
                foreach (ProfileSummary profile in state.Profiles)
                    result[profile.ProfileId] = CodexUsageSnapshot.Unavailable(profile.IsActive
                        ? SafetyReason : "No verified snapshot. Inactive accounts are never queried or activated.");
            return result;
        }

        public static CodexUsageSnapshot AcceptVerifiedResponse(string json, string requestedProfileId,
            string currentProfileId, string expectedAccountContext, string returnedAccountContext,
            long requestedGeneration, long currentGeneration, DateTimeOffset observedAt,
            DateTimeOffset now, bool sharedAccountVerified)
        {
            if (!string.Equals(requestedProfileId, currentProfileId, StringComparison.OrdinalIgnoreCase) ||
                requestedGeneration != currentGeneration || string.IsNullOrWhiteSpace(expectedAccountContext) ||
                !string.Equals(expectedAccountContext, returnedAccountContext, StringComparison.Ordinal))
                return CodexUsageSnapshot.Unavailable("Account/context mismatch or superseded result.");
            if (observedAt > now || json == null || json.Length > 65536)
                return CodexUsageSnapshot.Unavailable("Invalid quota observation.");
            try
            {
                RateResponse response;
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    response = (RateResponse)new DataContractJsonSerializer(typeof(RateResponse),
                        new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(stream);
                List<CodexUsageWindow> windows = new List<CodexUsageWindow>();
                if (response.RateLimitsByLimitId != null && response.RateLimitsByLimitId.Count > 0)
                {
                    foreach (KeyValuePair<string, RateSnapshot> bucket in response.RateLimitsByLimitId.OrderBy(item => item.Key, StringComparer.Ordinal))
                        AddWindows(windows, bucket.Key, bucket.Value);
                }
                else AddWindows(windows, "Codex", response.RateLimits);
                if (windows.Count == 0) return CodexUsageSnapshot.Unavailable("Provider returned no supported quota windows.");
                return new CodexUsageSnapshot { Status = "Cached", ObservedAt = observedAt, Windows = windows,
                    SharedAccountVerified = sharedAccountVerified };
            }
            catch (Exception exception)
            {
                if (!(exception is SerializationException) && !(exception is ArgumentException) &&
                    !(exception is System.Xml.XmlException) && !(exception is InvalidOperationException)) throw;
                return CodexUsageSnapshot.Unavailable("Provider quota response could not be validated.");
            }
        }

        private static void AddWindows(List<CodexUsageWindow> windows, string bucket, RateSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (string.IsNullOrWhiteSpace(bucket) || bucket.Any(char.IsControl) || bucket.Length > 80)
                throw new SerializationException("Invalid bucket.");
            foreach (RateWindow window in new[] { snapshot.Primary, snapshot.Secondary })
            {
                if (window == null) continue;
                if (!window.UsedPercent.HasValue || double.IsNaN(window.UsedPercent.Value) ||
                    window.UsedPercent < 0 || window.UsedPercent > 100 || !window.WindowDurationMins.HasValue ||
                    window.WindowDurationMins <= 0 || windows.Count >= 16)
                    throw new SerializationException("Invalid window.");
                windows.Add(new CodexUsageWindow { Bucket = bucket, UsedPercent = window.UsedPercent.Value,
                    DurationMinutes = window.WindowDurationMins.Value,
                    ResetsAt = window.ResetsAt.HasValue ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt.Value) : null });
            }
        }

        [DataContract] private sealed class RateResponse
        {
            [DataMember(Name = "rateLimits")] public RateSnapshot RateLimits { get; set; }
            [DataMember(Name = "rateLimitsByLimitId")] public Dictionary<string, RateSnapshot> RateLimitsByLimitId { get; set; }
        }
        [DataContract] private sealed class RateSnapshot
        {
            [DataMember(Name = "primary")] public RateWindow Primary { get; set; }
            [DataMember(Name = "secondary")] public RateWindow Secondary { get; set; }
        }
        [DataContract] private sealed class RateWindow
        {
            [DataMember(Name = "usedPercent")] public double? UsedPercent { get; set; }
            [DataMember(Name = "windowDurationMins")] public int? WindowDurationMins { get; set; }
            [DataMember(Name = "resetsAt")] public long? ResetsAt { get; set; }
        }
    }
}
