using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Display;

/// <summary>
/// Changes the seat's resolution and scaling for the agent holding the desktop, and puts the startup
/// display back when that agent's lease ends.
///
/// The Remote Desktop control that gives the seat its display lives in the daemon, so the daemon makes each
/// change. The seat host decides what to ask for and measures the seat's own screen, because Windows applies
/// a display change asynchronously and may not apply one live at all: nothing counts as applied until the
/// seat shows it. When a live change does not arrive, the daemon reconnects its viewer at the new size, which
/// keeps the session and every app in it.
/// </summary>
internal sealed class SeatDisplay
{
    /// <summary>The daemon's seat.display operation: (request, timeout in ms, cancellation) to its reply.</summary>
    private readonly Func<JsonObject, int, CancellationToken, Task<JsonObject>> _daemon;
    private readonly Func<DisplayMode> _measure;
    private readonly Action<string> _record;
    private readonly TimeSpan _liveWait, _reconnectWait, _poll;
    private readonly object _gate = new();
    /// <summary>The display differs from the startup display, so the current lease's end restores it.</summary>
    private bool _changed;
    private Task _restoring = Task.CompletedTask;

    public SeatDisplay(Func<JsonObject, int, CancellationToken, Task<JsonObject>> daemon, Func<DisplayMode>? measure = null,
        Action<string>? record = null, TimeSpan? liveWait = null, TimeSpan? reconnectWait = null, TimeSpan? poll = null)
    {
        _daemon = daemon;
        _measure = measure ?? Measure;
        _record = record ?? (_ => { });
        _liveWait = liveWait ?? TimeSpan.FromSeconds(6);
        _reconnectWait = reconnectWait ?? TimeSpan.FromSeconds(15);
        _poll = poll ?? TimeSpan.FromMilliseconds(150);
    }

    // ------------------------------------------------------------------ measuring

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private const int ScreenWidth = 0, ScreenHeight = 1, EffectiveDpi = 0;
    private const uint DefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Makes this process see physical pixels at every scale, so screenshots, input and window or control bounds
    /// are the seat's real pixels rather than the smaller, DPI-scaled ones Windows gives an unaware process.
    /// Call before anything creates a window.
    /// </summary>
    public static bool UsePhysicalPixels() => SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    /// <summary>The seat's primary display now, in physical pixels, whatever this process's DPI awareness.</summary>
    public static DisplayMode Measure()
    {
        IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            int width = GetSystemMetrics(ScreenWidth), height = GetSystemMetrics(ScreenHeight);
            IntPtr monitor = MonitorFromPoint(new Point(), DefaultToPrimary);
            int scale = GetDpiForMonitor(monitor, EffectiveDpi, out uint dpi, out _) == 0 && dpi > 0 ? DisplayMode.ScaleFromDpi((int)dpi) : 100;
            return new DisplayMode(width, height, scale);
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    // ------------------------------------------------------------------- the lease

    /// <summary>
    /// Called as a lease ends: puts back the startup display if the display was changed. It runs in the
    /// background; <see cref="RestoredAsync"/> holds the next desktop action until it is done.
    /// </summary>
    public void EndLease()
    {
        lock (_gate)
        {
            if (!_changed) return;
            _changed = false;
            Task earlier = _restoring;
            _restoring = Task.Run(async () =>
            {
                try { await earlier.ConfigureAwait(false); } catch { }
                try
                {
                    var reply = await SetAsync(new JsonObject { ["reset"] = true }, CancellationToken.None).ConfigureAwait(false);
                    _record(reply.Bool("ok") == true
                        ? $"restored the startup display {DisplayMode.FromJson(reply.Obj("result"))} after the desktop lease ended"
                        : $"could not restore the startup display after the desktop lease ended: {reply.Str("error")}");
                }
                catch (Exception ex) { _record($"could not restore the startup display after the desktop lease ended: {ex.Message}"); }
            });
        }
    }

    /// <summary>
    /// Null once no restore is pending; otherwise waits for it, so the next owner acts on the startup display.
    /// A failure explains why the action was not attempted.
    /// </summary>
    public async Task<JsonObject?> RestoredAsync(CancellationToken cancel)
    {
        Task pending;
        lock (_gate) pending = _restoring;
        if (pending.IsCompleted) return null;
        try { await pending.WaitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            var busy = JsonLine.Fail("The seat is still restoring its startup display after the previous lease. Nothing was done; try again in a few seconds.");
            busy["errorCode"] = "display_restoring";
            return busy;
        }
        return null;
    }

    // ---------------------------------------------------------------- changing it

    /// <summary>
    /// Applies width and height, scale or reset (validated by the seat_display schema) and replies with the
    /// display the seat shows afterwards. A change Windows did not make, fully or at all, is a failure that
    /// still reports the actual display.
    /// </summary>
    public async Task<JsonObject> SetAsync(JsonObject args, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var before = _measure();
        var asked = await _daemon(new JsonObject(), 10_000, cancel).ConfigureAwait(false);
        if (asked.Bool("ok") != true || DisplayMode.FromJson(asked.Obj("result")?.Obj("startup")) is not { } startup)
            return JsonLine.Fail($"The seat host could not reach Anode's daemon, so the display stays {before}. {asked.Str("error")}".TrimEnd());

        bool reset = args.Bool("reset") == true;
        var target = reset ? startup
            : new DisplayMode(args.Int("width") ?? before.Width, args.Int("height") ?? before.Height, args.Int("scale") ?? before.Scale);
        if (DisplayMode.Problem(target.Width, target.Height, target.Scale) is { } problem)
            return JsonLine.Fail($"{problem} The seat's display is {before}" + (reset ? "." : "; give the width, height and scale you want together."));

        string method = "none";
        string? failure = null;
        var after = before;
        if (target != before)
        {
            (after, method, failure) = await ApplyAsync(before, target, cancel).ConfigureAwait(false);
        }
        lock (_gate) _changed = after != startup;
        // The daemon's viewer asks for the requested display again on every later connection. When the seat shows
        // another one, the daemon learns which, so a Reconnect or sign-in never brings back a change that failed.
        if (target != before && after != target) await RememberAsync(after).ConfigureAwait(false);

        var result = Describe(after, before, startup, target, method, reset, clock.ElapsedMilliseconds);
        if (after == target) return JsonLine.Ok(result);
        var fail = JsonLine.Fail(failure is not null
            ? $"The seat's display could not be changed to {target}: {failure} It is {after}."
            : $"Windows applied {after} instead of the requested {target}. That is the seat's display now; screenshots and input use it.");
        fail["errorCode"] = "display_not_applied";
        fail["result"] = result;
        return fail;
    }

    // A reconnect takes the daemon at most 10 s to disconnect and 45 s to sign back in. With the live attempt and a
    // reconnect at the old display after a failed one, a change stays within the 175 s clients allow seat_display.
    private const int LiveTimeoutMs = 10_000, ReconnectTimeoutMs = 65_000;

    private async Task<(DisplayMode After, string Method, string? Failure)> ApplyAsync(DisplayMode before, DisplayMode target, CancellationToken cancel)
    {
        var live = await AskAsync(target, "live", LiveTimeoutMs, cancel).ConfigureAwait(false);
        var now = await WatchAsync(target, live is null ? _liveWait : TimeSpan.Zero, cancel).ConfigureAwait(false);
        if (now == target) return (now, "live", null);
        // Windows heard the request and chose something else, such as a smaller scale; a reconnect would ask the same.
        if (live is null && now != before) return (now, "live", null);

        string notLive = "Windows did not apply it live" + (live is null ? "." : $" ({Why(live)}).");
        _record($"the seat's display did not change live to {target}{(live is null ? "" : $" ({Why(live)})")}; reconnecting the viewer at the new size");
        var reconnect = await AskAsync(target, "reconnect", ReconnectTimeoutMs, cancel).ConfigureAwait(false);
        if (reconnect is null) return (await WatchAsync(target, _reconnectWait, cancel).ConfigureAwait(false), "reconnect", null);
        // A viewer the failed reconnect left without a connection goes back to the display the seat had.
        if (reconnect.Str("errorCode") == "viewer_detached" && DisplayMode.Problem(before.Width, before.Height, before.Scale) is null
            && await AskAsync(before, "reconnect", ReconnectTimeoutMs, cancel).ConfigureAwait(false) is { } stranded)
            _record($"the viewer could not reconnect at {before} either ({Why(stranded)}); it stays detached until someone presses Reconnect");
        return (_measure(), "reconnect", $"{notLive} {Why(reconnect)}");
    }

    /// <summary>Null when the daemon made the request, or its failure.</summary>
    private async Task<JsonObject?> AskAsync(DisplayMode target, string method, int timeoutMs, CancellationToken cancel)
    {
        var request = target.ToJson();
        request["method"] = method;
        var reply = await _daemon(request, timeoutMs, cancel).ConfigureAwait(false);
        return reply.Bool("ok") == true ? null : reply;
    }

    private static string Why(JsonObject failure) => failure.Str("error") ?? "Anode's daemon did not change the display.";

    /// <summary>
    /// Tells the daemon the display the seat shows. It runs even when the caller has gone, since the daemon would
    /// otherwise keep the display it was asked for; the daemon answers at once, so it is briefly bounded instead.
    /// </summary>
    private async Task RememberAsync(DisplayMode shown)
    {
        var request = shown.ToJson();
        request["method"] = "record";
        try
        {
            var reply = await _daemon(request, 5_000, CancellationToken.None).ConfigureAwait(false);
            if (reply.Bool("ok") != true) _record($"could not tell the daemon that the seat shows {shown}: {Why(reply)}");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            _record($"could not tell the daemon that the seat shows {shown}: {ex.Message}");
        }
    }

    private async Task<DisplayMode> WatchAsync(DisplayMode target, TimeSpan wait, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var now = _measure();
            if (now == target || clock.Elapsed >= wait) return now;
            await Task.Delay(_poll, cancel).ConfigureAwait(false);
        }
    }

    private static JsonObject Describe(DisplayMode after, DisplayMode before, DisplayMode startup, DisplayMode target,
        string method, bool reset, long elapsedMs)
    {
        var result = after.ToJson();
        result["effectiveWidth"] = after.EffectiveWidth;
        result["effectiveHeight"] = after.EffectiveHeight;
        result["previous"] = before.ToJson();
        result["startup"] = startup.ToJson();
        result["changed"] = after != before;
        result["method"] = method;
        result["elapsedMs"] = elapsedMs;

        string effective = after.Scale == 100 ? "" : $" ({after.EffectiveWidth}x{after.EffectiveHeight} effective)";
        string lasts = after == startup ? "" : $" It lasts until your lease ends or seat_display reset=true, which restore {startup}.";
        if (after == before)
        {
            result["summary"] = (after == target ? $"The seat's display is already {after}{effective}." : $"The seat's display is still {after}{effective}.") + lasts;
            return result;
        }
        string how = method == "reconnect"
            ? "Anode reconnected its viewer at the new size, because Windows did not change the display live; the seat and its apps kept running."
            : "Windows changed it live, as when a monitor changes, and the seat's apps kept running.";
        string scaled = after.Scale == before.Scale ? ""
            : " Apps that read scaling only at startup keep their old layout, stretched by Windows, until restarted; launch the app under test again to see it start at this scale.";
        result["summary"] = (reset ? $"The seat's startup display {after}{effective} is back, from {before}. " : $"The seat's display is now {after}{effective}, from {before}. ")
            + how + $" Screenshots and click coordinates use {after.Width}x{after.Height} pixels; observe again before acting, since windows and controls moved."
            + scaled + lasts;
        return result;
    }
}
