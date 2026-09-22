using System.Drawing;
using System.Windows.Forms;

namespace Anode.Daemon;

/// <summary>
/// What the viewer shows while there is no seat picture: the state, its message and, while
/// something is under way, a quiet progress indicator, instead of the Remote Desktop control's
/// blank window. The window hides it as soon as that control connects, so it never covers a live
/// seat, including a Windows sign-in screen inside it.
/// </summary>
internal sealed class SeatPlaceholder : Control
{
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 420 };
    private readonly Icon? _icon;
    private readonly Font _headingFont = ViewerTheme.StrongFont(13f);
    private readonly Font _messageFont = ViewerTheme.UiFont(9.5f);
    private Icon? _sizedIcon;
    private string _heading = "Starting";
    private string _message = string.Empty;
    private bool _busy = true;
    private bool _problem;
    private int _phase;

    public SeatPlaceholder(Icon? icon)
    {
        _icon = icon;
        Name = "SeatPlaceholder";
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = _heading;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        _pulse.Tick += (_, _) =>
        {
            _phase = (_phase + 1) % 3;
            Invalidate();
        };
    }

    public string Heading => _heading;

    /// <summary>Busy states pulse; problems draw the heading in the danger color.</summary>
    public void Describe(string heading, string message, bool busy, bool problem)
    {
        _heading = heading;
        _message = message;
        _busy = busy;
        _problem = problem;
        AccessibleName = heading;
        AccessibleDescription = message;
        UpdatePulse();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdatePulse();
    }

    // Animate only while someone could see it; a hidden viewer costs nothing.
    private void UpdatePulse() => _pulse.Enabled = _busy && Visible;

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(ViewerTheme.Background);
        int Scaled(int value) => LogicalToDeviceUnits(value);
        int width = Math.Max(1, Math.Min(ClientSize.Width - Scaled(48), Scaled(520)));
        const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl;

        int iconSize = Scaled(48);
        var headingSize = TextRenderer.MeasureText(graphics, _heading, _headingFont, new Size(width, int.MaxValue), centered);
        // At most five lines of message; Details holds the rest.
        int messageLimit = TextRenderer.MeasureText(graphics, "Ag", _messageFont).Height * 5;
        var messageSize = string.IsNullOrEmpty(_message) ? Size.Empty
            : TextRenderer.MeasureText(graphics, _message, _messageFont, new Size(width, int.MaxValue), centered);
        int messageHeight = Math.Min(messageSize.Height, messageLimit);
        int dotSize = Scaled(6), dotGap = Scaled(8), dotsHeight = _busy ? Scaled(28) : 0;
        int total = iconSize + Scaled(18) + headingSize.Height + Scaled(8) + messageHeight + dotsHeight;
        int y = Math.Max(Scaled(12), (ClientSize.Height - total) / 2);
        int left = (ClientSize.Width - width) / 2;

        if (_icon is not null)
        {
            if (_sizedIcon?.Width != iconSize)
            {
                _sizedIcon?.Dispose();
                _sizedIcon = new Icon(_icon, iconSize, iconSize);
            }
            graphics.DrawIcon(_sizedIcon, new Rectangle((ClientSize.Width - iconSize) / 2, y, iconSize, iconSize));
        }
        y += iconSize + Scaled(18);
        TextRenderer.DrawText(graphics, _heading, _headingFont, new Rectangle(left, y, width, headingSize.Height),
            _problem ? ViewerTheme.Danger : ViewerTheme.Text, centered);
        y += headingSize.Height + Scaled(8);
        if (messageHeight > 0)
            TextRenderer.DrawText(graphics, _message, _messageFont, new Rectangle(left, y, width, messageHeight), ViewerTheme.Muted, centered);
        y += messageHeight;

        if (!_busy) return;
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int x = (ClientSize.Width - (dotSize * 3 + dotGap * 2)) / 2;
        for (int i = 0; i < 3; i++)
        {
            using var brush = new SolidBrush(i == _phase ? ViewerTheme.Accent : Color.FromArgb(70, 70, 76));
            graphics.FillEllipse(brush, x + i * (dotSize + dotGap), y + Scaled(18), dotSize, dotSize);
        }
        graphics.SmoothingMode = smoothing;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulse.Dispose();
            _sizedIcon?.Dispose();
            _headingFont.Dispose();
            _messageFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
