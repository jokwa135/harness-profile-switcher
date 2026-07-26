using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HarnessProfileSwitcher.UI
{
    internal static class VisualTheme
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static void ApplyDarkTitleBar(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            int enabled = 1;
            try
            {
                if (DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(window, 19, ref enabled, sizeof(int));
                }
            }
            catch
            {
            }
        }

        private static readonly string PreferredFontFamily = ResolveFontFamily();

        // Dark theme — GitHub/IDE "code dark" developer aesthetic, accents derived from the brand logo (blue #459AE5, orange #FA8C42).
        public static readonly Color Canvas = Color.FromArgb(13, 17, 23);
        public static readonly Color Surface = Color.FromArgb(22, 27, 34);
        public static readonly Color Chrome = Color.FromArgb(10, 13, 18);
        public static readonly Color Elevated = Color.FromArgb(33, 38, 45);
        public static readonly Color ElevatedHover = Color.FromArgb(45, 51, 59);
        public static readonly Color InputSurfaceColor = Color.FromArgb(13, 17, 23);
        public static readonly Color Ink = Color.FromArgb(230, 237, 243);
        public static readonly Color MutedInk = Color.FromArgb(139, 148, 158);
        public static readonly Color SubtleInk = Color.FromArgb(110, 118, 129);
        public static readonly Color Accent = Color.FromArgb(46, 124, 196);
        public static readonly Color AccentHover = Color.FromArgb(54, 138, 214);
        public static readonly Color AccentPressed = Color.FromArgb(36, 95, 155);
        public static readonly Color AccentGradientTop = Color.FromArgb(61, 148, 224);
        public static readonly Color AccentGradientBottom = Color.FromArgb(43, 119, 190);
        public static readonly Color AccentText = Color.FromArgb(88, 166, 255);
        public static readonly Color SoftAccent = Color.FromArgb(19, 35, 58);
        public static readonly Color BrandOrange = Color.FromArgb(245, 136, 60);
        public static readonly Color Danger = Color.FromArgb(248, 81, 73);
        public static readonly Color DangerHover = Color.FromArgb(250, 106, 99);
        public static readonly Color SoftDanger = Color.FromArgb(45, 23, 23);
        public static readonly Color Success = Color.FromArgb(63, 185, 80);
        public static readonly Color SoftSuccess = Color.FromArgb(18, 38, 28);
        public static readonly Color Warning = Color.FromArgb(227, 179, 65);
        public static readonly Color SoftWarning = Color.FromArgb(39, 33, 21);
        public static readonly Color Line = Color.FromArgb(48, 54, 61);
        public static readonly Color DisabledSurface = Color.FromArgb(28, 33, 40);
        public static readonly Color DisabledInk = Color.FromArgb(84, 93, 104);

        public static Font Font(float size, FontStyle style)
        {
            try
            {
                return new Font(PreferredFontFamily, size, style, GraphicsUnit.Point);
            }
            catch
            {
                return new Font("Segoe UI", size, style, GraphicsUnit.Point);
            }
        }

        public static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
            GraphicsPath path = new GraphicsPath();
            Rectangle arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static GraphicsPath RoundedRectangleF(RectangleF rectangle, float radius)
        {
            float diameter = Math.Max(2f, Math.Min(radius * 2f, Math.Min(rectangle.Width, rectangle.Height)));
            GraphicsPath path = new GraphicsPath();
            RectangleF arc = new RectangleF(rectangle.Location, new SizeF(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void DrawGlyph(Graphics graphics, ButtonGlyph glyph, RectangleF rectangle, Color color)
        {
            if (glyph == ButtonGlyph.None) return;
            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float span = Math.Min(rectangle.Width, rectangle.Height);
            float cx = rectangle.X + rectangle.Width / 2f;
            float cy = rectangle.Y + rectangle.Height / 2f;
            using (Pen pen = new Pen(color, Math.Max(2f, span * 0.135f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (glyph)
                {
                    case ButtonGlyph.Add:
                        graphics.DrawLine(pen, cx, cy - span * 0.34f, cx, cy + span * 0.34f);
                        graphics.DrawLine(pen, cx - span * 0.34f, cy, cx + span * 0.34f, cy);
                        break;
                    case ButtonGlyph.Close:
                        float d = span * 0.30f;
                        graphics.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                        graphics.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
                        break;
                    case ButtonGlyph.Delete:
                        float binWidth = span * 0.46f;
                        float binTop = cy - span * 0.16f;
                        float binBottom = cy + span * 0.34f;
                        graphics.DrawRectangle(
                            pen,
                            cx - binWidth / 2f,
                            binTop,
                            binWidth,
                            binBottom - binTop);
                        graphics.DrawLine(
                            pen,
                            cx - span * 0.34f,
                            cy - span * 0.29f,
                            cx + span * 0.34f,
                            cy - span * 0.29f);
                        graphics.DrawLine(
                            pen,
                            cx - span * 0.13f,
                            cy - span * 0.40f,
                            cx + span * 0.13f,
                            cy - span * 0.40f);
                        break;
                    case ButtonGlyph.Check:
                        graphics.DrawLines(pen, new[]
                        {
                            new PointF(cx - span * 0.32f, cy + span * 0.02f),
                            new PointF(cx - span * 0.08f, cy + span * 0.26f),
                            new PointF(cx + span * 0.34f, cy - span * 0.28f)
                        });
                        break;
                    case ButtonGlyph.Switch:
                        float w = span * 0.34f;
                        float off = span * 0.17f;
                        float ah = span * 0.13f;
                        graphics.DrawLine(pen, cx - w, cy - off, cx + w, cy - off);
                        graphics.DrawLines(pen, new[]
                        {
                            new PointF(cx + w - ah, cy - off - ah),
                            new PointF(cx + w, cy - off),
                            new PointF(cx + w - ah, cy - off + ah)
                        });
                        graphics.DrawLine(pen, cx + w, cy + off, cx - w, cy + off);
                        graphics.DrawLines(pen, new[]
                        {
                            new PointF(cx - w + ah, cy + off - ah),
                            new PointF(cx - w, cy + off),
                            new PointF(cx - w + ah, cy + off + ah)
                        });
                        break;
                    case ButtonGlyph.Refresh:
                        DrawCircularArrow(graphics, pen, cx, cy, span * 0.38f, false);
                        break;
                    case ButtonGlyph.Recover:
                        DrawCircularArrow(graphics, pen, cx, cy, span * 0.38f, true);
                        break;
                }
            }
            graphics.SmoothingMode = previous;
        }

        private static void DrawCircularArrow(Graphics graphics, Pen pen, float cx, float cy, float radius, bool reverse)
        {
            RectangleF box = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);
            float startDeg = reverse ? 230f : -50f;
            float sweepDeg = reverse ? -285f : 285f;
            graphics.DrawArc(pen, box, startDeg, sweepDeg);
            double end = (startDeg + sweepDeg) * Math.PI / 180.0;
            float ex = cx + radius * (float)Math.Cos(end);
            float ey = cy + radius * (float)Math.Sin(end);
            float tx = -(float)Math.Sin(end);
            float ty = (float)Math.Cos(end);
            if (reverse) { tx = -tx; ty = -ty; }
            float barb = radius * 0.66f;
            graphics.DrawLines(pen, new[]
            {
                RotatedPoint(tx, ty, 2.5f, ex, ey, barb),
                new PointF(ex, ey),
                RotatedPoint(tx, ty, -2.5f, ex, ey, barb)
            });
        }

        private static PointF RotatedPoint(float tx, float ty, float angle, float originX, float originY, float length)
        {
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            float rx = tx * cos - ty * sin;
            float ry = tx * sin + ty * cos;
            return new PointF(originX + rx * length, originY + ry * length);
        }

        public static Color BackgroundBehind(Control control)
        {
            Control current = control == null ? null : control.Parent;
            while (current != null)
            {
                SurfacePanel surface = current as SurfacePanel;
                if (surface != null) return surface.FillColor;
                if (current.BackColor.A == 255) return current.BackColor;
                current = current.Parent;
            }
            return Surface;
        }

        private static string ResolveFontFamily()
        {
            string[] preferences = { "GoldmanUI", "Goldman", "Satoshi Variable" };
            try
            {
                using (InstalledFontCollection fonts = new InstalledFontCollection())
                {
                    foreach (string preference in preferences)
                    {
                        foreach (FontFamily family in fonts.Families)
                        {
                            if (string.Equals(family.Name, preference, StringComparison.OrdinalIgnoreCase))
                            {
                                return family.Name;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return "Segoe UI";
        }
    }

    internal enum ActionButtonKind
    {
        Primary,
        Secondary,
        Danger,
        DangerGhost,
        Ghost
    }

    internal enum ButtonGlyph
    {
        None,
        Add,
        Switch,
        Refresh,
        Check,
        Recover,
        Delete,
        Close
    }

    internal sealed class ActionButton : Button
    {
        private bool hovered;
        private bool pressed;
        private ActionButtonKind kind;
        private ButtonGlyph glyph = ButtonGlyph.None;

        public ButtonGlyph Glyph
        {
            get { return glyph; }
            set { glyph = value; Invalidate(); }
        }

        public ActionButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = VisualTheme.Surface;
            Font = VisualTheme.Font(10f, FontStyle.Bold);
            Height = 42;
            MinimumSize = new Size(92, 42);
            Padding = new Padding(16, 0, 16, 0);
            Cursor = Cursors.Hand;
            TabStop = true;
            Kind = ActionButtonKind.Primary;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            float textWidth;
            using (Graphics graphics = CreateGraphics())
            using (StringFormat format = TightFormat())
            {
                textWidth = graphics.MeasureString(Text ?? string.Empty, Font, PointF.Empty, format).Width;
            }
            int iconAllowance = glyph == ButtonGlyph.None ? 0 : 26;
            return new Size(
                Math.Max(MinimumSize.Width, (int)Math.Ceiling(textWidth) + Padding.Horizontal + 8 + iconAllowance),
                Math.Max(MinimumSize.Height, 42));
        }

        private static StringFormat TightFormat()
        {
            StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            format.Trimming = StringTrimming.EllipsisCharacter;
            return format;
        }

        public ActionButtonKind Kind
        {
            get { return kind; }
            set
            {
                kind = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(eventArgs);
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            hovered = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(eventArgs);
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            if (eventArgs.Button == MouseButtons.Left) pressed = true;
            Invalidate();
            base.OnMouseDown(eventArgs);
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(eventArgs);
        }

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            if (eventArgs.KeyCode == Keys.Space || eventArgs.KeyCode == Keys.Enter)
            {
                pressed = true;
                Invalidate();
            }
            base.OnKeyDown(eventArgs);
        }

        protected override void OnKeyUp(KeyEventArgs eventArgs)
        {
            if (pressed)
            {
                pressed = false;
                Invalidate();
            }
            base.OnKeyUp(eventArgs);
        }

        protected override void OnEnabledChanged(EventArgs eventArgs)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            if (Width <= 0 || Height <= 0) return;
            Graphics graphics = eventArgs.Graphics;

            Color background;
            Color foreground;
            Color outline;
            ResolveColors(out background, out foreground, out outline);

            // Flat rectangle that fills the whole control — no rounded corners, so there are
            // no corner pixels to alias or bleed. Pixel-perfect at any DPI.
            Rectangle fill = new Rectangle(0, 0, Width, Height);
            bool gradientFill = Enabled && !pressed && (kind == ActionButtonKind.Primary || kind == ActionButtonKind.Danger);
            if (gradientFill)
            {
                Color top = kind == ActionButtonKind.Primary
                    ? (hovered ? VisualTheme.AccentHover : VisualTheme.AccentGradientTop)
                    : ControlPaint.Light(background, 0.16f);
                Color bottom = kind == ActionButtonKind.Primary
                    ? (hovered ? VisualTheme.AccentPressed : VisualTheme.AccentGradientBottom)
                    : background;
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(fill.X, fill.Y, fill.Width, fill.Height + 1), top, bottom, LinearGradientMode.Vertical))
                {
                    graphics.FillRectangle(brush, fill);
                }
            }
            else
            {
                using (SolidBrush brush = new SolidBrush(background))
                {
                    graphics.FillRectangle(brush, fill);
                }
            }
            if (outline.A > 0)
            {
                using (Pen pen = new Pen(outline, 1f))
                {
                    graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                }
            }

            Rectangle bounds = new Rectangle(0, pressed && Enabled ? 1 : 0, Width, Height);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            string label = Text ?? string.Empty;
            if (glyph == ButtonGlyph.None)
            {
                TextRenderer.DrawText(
                    graphics, label, Font, bounds, foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            else
            {
                const float iconSize = 18f;
                const float gap = 9f;
                using (StringFormat format = TightFormat())
                {
                    SizeF textSize = graphics.MeasureString(label, Font, PointF.Empty, format);
                    float groupWidth = textSize.Width + iconSize + gap;
                    float startX = bounds.Left + (bounds.Width - groupWidth) / 2f;
                    if (startX < bounds.Left + 6f) startX = bounds.Left + 6f;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    RectangleF iconRectangle = new RectangleF(
                        startX, bounds.Top + (bounds.Height - iconSize) / 2f, iconSize, iconSize);
                    VisualTheme.DrawGlyph(graphics, glyph, iconRectangle, foreground);
                    using (SolidBrush textBrush = new SolidBrush(foreground))
                    {
                        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                        float textY = bounds.Top + (bounds.Height - textSize.Height) / 2f;
                        graphics.DrawString(label, Font, textBrush, new PointF(startX + iconSize + gap, textY), format);
                    }
                }
            }

            if (Focused && ShowFocusCues && Enabled)
            {
                Color focusColor = kind == ActionButtonKind.Primary
                    ? Color.White
                    : kind == ActionButtonKind.Danger || kind == ActionButtonKind.DangerGhost
                        ? VisualTheme.Danger
                        : VisualTheme.AccentText;
                using (Pen focusPen = new Pen(focusColor, 1f))
                {
                    graphics.DrawRectangle(focusPen, 2, 2, Width - 5, Height - 5);
                }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(VisualTheme.BackgroundBehind(this));
        }

        private void ResolveColors(out Color background, out Color foreground, out Color outline)
        {
            outline = Color.Transparent;
            if (!Enabled)
            {
                background = VisualTheme.DisabledSurface;
                foreground = VisualTheme.DisabledInk;
                return;
            }
            if (kind == ActionButtonKind.Primary)
            {
                background = pressed ? VisualTheme.AccentPressed : hovered ? VisualTheme.AccentHover : VisualTheme.Accent;
                foreground = Color.White;
                return;
            }
            if (kind == ActionButtonKind.Danger)
            {
                background = pressed || hovered ? VisualTheme.DangerHover : VisualTheme.Danger;
                foreground = Color.White;
                return;
            }
            if (kind == ActionButtonKind.DangerGhost)
            {
                background = pressed
                    ? VisualTheme.SoftDanger
                    : hovered ? Color.FromArgb(55, 27, 28) : VisualTheme.BackgroundBehind(this);
                foreground = hovered || pressed ? VisualTheme.DangerHover : VisualTheme.Danger;
                return;
            }
            if (kind == ActionButtonKind.Ghost)
            {
                background = pressed
                    ? VisualTheme.Elevated
                    : hovered ? VisualTheme.Elevated : VisualTheme.BackgroundBehind(this);
                foreground = VisualTheme.Ink;
                outline = hovered ? VisualTheme.AccentText : Color.FromArgb(61, 68, 77);
                return;
            }

            background = pressed ? VisualTheme.Elevated : hovered ? VisualTheme.ElevatedHover : VisualTheme.Elevated;
            foreground = VisualTheme.Ink;
            outline = hovered ? VisualTheme.AccentText : Color.FromArgb(72, 80, 90);
        }
    }

    internal class SurfacePanel : Panel
    {
        private Color fillColor = VisualTheme.Surface;

        public SurfacePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Padding = new Padding(24);
            CornerRadius = 16;
        }

        public int CornerRadius { get; set; }

        public Color FillColor
        {
            get { return fillColor; }
            set
            {
                fillColor = value;
                Invalidate();
            }
        }

        public bool Emphasized { get; set; }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(Parent == null ? VisualTheme.Canvas : Parent.BackColor);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle diffuseBounds = new Rectangle(2, 6, Math.Max(1, Width - 5), Math.Max(1, Height - 9));
            Rectangle ambientBounds = new Rectangle(2, 4, Math.Max(1, Width - 5), Math.Max(1, Height - 8));
            Rectangle liftBounds = new Rectangle(1, 2, Math.Max(1, Width - 3), Math.Max(1, Height - 6));
            Rectangle surfaceBounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 4));
            using (GraphicsPath diffuse = VisualTheme.RoundedRectangle(diffuseBounds, CornerRadius))
            using (SolidBrush diffuseBrush = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            using (GraphicsPath ambient = VisualTheme.RoundedRectangle(ambientBounds, CornerRadius))
            using (SolidBrush ambientBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
            using (GraphicsPath lift = VisualTheme.RoundedRectangle(liftBounds, CornerRadius))
            using (SolidBrush liftBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            using (GraphicsPath surface = VisualTheme.RoundedRectangle(surfaceBounds, CornerRadius))
            using (SolidBrush surfaceBrush = new SolidBrush(fillColor))
            using (Pen ring = new Pen(Emphasized ? Color.FromArgb(190, VisualTheme.AccentText) : Color.FromArgb(20, 255, 255, 255), Emphasized ? 1.5f : 1f))
            {
                eventArgs.Graphics.FillPath(diffuseBrush, diffuse);
                eventArgs.Graphics.FillPath(ambientBrush, ambient);
                eventArgs.Graphics.FillPath(liftBrush, lift);
                eventArgs.Graphics.FillPath(surfaceBrush, surface);
                eventArgs.Graphics.DrawPath(ring, surface);
            }
        }
    }

    internal sealed class BrandMark : Control
    {
        public BrandMark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(40, 40);
            MinimumSize = new Size(40, 40);
            MaximumSize = new Size(40, 40);
            AccessibleName = "Harness Profile Switcher";
            TabStop = false;
        }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(VisualTheme.BackgroundBehind(this));
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            try
            {
                Image logo = BrandAssets.Logo();
                eventArgs.Graphics.DrawImage(logo, new Rectangle(0, 0, Width, Height));
            }
            catch
            {
                Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath shape = VisualTheme.RoundedRectangle(bounds, 12))
                using (SolidBrush brush = new SolidBrush(VisualTheme.Accent))
                {
                    eventArgs.Graphics.FillPath(brush, shape);
                }
            }
        }
    }

    internal enum StatusBadgeKind
    {
        Neutral,
        Success,
        Warning
    }

    internal sealed class StatusBadge : Control
    {
        private StatusBadgeKind kind;

        public StatusBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = VisualTheme.Font(9.25f, FontStyle.Bold);
            Height = 36;
            MinimumSize = new Size(132, 36);
            TabStop = false;
        }

        public StatusBadgeKind Kind
        {
            get { return kind; }
            set
            {
                kind = value;
                Invalidate();
            }
        }

        protected override void OnTextChanged(EventArgs eventArgs)
        {
            AccessibleName = Text;
            Invalidate();
            base.OnTextChanged(eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            eventArgs.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Color background = kind == StatusBadgeKind.Success
                ? VisualTheme.SoftSuccess
                : kind == StatusBadgeKind.Warning ? VisualTheme.SoftWarning : VisualTheme.Elevated;
            Color foreground = kind == StatusBadgeKind.Success
                ? VisualTheme.Success
                : kind == StatusBadgeKind.Warning ? VisualTheme.Warning : VisualTheme.MutedInk;
            Rectangle bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath shape = VisualTheme.RoundedRectangle(bounds, 9))
            using (SolidBrush backgroundBrush = new SolidBrush(background))
            using (SolidBrush dotBrush = new SolidBrush(foreground))
            using (SolidBrush textBrush = new SolidBrush(foreground))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                eventArgs.Graphics.FillPath(backgroundBrush, shape);

                const float dot = 8f;
                const float gap = 9f;
                string label = Text ?? string.Empty;
                SizeF textSize = eventArgs.Graphics.MeasureString(label, Font, PointF.Empty, format);
                float groupWidth = dot + gap + textSize.Width;
                float startX = bounds.Left + (bounds.Width - groupWidth) / 2f;
                if (startX < bounds.Left + 12f) startX = bounds.Left + 12f;

                eventArgs.Graphics.FillEllipse(dotBrush, startX, bounds.Top + (bounds.Height - dot) / 2f, dot, dot);
                eventArgs.Graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                eventArgs.Graphics.DrawString(label, Font, textBrush,
                    new PointF(startX + dot + gap, bounds.Top + (bounds.Height - textSize.Height) / 2f), format);
            }
        }
    }

    internal sealed class InputSurface : Panel
    {
        private bool focused;

        public InputSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Padding = new Padding(12, 10, 12, 8);
            Height = 46;
        }

        public void Attach(TextBox textBox)
        {
            textBox.Enter += delegate
            {
                focused = true;
                Invalidate();
            };
            textBox.Leave += delegate
            {
                focused = false;
                Invalidate();
            };
            Controls.Add(textBox);
        }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(Parent == null ? VisualTheme.Surface : Parent.BackColor);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (GraphicsPath shape = VisualTheme.RoundedRectangle(bounds, 9))
            using (SolidBrush fill = new SolidBrush(VisualTheme.InputSurfaceColor))
            using (Pen outline = new Pen(focused ? VisualTheme.AccentText : VisualTheme.Line, focused ? 2f : 1f))
            {
                eventArgs.Graphics.FillPath(fill, shape);
                eventArgs.Graphics.DrawPath(outline, shape);
            }
        }
    }

    internal sealed class Avatar : Control
    {
        private static readonly Color[][] Palettes =
        {
            new[] { Color.FromArgb(69, 154, 229), Color.FromArgb(43, 119, 190) },
            new[] { Color.FromArgb(45, 178, 166), Color.FromArgb(22, 130, 121) },
            new[] { Color.FromArgb(245, 136, 60), Color.FromArgb(205, 100, 32) },
            new[] { Color.FromArgb(206, 92, 130), Color.FromArgb(170, 60, 102) },
            new[] { Color.FromArgb(139, 128, 237), Color.FromArgb(104, 92, 214) }
        };

        private string glyph = "?";
        private Color[] palette = Palettes[0];
        private bool muted;

        public Avatar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(44, 44);
            TabStop = false;
        }

        public bool Muted
        {
            get { return muted; }
            set { muted = value; Invalidate(); }
        }

        public void SetName(string name)
        {
            glyph = InitialsFor(name);
            palette = Palettes[Math.Abs(StableHash(name)) % Palettes.Length];
            AccessibleName = name;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(VisualTheme.BackgroundBehind(this));
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            eventArgs.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int size = Math.Min(Width, Height) - 1;
            Rectangle bounds = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            Color top = muted ? Color.FromArgb(72, 82, 100) : palette[0];
            Color bottom = muted ? Color.FromArgb(54, 63, 80) : palette[1];
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height + 1),
                top,
                bottom,
                LinearGradientMode.ForwardDiagonal))
            {
                eventArgs.Graphics.FillEllipse(brush, bounds);
            }
            // Center the glyph by its actual ink bounds (not the font line box), so caps sit
            // dead-center in the circle regardless of the font's ascent/descent metrics.
            eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float emSize = size * 0.5f * eventArgs.Graphics.DpiY / 72f;
            using (Font font = VisualTheme.Font(size * 0.5f, FontStyle.Bold))
            using (GraphicsPath textPath = new GraphicsPath())
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                textPath.AddString(glyph, font.FontFamily, (int)FontStyle.Bold, emSize, PointF.Empty, format);
                if (textPath.PointCount > 0)
                {
                    RectangleF ink = textPath.GetBounds();
                    float dx = bounds.X + bounds.Width / 2f - (ink.X + ink.Width / 2f);
                    float dy = bounds.Y + bounds.Height / 2f - (ink.Y + ink.Height / 2f);
                    using (Matrix move = new Matrix())
                    {
                        move.Translate(dx, dy);
                        textPath.Transform(move);
                    }
                    using (SolidBrush textBrush = new SolidBrush(muted ? VisualTheme.MutedInk : Color.White))
                    {
                        eventArgs.Graphics.FillPath(textBrush, textPath);
                    }
                }
            }
        }

        private static string InitialsFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            string[] parts = name.Trim().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0].Substring(0, 1).ToUpperInvariant();
            return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
        }

        private static int StableHash(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            unchecked
            {
                int hash = 17;
                foreach (char character in value) hash = hash * 31 + character;
                return hash;
            }
        }
    }

    internal sealed class ActivityBar : Control
    {
        private readonly Timer timer;
        private int phase = 80;

        public ActivityBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = VisualTheme.Surface;
            Height = 6;
            MinimumSize = new Size(120, 6);
            AccessibleName = "Operation in progress";
            timer = new Timer { Interval = 24 };
            timer.Tick += delegate
            {
                phase = (phase + 7) % Math.Max(1, Width + 80);
                Invalidate();
            };
        }

        protected override void OnVisibleChanged(EventArgs eventArgs)
        {
            if (Visible) timer.Start(); else timer.Stop();
            base.OnVisibleChanged(eventArgs);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath trackPath = VisualTheme.RoundedRectangle(track, 2))
            using (SolidBrush trackBrush = new SolidBrush(VisualTheme.SoftAccent))
            {
                eventArgs.Graphics.FillPath(trackBrush, trackPath);
            }
            int segmentWidth = Math.Max(36, Width / 3);
            int x = phase - 80;
            Rectangle segment = new Rectangle(x, 0, segmentWidth, Math.Max(1, Height - 1));
            using (GraphicsPath segmentPath = VisualTheme.RoundedRectangle(segment, 2))
            using (SolidBrush segmentBrush = new SolidBrush(VisualTheme.Accent))
            {
                eventArgs.Graphics.FillPath(segmentBrush, segmentPath);
            }
        }
    }
}
