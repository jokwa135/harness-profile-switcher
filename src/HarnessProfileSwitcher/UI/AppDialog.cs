using System;
using System.Drawing;
using System.Windows.Forms;

namespace HarnessProfileSwitcher.UI
{
    internal enum AppDialogTone
    {
        Information,
        Warning,
        Error
    }

    internal sealed class AppDialog : Form
    {
        private AppDialog(
            string title,
            string message,
            string supportingText,
            string primaryText,
            string secondaryText,
            AppDialogTone tone,
            bool destructivePrimary)
        {
            Text = title;
            Font = VisualTheme.Font(10f, FontStyle.Regular);
            BackColor = VisualTheme.Surface;
            ForeColor = VisualTheme.Ink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(580, string.IsNullOrWhiteSpace(supportingText) ? 258 : 344);
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = BrandAssets.WindowIcon(); } catch { }
            HandleCreated += delegate { VisualTheme.ApplyDarkTitleBar(Handle); };

            Panel accent = new Panel
            {
                Dock = DockStyle.Top,
                Height = 5,
                BackColor = tone == AppDialogTone.Error
                    ? VisualTheme.Danger
                    : tone == AppDialogTone.Warning ? VisualTheme.Warning : VisualTheme.Accent
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = VisualTheme.Surface,
                Padding = new Padding(30, 25, 30, 24),
                ColumnCount = 1,
                RowCount = string.IsNullOrWhiteSpace(supportingText) ? 3 : 4,
                Margin = Padding.Empty
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            if (!string.IsNullOrWhiteSpace(supportingText))
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            Label heading = new Label
            {
                Text = title,
                Font = VisualTheme.Font(17f, FontStyle.Bold),
                ForeColor = VisualTheme.Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = Padding.Empty,
                AutoEllipsis = false
            };
            Label body = new Label
            {
                Text = message,
                Font = VisualTheme.Font(10.25f, FontStyle.Regular),
                ForeColor = VisualTheme.MutedInk,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 3, 0, 10),
                AutoEllipsis = false
            };
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(body, 0, 1);

            int buttonRow = 2;
            if (!string.IsNullOrWhiteSpace(supportingText))
            {
                Panel supportingPanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = tone == AppDialogTone.Error
                        ? VisualTheme.SoftDanger
                        : tone == AppDialogTone.Warning ? VisualTheme.SoftWarning : VisualTheme.SoftAccent,
                    Margin = new Padding(0, 0, 0, 10),
                    Padding = new Padding(16, 9, 16, 9)
                };
                supportingPanel.Resize += delegate(object sender, EventArgs resizeArgs)
                {
                    Panel target = (Panel)sender;
                    if (target.Width <= 0 || target.Height <= 0) return;
                    Region previous = target.Region;
                    using (System.Drawing.Drawing2D.GraphicsPath clip =
                        VisualTheme.RoundedRectangle(new Rectangle(0, 0, target.Width, target.Height), 10))
                    {
                        target.Region = new Region(clip);
                    }
                    if (previous != null) previous.Dispose();
                };
                supportingPanel.Controls.Add(new Label
                {
                    Text = supportingText,
                    Font = VisualTheme.Font(9.25f, FontStyle.Bold),
                    ForeColor = tone == AppDialogTone.Error
                        ? VisualTheme.Danger
                        : tone == AppDialogTone.Warning ? VisualTheme.Warning : VisualTheme.AccentText,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = false
                });
                layout.Controls.Add(supportingPanel, 0, 2);
                buttonRow = 3;
            }

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = VisualTheme.Surface,
                Padding = new Padding(0, 8, 0, 0),
                Margin = Padding.Empty
            };
            ActionButton primary = new ActionButton
            {
                Text = primaryText,
                Kind = destructivePrimary ? ActionButtonKind.Danger : ActionButtonKind.Primary,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(116, 42),
                DialogResult = DialogResult.OK,
                AccessibleName = primaryText
            };
            buttons.Controls.Add(primary);
            AcceptButton = primary;

            if (!string.IsNullOrWhiteSpace(secondaryText))
            {
                ActionButton secondary = new ActionButton
                {
                    Text = secondaryText,
                    Kind = ActionButtonKind.Secondary,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    MinimumSize = new Size(96, 42),
                    DialogResult = DialogResult.Cancel,
                    Margin = new Padding(10, 0, 0, 0),
                    AccessibleName = secondaryText
                };
                buttons.Controls.Add(secondary);
                CancelButton = secondary;
            }
            layout.Controls.Add(buttons, 0, buttonRow);

            Controls.Add(layout);
            Controls.Add(accent);
        }

        public static DialogResult Show(
            IWin32Window owner,
            string title,
            string message,
            string primaryText,
            string secondaryText,
            AppDialogTone tone,
            string supportingText,
            bool destructivePrimary)
        {
            using (AppDialog dialog = new AppDialog(
                title,
                message,
                supportingText,
                primaryText,
                secondaryText,
                tone,
                destructivePrimary))
            {
                return dialog.ShowDialog(owner);
            }
        }

        internal static AppDialog CreatePreview(string mode)
        {
            if (string.Equals(mode, "dialog-delete", StringComparison.OrdinalIgnoreCase))
            {
                return new AppDialog(
                    "Delete Studio?",
                    "This permanently removes the saved sign-in and browser session for this profile. Adding it again will require OAuth.",
                    "The active ChatGPT account stays open. Local Codex work is stored separately from this profile.",
                    "Delete profile",
                    "Cancel",
                    AppDialogTone.Warning,
                    true);
            }
            if (string.Equals(mode, "dialog-force", StringComparison.OrdinalIgnoreCase))
            {
                return new AppDialog(
                    "ChatGPT is still closing",
                    "ChatGPT did not close within eight seconds. Force close 10 verified ChatGPT processes so the account operation can continue?",
                    "Only processes inside the installed OpenAI package will be closed. Other Codex terminals are excluded.",
                    "Force close ChatGPT",
                    "Cancel operation",
                    AppDialogTone.Warning,
                    true);
            }
            if (string.Equals(mode, "dialog-error", StringComparison.OrdinalIgnoreCase))
            {
                return new AppDialog(
                    "The operation didn’t finish",
                    "The installed ChatGPT layout is not supported right now. Close extra ChatGPT windows, refresh, and try again.",
                    "The switcher refreshed the current state. Follow the action shown in the main window before trying again.",
                    "Review status",
                    null,
                    AppDialogTone.Error,
                    false);
            }
            return new AppDialog(
                "Register Personal?",
                "ChatGPT will close while its browser session moves to protected storage on D:, then reopen with the same account.",
                "Projects, history, settings, skills, and plugins remain shared on this PC.",
                "Register account",
                "Cancel",
                AppDialogTone.Information,
                false);
        }
    }
}
