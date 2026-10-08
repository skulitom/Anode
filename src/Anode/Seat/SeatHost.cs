using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Capture;
using Anode.Core.Gamepad;
using Anode.Core.Input;
using Anode.Core.Processes;
using Anode.Core.Session;
using Anode.Core.Util;
using Anode.Core.Desktop;
using Anode.Core.Agents;
using Anode.Core.Android;
using Anode.Core.Audio;
using Anode.Core.Browser;
using Anode.Core.Display;

namespace Anode.Seat;

/// <summary>
/// The half of Anode that lives inside the seat.
///
/// It is launched once, into the child session, by the daemon. From then on it is an
/// ordinary interactive process in that session, which is precisely what makes the
/// isolation real: the programs it starts are children of a process in the seat, the
/// input it injects goes to the seat's input queue, and the screen it captures is the
/// seat's desktop. Applications can still hand work to their existing instances
/// elsewhere; process placement alone does not isolate shared application state.
///
/// It serves one named pipe and speaks the same newline-delimited JSON as everything
/// else. It dies when the seat is logged off, which is the intended way to stop it.
/// </summary>
internal static class SeatHost
{
    private static readonly GamepadManager Gamepads = new(record: Log.Info);
    private static PadIsolation? _padIsolation;
    private static readonly ManualResetEventSlim Stopping = new(false);
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly DesktopTools Desktop = new();
    private static readonly ExecutionJobs Jobs = new();
    private static readonly SeatAudio Audio = new();
    private static readonly SeatDisplay Display = new(AskDaemonAsync, record: Log.Info);
    private static readonly Lazy<SeatEmulators> Emulators = new(() =>
        new SeatEmulators(ChildSession.CurrentSessionId(), Path.Combine(Env.StateDirectory, "android")));
    private static readonly DesktopLease Lease = new(EndLease, Jobs.CancelOwned, record: Log.Info);
    private static readonly CancellationTokenSource DesktopStopping = new();
    /// <summary>The user's own session, as the parent daemon reported it; Steam belongs there.</summary>
    private static uint? _desktopSession;

    public static int Run()
    {
        Log.SetRole("seat");
        uint session = ChildSession.CurrentSessionId();
        Log.Info($"seat host starting in session {session} (pid {System.Environment.ProcessId})");
        // Screenshots and input use the seat's physical pixels at every scale, as the desktop worker's bounds do.
        if (!SeatDisplay.UsePhysicalPixels()) Log.Warn("could not make the seat host DPI aware; above 100% scaling, screenshots and input use DPI-scaled coordinates");

        // Refuse to run in the parent session. If the Task Scheduler hand-off ever
        // misfires, injecting input here would type onto the user's screen, which is
        // the one thing this project exists to prevent.
        // WTSGetChildSessionId is relative to the caller's session. Ask the daemon
        // in the parent session to resolve it, rather than looking for a child of
        // the child session from here.
        try { VerifyCurrentSession(); }
        catch (InvalidOperationException)
        {
            Log.Error($"seat host refuses to run: the parent daemon did not identify session {session} as its child");
            Console.Error.WriteLine(
                $"anode seat host must run inside the child session. This process is in session {session}.");
            return 3;
        }

        // Virtual controllers plugged in while the seat runs stay in the seat when HidHide is installed.
        try
        {
            _padIsolation = new PadIsolation(session, desktopProbe: AskDesktop, record: Log.Info);
            Gamepads.Isolate(_padIsolation);
            _padIsolation.Start();
        }
        catch (Exception ex) { Log.Error("could not start keeping virtual controllers inside the seat", ex); }

        using var server = new JsonPipeServer(Env.SeatPipe, HandleAsync);
        server.Start();
        using var expiry = new System.Threading.Timer(_ =>
        {
            try { Lease.ExpireIdle(); }
            catch (Exception ex) { Log.Error("lease cleanup failed; desktop transfer remains blocked", ex); }
        }, null, 1000, 1000);
        Log.Info($"seat host listening on \\\\.\\pipe\\{Env.SeatPipe}");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => { Audio.CancelPlayback(); Jobs.Dispose(); Gamepads.Dispose(); _padIsolation?.Dispose(); };
        Stopping.Wait();

        Gamepads.Dispose();
        _padIsolation?.Dispose();
        Jobs.Dispose();
        Audio.Dispose();
        Log.Info("seat host stopped");
        return 0;
    }

    /// <summary>The daemon's display operation: the startup display, or a change it makes through its viewer.</summary>
    private static async Task<JsonObject> AskDaemonAsync(JsonObject request, int timeoutMs, CancellationToken cancel)
    {
        using var daemon = await JsonPipeClient.TryConnectAsync(Env.ControlPipe, 2000, cancel).ConfigureAwait(false);
        return daemon is null ? JsonLine.Fail("Anode's daemon did not answer.")
            : await daemon.RequestAsync("seat.display", request, timeoutMs, cancel).ConfigureAwait(false);
    }

    /// <summary>Asks the daemon, which runs in the user's session, whether that session can open these pad devices.</summary>
    private static JsonObject? AskDesktop(IReadOnlyList<string> devices)
    {
        using var daemon = JsonPipeClient.TryConnectAsync(Env.ControlPipe, 2000).GetAwaiter().GetResult();
        var response = daemon?.RequestAsync("seat.pad-visibility",
            new JsonObject { ["devices"] = new JsonArray(devices.Select(device => (JsonNode)device).ToArray()) }, 5000).GetAwaiter().GetResult();
        return response?.Bool("ok") == true ? response.Obj("result") : null;
    }

    internal static void VerifyCurrentSession()
    {
        uint session = ChildSession.CurrentSessionId();
        using var daemon = JsonPipeClient.TryConnectAsync(Env.ControlPipe, 5000).GetAwaiter().GetResult();
        var identity = daemon?.RequestAsync("seat.identity", timeoutMs: 5000).GetAwaiter().GetResult();
        if (!MatchesSeat(session, identity))
            throw new InvalidOperationException($"Refusing desktop access: the parent daemon did not verify session {session} as its child.");
        _desktopSession = (uint?)identity!.Obj("result")!.Int("parentSession");
    }

    private static void EndLease()
    {
        Audio.StopPlayback();
        Desktop.InvalidateLease();
        InputInjector.ReleaseHeld();
        Gamepads.DetachAll(requireSuccess: true);
        Display.EndLease();
    }

    internal static bool MatchesSeat(uint session, JsonObject? identity)
    {
        var result = identity?.Obj("result");
        return session is > 0 and < int.MaxValue
            && identity?.Bool("ok") == true
            && result?.Int("session") == (int)session
            && result.Int("parentSession") is int parent and > 0
            && parent != (int)session;
    }

    private static Task<JsonObject> HandleAsync(JsonObject request)
    {
        // Emergency shutdown remains independent of desktop ownership and the interaction queue.
        if (request.Str("op") == "shutdown")
        {
            DesktopStopping.Cancel(); Audio.CancelPlayback(); Jobs.Dispose();
            return Task.FromResult(Dispatch("shutdown", request));
        }
        return Lease.HandleAsync(request, HandleOwnedAsync, DesktopStopping.Token);
    }

    private static async Task<JsonObject> HandleOwnedAsync(JsonObject request, CancellationToken cancel)
    {
        string op = request.Str("op") ?? string.Empty;
        // The display a previous lease changed is put back before the next owner looks or acts, in the action's time.
        if (AgentAccess.RequiresLease(op) && await Display.RestoredAsync(cancel, request).ConfigureAwait(false) is { } restoring) return restoring;
        if (op == "display.set") return await ChangeDisplayAsync(request, cancel).ConfigureAwait(false);
        if (op == "android.status") return JsonLine.Ok(await Emulators.Value.StatusAsync(cancel).ConfigureAwait(false));
        if (op == "android.emulator") return await EmulatorAsync(request, cancel).ConfigureAwait(false);
        if (op == "audio.listen") return JsonLine.Ok(await SeatAudio.ListenAsync(request.Int("durationMs") ?? 5000, cancel).ConfigureAwait(false));
        if (op == "audio.play") return JsonLine.Ok(await Audio.PlayAsync(AgentAccess.Arguments(request), cancel).ConfigureAwait(false));
        if (op is "exec.start" or "exec.read")
        {
            var args = AgentAccess.Arguments(request);
            string owner = request.Str("agentId")!;
            if (op == "exec.read") return JsonLine.Ok(await Jobs.ReadAsync(args, owner, cancel).ConfigureAwait(false));
            Desktop.InvalidateObservations();
            return JsonLine.Ok(await Jobs.StartAsync(args, owner, cancel).ConfigureAwait(false));
        }
        bool desktop = DesktopTools.ToolName(op) is not null;
        bool changesUi = op.StartsWith("input.", StringComparison.Ordinal) || op is "run" or "steam.launch" or "ps.kill" or "android.studio" or "browser.open";
        if (!desktop && !changesUi) return Dispatch(op, request);
        if (changesUi) Desktop.InvalidateObservations();
        // Typing can take most of a request's time, so it stops at the request's deadline, or at a shutdown sent on
        // another connection. The daemon's own shutdown waits behind it; the sign-out that follows ends it.
        if (op == "input.text")
            return InputInjector.TypeText(request.Str("text") ?? throw new ArgumentException("text needs 'text'."),
                request.Int("perCharMs"), request.Int("timeoutMs") ?? 60_000, cancel);
        return desktop ? await Desktop.HandleAsync(op, request, cancel).ConfigureAwait(false) : Dispatch(op, request);
    }

    private static async Task<JsonObject> EmulatorAsync(JsonObject request, CancellationToken cancel)
    {
        var args = AgentAccess.Arguments(request);
        string action = args.Str("action")!;
        // Starting, stopping and driving an emulator change what its window shows.
        if (action != "screenshot") Desktop.InvalidateObservations();
        return action switch
        {
            // Start reads the caller's agent and deadline as well.
            "start" => await Emulators.Value.StartAsync(request, cancel).ConfigureAwait(false),
            "stop" => await Emulators.Value.StopAsync(args, cancel).ConfigureAwait(false),
            "screenshot" => await Emulators.Value.ScreenshotAsync(args, cancel).ConfigureAwait(false),
            _ => await Emulators.Value.AdbAsync(args, cancel).ConfigureAwait(false)
        };
    }

    private static async Task<JsonObject> ChangeDisplayAsync(JsonObject request, CancellationToken cancel)
    {
        var args = AgentAccess.Arguments(request);
        // Every window and control may move or resize; window IDs stay valid.
        Desktop.InvalidateObservations();
        var reply = await Display.SetAsync(args, cancel).ConfigureAwait(false);
        if (reply.Bool("ok") != true || args.Bool("screenshot") != true || reply.Obj("result") is not { } result) return reply;
        // Apps redraw and Explorer lays the taskbar out again after a display change.
        await Task.Delay(750, cancel).ConfigureAwait(false);
        try
        {
            var shot = ScreenCapture.Capture(args.Int("maxWidth") ?? 1280, "png");
            result["screenshot"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(shot.Bytes), ["mimeType"] = shot.MimeType,
                ["width"] = shot.Width, ["height"] = shot.Height, ["sourceWidth"] = shot.SourceWidth, ["sourceHeight"] = shot.SourceHeight
            };
        }
        catch (Exception ex)
        {
            result["screenshotError"] = "Seat capture unavailable: " + ex.Message + " The display change stands; use seat_observe for accessible controls.";
        }
        return reply;
    }

    private static JsonObject Dispatch(string op, JsonObject r)
    {
        switch (op)
        {
            case "audio.status": return JsonLine.Ok(Audio.Status());
            case "audio.stop": return JsonLine.Ok(Audio.StopPlayback());

            case "ping":
            {
                var display = SeatDisplay.Measure();
                var cursor = InputInjector.CursorPosition();
                return JsonLine.Ok(new JsonObject
                {
                    ["session"] = ChildSession.CurrentSessionId(),
                    ["pid"] = System.Environment.ProcessId,
                    ["user"] = System.Environment.UserName,
                    ["uptimeSeconds"] = Math.Round(Uptime.Elapsed.TotalSeconds, 1),
                    ["screen"] = display.ToJson(),
                    ["cursor"] = new JsonObject { ["x"] = cursor.X, ["y"] = cursor.Y }
                });
            }

            case "screenshot":
            {
                var shot = ScreenCapture.Capture(
                    maxWidth: r.Int("maxWidth"),
                    format: r.Str("format") ?? "png",
                    jpegQuality: r.Int("quality") ?? 80);

                return JsonLine.Ok(new JsonObject
                {
                    ["mimeType"] = shot.MimeType,
                    ["width"] = shot.Width,
                    ["height"] = shot.Height,
                    ["sourceWidth"] = shot.SourceWidth,
                    ["sourceHeight"] = shot.SourceHeight,
                    ["bytes"] = shot.Bytes.Length,
                    ["data"] = Convert.ToBase64String(shot.Bytes)
                });
            }

            case "input.move":
            {
                int? dx = r.Int("dx");
                int? dy = r.Int("dy");
                if (dx.HasValue || dy.HasValue)
                {
                    // Relative motion, which is what games reading raw input expect.
                    InputInjector.MoveBy(dx ?? 0, dy ?? 0);
                }
                else
                {
                    int x = r.Int("x") ?? throw new ArgumentException("move needs x and y, or dx and dy.");
                    int y = r.Int("y") ?? throw new ArgumentException("move needs x and y, or dx and dy.");
                    InputInjector.MoveTo(x, y);
                }
                var at = InputInjector.CursorPosition();
                return JsonLine.Ok(new JsonObject { ["x"] = at.X, ["y"] = at.Y });
            }

            case "input.click":
            {
                InputInjector.Click(
                    r.Str("button") ?? "left",
                    r.Int("x"), r.Int("y"),
                    r.Int("count") ?? 1,
                    r.Int("holdMs") ?? 20);
                return JsonLine.Ok();
            }

            case "input.down":
                InputInjector.MouseDown(r.Str("button") ?? "left", r.Int("x"), r.Int("y"));
                return JsonLine.Ok();

            case "input.up":
                InputInjector.MouseUp(r.Str("button") ?? "left", r.Int("x"), r.Int("y"));
                return JsonLine.Ok();

            case "input.drag":
                InputInjector.Drag(
                    r.Int("fromX") ?? throw new ArgumentException("drag needs fromX."),
                    r.Int("fromY") ?? throw new ArgumentException("drag needs fromY."),
                    r.Int("toX") ?? throw new ArgumentException("drag needs toX."),
                    r.Int("toY") ?? throw new ArgumentException("drag needs toY."),
                    r.Str("button") ?? "left",
                    r.Int("steps") ?? 24,
                    r.Int("stepMs") ?? 8);
                return JsonLine.Ok();

            case "input.scroll":
                InputInjector.Scroll(r.Int("amount") ?? -3, r.Bool("horizontal") ?? false);
                return JsonLine.Ok();

            case "input.key":
                InputInjector.Press(
                    r.Str("keys") ?? throw new ArgumentException("key needs 'keys', for example \"ctrl+s\"."),
                    r.Int("holdMs") ?? 40,
                    r.Bool("scanCode") ?? true);
                return JsonLine.Ok();

            case "input.keydown":
                InputInjector.KeyDown(r.Str("key") ?? throw new ArgumentException("keydown needs 'key'."),
                    r.Bool("scanCode") ?? true);
                return JsonLine.Ok();

            case "input.keyup":
                InputInjector.KeyUp(r.Str("key") ?? throw new ArgumentException("keyup needs 'key'."),
                    r.Bool("scanCode") ?? true);
                return JsonLine.Ok();

            case "run":
                return RunProgram(r, ChildSession.CurrentSessionId());

            case "android.studio":
                return SeatStudio.Open(AgentAccess.Arguments(r), System.Environment.GetEnvironmentVariable, Process.Start);

            case "browser.open":
                return SeatBrowser.Open(AgentAccess.Arguments(r), System.Environment.GetEnvironmentVariable, SeatBrowser.AppPath, Process.Start);

            case "steam.status":
            {
                // Clients that read only structured results need the verdict as data, not just in the summary.
                uint session = ChildSession.CurrentSessionId();
                int[] running = Core.Steam.Steam.RunningSessions();
                var client = Core.Steam.Steam.ActiveClient();
                return JsonLine.Ok(new JsonObject
                {
                    ["steamExe"] = Core.Steam.Steam.FindExecutable(),
                    ["seatSession"] = (int)session,
                    ["runningSessions"] = new JsonArray(running.Select(s => (JsonNode)s).ToArray()),
                    ["runningOutsideSeat"] = running.Any(s => s != (int)session),
                    ["clientSession"] = client?.Session,
                    ["signedIn"] = client?.SignedIn ?? false,
                    ["summary"] = Core.Steam.Steam.Describe(session, _desktopSession, client)
                });
            }

            case "steam.launch":
                return Core.Steam.SteamLaunch.Run(r, ChildSession.CurrentSessionId(), _desktopSession, Core.Steam.SteamLaunch.Machine.Real);

            case "ps.list":
                return JsonLine.Ok(new JsonObject
                {
                    ["session"] = ChildSession.CurrentSessionId(),
                    ["processes"] = ProcessControl.List(ChildSession.CurrentSessionId(), r.Bool("windowedOnly") ?? true)
                });

            case "ps.kill":
            {
                uint session = ChildSession.CurrentSessionId();
                if (r.Int("pid") is int pid)
                {
                    ProcessControl.Kill(pid, session);
                    return JsonLine.Ok(new JsonObject { ["killed"] = pid });
                }
                string name = r.Str("name") ?? throw new ArgumentException("ps.kill needs 'pid' or 'name'.");
                int count = ProcessControl.KillByName(name, session);
                return JsonLine.Ok(new JsonObject { ["killed"] = count, ["name"] = name });
            }

            case "gamepad.attach":
                try { return JsonLine.Ok(Gamepads.Attach(r.Int("slot") ?? 0)); }
                catch (PadIsolationException ex)
                {
                    var refused = JsonLine.Fail(ex.Message);
                    refused["errorCode"] = "not_isolated";
                    return refused;
                }

            case "gamepad.detach":
                return JsonLine.Ok(Gamepads.Detach(r.Int("slot") ?? 0));

            case "gamepad.reset":
                return JsonLine.Ok(Gamepads.Reset(r.Int("slot") ?? 0));

            case "gamepad.set":
                return JsonLine.Ok(Gamepads.Apply(r.Int("slot") ?? 0, r));

            case "gamepad.tap":
                return JsonLine.Ok(Gamepads.Tap(
                    r.Int("slot") ?? 0,
                    r.Str("button") ?? throw new ArgumentException("gamepad.tap needs 'button'."),
                    r.Int("ms") ?? 80));

            case "gamepad.state":
                return JsonLine.Ok(new JsonObject { ["slots"] = Gamepads.Slots(), ["isolation"] = _padIsolation?.Describe() });

            case "shutdown":
                Log.Info("seat host asked to stop");
                Task.Run(async () => { await Task.Delay(200); Stopping.Set(); });
                return JsonLine.Ok();

            default:
                return JsonLine.Fail($"Unknown seat operation '{op}'.");
        }
    }

    internal static JsonObject RunProgram(JsonObject request, uint session,
        Func<ProcessStartInfo, Process?>? launch = null, Func<int[]>? steamSessions = null,
        Func<string, int[]>? browserSessions = null, Func<string?>? defaultBrowser = null)
    {
        string path = request.Str("path") ?? throw new ArgumentException("run needs 'path'.");
        if (Core.Steam.Steam.DirectLaunchFailure(path, session, steamSessions) is { } error)
            return JsonLine.Fail(error);
        // Programs that hand a second start on the same profile to an instance elsewhere, or cannot share it.
        if (SeatStudio.DirectLaunchFailure(path) is { } studio) return JsonLine.Fail(studio);
        var passed = (request["args"] as JsonArray)?.Select(argument => argument?.ToString() ?? "").ToArray() ?? Array.Empty<string>();
        if (SeatBrowser.DirectLaunchFailure(path, passed, session, browserSessions ?? SeatBrowser.Sessions, defaultBrowser ?? SeatBrowser.DefaultBrowser) is { } browser)
            return JsonLine.Fail(browser);

        var info = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            WorkingDirectory = request.Str("cwd") ?? SafeWorkingDirectory(path)
        };
        if (request["args"] is JsonArray args)
            foreach (var argument in args)
                if (argument is not null) info.ArgumentList.Add(argument.ToString());

        using var started = (launch ?? Process.Start)(info);
        Log.Info($"seat launched: {path}");
        return JsonLine.Ok(new JsonObject { ["pid"] = started?.Id, ["path"] = path, ["session"] = session, ["agentId"] = request.Str("agentId") });
    }

    private static string SafeWorkingDirectory(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return Directory.Exists(directory) ? directory! : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            return System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        }
    }
}
