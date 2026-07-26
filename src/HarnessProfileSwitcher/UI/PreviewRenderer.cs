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
            if (!outputPath.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("UI previews must be written to D:.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (Form form = CreatePreviewForm(mode))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Show();
                System.Windows.Forms.Application.DoEvents();
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

        private static Form CreatePreviewForm(string mode)
        {
            if (string.Equals(mode, "dialog-register", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "dialog-force", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "dialog-error", StringComparison.OrdinalIgnoreCase))
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
                new ProfileSummary { ProfileId = primaryId, DisplayName = "Personal", Status = "Active", IsActive = true },
                new ProfileSummary { ProfileId = studioId, DisplayName = "Studio", Status = "Ready", CanSwitch = true },
                new ProfileSummary { ProfileId = clientId, DisplayName = "Client workspace", Status = "Ready", CanSwitch = true }
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
            return new DesktopInspection
            {
                State = state,
                PackageVersion = "26.715.9079.0",
                PackageProcessCount = 14,
                MainProcessCount = 1
            };
        }
    }
}
