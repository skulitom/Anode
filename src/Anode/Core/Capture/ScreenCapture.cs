using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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
            try { graphics.CopyFromScreen(source.Location, Point.Empty, source.Size, CopyPixelOperation.SourceCopy); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 6 or 5)
            {
                throw new InvalidOperationException(UnavailableMessage(IsSessionDisconnected()), ex);
            }
        }
        return Encode(raw, maxWidth, format, jpegQuality);
    }

    internal static string UnavailableMessage(bool disconnected) => disconnected
        ? "The seat's display is unavailable for capture because its viewer is disconnected, so Windows has stopped drawing the seat. "
            + "Programs and command jobs keep running. seat_status (lastError) says why and how to reconnect; if the seat signed in with a password, only the user can. "
            + "No foreground capture or viewer activation was attempted. Use seat_observe with includeScreenshot=false for accessible native controls; do not guess screen coordinates."
        : "The seat's display is unavailable for capture. RDP may have suspended rendering while its viewer was hidden or minimized, "
            + "or the seat may be locked. Restarting the updated Anode daemon applies its background-rendering preference. "
            + "No foreground capture or viewer activation was attempted. Use seat_observe with includeScreenshot=false for accessible controls; do not guess screen coordinates.";

    private static bool IsSessionDisconnected()
    {
        const uint currentSession = uint.MaxValue; // WTS_CURRENT_SESSION
        const int connectState = 8, disconnected = 4; // WTSConnectState, WTSDisconnected
        IntPtr buffer = IntPtr.Zero;
        try
        {
            return WTSQuerySessionInformation(Native.Native.CurrentServer, currentSession, connectState, out buffer, out uint bytes)
                && buffer != IntPtr.Zero && bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == disconnected;
        }
        catch { return false; } // A failed query leaves the cause unknown.
        finally { if (buffer != IntPtr.Zero) Native.Native.WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass,
        out IntPtr buffer, out uint bytesReturned);

    /// <summary>A picture scaled down to maxWidth, if wider, and encoded; source sizes are the picture's own.</summary>
    public static Shot Encode(Bitmap raw, int? maxWidth, string format, int jpegQuality)
    {
        var source = new Size(raw.Width, raw.Height);
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
    /// It is read afresh each time, never from WinForms' cached screens, because an agent
    /// can change the seat's resolution while the seat host runs.
    /// </summary>
    public static Rectangle SeatBounds() => new(Point.Empty, Input.InputInjector.ScreenSize());
}
