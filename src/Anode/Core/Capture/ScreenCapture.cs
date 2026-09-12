using System.Drawing;
using System.Drawing.Imaging;

namespace Anode.Core.Capture;

/// <summary>
/// Grabs the seat's screen. Runs inside the seat host, so "the screen" is the child
/// session's own desktop and never the user's monitors.
///
/// Downscaling and JPEG are offered because the usual consumer is a model with a
/// token budget: a 1920x1080 PNG is an expensive way to answer "is the game loaded yet".
/// </summary>
internal static class ScreenCapture
{
    public sealed record Shot(byte[] Bytes, string MimeType, int Width, int Height, int SourceWidth, int SourceHeight);

    public static Shot Capture(int? maxWidth = null, string format = "png", int jpegQuality = 80, Rectangle? region = null)
    {
        Rectangle source = region ?? SeatBounds();
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("The seat reported an empty desktop. It may still be signing in.");

        using var raw = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(raw))
        {
            graphics.CopyFromScreen(source.Location, Point.Empty, source.Size, CopyPixelOperation.SourceCopy);
        }

        Bitmap output = raw;
        bool scaled = false;
        if (maxWidth is int limit && limit > 0 && source.Width > limit)
        {
            int height = Math.Max(1, (int)Math.Round(source.Height * (double)limit / source.Width));
            var resized = new Bitmap(limit, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(raw, 0, 0, limit, height);
            }
            output = resized;
            scaled = true;
        }

        try
        {
            using var buffer = new MemoryStream();
            string mime;
            if (format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) || format.Equals("jpg", StringComparison.OrdinalIgnoreCase))
            {
                var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
                using var flattened = Flatten(output);
                flattened.Save(buffer, encoder, parameters);
                mime = "image/jpeg";
            }
            else
            {
                output.Save(buffer, ImageFormat.Png);
                mime = "image/png";
            }

            return new Shot(buffer.ToArray(), mime, output.Width, output.Height, source.Width, source.Height);
        }
        finally
        {
            if (scaled) output.Dispose();
        }
    }

    private static Bitmap Flatten(Bitmap source)
    {
        var flattened = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(flattened);
        graphics.Clear(Color.Black);
        graphics.DrawImageUnscaled(source, 0, 0);
        return flattened;
    }

    /// <summary>
    /// The rectangle a screenshot covers. Deliberately the primary display rather than
    /// the whole virtual desktop, because that is the coordinate space mouse injection
    /// uses: a pixel in a screenshot is the pixel a click with the same x and y hits.
    /// A child session only ever has one display, so inside the seat the two agree.
    /// </summary>
    public static Rectangle SeatBounds()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        if (bounds.Width > 0 && bounds.Height > 0) return bounds;
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        return virtualScreen.Width > 0 ? virtualScreen : new Rectangle(0, 0, 1920, 1080);
    }
}
