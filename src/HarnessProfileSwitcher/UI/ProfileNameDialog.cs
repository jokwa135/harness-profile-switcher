using System;
using System.Drawing;
using System.Windows.Forms;

namespace HarnessProfileSwitcher.UI
{
    internal sealed class ProfileNameDialog : Form
    {
        private readonly TextBox nameBox;
        private readonly Label validation;

        public ProfileNameDialog(string title, string prompt, string actionText)
            : this(title, prompt, actionText, string.Empty)
        {
        }

        public ProfileNameDialog(string title, string prompt, string actionText, string initialName)
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
            ClientSize = new Size(548, 344);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            try { Icon = BrandAssets.WindowIcon(); } catch { }
            HandleCreated += delegate { VisualTheme.ApplyDarkTitleBar(Handle); };

            Panel accent = new Panel
            {
                Dock = DockStyle.Top,
                Height = 5,
                BackColor = VisualTheme.Accent
            };
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = VisualTheme.Surface,
                Padding = new Padding(30, 25, 30, 24),
                ColumnCount = 1,
                RowCount = 6,
                Margin = Padding.Empty
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label heading = new Label
            {
                Text = title,
                Font = VisualTheme.Font(17f, FontStyle.Bold),
                ForeColor = VisualTheme.Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = Padding.Empty
            };
            Label description = new Label
            {
                Text = prompt,
                Font = VisualTheme.Font(10.25f, FontStyle.Regular),
                ForeColor = VisualTheme.MutedInk,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 2, 0, 8),
                AutoEllipsis = false
            };
            Label fieldLabel = new Label
            {
                Text = "Profile name",
                Font = VisualTheme.Font(9.25f, FontStyle.Bold),
                ForeColor = VisualTheme.Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = Padding.Empty
            };
            nameBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = VisualTheme.Font(11.25f, FontStyle.Regular),
                AutoSize = false,
                BorderStyle = BorderStyle.None,
                BackColor = VisualTheme.InputSurfaceColor,
                ForeColor = VisualTheme.Ink,
                Margin = Padding.Empty,
                AccessibleName = "Profile display name"
            };
            InputSurface inputSurface = new InputSurface
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            inputSurface.Attach(nameBox);
            nameBox.TextChanged += delegate
            {
                validation.Visible = false;
            };
            validation = new Label
            {
                Text = "Enter a name you will recognize in the account list.",
                Font = VisualTheme.Font(9.25f, FontStyle.Bold),
                ForeColor = VisualTheme.Danger,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = Padding.Empty,
                Visible = false,
                AccessibleName = "Profile name validation"
            };

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 7, 0, 0),
                BackColor = VisualTheme.Surface,
                Margin = Padding.Empty
            };
            ActionButton accept = new ActionButton
            {
                Text = actionText,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(118, 42),
                AccessibleName = actionText
            };
            ActionButton cancel = new ActionButton
            {
                Text = "Cancel",
                Kind = ActionButtonKind.Secondary,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(96, 42),
                DialogResult = DialogResult.Cancel,
                Margin = new Padding(10, 0, 0, 0),
                AccessibleName = "Cancel"
            };
            accept.Click += delegate
            {
                try
                {
                    Core.ProfileMetadataEditor.ValidateName(nameBox.Text);
                }
                catch (ArgumentException exception)
                {
                    validation.Text = exception.ParamName == "name" && !string.IsNullOrWhiteSpace(nameBox.Text)
                        ? "Names cannot contain control characters." : "Enter a profile name.";
                    validation.Visible = true;
                    nameBox.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(accept);
            buttons.Controls.Add(cancel);

            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(description, 0, 1);
            layout.Controls.Add(fieldLabel, 0, 2);
            layout.Controls.Add(inputSurface, 0, 3);
            layout.Controls.Add(validation, 0, 4);
            layout.Controls.Add(buttons, 0, 5);
            Controls.Add(layout);
            Controls.Add(accent);
            AcceptButton = accept;
            CancelButton = cancel;
            nameBox.Text = initialName ?? string.Empty;
            Shown += delegate { nameBox.Focus(); nameBox.SelectAll(); };
        }

        public string ProfileName
        {
            get { return nameBox.Text.Trim(); }
        }
    }
}
