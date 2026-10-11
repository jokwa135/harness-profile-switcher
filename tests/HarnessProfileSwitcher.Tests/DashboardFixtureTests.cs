using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HarnessProfileSwitcher.Application;
using HarnessProfileSwitcher.Core;
using HarnessProfileSwitcher.UI;

namespace HarnessProfileSwitcher.Tests
{
    internal static class DashboardFixtureTests
    {
        public static int Run()
        {
            TestRenamePreservationAndProcessIsolation();
            TestRenameValidationNoopAndFailures();
            TestRenamePendingAndMalformedState();
            TestRenameLeaseContention();
            TestRenameReparseAndAclRejection();
            TestQuotaWindowsAndFreshness();
            TestQuotaIdentityGenerationAndInvalidData();
            TestDashboardAndKeyboard();
            Console.WriteLine("PASS: 8 dashboard suites (rename preservation/no-process-touch, validation, faults, leases, ACL/reparse, provider windows, identity/freshness, keyboard/layout)");
            return 8;
        }

        private static void TestRenamePreservationAndProcessIsolation()
        {
            Fixture fixture = new Fixture();
            Dictionary<string, string> before = fixture.Snapshot();
            using (Process sentinel = Process.Start(new ProcessStartInfo(FixtureWorkspace.HelperExecutable("HPS_TEST_OUTSIDE_CODEX_EXE"))
                { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    ApplicationServices services = new ApplicationServices(fixture.Paths, new ForbiddenDiscovery(), fixture.Security, new SilentDiagnostics());
                    Assert(services.RenameProfile(fixture.Active, "个人账户 · Renamed"), "Active rename did not change the label.");
                    Assert(services.RenameProfile(fixture.Inactive, "Équipe studio"), "Inactive rename did not change the label.");
                    sentinel.Refresh(); Assert(!sentinel.HasExited, "Rename touched the sentinel process.");
                    fixture.AssertPreserved(before, new[] { fixture.Paths.ProfileMetadataPath(fixture.Active), fixture.Paths.ProfileMetadataPath(fixture.Inactive) });
                    foreach (string id in new[] { fixture.Active, fixture.Inactive })
                    {
                        ProfileMetadata after = fixture.Store.Read<ProfileMetadata>(fixture.Paths.ProfileMetadataPath(id));
                        Assert(after.ProfileId == id && after.CreatedAtUtc == "fixture-created" && after.LastActivatedAtUtc == "fixture-activated", "Rename changed identity/timestamps.");
                        Assert(fixture.Security.AuditFile(fixture.Paths.ProfileMetadataPath(id)).Healthy, "Renamed metadata ACL is unhealthy.");
                    }
                    Assert(fixture.Junctions.ResolveTarget(fixture.Paths.LiveBrowserLinkPath) == fixture.Paths.ProfileBrowserPath(fixture.Active), "Live junction changed.");
                }
                finally { if (!sentinel.HasExited) { sentinel.Kill(); sentinel.WaitForExit(5000); } }
            }
        }

        private static void TestRenameValidationNoopAndFailures()
        {
            Fixture fixture = new Fixture();
            ProfileMetadataEditor editor = fixture.Editor(new NoFaultInjector());
            Dictionary<string, string> before = fixture.Snapshot();
            Assert(!editor.Rename(fixture.Active, " Active "), "Identical name was not a no-op.");
            Expect<ArgumentException>(delegate { editor.Rename(fixture.Active, "  "); });
            Expect<ArgumentException>(delegate { editor.Rename(fixture.Active, "Bad\nName"); });
            Expect<InvalidOperationException>(delegate { fixture.Editor(new FailBeforeWrite()).Rename(fixture.Active, "Changed"); });
            fixture.AssertPreserved(before, new string[0]);
            using (FileStream blocked = new FileStream(fixture.Paths.ProfileMetadataPath(fixture.Active), FileMode.Open, FileAccess.Read, FileShare.None))
                Expect<InvalidOperationException>(delegate { editor.Rename(fixture.Active, "Blocked"); });
            fixture.AssertPreserved(before, new string[0]);
        }

        private static void TestRenamePendingAndMalformedState()
        {
            Fixture fixture = new Fixture();
            Ledger ledger = fixture.Store.Read<Ledger>(fixture.Paths.LedgerPath);
            ledger.ActiveProfileId = null;
            ledger.PendingOnboarding = new PendingOnboarding { PreviousProfileId = fixture.Active, CandidateProfileId = fixture.Inactive, TransactionId = Guid.NewGuid().ToString("D") };
            fixture.Store.Write(fixture.Paths.LedgerPath, ledger);
            Dictionary<string, string> before = fixture.Snapshot();
            Expect<InvalidOperationException>(delegate { fixture.Editor(new NoFaultInjector()).Rename(fixture.Active, "No"); });
            fixture.AssertPreserved(before, new string[0]);
            Fixture malformed = new Fixture();
            ProfileMetadata metadata = malformed.Store.Read<ProfileMetadata>(malformed.Paths.ProfileMetadataPath(malformed.Active));
            metadata.SchemaVersion = 2; malformed.Store.Write(malformed.Paths.ProfileMetadataPath(malformed.Active), metadata);
            before = malformed.Snapshot();
            Expect<InvalidOperationException>(delegate { malformed.Editor(new NoFaultInjector()).Rename(malformed.Active, "No"); });
            malformed.AssertPreserved(before, new string[0]);
        }

        private static void TestRenameLeaseContention()
        {
            Fixture fixture = new Fixture();
            using (OperationLease lease = OperationLease.Acquire(fixture.Paths))
                Expect<IOException>(delegate { fixture.Editor(new NoFaultInjector()).Rename(fixture.Active, "No"); });
            Assert(fixture.Store.Read<ProfileMetadata>(fixture.Paths.ProfileMetadataPath(fixture.Active)).DisplayName == "Active", "Contended rename changed metadata.");
        }

        private static void TestRenameReparseAndAclRejection()
        {
            Fixture fixture = new Fixture();
            Dictionary<string, string> before = fixture.Snapshot();
            Expect<UnauthorizedAccessException>(delegate { new ProfileMetadataEditor(fixture.Paths, fixture.Store, fixture.Junctions, new RejectSecurity(), new NoFaultInjector()).Rename(fixture.Active, "No"); });
            fixture.AssertPreserved(before, new string[0]);
            string outside = Path.Combine(fixture.Root, "outside-state");
            Directory.CreateDirectory(outside);
            string parked = fixture.Paths.StateDirectory + "-parked";
            Directory.Move(fixture.Paths.StateDirectory, parked);
            fixture.Junctions.Create(fixture.Paths.StateDirectory, outside);
            Expect<IOException>(delegate { fixture.Editor(new NoFaultInjector()).Rename(fixture.Active, "No"); });
            Assert(!File.Exists(Path.Combine(outside, "operation.lock")), "Lease followed a redirected state directory.");
        }

        private static void TestQuotaWindowsAndFreshness()
        {
            DateTimeOffset now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
            string json = "{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":90,\"resetsAt\":1791763200},\"secondary\":{\"usedPercent\":100,\"windowDurationMins\":2880}}}}";
            CodexUsageSnapshot usage = Accept(json, now.AddMinutes(-1), now);
            Assert(usage.Windows.Count == 2 && usage.Windows[0].DurationMinutes == 90 && usage.Windows[0].UsedPercent == 25, "Provider windows changed.");
            Assert(usage.Detail(now).Contains("75% remaining") && usage.Detail(now).Contains("90m window") && usage.Detail(now).Contains("2d window"), "Provider quota formatting failed.");
            Assert(usage.Freshness(now).StartsWith("Cached"), "Recent snapshot not marked cached.");
            Assert(usage.Freshness(now.AddDays(3)).StartsWith("Stale"), "Old/reset-passed snapshot not marked stale.");
            Assert(usage.Detail(now.AddDays(3)).Contains("75% remaining") && usage.Detail(now.AddDays(3)).Contains("reset passed"), "Reset invented replenished quota.");
        }

        private static void TestQuotaIdentityGenerationAndInvalidData()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string json = "{\"rateLimits\":{\"primary\":{\"usedPercent\":4,\"windowDurationMins\":120}}}";
            Assert(CodexUsageReader.AcceptVerifiedResponse(json, "a", "b", "account/context", "account/context", 1, 1, now, now, false).Windows.Count == 0, "Profile mismatch accepted.");
            Assert(CodexUsageReader.AcceptVerifiedResponse(json, "a", "a", "account/context", "other", 1, 1, now, now, false).Windows.Count == 0, "Context mismatch accepted.");
            Assert(CodexUsageReader.AcceptVerifiedResponse(json, "a", "a", "account/context", "account/context", 1, 2, now, now, false).Windows.Count == 0, "Superseded result accepted.");
            Assert(CodexUsageReader.AcceptVerifiedResponse(json, "a", "a", null, null, 1, 1, now, now, false).Windows.Count == 0, "Unknown mapping accepted.");
            foreach (string invalid in new[] { "{}", "not-json", json.Replace(":4,", ":104,"), json.Replace(":120", ":0") })
                Assert(Accept(invalid, now, now).Windows.Count == 0, "Invalid quota response produced numbers.");
            Assert(Accept(json, now.AddSeconds(1), now).Windows.Count == 0, "Future observation accepted.");
            CodexUsageSnapshot shared = CodexUsageReader.AcceptVerifiedResponse(json, "a", "a", "account/context", "account/context", 1, 1, now, now, true);
            Assert(shared.Freshness(now).Contains("shared account/context verified"), "Verified shared context missing.");
            var unavailable = CodexUsageReader.Inspect(new ApplicationStateSnapshot { Profiles = new[] { new ProfileSummary { ProfileId = "a", IsActive = true }, new ProfileSummary { ProfileId = "b" } } });
            Assert(unavailable.Values.All(item => item.Windows.Count == 0 && !item.ObservedAt.HasValue), "Production adapter fabricated quotas.");
        }

        private static CodexUsageSnapshot Accept(string json, DateTimeOffset observed, DateTimeOffset now)
        {
            return CodexUsageReader.AcceptVerifiedResponse(json, "a", "a", "account/context", "account/context", 1, 1, observed, now, false);
        }

        private static void TestDashboardAndKeyboard()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            using (Font font = VisualTheme.Font(10f, FontStyle.Regular))
            {
                string native = VisualTheme.NativeFontFace(font);
                Assert(new[] { "Segoe UI", "Tahoma", "Microsoft Sans Serif" }.Contains(native), "Functional font substituted: " + native);
                Console.WriteLine("Verified native functional font: " + native);
            }
            using (Font boldFont = VisualTheme.Font(12f, FontStyle.Bold))
            {
                string native = VisualTheme.NativeFontFace(boldFont);
                Assert(new[] { "Tahoma", "Microsoft Sans Serif" }.Contains(native), "Bold functional font substituted: " + native);
                Console.WriteLine("Verified native bold functional font: " + native);
            }
            foreach (string mode in new[] { "active", "pending", "recovery", "small-unicode", "quota-cached" })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            using (Form form = PreviewRenderer.CreatePreviewForm(mode))
            {
                form.Location = new Point(-32000, -32000); form.Show();
                if (mode == "small-unicode") form.ClientSize = new Size(760, 600);
                System.Windows.Forms.Application.DoEvents(); form.PerformLayout();
                if (scale != 1f) PreviewRenderer.ScaleFormForPreview(form, scale);
                Control[] controls = Descendants(form).ToArray();
                Label heading = controls.OfType<Label>().Single(item => item.Text == "Accounts");
                Assert(heading.Visible && heading.Height >= 20 * scale && heading.Bottom <= heading.Parent.ClientSize.Height,
                    "Accounts heading clipped at " + scale);
                foreach (Control card in controls.Where(item => Convert.ToString(item.Tag) == "profile-card"))
                {
                    Assert(card.Width <= card.Parent.ClientSize.Width - card.Parent.Padding.Horizontal, "Scaled card escaped the viewport.");
                    Assert(!((ScrollableControl)card.Parent).HorizontalScroll.Visible, "Dashboard has a stale horizontal scrollbar.");
                }
                Button[] more = controls.OfType<Button>().Where(item => item.Text == "More").ToArray();
                Assert(more.Length > 0, "More menu missing.");
                Assert(!more[0].ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(item => item.Text.StartsWith("Delete")), "Active deletion offered.");
                if (mode == "active" || mode == "small-unicode" || mode == "quota-cached")
                {
                    Assert(more[0].Enabled, "Active rename unavailable.");
                    Assert(controls.OfType<Button>().Count(item => item.Text == "Switch") == 2, "Switch actions missing.");
                    Assert(more.All(item => item.Width >= 92 && item.Height >= 40), "Action target clipped.");
                    Assert(more.All(item => item.Right <= item.Parent.ClientSize.Width && item.Bottom <= item.Parent.ClientSize.Height), "More action escaped its row.");
                }
                else Assert(more.All(item => !item.Enabled), "Exceptional state permits rename.");
                Assert(form.SelectNextControl(null, true, true, true, false), "No keyboard navigation target.");
                form.Close();
            }
            using (ProfileNameDialog dialog = new ProfileNameDialog("Rename profile", "Local label only", "Save name", "个人账户"))
            {
                dialog.Location = new Point(-32000, -32000); dialog.Show();
                TextBox box = Descendants(dialog).OfType<TextBox>().Single();
                Assert(box.Text == "个人账户" && box.SelectionLength == box.Text.Length, "Rename did not prefill/select name.");
                box.Text = " "; DialogKey(dialog, Keys.Enter);
                Assert(dialog.DialogResult != DialogResult.OK && Descendants(dialog).OfType<Label>().Any(item => item.Visible && item.Text == "Enter a profile name."), "Inline validation missing.");
                Assert(dialog.AcceptButton != null && dialog.CancelButton != null, "Enter/Escape contracts missing.");
                DialogKey(dialog, Keys.Escape); Assert(dialog.DialogResult == DialogResult.Cancel, "Escape cancellation failed.");
                dialog.Close();
            }
            using (ProfileNameDialog dialog = new ProfileNameDialog("Rename profile", "Local label only", "Save name", "Personal"))
            {
                dialog.Location = new Point(-32000, -32000); dialog.Show();
                DialogKey(dialog, Keys.Enter);
                Assert(dialog.DialogResult == DialogResult.OK && dialog.ProfileName == "Personal", "Enter/save unchanged-name dialog contract failed.");
                dialog.Close();
            }
        }

        private static void DialogKey(Form dialog, Keys key)
        {
            typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(dialog, new object[] { key });
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
        }
        private sealed class FailBeforeWrite : IFaultInjector { public void Checkpoint(string phase) { throw new InvalidOperationException("Fixture failure"); } }
        private sealed class ForbiddenDiscovery : IChatGptInstallationDiscovery { public ChatGptInstallation Discover() { throw new Exception("Rename invoked package discovery/process context."); } }
        private sealed class SilentDiagnostics : ISecureDiagnosticLogger
        {
            public void OperationStarted(string operation, string version) { throw new Exception("Rename entered lifecycle diagnostics."); }
            public void OperationSucceeded(string operation, string version, string transaction) { throw new Exception("Rename entered lifecycle diagnostics."); }
            public void OperationFailed(string operation, string version, string transaction, Exception error) { throw new Exception("Rename entered lifecycle diagnostics."); }
            public void OperationNoop(string operation, string version) { throw new Exception("Rename entered lifecycle diagnostics."); }
        }
        private sealed class RejectSecurity : IRuntimeSecurity
        {
            public void EnsureProtectedDirectory(string path) { throw new Exception("Unexpected mutation"); }
            public void ProtectFile(string path) { throw new Exception("Unexpected mutation"); }
            public void ProtectTree(string path) { throw new Exception("Unexpected mutation"); }
            public RuntimeAclAudit AuditDirectory(string path) { return new RuntimeAclAudit { Exists = true, Protected = false, Healthy = false }; }
            public RuntimeAclAudit AuditFile(string path) { return AuditDirectory(path); }
        }

        private sealed class Fixture
        {
            public readonly string Root = Path.Combine(FixtureWorkspace.PathFor("Dashboard"), Guid.NewGuid().ToString("N"));
            public readonly string Active = Guid.NewGuid().ToString("D");
            public readonly string Inactive = Guid.NewGuid().ToString("D");
            public readonly EnginePaths Paths;
            public readonly AtomicJsonStore Store = new AtomicJsonStore();
            public readonly WindowsJunctionManager Junctions = new WindowsJunctionManager();
            public readonly WindowsRuntimeSecurity Security;
            public Fixture()
            {
                Paths = new EnginePaths(Path.Combine(Root, "runtime"), Path.Combine(Root, "live/auth.json"),
                    Path.Combine(Root, "live/browser"), Path.Combine(Root, "live/links"), Path.Combine(Root, "live/staging"), Path.Combine(Root, "quarantine"));
                Security = new WindowsRuntimeSecurity(Paths.RuntimeRoot, Paths.DeletionRoot);
                Directory.CreateDirectory(Paths.StateDirectory); Directory.CreateDirectory(Paths.TransactionsDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(Paths.LiveAuthPath)); Directory.CreateDirectory(Paths.InactiveLinksDirectory);
                File.WriteAllText(Paths.LiveAuthPath, "fixture-active-auth");
                File.WriteAllText(Path.Combine(Root, "live/history.jsonl"), "fixture-shared-history");
                foreach (string id in new[] { Active, Inactive })
                {
                    Directory.CreateDirectory(Paths.ProfileBrowserPath(id));
                    File.WriteAllText(Path.Combine(Paths.ProfileBrowserPath(id), "browser.marker"), "fixture-browser-" + id);
                    Store.Write(Paths.ProfileMetadataPath(id), new ProfileMetadata { SchemaVersion = 1, ProfileId = id,
                        DisplayName = id == Active ? "Active" : "Inactive", CreatedAtUtc = "fixture-created", LastActivatedAtUtc = "fixture-activated",
                        RegisteredPackageFamily = "fixture-family", RegisteredPackageVersion = "fixture-version" });
                }
                File.WriteAllText(Paths.ProfileAuthPath(Inactive), "fixture-inactive-auth");
                Store.Write(Paths.LedgerPath, new Ledger { SchemaVersion = 1, ActiveProfileId = Active, UpdatedAtUtc = "fixture-ledger-time" });
                Junctions.Create(Paths.LiveBrowserLinkPath, Paths.ProfileBrowserPath(Active));
                Junctions.Create(Paths.InactiveLinkPath(Inactive), Paths.ProfileBrowserPath(Inactive));
                Security.ProtectTree(Paths.RuntimeRoot);
                using (OperationLease lease = OperationLease.Acquire(Paths)) { }
            }
            public ProfileMetadataEditor Editor(IFaultInjector faults) { return new ProfileMetadataEditor(Paths, Store, Junctions, Security, faults); }
            public Dictionary<string, string> Snapshot()
            {
                Dictionary<string, string> snapshot = new Dictionary<string, string>();
                Collect(Root, snapshot); return snapshot;
            }
            private static void Collect(string directory, Dictionary<string, string> snapshot)
            {
                foreach (string file in Directory.GetFiles(directory))
                    snapshot[file] = Convert.ToBase64String(File.ReadAllBytes(file));
                foreach (string child in Directory.GetDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Collect(child, snapshot);
            }
            public void AssertPreserved(Dictionary<string, string> before, string[] changed)
            {
                Dictionary<string, string> after = Snapshot();
                Assert(before.Keys.OrderBy(item => item).SequenceEqual(after.Keys.OrderBy(item => item)), "Rename changed file inventory.");
                foreach (string path in before.Keys)
                    if (!changed.Contains(path)) Assert(before[path] == after[path], "Rename changed preserved fixture data.");
            }
        }
    }
}
