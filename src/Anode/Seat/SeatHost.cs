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
    private static readonly GamepadManager Gamepads = new();
    private static readonly ManualResetEventSlim Stopping = new(false);
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly DesktopTools Desktop = new();
    private static readonly SemaphoreSlim InteractionGate = new(1, 1);
    private static readonly CancellationTokenSource DesktopStopping = new();

    public static int Run()
    {
        Log.SetRole("seat");
        uint session = ChildSession.CurrentSessionId();
        Log.Info($"seat host starting in session {session} (pid {System.Environment.ProcessId})");

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

        using var server = new JsonPipeServer(Env.SeatPipe, HandleAsync);
        server.Start();
        Log.Info($"seat host listening on \\\\.\\pipe\\{Env.SeatPipe}");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Gamepads.Dispose();
        Stopping.Wait();

        Gamepads.Dispose();
        Log.Info("seat host stopped");
        return 0;
    }

    internal static void VerifyCurrentSession()
    {
        uint session = ChildSession.CurrentSessionId();
        using var daemon = JsonPipeClient.TryConnectAsync(Env.ControlPipe, 5000).GetAwaiter().GetResult();
        var identity = daemon?.RequestAsync("seat.identity", timeoutMs: 5000).GetAwaiter().GetResult();
        if (!MatchesSeat(session, identity))
            throw new InvalidOperationException($"Refusing desktop access: the parent daemon did not verify session {session} as its child.");
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

    private static async Task<JsonObject> HandleAsync(JsonObject request)
    {
        string op = request.Str("op") ?? string.Empty;
        if (op == "shutdown") DesktopStopping.Cancel();
        bool desktop = DesktopTools.ToolName(op) is not null;
        bool changesUi = op.StartsWith("input.", StringComparison.Ordinal) || op is "run" or "steam.launch" or "ps.kill";
        if (!desktop && !changesUi) return Dispatch(op, request);
        await InteractionGate.WaitAsync(DesktopStopping.Token).ConfigureAwait(false);
        try
        {
            if (changesUi) Desktop.InvalidateObservations();
            return desktop ? await Desktop.HandleAsync(op, request, DesktopStopping.Token).ConfigureAwait(false) : Dispatch(op, request);
        }
        finally { InteractionGate.Release(); }
    }

    private static JsonObject Dispatch(string op, JsonObject r)
    {
        switch (op)
        {
            case "ping":
            {
                var size = InputInjector.ScreenSize();
                var cursor = InputInjector.CursorPosition();
                return JsonLine.Ok(new JsonObject
                {
                    ["session"] = ChildSession.CurrentSessionId(),
                    ["pid"] = System.Environment.ProcessId,
                    ["user"] = System.Environment.UserName,
                    ["uptimeSeconds"] = Math.Round(Uptime.Elapsed.TotalSeconds, 1),
                    ["screen"] = new JsonObject { ["width"] = size.Width, ["height"] = size.Height },
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

            case "input.text":
                InputInjector.TypeText(
                    r.Str("text") ?? throw new ArgumentException("text needs 'text'."),
                    r.Int("perCharMs") ?? 0);
                return JsonLine.Ok();

            case "run":
                return RunProgram(r, ChildSession.CurrentSessionId());

            case "steam.status":
                return JsonLine.Ok(new JsonObject
                {
                    ["steamExe"] = Core.Steam.Steam.FindExecutable(),
                    ["runningSessions"] = new JsonArray(Core.Steam.Steam.RunningSessions().Select(s => (JsonNode)s).ToArray()),
                    ["summary"] = Core.Steam.Steam.Describe(ChildSession.CurrentSessionId())
                });

            case "steam.launch":
            {
                int appId = r.Int("appId") ?? throw new ArgumentException("steam.launch needs 'appId'.");
                uint session = ChildSession.CurrentSessionId();
                string summary = Core.Steam.Steam.Describe(session);

                int[] elsewhere = Core.Steam.Steam.RunningSessions().Where(s => s != (int)session).ToArray();
                if (elsewhere.Length > 0 && (r.Bool("force") ?? false) == false)
                {
                    return JsonLine.Fail(summary + " Pass force=true to launch anyway.");
                }

                if (Core.Steam.Steam.RunningSessions().Length == 0 && (r.Bool("startClient") ?? true))
                {
                    Core.Steam.Steam.StartClient();
                    Thread.Sleep(r.Int("clientWarmupMs") ?? 4000);
                }

                var arguments = new List<string>();
                if (r["args"] is JsonArray extra)
                {
                    foreach (var argument in extra)
                        if (argument is not null) arguments.Add(argument.ToString());
                }

                using var launcher = Core.Steam.Steam.LaunchApp(appId, arguments);
                Log.Info($"seat launched steam app {appId}");
                return JsonLine.Ok(new JsonObject
                {
                    ["appId"] = appId,
                    ["session"] = session,
                    ["note"] = summary
                });
            }

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
                return JsonLine.Ok(Gamepads.Attach(r.Int("slot") ?? 0));

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
                return JsonLine.Ok(new JsonObject { ["slots"] = Gamepads.Slots() });

            case "shutdown":
                Log.Info("seat host asked to stop");
                Task.Run(async () => { await Task.Delay(200); Stopping.Set(); });
                return JsonLine.Ok();

            default:
                return JsonLine.Fail($"Unknown seat operation '{op}'.");
        }
    }

    internal static JsonObject RunProgram(JsonObject request, uint session,
        Func<ProcessStartInfo, Process?>? launch = null, Func<int[]>? steamSessions = null)
    {
        string path = request.Str("path") ?? throw new ArgumentException("run needs 'path'.");
        if (Core.Steam.Steam.DirectLaunchFailure(path, session, steamSessions) is { } error)
            return JsonLine.Fail(error);

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
        return JsonLine.Ok(new JsonObject { ["pid"] = started?.Id, ["path"] = path, ["session"] = session });
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
