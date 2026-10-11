using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using HarnessProfileSwitcher.Application;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.UI
{
    internal sealed class MainForm : Form
    {
        private readonly ApplicationServices services;
        private readonly StatusBadge packageStatus;
        private readonly Label stateContext;
        private readonly Label heroTitle;
        private readonly Label heroDescription;
        private readonly FlowLayoutPanel heroActions;
        private readonly Panel operationPanel;
        private readonly Label operationStatus;
        private readonly ActivityBar activityBar;
        private readonly FlowLayoutPanel profilesPanel;
        private readonly Label accountsSummary;
        private readonly Label footerStatus;
        private readonly ActionButton refreshButton;
        private readonly TableLayoutPanel content;
        private readonly FlowLayoutPanel accountActions;
        private DesktopInspection inspection;
        private bool busy;
        private bool previewScaling;
        private bool resizingCards;

        public MainForm(ApplicationServices services)
            : this(services, null)
        {
        }

        public MainForm(DesktopInspection preview)
            : this(null, preview)
        {
        }

        private MainForm(ApplicationServices services, DesktopInspection preview)
        {
            this.services = services;
            Text = "Harness Profile Switcher";
            try { Icon = BrandAssets.WindowIcon(); } catch { Icon = SystemIcons.Application; }
            HandleCreated += delegate { VisualTheme.ApplyDarkTitleBar(Handle); };
            DoubleBuffered = true;
            BackColor = VisualTheme.Canvas;
            ForeColor = VisualTheme.Ink;
            Font = VisualTheme.Font(10f, FontStyle.Regular);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(960, 760);
            MinimumSize = new Size(760, 600);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = VisualTheme.Canvas,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            Panel header = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = VisualTheme.Chrome,
                Padding = new Padding(28, 14, 28, 14),
                Margin = Padding.Empty
            };
            TableLayoutPanel headerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = VisualTheme.Chrome,
                Margin = Padding.Empty
            };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 192));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            BrandMark mark = new BrandMark
            {
                Margin = new Padding(0, 5, 12, 0)
            };
            TableLayoutPanel titles = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = VisualTheme.Chrome,
                Margin = Padding.Empty
            };
            titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            titles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            titles.Controls.Add(new Label
            {
                Text = "Harness Profile Switcher",
                Font = VisualTheme.Font(15.5f, FontStyle.Bold),
                ForeColor = VisualTheme.Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = Padding.Empty
            }, 0, 0);
            titles.Controls.Add(new Label
            {
                Text = "ChatGPT accounts · shared local Codex work",
                Font = VisualTheme.Font(9.5f, FontStyle.Regular),
                ForeColor = VisualTheme.MutedInk,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = Padding.Empty
            }, 0, 1);

            packageStatus = new StatusBadge
            {
                Text = "Checking ChatGPT",
                Kind = StatusBadgeKind.Neutral,
                Size = new Size(182, 38),
                Anchor = AnchorStyles.None,
                Margin = new Padding(8, 0, 0, 0)
            };
            refreshButton = new ActionButton
            {
                Text = "Refresh",
                Kind = ActionButtonKind.Secondary,
                Glyph = ButtonGlyph.Refresh,
                Size = new Size(112, 42),
                Anchor = AnchorStyles.None,
                Margin = new Padding(10, 0, 0, 0),
                AccessibleName = "Refresh account state"
            };
            refreshButton.Click += async delegate { await RefreshInspectionAsync(); };
            headerLayout.Controls.Add(mark, 0, 0);
            headerLayout.Controls.Add(titles, 1, 0);
            headerLayout.Controls.Add(packageStatus, 2, 0);
            headerLayout.Controls.Add(refreshButton, 3, 0);
            header.Controls.Add(headerLayout);

            content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(24, 18, 24, 12),
                BackColor = VisualTheme.Canvas,
                Margin = Padding.Empty
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            SurfacePanel hero = new SurfacePanel
            {
                Dock = DockStyle.Fill,
                CornerRadius = 12,
                Padding = new Padding(20, 12, 20, 10),
                Margin = Padding.Empty
            };
            TableLayoutPanel heroLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.Transparent,
                Margin = Padding.Empty
            };
            heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heroLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stateContext = new Label
            {
                Text = "Account status",
                Font = VisualTheme.Font(9f, FontStyle.Bold),
                ForeColor = VisualTheme.AccentText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = Padding.Empty
            };
            heroTitle = new Label
            {
                Text = "Loading account state",
                AutoEllipsis = true,
                Font = VisualTheme.Font(16f, FontStyle.Bold),
                ForeColor = VisualTheme.Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
            heroDescription = new Label
            {
                Text = "Checking the local profile ledger and ChatGPT package.",
                Font = VisualTheme.Font(10.5f, FontStyle.Regular),
                ForeColor = VisualTheme.MutedInk,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 2, 0, 5),
                AutoEllipsis = false
            };

            Panel actionHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Height = 50,
                Visible = false
            };
            heroActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = new Padding(0, 4, 0, 0)
            };
            operationPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Visible = false
            };
            TableLayoutPanel operationLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = Padding.Empty
            };
            operationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            operationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            operationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            operationStatus = new Label
            {
                Text = "Working safely",
                Font = VisualTheme.Font(10f, FontStyle.Bold),
                ForeColor = VisualTheme.AccentText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
            activityBar = new ActivityBar
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Height = 6,
                Margin = new Padding(18, 0, 0, 0),
                Visible = false
            };
            operationLayout.Controls.Add(operationStatus, 0, 0);
            operationLayout.Controls.Add(activityBar, 1, 0);
            operationPanel.Controls.Add(operationLayout);
            actionHost.Controls.Add(operationPanel);
            actionHost.Controls.Add(heroActions);

            heroLayout.Controls.Add(stateContext, 0, 0);
            heroLayout.Controls.Add(heroTitle, 0, 1);
            heroLayout.Controls.Add(heroDescription, 0, 2);
            heroLayout.Controls.Add(actionHost, 0, 3);
            hero.Controls.Add(heroLayout);

            TableLayoutPanel accountsHeader = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = VisualTheme.Canvas,
                Margin = Padding.Empty
            };
            accountsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            accountsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            accountsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
            accountsHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            accountsHeader.Controls.Add(new Label
            {
                Text = "Accounts",
                Font = VisualTheme.Font(12f, FontStyle.Bold),
                ForeColor = VisualTheme.Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(2, 0, 0, 10)
            }, 0, 0);
            accountsSummary = new Label
            {
                Text = "No saved accounts",
                Font = VisualTheme.Font(9f, FontStyle.Regular),
                ForeColor = VisualTheme.SubtleInk,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0, 0, 8, 10)
            };
            accountsHeader.Controls.Add(accountsSummary, 1, 0);
            accountActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, BackColor = VisualTheme.Canvas, Margin = Padding.Empty,
                Padding = new Padding(0, 8, 0, 0)
            };
            accountsHeader.Controls.Add(accountActions, 2, 0);

            profilesPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = VisualTheme.Canvas,
                Margin = Padding.Empty,
                Padding = new Padding(0, 0, 6, 0)
            };
            profilesPanel.SizeChanged += delegate { ResizeProfileCards(); };
            content.Controls.Add(hero, 0, 0);
            content.Controls.Add(accountsHeader, 0, 1);
            content.Controls.Add(profilesPanel, 0, 2);

            Panel footer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = VisualTheme.Chrome,
                Padding = new Padding(30, 0, 30, 0),
                Margin = Padding.Empty
            };
            footerStatus = new Label
            {
                Text = "Ready",
                Font = VisualTheme.Font(9f, FontStyle.Regular),
                ForeColor = VisualTheme.MutedInk,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
            footer.Controls.Add(footerStatus);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(content, 0, 1);
            root.Controls.Add(footer, 0, 2);
            Controls.Add(root);
            EnableDoubleBuffering(root);

            Shown += async delegate
            {
                if (preview != null)
                {
                    ApplyInspection(preview);
                }
                else
                {
                    await RefreshInspectionAsync();
                }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs eventArgs)
            {
                if (!busy) return;
                System.Media.SystemSounds.Beep.Play();
                eventArgs.Cancel = true;
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // WS_EX_COMPOSITED: composite the whole window (and all children) into one
                // off-screen buffer, so resizing repaints in a single flicker-free pass.
                CreateParams createParams = base.CreateParams;
                createParams.ExStyle |= 0x02000000;
                return createParams;
            }
        }

        private static void EnableDoubleBuffering(Control control)
        {
            System.Reflection.PropertyInfo property = typeof(Control).GetProperty(
                "DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (property != null && (control is TableLayoutPanel || control is FlowLayoutPanel || control is Panel))
            {
                try { property.SetValue(control, true, null); }
                catch { }
            }
            foreach (Control child in control.Controls)
            {
                EnableDoubleBuffering(child);
            }
        }

        private async Task RefreshInspectionAsync()
        {
            if (services == null || busy) return;
            SetBusy(true, "Checking ChatGPT and saved account state");
            try
            {
                inspection = await Task.Run(delegate { return services.Inspect(); });
                ApplyInspection(inspection);
                footerStatus.Text = "Account state refreshed";
            }
            catch (Exception exception)
            {
                ApplyInspection(ErrorInspection(exception));
                footerStatus.Text = "ChatGPT compatibility check failed";
            }
            finally
            {
                SetBusy(false, footerStatus.Text);
            }
        }

        private void ApplyInspection(DesktopInspection value)
        {
            inspection = value;
            ApplicationStateSnapshot state = value.State;
            stateContext.Text = ContextFor(state.Mode);
            heroTitle.Text = state.Title;
            heroDescription.Text = DescriptionFor(state);
            bool normal = state.Mode == HarnessMode.Active;
            content.RowStyles[0].Height = (normal ? 108 : 208) * CurrentAutoScaleDimensions.Width / 96f;
            heroActions.Parent.Visible = !normal;
            packageStatus.Text = value.MainProcessCount == 1
                ? "ChatGPT open"
                : value.MainProcessCount == 0 ? "ChatGPT closed" : "Check ChatGPT";
            packageStatus.Kind = value.MainProcessCount == 1
                ? StatusBadgeKind.Success
                : value.MainProcessCount == 0 ? StatusBadgeKind.Neutral : StatusBadgeKind.Warning;
            ConfigureHeroActions(state);
            ConfigureProfiles(state);
        }

        private void ConfigureHeroActions(ApplicationStateSnapshot state)
        {
            while (heroActions.Controls.Count > 0) heroActions.Controls[0].Dispose();
            while (accountActions.Controls.Count > 0) accountActions.Controls[0].Dispose();
            if (state.Mode == HarnessMode.Unmanaged)
            {
                AddHeroAction("Register current account", ActionButtonKind.Primary, ButtonGlyph.Add, async delegate { await RegisterCurrentAsync(); });
                return;
            }
            if (state.Mode == HarnessMode.Active)
            {
                ActionButton add = new ActionButton
                {
                    Text = "Add account", Kind = ActionButtonKind.Primary, Glyph = ButtonGlyph.Add,
                    Size = new Size(156, 40), AccessibleName = "Add account", Margin = Padding.Empty
                };
                add.Click += async delegate { await BeginOnboardingAsync(); };
                accountActions.Controls.Add(add);
                return;
            }
            if (state.Mode == HarnessMode.PendingOnboarding)
            {
                ActionButton finish = AddHeroAction("Complete setup", ActionButtonKind.Primary, ButtonGlyph.Check, async delegate { await FinalizeOnboardingAsync(); });
                finish.Enabled = state.PendingAuthenticationPresent;
                AddHeroAction("Check sign-in", ActionButtonKind.Secondary, ButtonGlyph.Refresh, async delegate { await RefreshInspectionAsync(); });
                AddHeroAction("Cancel setup", ActionButtonKind.Ghost, ButtonGlyph.Close, async delegate { await CancelOnboardingAsync(); });
                return;
            }
            if (state.Mode == HarnessMode.RecoveryRequired)
            {
                if (state.CanRecoverAutomatically)
                {
                    AddHeroAction("Recover safely", ActionButtonKind.Primary, ButtonGlyph.Recover, async delegate { await RecoverAsync(); });
                }
                else
                {
                    AddHeroAction("Check again", ActionButtonKind.Secondary, ButtonGlyph.Refresh, async delegate { await RefreshInspectionAsync(); });
                }
                return;
            }
            AddHeroAction("Check again", ActionButtonKind.Secondary, ButtonGlyph.Refresh, async delegate { await RefreshInspectionAsync(); });
        }

        private ActionButton AddHeroAction(string text, ActionButtonKind kind, ButtonGlyph glyph, EventHandler click)
        {
            ActionButton button = new ActionButton
            {
                Text = text,
                Kind = kind,
                Glyph = glyph,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 10, 0),
                AccessibleName = text
            };
            button.Click += click;
            heroActions.Controls.Add(button);
            return button;
        }

        private void ConfigureProfiles(ApplicationStateSnapshot state)
        {
            profilesPanel.SuspendLayout();
            while (profilesPanel.Controls.Count > 0) profilesPanel.Controls[0].Dispose();
            int count = state.Profiles == null ? 0 : state.Profiles.Count;
            accountsSummary.Text = count == 0
                ? "No saved accounts"
                : count == 1 ? "1 saved account" : count + " saved accounts";
            if (count == 0)
            {
                SurfacePanel empty = new SurfacePanel
                {
                    Height = 88,
                    CornerRadius = 14,
                    Padding = new Padding(20, 14, 20, 12),
                    FillColor = VisualTheme.Surface,
                    Tag = "profile-card"
                };
                TableLayoutPanel emptyLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 2,
                    BackColor = Color.Transparent,
                    Margin = Padding.Empty
                };
                emptyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                emptyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                emptyLayout.Controls.Add(new Label
                {
                    Text = state.Mode == HarnessMode.Unmanaged ? "No accounts registered yet" : "No readable accounts found",
                    Font = VisualTheme.Font(11f, FontStyle.Bold),
                    ForeColor = VisualTheme.Ink,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = Padding.Empty
                }, 0, 0);
                emptyLayout.Controls.Add(new Label
                {
                    Text = state.Mode == HarnessMode.Unmanaged
                        ? "Register the account currently open in ChatGPT to start switching."
                        : "Refresh the account state before taking another action.",
                    Font = VisualTheme.Font(9.5f, FontStyle.Regular),
                    ForeColor = VisualTheme.MutedInk,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.TopLeft,
                    Margin = Padding.Empty
                }, 0, 1);
                empty.Controls.Add(emptyLayout);
                profilesPanel.Controls.Add(empty);
            }
            else
            {
                foreach (ProfileSummary profile in state.Profiles)
                {
                    profilesPanel.Controls.Add(CreateProfileCard(profile, state.Mode));
                }
            }
            profilesPanel.ResumeLayout();
            ResizeProfileCards();
        }

        private Control CreateProfileCard(ProfileSummary profile, HarnessMode mode)
        {
            CodexUsageSnapshot usage;
            if (inspection.Usage == null || !inspection.Usage.TryGetValue(profile.ProfileId, out usage))
                usage = CodexUsageSnapshot.Unavailable(CodexUsageReader.SafetyReason);
            DateTimeOffset observedNow = DateTimeOffset.UtcNow;
            SurfacePanel card = new SurfacePanel
            {
                Height = 156 + Math.Max(0, usage.Windows.Count - 2) * 22, CornerRadius = 12, Padding = new Padding(18, 10, 18, 10),
                Margin = new Padding(0, 0, 0, 10), Tag = "profile-card",
                FillColor = profile.IsActive ? VisualTheme.SoftAccent : VisualTheme.Surface,
                Emphasized = profile.IsActive, AccessibleName = profile.DisplayName + " profile"
            };
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5,
                BackColor = Color.Transparent, Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            Avatar avatar = new Avatar { Size = new Size(42, 42), Anchor = AnchorStyles.Left,
                Muted = profile.Status == "Broken", Margin = Padding.Empty };
            avatar.SetName(profile.DisplayName);
            layout.Controls.Add(avatar, 0, 0);
            layout.SetRowSpan(avatar, 2);
            Label name = CardLabel(profile.DisplayName, 12f, VisualTheme.Ink, FontStyle.Bold);
            name.AutoEllipsis = true;
            name.AccessibleName = profile.DisplayName;
            layout.Controls.Add(name, 1, 0);
            layout.Controls.Add(CardLabel(ProfileDetailStatus(profile, mode), 9.5f, StatusColor(profile.Status), FontStyle.Regular), 1, 1);
            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
                BackColor = Color.Transparent, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0)
            };
            ActionButton more = new ActionButton { Text = "More", Kind = ActionButtonKind.Ghost,
                Size = new Size(92, 42), Margin = Padding.Empty, AccessibleName = "More actions for " + profile.DisplayName,
                Enabled = mode == HarnessMode.Active && profile.CanRename };
            ContextMenuStrip menu = new ContextMenuStrip { BackColor = VisualTheme.Elevated, ForeColor = VisualTheme.Ink,
                Font = VisualTheme.Font(10f, FontStyle.Regular), ShowImageMargin = false };
            ToolStripMenuItem rename = new ToolStripMenuItem("Rename profile…") { Enabled = more.Enabled };
            rename.Click += async delegate { await RenameProfileAsync(profile); };
            menu.Items.Add(rename);
            if (!profile.IsActive && profile.CanDelete)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem delete = new ToolStripMenuItem("Delete profile…") { ForeColor = VisualTheme.Danger,
                    Enabled = mode == HarnessMode.Active };
                delete.Click += async delegate { await DeleteProfileAsync(profile); };
                menu.Items.Add(delete);
            }
            more.ContextMenuStrip = menu;
            more.Click += delegate { menu.Show(more, new Point(0, more.Height)); };
            more.Disposed += delegate { menu.Dispose(); };
            actions.Controls.Add(more);
            if (profile.CanSwitch && mode == HarnessMode.Active)
            {
                ActionButton switchButton = new ActionButton { Text = "Switch", Kind = ActionButtonKind.Secondary,
                    Glyph = ButtonGlyph.Switch, Size = new Size(120, 42), Margin = new Padding(0, 0, 8, 0),
                    AccessibleName = "Switch to " + profile.DisplayName };
                switchButton.Click += async delegate { await SwitchAsync(profile); };
                actions.Controls.Add(switchButton);
            }
            else
            {
                Label status = CardLabel(ProfileActionStatus(profile, mode), 9.5f, ProfileActionColor(profile, mode), FontStyle.Bold);
                status.Size = new Size(120, 42); status.Dock = DockStyle.None;
                status.TextAlign = ContentAlignment.MiddleRight;
                status.Margin = new Padding(0, 0, 8, 0);
                actions.Controls.Add(status);
            }
            layout.Controls.Add(actions, 2, 0); layout.SetRowSpan(actions, 2);
            string quotaStatus = usage.ObservedAt.HasValue && usage.Freshness(observedNow).StartsWith("Stale", StringComparison.Ordinal)
                ? "Stale snapshot" : usage.Status;
            Label limits = CardLabel("Codex limits · " + quotaStatus, 9.5f, VisualTheme.MutedInk, FontStyle.Bold);
            limits.Padding = new Padding(0, 4, 0, 0);
            layout.Controls.Add(limits, 0, 2); layout.SetColumnSpan(limits, 3);
            Label quota = CardLabel(usage.Detail(observedNow), 9.5f, VisualTheme.MutedInk, FontStyle.Regular);
            quota.AutoEllipsis = true;
            layout.Controls.Add(quota, 0, 3); layout.SetColumnSpan(quota, 3);
            Label freshness = CardLabel(usage.Freshness(observedNow), 9f, VisualTheme.SubtleInk, FontStyle.Regular);
            freshness.AutoEllipsis = true;
            layout.Controls.Add(freshness, 0, 4); layout.SetColumnSpan(freshness, 3);
            card.Controls.Add(layout);
            float dpiScale = CurrentAutoScaleDimensions.Width / 96f;
            if (dpiScale != 1f) card.Scale(new SizeF(dpiScale, dpiScale));
            return card;
        }

        private static Label CardLabel(string text, float size, Color color, FontStyle style)
        {
            return new Label { Text = text, Font = VisualTheme.Font(size, style), ForeColor = color,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        }

        private async Task RenameProfileAsync(ProfileSummary profile)
        {
            if (services == null || busy) return;
            string name;
            using (ProfileNameDialog dialog = new ProfileNameDialog("Rename profile",
                "Changes this local label only. Your sign-in, account folder and local work stay unchanged. ChatGPT stays open.",
                "Save name", profile.DisplayName))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                name = dialog.ProfileName;
            }
            if (string.Equals(name, profile.DisplayName, StringComparison.Ordinal))
            {
                footerStatus.Text = "Name unchanged"; return;
            }
            SetBusy(true, "Saving local profile name");
            Exception failure = null;
            try
            {
                bool changed = await Task.Run(delegate { return services.RenameProfile(profile.ProfileId, name); });
                ApplyInspection(await Task.Run(delegate { return services.Inspect(); }));
                footerStatus.Text = changed ? "Profile renamed · ChatGPT stayed open" : "Name unchanged";
            }
            catch (Exception exception) { failure = exception; }
            if (failure != null)
            {
                // Metadata-only failure: no CreateOperationContext, recovery relaunch, or process control.
                try { ApplyInspection(await Task.Run(delegate { return services.Inspect(); })); }
                catch { }
                footerStatus.Text = "Could not confirm the saved name · ChatGPT was not touched";
                AppDialog.Show(this, "Review the profile name", SafeErrorMessage(failure), "OK", null,
                    AppDialogTone.Error, "No account was switched, stopped, or relaunched.", false);
            }
            SetBusy(false, footerStatus.Text);
        }

        private async Task RegisterCurrentAsync()
        {
            string name = PromptForName(
                "Register current account",
                "Choose a local name for the account already open in ChatGPT. Credentials are never read for an email address.",
                "Continue");
            if (name == null) return;
            if (AppDialog.Show(
                this,
                "Register " + name + "?",
                "ChatGPT will close while its browser session moves to protected storage on D:, then reopen with the same account.",
                "Register account",
                "Cancel",
                AppDialogTone.Information,
                "Projects, history, settings, skills, and plugins remain shared on this PC.",
                false) != DialogResult.OK) return;
            await RunMutationAsync("Registering " + name, delegate(OperationContext context)
            {
                services.RegisterFirstAndRelaunch(context, name);
            });
        }

        private async Task BeginOnboardingAsync()
        {
            string name = PromptForName(
                "Add an account",
                "Choose a local name for the account you are about to sign in to. ChatGPT will reopen at a clean sign-in screen.",
                "Prepare sign-in");
            if (name == null) return;
            await RunMutationAsync("Preparing a clean sign-in for " + name, delegate(OperationContext context)
            {
                services.BeginOnboardingAndRelaunch(context, name);
            });
        }

        private async Task FinalizeOnboardingAsync()
        {
            await RunMutationAsync("Completing account setup", delegate(OperationContext context)
            {
                services.FinalizeOnboardingAndRelaunch(context);
            });
        }

        private async Task CancelOnboardingAsync()
        {
            if (AppDialog.Show(
                this,
                "Cancel this account setup?",
                "The previous account will be restored and ChatGPT will reopen with it.",
                "Cancel setup",
                "Keep signing in",
                AppDialogTone.Warning,
                "Any partial sign-in data will be preserved under D:\\fordeletion, never discarded.",
                true) != DialogResult.OK) return;
            await RunMutationAsync("Restoring the previous account", delegate(OperationContext context)
            {
                services.CancelOnboardingAndRelaunch(context);
            });
        }

        private async Task SwitchAsync(ProfileSummary profile)
        {
            if (AppDialog.Show(
                this,
                "Switch to " + profile.DisplayName + "?",
                "ChatGPT will close and reopen using this account. Your local Codex projects and history stay shared.",
                "Switch account",
                "Cancel",
                AppDialogTone.Information,
                "Confirm the account shown in ChatGPT before starting sensitive work.",
                false) != DialogResult.OK) return;
            await RunMutationAsync("Switching to " + profile.DisplayName, delegate(OperationContext context)
            {
                services.SwitchAndRelaunch(context, profile.ProfileId);
            });
        }

        private async Task DeleteProfileAsync(ProfileSummary profile)
        {
            if (AppDialog.Show(
                this,
                "Delete " + profile.DisplayName + "?",
                "This permanently removes the saved sign-in and browser session for this profile. Adding it again will require OAuth.",
                "Delete profile",
                "Cancel",
                AppDialogTone.Warning,
                "The active ChatGPT account stays open. Local Codex work is stored separately from this profile.",
                true) != DialogResult.OK) return;
            await RunInPlaceMutationAsync(
                "Deleting " + profile.DisplayName,
                profile.DisplayName + " was deleted; ChatGPT stayed open",
                delegate(OperationContext context)
                {
                    services.DeleteProfile(context, profile.ProfileId);
                });
        }

        private async Task RecoverAsync()
        {
            PendingTransactionSummary pending = inspection == null ? null : inspection.State.PendingTransaction;
            if (pending == null) return;
            bool deletingProfile = string.Equals(
                pending.Operation,
                ProfileDeletionEngine.DeleteProfileOperation,
                StringComparison.Ordinal);
            if (AppDialog.Show(
                this,
                deletingProfile ? "Finish deleting the profile?" : "Recover the last account operation?",
                deletingProfile
                    ? "The saved deletion journal will finish permanently removing the selected inactive profile."
                    : "The switcher will use its saved journal to safely finish or reverse the interrupted " + FriendlyOperation(pending.Operation) + ".",
                deletingProfile ? "Finish deletion" : "Recover safely",
                "Cancel",
                AppDialogTone.Warning,
                deletingProfile
                    ? "The active ChatGPT account and shared local Codex work remain unchanged."
                    : "Recovery will not choose a different account or discard saved profile data.",
                deletingProfile) != DialogResult.OK) return;
            if (deletingProfile)
            {
                await RunInPlaceMutationAsync(
                    "Finishing profile deletion",
                    "Profile deletion recovered; ChatGPT stayed open",
                    delegate(OperationContext context)
                    {
                        services.RecoverProfileDeletion(context);
                    });
                return;
            }
            await RunMutationAsync("Recovering the last account operation", delegate(OperationContext context)
            {
                services.RecoverAndRelaunch(context, pending);
            });
        }

        private async Task RunMutationAsync(string status, Action<OperationContext> operation)
        {
            if (services == null || busy) return;
            OperationContext context = null;
            Exception failure = null;
            SetBusy(true, "Closing ChatGPT safely");
            try
            {
                context = await Task.Run(delegate { return services.CreateOperationContext(); });
                CloseAttempt close = await Task.Run(delegate
                {
                    return context.ProcessController.TryCloseGracefully(TimeSpan.FromSeconds(8));
                });
                if (!close.Stopped)
                {
                    int count = close.RemainingProcesses == null ? 0 : close.RemainingProcesses.Processes.Count;
                    DialogResult force = AppDialog.Show(
                        this,
                        "ChatGPT is still closing",
                        "ChatGPT did not close within eight seconds. Force close " + count + " verified ChatGPT process" + (count == 1 ? "" : "es") + " so the account operation can continue?",
                        "Force close ChatGPT",
                        "Cancel operation",
                        AppDialogTone.Warning,
                        "Only processes inside the installed OpenAI package will be closed. Other Codex terminals are excluded.",
                        true);
                    if (force != DialogResult.OK) throw new OperationCanceledException();
                    await Task.Run(delegate
                    {
                        context.ProcessController.ForceCloseConfirmed(close.ForceConfirmationToken, TimeSpan.FromSeconds(10));
                    });
                }

                SetBusy(true, status);
                await Task.Run(delegate { operation(context); });
                footerStatus.Text = "Operation completed; ChatGPT reopened";
                inspection = await Task.Run(delegate { return services.Inspect(); });
                ApplyInspection(inspection);
            }
            catch (OperationCanceledException)
            {
                footerStatus.Text = "Operation cancelled before account state changed";
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            if (failure != null)
            {
                await RefreshAfterFailureAsync(context);
                footerStatus.Text = "Review the current account state";
                AppDialog.Show(
                    this,
                    inspection != null && inspection.State.Mode == HarnessMode.RecoveryRequired
                        ? "Recovery is required"
                        : "The operation didn’t finish",
                    SafeErrorMessage(failure),
                    "Review status",
                    null,
                    AppDialogTone.Error,
                    inspection != null && inspection.State.Mode == HarnessMode.RecoveryRequired
                        ? "Use Recover safely before attempting another account action."
                        : "The switcher refreshed the current state. Follow the action shown in the main window before trying again.",
                    false);
            }
            SetBusy(false, footerStatus.Text);
        }

        private async Task RunInPlaceMutationAsync(
            string status,
            string completionStatus,
            Action<OperationContext> operation)
        {
            if (services == null || busy) return;
            OperationContext context = null;
            Exception failure = null;
            SetBusy(true, status);
            try
            {
                context = await Task.Run(delegate { return services.CreateOperationContext(); });
                await Task.Run(delegate { operation(context); });
                inspection = await Task.Run(delegate { return services.Inspect(); });
                ApplyInspection(inspection);
                footerStatus.Text = completionStatus;
            }
            catch (OperationCanceledException)
            {
                footerStatus.Text = "Operation cancelled before account state changed";
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            if (failure != null)
            {
                await RefreshAfterFailureAsync(context);
                footerStatus.Text = "Review the current account state";
                AppDialog.Show(
                    this,
                    inspection != null && inspection.State.Mode == HarnessMode.RecoveryRequired
                        ? "Recovery is required"
                        : "The operation didn’t finish",
                    SafeErrorMessage(failure),
                    "Review status",
                    null,
                    AppDialogTone.Error,
                    inspection != null && inspection.State.Mode == HarnessMode.RecoveryRequired
                        ? "Use Recover safely before attempting another account action."
                        : "The switcher refreshed the current state. Follow the action shown in the main window before trying again.",
                    false);
            }
            SetBusy(false, footerStatus.Text);
        }

        private async Task RefreshAfterFailureAsync(OperationContext context)
        {
            try
            {
                inspection = await Task.Run(delegate { return services.Inspect(); });
                if (context != null && inspection.MainProcessCount == 0 &&
                    inspection.State.PendingTransaction == null &&
                    (inspection.State.Mode == HarnessMode.Active ||
                     inspection.State.Mode == HarnessMode.PendingOnboarding ||
                     inspection.State.Mode == HarnessMode.Unmanaged))
                {
                    try
                    {
                        await Task.Run(delegate { context.Launcher.LaunchAndWait(TimeSpan.FromSeconds(30)); });
                        inspection = await Task.Run(delegate { return services.Inspect(); });
                    }
                    catch
                    {
                    }
                }
                ApplyInspection(inspection);
            }
            catch (Exception inspectionError)
            {
                ApplyInspection(ErrorInspection(inspectionError));
            }
        }

        private string PromptForName(string title, string prompt, string action)
        {
            using (ProfileNameDialog dialog = new ProfileNameDialog(title, prompt, action))
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.ProfileName : null;
            }
        }

        private void SetBusy(bool value, string status)
        {
            busy = value;
            heroActions.Enabled = !value;
            accountActions.Enabled = !value;
            profilesPanel.Enabled = !value;
            refreshButton.Enabled = !value;
            heroActions.Visible = !value;
            operationPanel.Visible = value;
            if (operationPanel.Parent != null)
                operationPanel.Parent.Visible = value || (inspection != null && inspection.State.Mode != HarnessMode.Active);
            activityBar.Visible = value;
            operationStatus.Text = status;
            footerStatus.Text = status;
            UseWaitCursor = value;
        }

        private void ResizeProfileCards()
        {
            if (previewScaling || resizingCards) return;
            resizingCards = true;
            profilesPanel.SuspendLayout();
            int width = Math.Max(300, profilesPanel.ClientSize.Width - profilesPanel.Padding.Horizontal - 12);
            foreach (Control control in profilesPanel.Controls)
            {
                if (string.Equals(Convert.ToString(control.Tag), "profile-card", StringComparison.Ordinal)) control.Width = width;
            }
            profilesPanel.ResumeLayout(true);
            profilesPanel.PerformLayout();
            resizingCards = false;
        }

        internal void BeginPreviewScale() { previewScaling = true; }
        internal void EndPreviewScale() { previewScaling = false; ResizeProfileCards(); PerformLayout(); }

        private static string ContextFor(HarnessMode mode)
        {
            if (mode == HarnessMode.Active) return "Active profile";
            if (mode == HarnessMode.Unmanaged) return "First-time setup";
            if (mode == HarnessMode.PendingOnboarding) return "Account sign-in";
            if (mode == HarnessMode.RecoveryRequired) return "Safe recovery";
            return "Account status";
        }

        private static string DescriptionFor(ApplicationStateSnapshot state)
        {
            if (state.Mode == HarnessMode.Active)
            {
                return "Projects, history, settings, skills, and plugins stay shared on this PC.";
            }
            if (state.Mode == HarnessMode.Unmanaged)
            {
                return "Register the account already open in ChatGPT. Its browser session moves to protected storage on D:, while your local Codex work remains shared.";
            }
            if (state.Mode == HarnessMode.PendingOnboarding)
            {
                return state.PendingAuthenticationPresent
                    ? "The new sign-in is ready. Complete setup to save this account and reopen ChatGPT."
                    : "Finish signing in through ChatGPT, then return here and check the sign-in state.";
            }
            return state.Description;
        }

        private static string FriendlyProfileStatus(ProfileSummary profile)
        {
            if (profile.IsActive) return "Current account";
            if (string.Equals(profile.Status, "Ready", StringComparison.Ordinal)) return "Ready to switch";
            if (string.Equals(profile.Status, "Signing in", StringComparison.Ordinal)) return "Sign-in in progress";
            if (string.Equals(profile.Status, "Broken", StringComparison.Ordinal)) return "Needs attention";
            return profile.Status;
        }

        private static string ProfileActionStatus(ProfileSummary profile, HarnessMode mode)
        {
            if (profile.IsActive) return "Current";
            if (mode == HarnessMode.PendingOnboarding)
            {
                return string.Equals(profile.Status, "Signing in", StringComparison.Ordinal)
                    ? "Finish setup above"
                    : "Available after setup";
            }
            if (mode == HarnessMode.RecoveryRequired) return "Locked for recovery";
            if (mode == HarnessMode.Broken) return "Unavailable";
            return FriendlyProfileStatus(profile);
        }

        private static string ProfileDetailStatus(ProfileSummary profile, HarnessMode mode)
        {
            if (mode == HarnessMode.PendingOnboarding && string.Equals(profile.Status, "Signing in", StringComparison.Ordinal))
            {
                return "New account sign-in";
            }
            if (mode != HarnessMode.Active && string.Equals(profile.Status, "Ready", StringComparison.Ordinal))
            {
                return "Saved account";
            }
            return FriendlyProfileStatus(profile);
        }

        private static Color ProfileActionColor(ProfileSummary profile, HarnessMode mode)
        {
            if (profile.IsActive) return VisualTheme.Success;
            if (mode == HarnessMode.PendingOnboarding && string.Equals(profile.Status, "Signing in", StringComparison.Ordinal))
            {
                return VisualTheme.Warning;
            }
            if (mode != HarnessMode.Active) return VisualTheme.SubtleInk;
            return StatusColor(profile.Status);
        }

        private static Color StatusColor(string status)
        {
            if (string.Equals(status, "Active", StringComparison.Ordinal)) return VisualTheme.Success;
            if (string.Equals(status, "Broken", StringComparison.Ordinal)) return VisualTheme.Danger;
            if (string.Equals(status, "Signing in", StringComparison.Ordinal)) return VisualTheme.Warning;
            return VisualTheme.MutedInk;
        }

        private static string FriendlyOperation(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return "account operation";
            return operation.Replace('-', ' ');
        }

        private static string SafeErrorMessage(Exception exception)
        {
            return SafeUserMessages.ForOperation(exception);
        }

        private static DesktopInspection ErrorInspection(Exception exception)
        {
            return new DesktopInspection
            {
                State = new ApplicationStateSnapshot
                {
                    Mode = HarnessMode.Broken,
                    Title = "ChatGPT needs attention",
                    Description = SafeErrorMessage(exception),
                    Profiles = new ProfileSummary[0]
                },
                PackageVersion = "unknown",
                PackageProcessCount = 0,
                MainProcessCount = 0
            };
        }
    }
}
