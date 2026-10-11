using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using HarnessProfileSwitcher.Application;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.UI
{
    internal static class PreviewRenderer
    {
        public static void Render(string mode, string outputPath)
        {
            outputPath = Path.GetFullPath(outputPath);
            if (!outputPath.StartsWith(@"D:\AI\Tools\codex-profile-switcher\artifacts\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("UI previews must stay inside this project's artifacts directory.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            if (File.Exists(outputPath)) throw new IOException("Preview outputs must be fresh; existing captures are preserved.");
            float scale = mode.EndsWith("-150", StringComparison.Ordinal) ? 1.5f : mode.EndsWith("-200", StringComparison.Ordinal) ? 2f : 1f;
            if (scale != 1f) mode = mode.Substring(0, mode.Length - 4);
            using (Form form = CreatePreviewForm(mode))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                if (mode == "small-unicode") form.ClientSize = new Size(760, 600);
                if (scale != 1f)
                {
                    ScaleFormForPreview(form, scale);
                }
                if (mode == "dialog-rename-validation") ((Button)form.AcceptButton).PerformClick();
                form.PerformLayout();
                form.Refresh();
                using (Bitmap image = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(outputPath, ImageFormat.Png);
                }
                form.Close();
            }
        }

        internal static Form CreatePreviewForm(string mode)
        {
            if (mode == "dialog-rename" || mode == "dialog-rename-validation")
                return new ProfileNameDialog("Rename profile",
                    "Changes this local label only. Your sign-in, account folder and local work stay unchanged. ChatGPT stays open.",
                    "Save name", mode == "dialog-rename" ? "Personal" : string.Empty);
            if (string.Equals(mode, "dialog-register", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "dialog-force", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "dialog-error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "dialog-delete", StringComparison.OrdinalIgnoreCase))
            {
                return AppDialog.CreatePreview(mode);
            }
            if (string.Equals(mode, "dialog-name", StringComparison.OrdinalIgnoreCase))
            {
                return new ProfileNameDialog(
                    "Add an account",
                    "Choose a local name for the account you are about to sign in to. ChatGPT will reopen at a clean sign-in screen.",
                    "Prepare sign-in");
            }
            return new MainForm(CreatePreview(mode));
        }

        private static DesktopInspection CreatePreview(string mode)
        {
            string primaryId = "11111111-1111-1111-1111-111111111111";
            string studioId = "22222222-2222-2222-2222-222222222222";
            string clientId = "33333333-3333-3333-3333-333333333333";
            List<ProfileSummary> profiles = new List<ProfileSummary>
            {
                new ProfileSummary { ProfileId = primaryId, DisplayName = "Personal", Status = "Active", IsActive = true, CanRename = true },
                new ProfileSummary { ProfileId = studioId, DisplayName = "Studio", Status = "Ready", CanSwitch = true, CanDelete = true, CanRename = true },
                new ProfileSummary { ProfileId = clientId, DisplayName = "Client workspace", Status = "Ready", CanSwitch = true, CanDelete = true, CanRename = true }
            };
            ApplicationStateSnapshot state;
            if (string.Equals(mode, "pending", StringComparison.OrdinalIgnoreCase))
            {
                profiles[0].Status = "Ready";
                profiles[0].IsActive = false;
                profiles[1].Status = "Signing in";
                profiles[1].CanSwitch = false;
                profiles.RemoveAt(2);
                state = new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.PendingOnboarding,
                    Title = "New account sign-in detected",
                    Description = "Return here after the sign-in window is ready, then finish setup.",
                    PreviousProfileId = primaryId,
                    CandidateProfileId = studioId,
                    PendingAuthenticationPresent = true,
                    Profiles = profiles
                };
            }
            else if (string.Equals(mode, "recovery", StringComparison.OrdinalIgnoreCase))
            {
                state = new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.RecoveryRequired,
                    Title = "A previous operation needs recovery",
                    Description = "No account changes are available until the interrupted operation is resolved.",
                    ActiveProfileId = primaryId,
                    PendingTransaction = new PendingTransactionSummary
                    {
                        TransactionId = "44444444-4444-4444-4444-444444444444",
                        Operation = "switch",
                        Phase = TransactionPhases.IncomingAuthStaged
                    },
                    CanRecoverAutomatically = true,
                    Profiles = profiles
                };
            }
            else
            {
                state = new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.Active,
                    Title = "Personal is active",
                    Description = "Switching preserves the newest authentication state while keeping local Codex work shared.",
                    ActiveProfileId = primaryId,
                    Profiles = profiles
                };
            }
            if (mode == "small-unicode")
            {
                profiles[0].DisplayName = "个人账户 · Personal research workspace — long Unicode name";
                profiles[1].DisplayName = "Équipe créative · 開発チーム · Studio workspace with a long label";
                state.Title = profiles[0].DisplayName + " is active";
            }
            IDictionary<string, CodexUsageSnapshot> usage = CodexUsageReader.Inspect(state);
            if (mode == "quota-cached")
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                string json = "{\"rateLimits\":{\"primary\":{\"usedPercent\":35,\"windowDurationMins\":180},\"secondary\":{\"usedPercent\":68,\"windowDurationMins\":4320}}}";
                json = json.Replace("\"windowDurationMins\":180", "\"windowDurationMins\":180,\"resetsAt\":" + now.AddHours(1).ToUnixTimeSeconds());
                usage[primaryId] = CodexUsageReader.AcceptVerifiedResponse(json, primaryId, primaryId, "fixture/account/context", "fixture/account/context", 1, 1, now.AddMinutes(-2), now, false);
                string staleJson = json.Replace(now.AddHours(1).ToUnixTimeSeconds().ToString(), now.AddHours(-3).ToUnixTimeSeconds().ToString());
                usage[studioId] = CodexUsageReader.AcceptVerifiedResponse(staleJson, studioId, studioId, "fixture/studio/context", "fixture/studio/context", 1, 1, now.AddDays(-1), now, false);
                state.Title = "Personal is active · quota preview fixtures";
            }
            return new DesktopInspection
            {
                State = state,
                Usage = usage,
                PackageVersion = "26.715.9079.0",
                PackageProcessCount = 14,
                MainProcessCount = 1
            };
        }

        internal static void ScaleFormForPreview(Form form, float scale)
        {
            Dictionary<Control, Font> fonts = new Dictionary<Control, Font>();
            CollectScaledFonts(form, scale, fonts);
            MainForm main = form as MainForm;
            if (main != null) main.BeginPreviewScale();
            form.SuspendLayout();
            form.AutoScaleMode = AutoScaleMode.None;
            form.Scale(new SizeF(scale, scale));
            foreach (KeyValuePair<Control, Font> entry in fonts) entry.Key.Font = entry.Value;
            form.ResumeLayout(true);
            if (main != null) main.EndPreviewScale();
        }

        private static void CollectScaledFonts(Control control, float scale, Dictionary<Control, Font> fonts)
        {
            fonts.Add(control, new Font(control.Font.FontFamily, control.Font.Size * scale, control.Font.Style));
            foreach (Control child in control.Controls) CollectScaledFonts(child, scale, fonts);
        }
    }
}
