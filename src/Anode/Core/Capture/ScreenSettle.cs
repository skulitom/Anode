using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Input;

namespace Anode.Core.Capture;

/// <summary>Waits for queued scroll input to be drawn, without holding up an already moving screen.</summary>
internal static class ScreenSettle
{
    // Milliseconds between frames, including the two frames before input.
    public const int PollMs = 40;
    // A changed screen must stay still this long before it is settled.
    public const int QuietMs = 200;
    // Allow this long for queued input to cause its first visible change.
    public const int StartMs = 500;
    // Stop watching after this long, even if the screen keeps changing.
    public const int CapMs = 2500;

    /// <summary>What watching needs from Windows. Quick checks pass stand-ins.</summary>
    internal sealed class Watcher
    {
        /// <summary>Pixels that stay valid through the next grab, or null when capture fails.</summary>
        public required Func<byte[]?> Grab { get; init; }
        public required Action<int> Wait { get; init; }
        /// <summary>Milliseconds on a monotonic clock.</summary>
        public required Func<double> Now { get; init; }
        public CancellationToken Cancel { get; init; }
    }

    public static bool IsScrollingKey(string chord) => KeyCodes.Chord(chord).Any(key => key is >= 0x21 and <= 0x24);

    /// <summary>
    /// Runs input exactly once; a failure to observe it never prevents it or asks for it to be replayed. Cancellation
    /// before the input throws <see cref="OperationCanceledException"/> without sending it.
    /// </summary>
    public static JsonObject Around(Action input, string action, Watcher watcher, int timeoutMs = 60_000)
    {
        double began = watcher.Now();
        int cap = Math.Min(CapMs, Math.Max(0, timeoutMs / 2));
        const string captureFailed = "whether the screen changed is unknown because the seat could not be captured";
        const string cancelled = "whether the screen changed is unknown because the wait was cancelled";
        const string outOfTime = "whether the screen changed is unknown because the time for watching ran out";

        byte[]? Grab()
        {
            try { return watcher.Grab(); }
            catch { return null; }
        }

        bool Wait(int milliseconds)
        {
            if (watcher.Cancel.IsCancellationRequested) return false;
            try { watcher.Wait(milliseconds); }
            catch (OperationCanceledException) { return false; }
            return !watcher.Cancel.IsCancellationRequested;
        }

        string? immediate = null, reason = null;
        byte[]? previous = null;
        if (watcher.Cancel.IsCancellationRequested) { immediate = "unknown"; reason = cancelled; }
        else if (Grab() is not { } first) { immediate = "unknown"; reason = captureFailed; }
        // Leave room for input and the reply even when very little of a request's time remains.
        else if (watcher.Now() - began + PollMs >= timeoutMs / 2.0) { immediate = "unknown"; reason = outOfTime; }
        else if (!Wait(PollMs)) { immediate = "unknown"; reason = cancelled; }
        else if ((previous = Grab()) is null) { immediate = "unknown"; reason = captureFailed; }
        else if (!first.AsSpan().SequenceEqual(previous))
        {
            immediate = "changing";
            reason = "the screen was already changing, so take a fresh screenshot before clicking by coordinates";
        }

        // A request cancelled before its input (its deadline, or a stopping seat) sends nothing and answers timed_out,
        // as InputInjector.Press does: its caller has given up, so the scroll must not happen afterwards.
        watcher.Cancel.ThrowIfCancellationRequested();
        // Keep input outside capture's catch: a real input error must still reach the caller, without a retry.
        input();
        double start = watcher.Now(), lastChange = start;
        if (watcher.Cancel.IsCancellationRequested) return Result("unknown", 0, cancelled);
        if (immediate is not null) return Result(immediate, 0, reason!);
        // A held key or slow capture may have used most of the request. Reserve time for its reply as well.
        double limit = Math.Min(cap, Math.Max(0, timeoutMs - (start - began) - PollMs));
        bool changed = false;
        while (true)
        {
            double now = watcher.Now(), elapsed = now - start;
            if (watcher.Cancel.IsCancellationRequested) return Result("unknown", elapsed, cancelled);
            if (elapsed >= limit) return End(elapsed);
            double next = changed ? lastChange + QuietMs - now : StartMs - elapsed;
            int wait = (int)Math.Ceiling(Math.Min(PollMs, Math.Min(limit - elapsed, next)));
            // Comparing frames takes time, so the quiet period or start window can end before the loop comes round:
            // look again at once. A negative wait would throw after the input, or never end at -1.
            if (wait > 0 && !Wait(wait)) return Result("unknown", watcher.Now() - start, cancelled);
            if (Grab() is not { } frame) return Result("unknown", watcher.Now() - start, captureFailed);
            now = watcher.Now();
            elapsed = now - start;
            if (watcher.Cancel.IsCancellationRequested) return Result("unknown", elapsed, cancelled);
            if (!previous.AsSpan().SequenceEqual(frame)) { changed = true; lastChange = now; }
            previous = frame;
            if (changed && now - lastChange >= QuietMs)
                return Result("settled", elapsed, $"the screen settled {(int)Math.Round(elapsed)} ms later");
            if (!changed && elapsed >= StartMs)
                return Result("unchanged", elapsed, $"the screen did not change within {(int)Math.Round(elapsed)} ms");
        }

        JsonObject End(double elapsed) => changed
            ? Result("changing", elapsed, "the screen was still changing, so take a fresh screenshot before clicking by coordinates")
            : Result("unknown", elapsed, outOfTime);

        JsonObject Result(string screen, double elapsed, string detail) => new()
        {
            ["screen"] = screen,
            ["screenWaitMs"] = (int)Math.Round(elapsed),
            ["summary"] = $"{action}; {detail}."
        };
    }

    /// <summary>Only the seat host calls this; it never changes focus, the pointer or the viewer.</summary>
    public static JsonObject Real(Action input, string action, int timeoutMs, CancellationToken cancel)
    {
        using var frames = new Frames();
        var clock = Stopwatch.StartNew();
        return Around(input, action, new Watcher
        {
            Grab = frames.Grab,
            Wait = milliseconds => cancel.WaitHandle.WaitOne(Math.Max(0, milliseconds)),
            Now = () => clock.Elapsed.TotalMilliseconds,
            Cancel = cancel
        }, timeoutMs);
    }

    private sealed class Frames : IDisposable
    {
        private Bitmap? _bitmap;
        private Graphics? _graphics;
        private byte[] _next = [], _previous = [];

        public byte[]? Grab()
        {
            try
            {
                Rectangle bounds = ScreenCapture.SeatBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                if (_bitmap is null || _bitmap.Size != bounds.Size)
                {
                    Dispose();
                    _bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                    _graphics = Graphics.FromImage(_bitmap);
                    _next = new byte[checked(bounds.Width * bounds.Height * 4)];
                    _previous = new byte[_next.Length];
                }
                _graphics!.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                var bits = _bitmap.LockBits(new Rectangle(Point.Empty, bounds.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = checked(bounds.Width * 4);
                    if (bits.Stride == rowBytes) Marshal.Copy(bits.Scan0, _next, 0, _next.Length);
                    else
                        for (int row = 0; row < bounds.Height; row++)
                            Marshal.Copy(IntPtr.Add(bits.Scan0, row * bits.Stride), _next, row * rowBytes, rowBytes);
                }
                finally { _bitmap.UnlockBits(bits); }
                // Keep the returned pixels intact while the next frame is copied into the other buffer.
                (_next, _previous) = (_previous, _next);
                return _previous;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            // Even cleanup of unavailable capture resources must not turn a sent scroll into a failure.
            try { _graphics?.Dispose(); } catch { }
            try { _bitmap?.Dispose(); } catch { }
            _graphics = null;
            _bitmap = null;
        }
    }
}
