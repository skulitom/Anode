using System.Globalization;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Display;

/// <summary>
/// A seat display: its size in physical pixels and its Windows scaling. Remote Desktop's display-control
/// channel takes 200-8192 pixels with an even width; the seat keeps to 640x480 and up, where Windows' own
/// UI still fits, and to the scaling steps Windows offers in Settings.
/// </summary>
internal readonly record struct DisplayMode(int Width, int Height, int Scale)
{
    public const int MinWidth = 640, MinHeight = 480, MaxSide = 8192;

    /// <summary>The steps Windows offers under Settings > System > Display > Scale.</summary>
    public static readonly int[] Scales = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    /// <summary>What a DPI-unaware app, or a web page in CSS pixels, has to lay out in.</summary>
    public int EffectiveWidth => (int)Math.Round(Width * 100.0 / Scale);
    public int EffectiveHeight => (int)Math.Round(Height * 100.0 / Scale);

    /// <summary>96 DPI is 100%, so 120 is 125% and 144 is 150%.</summary>
    public static int ScaleFromDpi(int dpi) => (int)Math.Round(dpi * 100 / 96.0);

    /// <summary>Why this cannot be a seat display, or null.</summary>
    public static string? Problem(int width, int height, int scale)
    {
        if (width is < MinWidth or > MaxSide || height is < MinHeight or > MaxSide)
            return $"A seat display is {MinWidth}x{MinHeight} to {MaxSide}x{MaxSide} pixels, not {width}x{height}.";
        if (width % 2 != 0)
            return $"The width must be even, such as 1366 or 1920; Remote Desktop cannot show a display {width} pixels wide.";
        if (!Scales.Contains(scale))
            return $"Scaling is one of Windows' steps, {string.Join(", ", Scales[..^1])} or {Scales[^1]} percent, not {scale}.";
        return null;
    }

    /// <summary>
    /// The size of a monitor whose pixel density matches the scale, in millimetres, as Remote Desktop describes
    /// a monitor. It ignores the scale unless both sides are 10-10000 mm, which every seat display gives.
    /// </summary>
    public (uint Width, uint Height) PhysicalMillimetres() =>
        ((uint)Math.Round(Width * 2540.0 / (96 * Scale)), (uint)Math.Round(Height * 2540.0 / (96 * Scale)));

    public override string ToString() => $"{Width}x{Height} at {Scale}%";

    public JsonObject ToJson() => new() { ["width"] = Width, ["height"] = Height, ["scale"] = Scale };

    /// <summary>A display from {width, height, scale}; a reply without scale comes from a host that measures none, at 100%.</summary>
    public static DisplayMode? FromJson(JsonObject? json) =>
        json?.Int("width") is int width and > 0 && json.Int("height") is int height and > 0
            ? new DisplayMode(width, height, json.Int("scale") ?? 100)
            : null;

    /// <summary>Reads a size written as the CLI takes it, such as 1920x1080.</summary>
    public static bool TryParseSize(string text, out int width, out int height)
    {
        width = height = 0;
        string[] sides = text.Split('x', 'X');
        return sides.Length == 2
            && int.TryParse(sides[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(sides[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
    }
}
