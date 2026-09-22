using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Anode.Core.Native;

namespace Anode.Daemon;

/// <summary>
/// One look for the viewer and its tray menu. The title bar, header, details and footer share a
/// single dark surface with no divider lines; hover, pressed and checked states are rounded fills
/// instead of outlines. High-contrast mode keeps the system colors, renderer and window frame.
/// </summary>
internal static class ViewerTheme
{
    /// <summary>How a toolbar item reads: a status dot's color, or the emergency button's fill.</summary>
    internal enum Tone { Neutral, Good, Busy, Problem, Danger }

    /// <summary>A toolbar item's tone and, for buttons, its icon glyph.</summary>
    internal sealed record Look(Tone Tone = Tone.Neutral, char Glyph = '\0');

    /// <summary>Codepoints shared by Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10).</summary>
    internal static class Glyphs
    {
        public const char Stop = '', Pointer = '', FullScreen = '', ExitFullScreen = '',
            Refresh = '', Person = '', Info = '', More = '';
    }

    private static bool HighContrast => SystemInformation.HighContrast;

    /// <summary>Title bar, header, details and footer: one continuous surface.</summary>
    public static Color Chrome => HighContrast ? SystemColors.Control : Color.FromArgb(32, 32, 35);
    /// <summary>Behind the seat picture; darker, so the picture needs no frame.</summary>
    public static Color Background => HighContrast ? SystemColors.Window : Color.FromArgb(17, 17, 19);
    /// <summary>Menus float just above the chrome.</summary>
    public static Color Menu => HighContrast ? SystemColors.Menu : Color.FromArgb(44, 44, 48);
    public static Color Text => HighContrast ? SystemColors.ControlText : Color.FromArgb(243, 243, 245);
    public static Color Muted => HighContrast ? SystemColors.ControlText : Color.FromArgb(166, 166, 176);
    public static Color Accent => HighContrast ? SystemColors.ControlText : Color.FromArgb(255, 184, 77);
    public static Color Danger => HighContrast ? SystemColors.ControlText : Color.FromArgb(255, 158, 158);

    private static readonly Color Disabled = Color.FromArgb(112, 112, 120);
    private static readonly Color Hover = Color.FromArgb(52, 52, 56);
    private static readonly Color Pressed = Color.FromArgb(43, 43, 47);
    private static readonly Color MenuHover = Color.FromArgb(62, 62, 67);
    private static readonly Color MenuLine = Color.FromArgb(64, 64, 70);
    private static readonly Color CheckedFill = Color.FromArgb(76, 59, 34);
    private static readonly Color CheckedHover = Color.FromArgb(96, 72, 37);
    private static readonly Color DangerFill = Color.FromArgb(68, 38, 41);
    private static readonly Color DangerHover = Color.FromArgb(92, 43, 47);

    /// <summary>Windows 11 rounds menus and draws their border itself; earlier versions get a drawn hairline.</summary>
    private static readonly bool WindowsDrawsMenuFrames = Environment.OSVersion.Version.Build >= 22000;
    private static readonly Renderer Shared = new();

    private static readonly Lazy<string[]> Families = new(() =>
    {
        using var installed = new InstalledFontCollection();
        return installed.Families.Select(f => f.Name).ToArray();
    });

    /// <summary>Windows 11's interface font where it exists, otherwise Segoe UI.</summary>
    public static Font UiFont(float points = 9f) => new(Pick("Segoe UI Variable Text", "Segoe UI"), points);

    /// <summary>A semibold weight, for the one action that must stand out and for headings.</summary>
    public static Font StrongFont(float points = 9f) => Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold") is { } semibold && Families.Value.Contains(semibold)
        ? new Font(semibold, points)
        : new Font(Pick("Segoe UI Variable Text", "Segoe UI"), points, FontStyle.Bold);

    private static string Pick(string preferred, string fallback) => Families.Value.Contains(preferred) ? preferred : fallback;

    private static readonly Lazy<string?> IconFamily = new(() =>
        new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }.FirstOrDefault(Families.Value.Contains));
    private static readonly Dictionary<int, Font> IconFonts = new();

    /// <summary>
    /// Gives an item its tone and icon. A blank image reserves a DPI-scaled slot before the text,
    /// where the renderer draws the glyph, or a label's state dot.
    /// </summary>
    public static void Decorate(ToolStripItem item, Look look)
    {
        item.Tag = look;
        bool slot = look.Glyph != '\0' ? IconFamily.Value is not null : item is ToolStripLabel;
        if (slot && item.Image is null)
        {
            item.Image = new Bitmap(1, 1);
            item.ImageScaling = ToolStripItemImageScaling.SizeToFit;
            item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            item.TextImageRelation = TextImageRelation.ImageBeforeText;
        }
        item.Invalidate();
    }

    private static void DrawGlyph(Graphics graphics, char glyph, Rectangle bounds, Color color, ToolStrip? strip)
    {
        if (IconFamily.Value is not { } family) return;
        int pixels = Scale(strip, 14);
        if (!IconFonts.TryGetValue(pixels, out var font))
            IconFonts[pixels] = font = new Font(family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(graphics, glyph.ToString(), font, bounds, color, TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    public static Color Dot(Tone tone) => tone switch
    {
        Tone.Good => Color.FromArgb(74, 222, 128),
        Tone.Busy => Color.FromArgb(251, 191, 36),
        Tone.Problem or Tone.Danger => Color.FromArgb(248, 113, 113),
        _ => Muted
    };

    public static void Apply(ToolStrip strip)
    {
        strip.BackColor = strip is ToolStripDropDown ? Menu : Chrome;
        strip.ForeColor = Text;
        strip.Renderer = HighContrast ? new ToolStripSystemRenderer() : Shared;
    }

    /// <summary>A context menu in the Windows 11 manner: rounded, no icon margin, roomy rows.</summary>
    public static void ApplyMenu(ToolStripDropDownMenu menu)
    {
        Apply(menu);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = Text;
            // Taller rows only: the menu's width ignores item padding, so horizontal padding would clip text.
            if (item is ToolStripMenuItem) item.Padding = new Padding(0, 5, 0, 5);
        }
        menu.HandleCreated += (_, _) => ApplyMenuFrame(menu.Handle);
        if (menu.IsHandleCreated) ApplyMenuFrame(menu.Handle);
    }

    /// <summary>
    /// A dark title bar whose color matches the header on Windows 11, so frame and header read as
    /// one surface. High contrast restores the system's own frame.
    /// </summary>
    public static void ApplyFrame(IntPtr handle)
    {
        const int SystemDefault = unchecked((int)0xFFFFFFFF);
        int dark = HighContrast ? 0 : 1;
        if (Native.DwmSetWindowAttribute(handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            Native.DwmSetWindowAttribute(handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));
        // Windows 10 ignores these; its dark title bar is already close to the header.
        int caption = HighContrast ? SystemDefault : ColorRef(Chrome);
        int border = caption;
        int text = HighContrast ? SystemDefault : ColorRef(Text);
        Native.DwmSetWindowAttribute(handle, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        Native.DwmSetWindowAttribute(handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        Native.DwmSetWindowAttribute(handle, Native.DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    /// <summary>Rounded corners and a quiet border for a menu window, drawn by Windows 11.</summary>
    public static void ApplyMenuFrame(IntPtr handle)
    {
        if (HighContrast || !WindowsDrawsMenuFrames) return;
        int corner = Native.DWMWCP_ROUNDSMALL, border = ColorRef(MenuLine);
        Native.DwmSetWindowAttribute(handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        Native.DwmSetWindowAttribute(handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static int Scale(ToolStrip? strip, int value) => (int)Math.Round(value * (strip?.DeviceDpi ?? 96) / 96f);

    private static void FillRounded(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        using var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
        graphics.SmoothingMode = smoothing;
    }

    private static void FillCircle(Graphics graphics, Rectangle bounds, Color color)
    {
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, bounds);
        graphics.SmoothingMode = smoothing;
    }

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Palette()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.ToolStrip.BackColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // The header and footer have no lines. Menus get a hairline only where Windows draws none.
            if (e.ToolStrip is not ToolStripDropDown || WindowsDrawsMenuFrames) return;
            using var pen = new Pen(MenuLine);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        private static readonly Look Plain = new();

        private static Look LookOf(ToolStripItem item) => item.Tag as Look ?? Plain;

        /// <summary>Text and icon share one color, so an icon follows its button's state.</summary>
        private static Color InkOf(ToolStripItem item) => !item.Enabled ? Disabled
            : item is ToolStripButton { Checked: true } && LookOf(item).Tone != Tone.Danger ? Accent
            : item.ForeColor;

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            bool danger = LookOf(item).Tone == Tone.Danger;
            bool isChecked = item is ToolStripButton { Checked: true };
            Color? fill = item.Enabled && (item.Pressed || item.Selected)
                ? danger ? DangerHover : isChecked ? CheckedHover : item.Pressed ? Pressed : Hover
                : danger ? DangerFill : isChecked ? CheckedFill : null;
            if (fill is { } color) FillRounded(e.Graphics, Pill(e), color, Scale(e.ToolStrip, 5));
        }

        protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Pressed || e.Item.Selected)
                FillRounded(e.Graphics, Pill(e), e.Item.Pressed ? Pressed : Hover, Scale(e.ToolStrip, 5));
            // "More" instead of the classic chevron; drawn dots where no icon font exists.
            if (IconFamily.Value is not null)
            {
                DrawGlyph(e.Graphics, Glyphs.More, new Rectangle(Point.Empty, e.Item.Size), Text, e.ToolStrip);
                return;
            }
            int size = Math.Max(2, Scale(e.ToolStrip, 3)), gap = Scale(e.ToolStrip, 3);
            int x = (e.Item.Width - (size * 3 + gap * 2)) / 2, y = (e.Item.Height - size) / 2;
            for (int i = 0; i < 3; i++) FillCircle(e.Graphics, new Rectangle(x + i * (size + gap), y, size, size), Text);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            bounds.Inflate(-Scale(e.ToolStrip, 2), -Scale(e.ToolStrip, 1));
            FillRounded(e.Graphics, bounds, MenuHover, Scale(e.ToolStrip, 4));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            // The header separates groups with space alone; menus keep a quiet hairline.
            if (e.ToolStrip is not ToolStripDropDown) return;
            int y = e.Item.Height / 2, inset = Scale(e.ToolStrip, 8);
            using var pen = new Pen(MenuLine);
            e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = InkOf(e.Item);
            if (e.Item is ToolStripStatusLabel { Spring: true }) e.TextFormat |= TextFormatFlags.EndEllipsis;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            // A decorated item's image slot holds its glyph, or a label's state dot.
            if (e.Item.Tag is not Look look)
            {
                base.OnRenderItemImage(e);
                return;
            }
            // The slot is wider than tall; drawing in its left square leaves a gap before the text.
            var slot = e.ImageRectangle;
            var square = new Rectangle(slot.X, slot.Y, Math.Min(slot.Width, slot.Height), slot.Height);
            if (look.Glyph != '\0') DrawGlyph(e.Graphics, look.Glyph, square, InkOf(e.Item), e.ToolStrip);
            else if (e.Item is ToolStripLabel)
            {
                int size = Scale(e.ToolStrip, 8);
                FillCircle(e.Graphics, new Rectangle(square.X + (square.Width - size) / 2, square.Y + (square.Height - size) / 2, size, size), Dot(look.Tone));
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderLabelBackground(ToolStripItemRenderEventArgs e) { }

        private static Rectangle Pill(ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            bounds.Inflate(0, -Scale(e.ToolStrip, 1));
            return bounds;
        }
    }

    /// <summary>Colors for whatever the renderer leaves to the professional base; no borders anywhere.</summary>
    private sealed class Palette : ProfessionalColorTable
    {
        public Palette() { UseSystemColors = false; }
        public override Color ToolStripGradientBegin => Chrome;
        public override Color ToolStripGradientMiddle => Chrome;
        public override Color ToolStripGradientEnd => Chrome;
        public override Color ToolStripBorder => Chrome;
        public override Color ToolStripDropDownBackground => Menu;
        public override Color ImageMarginGradientBegin => Menu;
        public override Color ImageMarginGradientMiddle => Menu;
        public override Color ImageMarginGradientEnd => Menu;
        public override Color StatusStripGradientBegin => Chrome;
        public override Color StatusStripGradientEnd => Chrome;
        public override Color ButtonSelectedBorder => Hover;
        public override Color ButtonPressedBorder => Pressed;
        public override Color ButtonCheckedHighlightBorder => CheckedFill;
        public override Color MenuItemSelected => MenuHover;
        public override Color MenuItemBorder => MenuHover;
        public override Color MenuBorder => MenuLine;
        public override Color SeparatorDark => MenuLine;
        public override Color SeparatorLight => Menu;
    }
}
