using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Agents;
using Anode.Core.Bridge;
using Anode.Core.Display;
using Anode.Daemon;
using Anode.Mcp;

namespace Anode.Cli;

/// <summary>
/// Display changes against a stand-in daemon and screen. Nothing here changes a display, starts a viewer or reaches
/// a seat; the one measurement reads this desktop's size and scaling.
/// </summary>
internal static class DisplayChecks
{
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    public static string Modes()
    {
        var wide = new DisplayMode(1920, 1080, 150);
        Require(wide.EffectiveWidth == 1280 && wide.EffectiveHeight == 720 && wide.ToString() == "1920x1080 at 150%",
            "1920x1080 at 150% is not 1280x720 effective, or reads differently");
        foreach (int scale in DisplayMode.Scales)
            Require(DisplayMode.ScaleFromDpi(scale * 96 / 100) == scale, $"{scale}% does not survive its DPI");
        Require(new DisplayMode(1920, 1080, 100).PhysicalMillimetres() == (508u, 286u), "a 1920x1080 monitor at 100% is not 96 DPI");
        // Remote Desktop ignores the scale unless both physical sides are 10-10000 mm.
        foreach (var corner in new[] { new DisplayMode(640, 480, 500), new DisplayMode(8192, 8192, 100), new DisplayMode(640, 8192, 500), new DisplayMode(8192, 480, 100) })
        {
            var (width, height) = corner.PhysicalMillimetres();
            Require(width is >= 10 and <= 10000 && height is >= 10 and <= 10000, $"{corner} describes a monitor whose scale Remote Desktop ignores");
        }

        foreach (var (width, height, scale) in new[] { (640, 480, 100), (8192, 8192, 500), (1366, 768, 125), (1080, 1920, 100), (3840, 2160, 225) })
            Require(DisplayMode.Problem(width, height, scale) is null, $"{width}x{height} at {scale}% was refused");
        foreach (var (width, height, scale) in new[] { (639, 480, 100), (641, 480, 100), (640, 479, 100), (8194, 1080, 100), (1920, 8193, 100),
                     (1920, 1080, 110), (1920, 1080, 0), (1921, 1080, 150) })
            Require(DisplayMode.Problem(width, height, scale) is not null, $"{width}x{height} at {scale}% was accepted");

        Require(DisplayMode.TryParseSize("1920x1080", out int w, out int h) && (w, h) == (1920, 1080)
            && DisplayMode.TryParseSize("1080X1920", out w, out h) && (w, h) == (1080, 1920), "a written size did not parse");
        foreach (string text in new[] { "1920", "1920x", "x1080", "-1920x1080", "1920x1080x1", "1,920x1080", " 1920x1080", "1920×1080", "" })
            Require(!DisplayMode.TryParseSize(text, out _, out _), $"'{text}' parsed as a size");

        Require(DisplayMode.FromJson(wide.ToJson()) == wide && DisplayMode.FromJson(new JsonObject { ["width"] = 800, ["height"] = 600 }) == new DisplayMode(800, 600, 100)
            && DisplayMode.FromJson(new JsonObject { ["height"] = 600 }) is null && DisplayMode.FromJson(new JsonObject { ["width"] = 0, ["height"] = 600 }) is null
            && DisplayMode.FromJson(null) is null, "display JSON did not round-trip");
        return $"sizes, {DisplayMode.Scales.Length} scaling steps, DPI, physical size, written sizes and JSON";
    }

    public static string Validation()
    {
        JsonObject[] accepted =
        {
            new() { ["width"] = 1920, ["height"] = 1080 }, new() { ["scale"] = 150 }, new() { ["reset"] = true },
            new() { ["width"] = 1080, ["height"] = 1920, ["scale"] = 175, ["screenshot"] = true, ["maxWidth"] = 1600 },
            new() { ["reset"] = true, ["screenshot"] = true }, new() { ["reset"] = false, ["scale"] = 125 }
        };
        foreach (var args in accepted)
            Require(Tools.ValidateArguments("seat_display", args) is null, $"{args.ToJsonString()} was refused: {Tools.ValidateArguments("seat_display", args)}");
        JsonObject[] rejected =
        {
            new(), new() { ["reset"] = false }, new() { ["width"] = 1920 }, new() { ["height"] = 1080, ["scale"] = 100 },
            new() { ["width"] = 1921, ["height"] = 1080 }, new() { ["width"] = 600, ["height"] = 400 }, new() { ["scale"] = 110 },
            new() { ["reset"] = true, ["scale"] = 150 }, new() { ["reset"] = true, ["width"] = 1920, ["height"] = 1080 },
            new() { ["scale"] = 150, ["maxWidth"] = 800 }, new() { ["width"] = "1920", ["height"] = 1080 }, new() { ["scale"] = 150, ["orientation"] = 90 }
        };
        foreach (var args in rejected)
            Require(Tools.ValidateArguments("seat_display", args) is not null, $"{args.ToJsonString()} was accepted");
        Require(Tools.ValidateOperation("display.set", new JsonObject { ["width"] = 1921, ["height"] = 1080 }) is not null,
            "the daemon and seat host would forward an odd width");

        Require(Tools.TryResolve("seat_display", out string op, out bool starts) && op == "display.set" && !starts, "seat_display maps elsewhere or starts a seat");
        Require(AgentAccess.RequiresLease("display.set") && !Tools.ActsOnScreen("display.set"),
            "a display change must need the lease, and need no fresh look when Anode takes the lease for it");

        var sized = Cli.DisplayRequest(new List<string> { "1920x1080" }, new Args(new[] { "--scale", "150" }));
        Require(JsonNode.DeepEquals(sized, new JsonObject { ["width"] = 1920, ["height"] = 1080, ["scale"] = 150 })
            && JsonNode.DeepEquals(Cli.DisplayRequest(new List<string> { "reset" }, new Args(Array.Empty<string>())), new JsonObject { ["reset"] = true })
            && JsonNode.DeepEquals(Cli.DisplayRequest(new List<string>(), new Args(new[] { "--scale=125" })), new JsonObject { ["scale"] = 125 })
            && Cli.DisplayRequest(new List<string>(), new Args(new[] { "--json" })).Count == 0, "the CLI asked for a different display");
        return $"{accepted.Length} accepted and {rejected.Length} refused before any lease or daemon; the CLI maps to the same arguments";
    }

    /// <summary>A seat's screen and the daemon's display requests, with what each request does to the screen.</summary>
    private sealed class StandIn
    {
        private readonly object _gate = new();
        private DisplayMode _screen;
        public DisplayMode Startup = new(1280, 720, 100);
        /// <summary>What a live request leaves on screen; null ignores it, as Windows does without the display channel.</summary>
        public Func<DisplayMode, DisplayMode>? Live = target => target;
        public Func<DisplayMode, DisplayMode>? Reconnect = target => target;
        public string? LiveError, ReconnectError, ReconnectCode, RecordError;
        public bool Unreachable;
        /// <summary>Applies a live change this long after the request, as Windows does.</summary>
        public int LiveDelayMs;
        public readonly List<string> Calls = new();

        public StandIn(DisplayMode screen) => _screen = screen;

        public DisplayMode Screen
        {
            get { lock (_gate) return _screen; }
            set { lock (_gate) _screen = value; }
        }

        public Task<JsonObject> Daemon(JsonObject request, int timeoutMs, CancellationToken cancel)
        {
            if (Unreachable) return Task.FromResult(JsonLine.Fail("Anode's daemon did not answer."));
            if (!request.ContainsKey("width"))
            {
                lock (Calls) Calls.Add("startup");
                return Task.FromResult(JsonLine.Ok(new JsonObject { ["startup"] = Startup.ToJson() }));
            }
            var target = DisplayMode.FromJson(request)!.Value;
            string method = request.Str("method")!;
            lock (Calls) Calls.Add($"{method} {target}");
            // The seat host telling the daemon what the seat shows; nothing on screen changes.
            if (method == "record")
                return Task.FromResult(RecordError is { } refused ? JsonLine.Fail(refused) : JsonLine.Ok(new JsonObject { ["method"] = method, ["display"] = target.ToJson() }));
            if ((method == "live" ? LiveError : ReconnectError) is { } error)
            {
                var failure = JsonLine.Fail(error);
                if (method == "reconnect" && ReconnectCode is not null) failure["errorCode"] = ReconnectCode;
                return Task.FromResult(failure);
            }
            if ((method == "live" ? Live : Reconnect) is { } effect)
            {
                if (method == "live" && LiveDelayMs > 0) _ = Task.Delay(LiveDelayMs).ContinueWith(_ => Screen = effect(target));
                else Screen = effect(target);
            }
            return Task.FromResult(JsonLine.Ok(new JsonObject { ["method"] = method, ["display"] = target.ToJson() }));
        }

        public string[] Changes() { lock (Calls) return Calls.Where(call => call != "startup").ToArray(); }

        public SeatDisplay Display(List<string>? record = null) => new(Daemon, () => Screen, line => { lock (Calls) record?.Add(line); },
            liveWait: TimeSpan.FromMilliseconds(300), reconnectWait: TimeSpan.FromMilliseconds(300), poll: TimeSpan.FromMilliseconds(10));
    }

    private static readonly DisplayMode Hd = new(1280, 720, 100), Wide = new(1920, 1080, 150);

    private static JsonObject Change(int width, int height, int scale) => new() { ["width"] = width, ["height"] = height, ["scale"] = scale };

    public static async Task<string> Changes()
    {
        // Windows applies a live change a moment after the request; the seat host waits for it and never reconnects.
        var live = new StandIn(Hd) { LiveDelayMs = 60 };
        var reply = await live.Display().SetAsync(Change(1920, 1080, 150), CancellationToken.None);
        var result = reply.Obj("result") ?? new JsonObject();
        Require(reply.Bool("ok") == true && DisplayMode.FromJson(result) == Wide && result.Str("method") == "live" && result.Bool("changed") == true
            && DisplayMode.FromJson(result.Obj("previous")) == Hd && DisplayMode.FromJson(result.Obj("startup")) == Hd
            && result.Int("effectiveWidth") == 1280 && live.Changes().SequenceEqual(new[] { "live 1920x1080 at 150%" }),
            "a live change was not waited for, or was repeated: " + reply.ToJsonString());
        Require(result.Str("summary") is { } summary && summary.Contains("1920x1080 at 150%") && summary.Contains("changed it live")
            && summary.Contains("restore 1280x720 at 100%") && summary.Contains("launch the app under test again"),
            "the summary does not say what changed, how, or how it ends: " + result.Str("summary"));

        // Without the display channel nothing happens live, so the viewer reconnects at the new size.
        foreach (var standIn in new[] { new StandIn(Hd) { Live = null }, new StandIn(Hd) { LiveError = "E_UNEXPECTED" } })
        {
            reply = await standIn.Display().SetAsync(Change(1920, 1080, 150), CancellationToken.None);
            Require(reply.Bool("ok") == true && reply.Obj("result")?.Str("method") == "reconnect" && standIn.Screen == Wide
                && standIn.Changes().SequenceEqual(new[] { "live 1920x1080 at 150%", "reconnect 1920x1080 at 150%" }),
                "an ignored or refused live change did not fall back to one reconnect: " + reply.ToJsonString());
            Require(reply.Obj("result")!.Str("summary")!.Contains("reconnected its viewer"), "the summary hides the reconnect");
        }

        // Windows heard the request but chose another scale: reported as it is, without a pointless reconnect.
        var clamped = new StandIn(Hd) { Live = target => target with { Scale = 250 } };
        reply = await clamped.Display().SetAsync(Change(1280, 720, 300), CancellationToken.None);
        Require(reply.Bool("ok") == false && reply.Str("errorCode") == "display_not_applied" && DisplayMode.FromJson(reply.Obj("result")) == Hd with { Scale = 250 }
            && reply.Str("error")!.Contains("Windows applied 1280x720 at 250% instead of the requested 1280x720 at 300%")
            && clamped.Changes().SequenceEqual(new[] { "live 1280x720 at 300%", "record 1280x720 at 250%" }),
            "a display Windows chose differently was reported as applied, reconnected, or not told to the daemon: " + reply.ToJsonString());

        // Neither way works: the failure says why and what the seat still shows.
        var stuck = new StandIn(Hd) { Live = null, ReconnectError = "Windows did not sign the viewer back in within 45 seconds.", ReconnectCode = "viewer_detached" };
        reply = await stuck.Display().SetAsync(Change(1920, 1080, 100), CancellationToken.None);
        Require(reply.Bool("ok") == false && reply.Str("error")!.Contains("within 45 seconds") && reply.Str("error")!.Contains("It is 1280x720 at 100%")
            && DisplayMode.FromJson(reply.Obj("result")) == Hd && reply.Obj("result")!.Bool("changed") == false, "a failed change was not explained: " + reply.ToJsonString());
        // The viewer is asked back at the display the seat measured before, not left detached.
        Require(stuck.Changes().SequenceEqual(new[] { "live 1920x1080 at 100%", "reconnect 1920x1080 at 100%", "reconnect 1280x720 at 100%", "record 1280x720 at 100%" })
            && reply.Str("error")!.Contains("Windows did not apply it live."),
            "a failed reconnect did not ask for the display the seat had: " + string.Join(", ", stuck.Changes()));
        // A reconnect the daemon refused, as it does for a seat that signed in through the credential dialog, left the viewer alone.
        var kept = new StandIn(Hd) { Live = null, ReconnectError = "Reconnecting the viewer at the new size would need the Windows password" };
        reply = await kept.Display().SetAsync(Change(1920, 1080, 100), CancellationToken.None);
        // The daemon, which the live request left expecting 1920x1080, learns that the seat still shows 1280x720, so the
        // viewer's next connection (Reconnect, sign-in) does not bring back the change that failed.
        Require(reply.Bool("ok") == false && reply.Str("error")!.Contains("would need the Windows password")
            && kept.Changes().SequenceEqual(new[] { "live 1920x1080 at 100%", "reconnect 1920x1080 at 100%", "record 1280x720 at 100%" }),
            "a refused reconnect was followed by another, or the daemon kept the failed display: " + string.Join(", ", kept.Changes()));
        // A daemon that cannot take the note changes nothing for the agent; the seat host logs it.
        var notes = new List<string>();
        var deaf = new StandIn(Hd) { Live = null, ReconnectError = "Reconnecting the viewer at the new size would need the Windows password", RecordError = "The seat is not ready" };
        var unheard = await deaf.Display(notes).SetAsync(Change(1920, 1080, 100), CancellationToken.None);
        Require(unheard.Bool("ok") == false && unheard.Str("error") == reply.Str("error") && DisplayMode.FromJson(unheard.Obj("result")) == Hd
            && notes.Any(line => line.StartsWith("could not tell the daemon that the seat shows 1280x720 at 100%", StringComparison.Ordinal)),
            "a refused note changed the reply or went unlogged: " + unheard.ToJsonString());

        // Only the fields given change.
        var patch = new StandIn(new DisplayMode(1920, 1080, 100));
        reply = await patch.Display().SetAsync(new JsonObject { ["scale"] = 150 }, CancellationToken.None);
        Require(reply.Bool("ok") == true && patch.Changes().SequenceEqual(new[] { "live 1920x1080 at 150%" }), "a scale change moved the resolution");
        reply = await patch.Display().SetAsync(new JsonObject { ["width"] = 1366, ["height"] = 768 }, CancellationToken.None);
        Require(reply.Bool("ok") == true && patch.Screen == new DisplayMode(1366, 768, 150), "a resolution change moved the scale");

        // The same display again changes nothing.
        var same = new StandIn(Wide);
        reply = await same.Display().SetAsync(Change(1920, 1080, 150), CancellationToken.None);
        Require(reply.Bool("ok") == true && reply.Obj("result")!.Str("method") == "none" && same.Changes().Length == 0
            && reply.Obj("result")!.Str("summary")!.StartsWith("The seat's display is already 1920x1080 at 150%", StringComparison.Ordinal), "an unchanged display was requested again");

        // reset returns to the display the seat started with.
        var back = new StandIn(Wide);
        reply = await back.Display().SetAsync(new JsonObject { ["reset"] = true }, CancellationToken.None);
        Require(reply.Bool("ok") == true && back.Screen == Hd && back.Changes().SequenceEqual(new[] { "live 1280x720 at 100%" })
            && reply.Obj("result")!.Str("summary")!.Contains("startup display 1280x720 at 100% is back"), "reset did not restore the startup display");

        // A seat at a custom scale needs the whole display given; nothing is asked of Windows that it cannot show.
        var custom = new StandIn(new DisplayMode(1920, 1080, 110));
        reply = await custom.Display().SetAsync(new JsonObject { ["width"] = 2560, ["height"] = 1440 }, CancellationToken.None);
        Require(reply.Bool("ok") == false && reply.Str("error")!.Contains("not 110") && custom.Changes().Length == 0, "an impossible display was requested");

        var alone = new StandIn(Hd) { Unreachable = true };
        reply = await alone.Display().SetAsync(Change(1920, 1080, 100), CancellationToken.None);
        Require(reply.Bool("ok") == false && reply.Str("error")!.Contains("stays 1280x720 at 100%"), "an unreachable daemon was not reported");
        return "live changes are awaited; ignored or refused ones reconnect once; partial and failed changes report the actual display "
            + "and tell the daemon; only given fields change; reset and no-ops ask nothing extra";
    }

    public static async Task<string> Restore()
    {
        var record = new List<string>();
        var seat = new StandIn(Hd);
        var display = seat.Display(record);
        display.EndLease();
        Require(await display.RestoredAsync(CancellationToken.None) is null && seat.Calls.Count == 0, "an unchanged lease restored something");

        // An agent changes the display and its lease ends: the next owner's first action waits for the startup display.
        seat.LiveDelayMs = 120;
        Require((await display.SetAsync(Change(1920, 1080, 150), CancellationToken.None)).Bool("ok") == true, "the change failed");
        display.EndLease();
        using (var impatient = new CancellationTokenSource())
        {
            impatient.Cancel();
            var busy = await display.RestoredAsync(impatient.Token);
            Require(busy?.Str("errorCode") == "display_restoring" && busy.Str("error")!.Contains("Nothing was done"), "a pending restore did not hold the next action");
        }
        Require(await display.RestoredAsync(CancellationToken.None) is null && seat.Screen == Hd
            && seat.Changes().SequenceEqual(new[] { "live 1920x1080 at 150%", "live 1280x720 at 100%" })
            && record.Any(line => line.Contains("restored the startup display 1280x720 at 100%")), "the lease's end did not restore the startup display");
        display.EndLease();
        await display.RestoredAsync(CancellationToken.None);
        Require(seat.Changes().Length == 2, "a restored display was restored again");

        // An agent that resets before releasing leaves nothing to do.
        seat.LiveDelayMs = 0;
        await display.SetAsync(new JsonObject { ["scale"] = 200 }, CancellationToken.None);
        await display.SetAsync(new JsonObject { ["reset"] = true }, CancellationToken.None);
        display.EndLease();
        Require(await display.RestoredAsync(CancellationToken.None) is null && seat.Changes().Length == 4, "a reset display was restored again");

        // A restore that fails is tried again when the next lease ends.
        await display.SetAsync(new JsonObject { ["scale"] = 200 }, CancellationToken.None);
        seat.LiveError = "E_UNEXPECTED";
        seat.ReconnectError = "the viewer did not come back";
        display.EndLease();
        await display.RestoredAsync(CancellationToken.None);
        Require(seat.Screen == Hd with { Scale = 200 } && record.Any(line => line.StartsWith("could not restore", StringComparison.Ordinal)), "a failed restore was not recorded");
        seat.LiveError = seat.ReconnectError = null;
        display.EndLease();
        await display.RestoredAsync(CancellationToken.None);
        Require(seat.Screen == Hd, "a failed restore was not retried at the next lease's end");
        return "a lease's end restores a changed display once, holds the next desktop action until done, and retries a failed restore";
    }

    public static async Task<string> Daemon()
    {
        int changes = 0;
        Task Change(DisplayMode target, bool reconnect, CancellationToken cancel) { changes++; return Task.CompletedTask; }
        using var daemon = new AnodeDaemon(new SeatOptions { Width = 1600, Height = 900, ScaleFactor = 125 }, () => null, () => null,
            () => Task.CompletedTask, _ => null, () => { }, Change);
        var asked = await daemon.HandleControlAsync(new JsonObject { ["op"] = "seat.display" });
        Require(asked.Bool("ok") == true && DisplayMode.FromJson(asked.Obj("result")?.Obj("startup")) == new DisplayMode(1600, 900, 125),
            "the startup display is not the one Anode was started with: " + asked.ToJsonString());
        Require(DisplayMode.FromJson((await daemon.StatusAsync()).Obj("startupDisplay")) == new DisplayMode(1600, 900, 125), "status lacks the startup display");

        foreach (var (request, expected) in new[]
                 {
                     (new JsonObject { ["width"] = 1921, ["height"] = 1080, ["scale"] = 100, ["method"] = "live" }, "must be even"),
                     (new JsonObject { ["width"] = 1920, ["height"] = 1080, ["scale"] = 120, ["method"] = "live" }, "Scaling is one of"),
                     (new JsonObject { ["width"] = 1920, ["height"] = 1080, ["scale"] = 100, ["method"] = "sideways" }, "method must be"),
                     (new JsonObject { ["width"] = 1920, ["height"] = 1080, ["scale"] = 100, ["method"] = "live" }, "not ready"),
                     (new JsonObject { ["width"] = 1920, ["height"] = 1080, ["scale"] = 100, ["method"] = "reconnect" }, "not ready"),
                     // A measured display: a custom scale or an odd width is fine, but only a ready seat has one to remember.
                     (new JsonObject { ["width"] = 1921, ["height"] = 1080, ["scale"] = 110, ["method"] = "record" }, "not ready"),
                     (new JsonObject { ["width"] = 1920, ["height"] = 1080, ["scale"] = 600, ["method"] = "record" }, "not a display the viewer can ask for"),
                     (new JsonObject { ["width"] = 100, ["height"] = 1080, ["scale"] = 100, ["method"] = "record" }, "not a display the viewer can ask for")
                 })
        {
            var reply = await daemon.DisplayAsync(request);
            Require(reply.Bool("ok") == false && reply.Str("error")!.Contains(expected), $"{request.ToJsonString()} was not refused with '{expected}': {reply.ToJsonString()}");
        }
        Require(changes == 0, "a display change reached the viewer of a seat that is not ready");

        using var plain = new AnodeDaemon(new SeatOptions { Width = 100, Height = 50000 });
        Require(DisplayMode.FromJson((await plain.DisplayAsync(new JsonObject())).Obj("result")?.Obj("startup")) == new DisplayMode(640, 8192, 100),
            "an unscaled seat's startup display is not its clamped size at 100%");
        return "reports the startup display from the options and status; refuses invalid requests, and every change or record while the seat is not ready";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    /// <summary>Reads this desktop's display the way the seat host reads the seat's; nothing is captured or changed.</summary>
    public static string Measurement()
    {
        IntPtr before = GetThreadDpiAwarenessContext();
        var display = SeatDisplay.Measure();
        Require(AreDpiAwarenessContextsEqual(before, GetThreadDpiAwarenessContext()), "measuring changed this thread's DPI awareness");
        Require(display.Width > 0 && display.Height > 0 && display.Scale is >= 100 and <= 500, $"implausible display {display}");
        return $"this desktop reads as {display} in physical pixels; the thread's DPI awareness is restored";
    }
}
