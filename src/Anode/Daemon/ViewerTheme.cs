using System.Drawing;
using System.Windows.Forms;

namespace Anode.Daemon;

/// <summary>One palette for the viewer, including hover, overflow and checked states.</summary>
internal static class ViewerTheme
{
    public static Color Background => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(24, 24, 27);
    public static Color Surface => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(32, 32, 36);
    public static Color Text => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(244, 244, 245);
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(180, 180, 189);
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(255, 163, 26);
    public static Color Danger => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(252, 165, 165);

    public static void Apply(ToolStrip strip)
    {
        strip.BackColor = Surface;
        strip.ForeColor = Text;
        strip.Renderer = SystemInformation.HighContrast ? new ToolStripSystemRenderer() : new Renderer();
    }

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Palette()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? e.Item.ForeColor : Muted;
            if (e.Item is ToolStripStatusLabel { Spring: true }) e.TextFormat |= TextFormatFlags.EndEllipsis;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    private sealed class Palette : ProfessionalColorTable
    {
        private static Color Hover => Color.FromArgb(63, 63, 70);
        private static Color Checked => Color.FromArgb(76, 54, 24);
        private static Color Border => Color.FromArgb(113, 113, 122);
        public Palette() { UseSystemColors = false; }
        public override Color ToolStripGradientBegin => Surface;
        public override Color ToolStripGradientMiddle => Surface;
        public override Color ToolStripGradientEnd => Surface;
        public override Color ToolStripBorder => Border;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color StatusStripGradientBegin => Surface;
        public override Color StatusStripGradientEnd => Surface;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonSelectedBorder => Accent;
        public override Color ButtonPressedGradientBegin => Checked;
        public override Color ButtonPressedGradientMiddle => Checked;
        public override Color ButtonPressedGradientEnd => Checked;
        public override Color ButtonPressedBorder => Accent;
        public override Color ButtonCheckedGradientBegin => Checked;
        public override Color ButtonCheckedGradientMiddle => Checked;
        public override Color ButtonCheckedGradientEnd => Checked;
        public override Color ButtonCheckedHighlight => Checked;
        public override Color ButtonCheckedHighlightBorder => Accent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemBorder => Accent;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
        public override Color OverflowButtonGradientBegin => Hover;
        public override Color OverflowButtonGradientMiddle => Hover;
        public override Color OverflowButtonGradientEnd => Hover;
    }
}
