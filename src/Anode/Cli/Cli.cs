using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Core.Session;
using Anode.Core.Util;
using Anode.Daemon;

namespace Anode.Cli;

/// <summary>Command-line front end. Every command except `up`, `setup` and the internal
/// roles is a thin client of the running daemon.</summary>
internal static partial class Cli
{
    public static int Run(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();

        // Resolve before any role logs, including scheduled and elevated launches.
        if (command is "up" or "start" or "rendering" or "__seat-host" or "__apply-setup" or "__log-probe")
        {
            try
            {
                if (new Args(rest).Value("state-dir") is { } directory) Env.SetStateDirectory(directory);
                if (command != "__log-probe") Env.ResolveStateDirectory();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        }

        // Internal roles run before anything touches the console.
        switch (command)
        {
            case "__seat-host":
                return Seat.SeatHost.Run();
            case "__desktop-worker":
                return Core.Desktop.DesktopWorker.Run();
            case "__exec-worker":
                return Core.Processes.ExecutionWorker.Run();
            case "__desktop-fixture":
                return DesktopFixture.Run();
            case "__log-probe":
                return DiagnosticsChecks.LogProbe(rest);
            case "__apply-setup":
                return ApplySetupElevated(rest);
            case "mcp":
                return Mcp.McpServer.Run().GetAwaiter().GetResult();
        }

        ConsoleBridge.Attach();
        Log.SetRole("cli");

        try
        {
            return command switch
            {
                "help" or "--help" or "-h" or "/?" => Help(rest.FirstOrDefault()),
                "version" or "--version" or "-v" => Version(),
                "doctor" => Doctor(),
                "configure" => Configure(rest),
                "rendering" => Rendering(rest),
                "selftest" or "self-test" => SelfTest.Run(rest),
                "setup" => Setup(rest),
                "up" => Up(rest),
                "start" => StartDetached(rest).GetAwaiter().GetResult(),
                "status" => Status(rest).GetAwaiter().GetResult(),
                "kill" or "stop" => Simple("seat.stop").GetAwaiter().GetResult(),
                "quit" => Simple("quit").GetAwaiter().GetResult(),
                "show" => Simple("seat.show").GetAwaiter().GetResult(),
                "hide" => Simple("seat.hide").GetAwaiter().GetResult(),
                "control" => Control(rest).GetAwaiter().GetResult(),
                "run" => RunProgram(rest).GetAwaiter().GetResult(),
                "steam" => SteamCommand(rest).GetAwaiter().GetResult(),
                "shot" or "screenshot" => Screenshot(rest).GetAwaiter().GetResult(),
                "click" => Click(rest).GetAwaiter().GetResult(),
                "move" => Move(rest).GetAwaiter().GetResult(),
                "scroll" => Scroll(rest).GetAwaiter().GetResult(),
                "key" => Key(rest).GetAwaiter().GetResult(),
                "type" => TypeText(rest).GetAwaiter().GetResult(),
                "ps" => Processes(rest).GetAwaiter().GetResult(),
                "gamepad" or "pad" => Gamepad(rest).GetAwaiter().GetResult(),
                "windows" or "inspect" or "window" or "element" => DesktopCommand(command, rest).GetAwaiter().GetResult(),
                "capabilities" or "exec" or "job" or "jobs" or "wait" => DevelopmentCommand(command, rest).GetAwaiter().GetResult(),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"anode: {ex.Message}");
            Log.Error($"command '{command}' failed", ex);
            return 1;
        }
    }

    // ------------------------------------------------------------------ commands

    private static int Rendering(string[] args)
    {
        if (new Args(args).Flag("restore")) Console.WriteLine(BackgroundRendering.Restore());
        else Console.WriteLine("Per-user RDP background rendering: " + (BackgroundRendering.Current() == 2 ? "configured" : "not configured")
            + ". The daemon configures this before creating its viewer; an already running daemon must be restarted.");
        return 0;
    }

    private static async Task<int> DesktopCommand(string command, string[] args)
    {
        var options = new Args(args);
        string? At(int index) => args.Length > index && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index] : null;
        int? Integer(string name)
        {
            string? value = options.Value(name);
            if (value is null && !options.Flag(name)) return null;
            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                throw new ArgumentException($"--{name} requires an integer.");
            return parsed;
        }
        var payload = new JsonObject();
        string tool;
        switch (command)
        {
            case "windows":
                tool = "seat_windows";
                if (options.Value("query") is { } query) payload["query"] = query;
                if (Integer("pid") is int pid) payload["pid"] = pid;
                break;
            case "inspect":
                tool = "seat_observe";
                if (At(0) is { } window) payload["windowId"] = window;
                payload["includeScreenshot"] = options.Value("html") is not null || options.Value("image") is not null;
                if (Integer("max-elements") is int count) payload["maxElements"] = count;
                if (Integer("max-depth") is int depth) payload["maxDepth"] = depth;
                if (Integer("max-text") is int text) payload["maxTextChars"] = text;
                if (options.Flag("offscreen")) payload["includeOffscreen"] = true;
                break;
            case "window":
                tool = "seat_window";
                if (At(0) is { } id) payload["windowId"] = id;
                if (At(1) is { } action) payload["action"] = action;
                foreach (string field in new[] { "x", "y", "width", "height" })
                    if (Integer(field) is int value) payload[field] = value;
                break;
            default:
                tool = "seat_element";
                if (At(0) is { } snapshot) payload["snapshotId"] = snapshot;
                if (At(1) is { } element) payload["elementId"] = element;
                if (At(2) is { } operation) payload["action"] = operation;
                foreach (string field in new[] { "value", "direction", "amount" })
                    if (options.Value(field) is { } value) payload[field] = value;
                if (options.Value("number") is { } number)
                {
                    if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
                        throw new ArgumentException("--number must be a finite number using a decimal point.");
                    payload["number"] = parsed;
                }
                break;
        }
        if (Mcp.Tools.ValidateArguments(tool, payload) is { } error) { Console.Error.WriteLine(error); return 2; }
        Mcp.Tools.TryResolve(tool, out string op, out _);
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }
        var response = await client.RequestAsync(op, payload, 20_000);
        if (response.Bool("ok") != true) return Report(response);
        var result = response.Obj("result")!;
        if (options.Value("html") is { } html)
        {
            File.WriteAllText(html, Core.Desktop.DesktopPresentation.Html(result));
            Console.Error.WriteLine("Report: " + Path.GetFullPath(html));
        }
        if (options.Value("image") is { } image && result.Obj("screenshot")?.Str("data") is { } data)
        {
            File.WriteAllBytes(image, Convert.FromBase64String(data));
            Console.Error.WriteLine("Screenshot: " + Path.GetFullPath(image));
        }
        if (options.Flag("json")) Console.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine(result.Str("summary") ?? Core.Desktop.DesktopPresentation.Summary(result));
        return 0;
    }

    private static int Version()
    {
        var version = typeof(Cli).Assembly.GetName().Version;
        Console.WriteLine($"anode {version?.ToString(3) ?? "0.1.0"}");
        return 0;
    }

    private static int Doctor()
    {
        var checks = Preconditions.Run();
        Console.WriteLine($"Anode readiness on {Preconditions.ProductName()}\n");

        foreach (var check in checks)
        {
            string mark = check.State switch
            {
                CheckLevel.Pass => "ok  ",
                CheckLevel.Warn => "warn",
                _ => "FAIL"
            };
            Console.WriteLine($"  [{mark}] {check.Name,-20} {check.Detail}");
            if (check.Fix is not null && check.State != CheckLevel.Pass)
                Console.WriteLine($"           -> {check.Fix}");
        }

        bool failed = Preconditions.AnyFailed(checks);
        Console.WriteLine();
        Console.WriteLine(failed
            ? "Not ready. Fix the FAIL lines above, then run `anode doctor` again."
            : "Prerequisites passed. Run `anode up` to test seat startup and sign-in.");
        return failed ? 1 : 0;
    }

    private static int Setup(string[] args)
    {
        var options = new Args(args);
        bool undo = options.Flag("undo");
        bool fps = options.Value("fps") == "60" || options.Flag("fps60");
        bool gpu = options.Flag("gpu");

        if (!Preconditions.IsElevated())
        {
            Console.WriteLine("Anode needs one administrator approval to:");
            if (undo)
            {
                Console.WriteLine("  - turn child sessions back off");
            }
            else
            {
                Console.WriteLine("  - enable the Remote Desktop host (fDenyTSConnections = 0; no firewall rule added)");
                Console.WriteLine("  - start or restart TermService if its listener is missing (may disconnect Remote Desktop sessions)");
                Console.WriteLine("  - turn on child sessions (WTSEnableChildSessions)");
                if (fps) Console.WriteLine("  - raise the seat frame cap to 60 fps (DWMFRAMEINTERVAL = 15, needs a reboot)");
                if (gpu) Console.WriteLine("  - let the seat use the hardware graphics adapter (bEnumerateHWBeforeSW = 1, needs a reboot)");
            }
            Console.WriteLine("\nApprove the prompt Windows is about to show.\n");

            string forwarded = "__apply-setup" + (undo ? " --undo" : string.Empty)
                + (fps ? " --fps 60" : string.Empty) + (gpu ? " --gpu" : string.Empty)
                + " --state-dir " + DaemonLauncher.Quote(Env.StateDirectory);
            int code = Core.Session.Setup.RelaunchElevated(forwarded);
            if (code == 1223) return code;
            Console.WriteLine();
            int readiness = undo ? 0 : Doctor();
            return code != 0 ? code : readiness;
        }

        return ApplySetupElevated(args);
    }

    private static int ApplySetupElevated(string[] args)
    {
        ConsoleBridge.Attach();
        Log.SetRole("setup");
        var options = new Args(args);
        var result = Core.Session.Setup.Apply(
            setFrameRate: options.Value("fps") == "60" || options.Flag("fps60"),
            useGpu: options.Flag("gpu"),
            undo: options.Flag("undo"));

        foreach (string line in result.Done) Console.WriteLine($"  done    {line}");
        foreach (string line in result.Skipped) Console.WriteLine($"  skipped {line}");
        foreach (string line in result.Failed) Console.WriteLine($"  FAILED  {line}");

        if (result.RebootSuggested)
            Console.WriteLine("\nA restart is recommended so Windows picks up the Remote Desktop and frame-rate settings.");

        return result.Failed.Count == 0 ? 0 : 1;
    }

    private static int Up(string[] args)
    {
        var options = new Args(args);
        ValidateSignInOptions(options);
        var seat = new SeatOptions
        {
            Width = options.Int("width") ?? 1280,
            Height = options.Int("height") ?? 720,
            Audio = options.Flag("audio"),
            ShareClipboard = options.Flag("clipboard"),
            CaptureWindowsKeys = options.Flag("winkeys"),
            SmartSizing = !options.Flag("no-scaling"),
            ScaleFactor = options.Int("scale"),
            StartViewOnly = !options.Flag("control"),
            ShowWindow = !options.Flag("hidden"),
            KeepSeatOnExit = options.Flag("keep"),
            PromptForCredentials = options.Flag("sign-in"),
            ConfigureBackgroundRendering = !options.Flag("no-background-rendering")
        };
        return AnodeDaemon.Run(seat);
    }

    private static async Task<int> StartDetached(string[] args)
    {
        var options = new Args(args);
        ValidateSignInOptions(options);
        if (await Connect(autoStart: false) is { } already)
        {
            using (already)
            {
                if (options.Flag("sign-in"))
                {
                    Console.Error.WriteLine("Anode is already running. To change its sign-in mode, run `anode quit`, then `anode start --sign-in`.");
                    return 2;
                }
                return Report(await already.RequestAsync("seat.start", timeoutMs: 180_000));
            }
        }

        if (Preconditions.BlockingSummary() is { } blocked)
        {
            Console.Error.WriteLine(blocked);
            Console.Error.WriteLine("Run `anode doctor` for the full picture.");
            return 3;
        }

        DaemonLauncher.Launch(args);
        if (options.Flag("sign-in"))
            Console.WriteLine("Complete the Windows credential dialog in Anode. Your current desktop stays signed in.");
        var client = await WaitForDaemon(TimeSpan.FromSeconds(30));
        if (client is null)
        {
            Console.Error.WriteLine("Anode did not start. Run `anode up` to see the error directly.");
            return 1;
        }

        using (client)
        {
            Console.WriteLine("Anode started. Waiting for the seat...");
            var status = await SeatStartup.WaitAsync(client);
            Console.WriteLine($"Seat ready in session {status.Int("session")}.");
            return 0;
        }
    }

    private static void ValidateSignInOptions(Args options)
    {
        if (options.Flag("sign-in") && options.Flag("hidden"))
            throw new ArgumentException("--sign-in requires a visible viewer; remove --hidden.");
    }

    private static async Task<int> Status(string[] args)
    {
        using var client = await Connect(autoStart: false);
        if (client is null)
        {
            Console.WriteLine("Anode is not running. Start it with `anode start`.");
            return 1;
        }

        var response = await client.RequestAsync("status");
        var result = response.Obj("result");
        if (result is null) return Report(response);

        if (new Args(args).Flag("json"))
        {
            Console.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"state         {result.Str("state")}");
        Console.WriteLine($"seat session  {result.Int("session")?.ToString() ?? "none"}");
        Console.WriteLine($"agent         {(result.Bool("agentReady") == true ? "ready" : "not ready")}");
        Console.WriteLine($"viewer        {(result.Int("viewerConnection") == 1 ? "connected" : "disconnected")}, {(result.Bool("viewerVisible") == true ? "visible" : "hidden")}, {(result.Bool("viewOnly") == true ? "view only" : "you have control")}");

        if (result.Obj("seat") is { } seat)
        {
            var screen = seat.Obj("screen");
            Console.WriteLine($"seat screen   {screen?.Int("width")}x{screen?.Int("height")}");
        }
        if (result.Obj("steam") is { } steam)
            Console.WriteLine($"steam         {steam.Str("summary")}");
        if (result.Str("lastError") is { Length: > 0 } error)
            Console.WriteLine($"last error    {error}");
        if (result.Str("logError") is { Length: > 0 } logError)
            Console.WriteLine($"log error     {logError}");

        Console.WriteLine($"log           {result.Str("logPath")}");
        return 0;
    }

    private static async Task<int> Simple(string op)
    {
        using var client = await Connect(autoStart: false);
        if (client is null)
        {
            Console.WriteLine("Anode is not running, so there is nothing to do.");
            return 0;
        }
        return Report(await client.RequestAsync(op));
    }

    private static async Task<int> Control(string[] args)
    {
        bool viewOnly = !(args.FirstOrDefault()?.Equals("take", StringComparison.OrdinalIgnoreCase) ?? false);
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }
        return Report(await client.RequestAsync("seat.control", new JsonObject { ["viewOnly"] = viewOnly }));
    }

    private static async Task<int> RunProgram(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: anode run <program> [arguments...]"); return 2; }

        var payload = new JsonObject { ["path"] = args[0] };
        if (args.Length > 1) payload["args"] = new JsonArray(args.Skip(1).Select(a => (JsonNode)a!).ToArray());

        using var client = await Connect(autoStart: true);
        if (client is null) return 1;
        return Report(await client.RequestAsync("run", payload));
    }

    private static async Task<int> SteamCommand(string[] args)
    {
        using var client = await Connect(autoStart: true);
        if (client is null) return 1;

        if (args.Length == 0 || args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
            return Report(await client.RequestAsync("steam.status"));

        if (!int.TryParse(args[0], out int appId))
        {
            Console.Error.WriteLine("usage: anode steam <appid> [--force]   (find the app id on the game's Steam store URL)");
            return 2;
        }

        var options = new Args(args.Skip(1).ToArray());
        var payload = new JsonObject { ["appId"] = appId, ["force"] = options.Flag("force") };
        return Report(await client.RequestAsync("steam.launch", payload, 120_000));
    }

    private static async Task<int> Screenshot(string[] args)
    {
        var options = new Args(args);
        string? target = args.FirstOrDefault(a => !a.StartsWith('-'));
        target ??= Path.Combine(Directory.GetCurrentDirectory(),
            $"anode-{DateTime.Now:yyyyMMdd-HHmmss}.{(options.Flag("jpeg") ? "jpg" : "png")}");

        var payload = new JsonObject { ["format"] = options.Flag("jpeg") ? "jpeg" : "png" };
        if (options.Int("width") is int width) payload["maxWidth"] = width;

        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }

        var response = await client.RequestAsync("screenshot", payload, 30_000);
        var result = response.Obj("result");
        if (response.Bool("ok") != true || result?.Str("data") is not { } base64) return Report(response);

        File.WriteAllBytes(target, Convert.FromBase64String(base64));
        Console.WriteLine($"{target}  ({result.Int("width")}x{result.Int("height")} of {result.Int("sourceWidth")}x{result.Int("sourceHeight")})");
        return 0;
    }

    private static async Task<int> Click(string[] args)
    {
        var options = new Args(args);
        var numbers = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        var payload = new JsonObject
        {
            ["button"] = options.Flag("right") ? "right" : options.Flag("middle") ? "middle" : "left",
            ["count"] = options.Flag("double") ? 2 : 1
        };
        if (numbers.Length >= 2) { payload["x"] = numbers[0]; payload["y"] = numbers[1]; }

        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }
        return Report(await client.RequestAsync("input.click", payload));
    }

    private static async Task<int> Move(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y))
        {
            Console.Error.WriteLine("usage: anode move <x> <y>");
            return 2;
        }
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }
        return Report(await client.RequestAsync("input.move", new JsonObject { ["x"] = x, ["y"] = y }));
    }

    private static async Task<int> Scroll(string[] args)
    {
        int amount = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : -3;
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }
        return Report(await client.RequestAsync("input.scroll", new JsonObject { ["amount"] = amount }));
    }

    private static async Task<int> Key(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: anode key <chord>    e.g. anode key ctrl+shift+esc"); return 2; }
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }
        return Report(await client.RequestAsync("input.key", new JsonObject { ["keys"] = args[0] }));
    }

    private static async Task<int> TypeText(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: anode type <text>"); return 2; }
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }
        return Report(await client.RequestAsync("input.text", new JsonObject { ["text"] = string.Join(' ', args) }));
    }

    private static async Task<int> Processes(string[] args)
    {
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }

        if (args.Length >= 2 && args[0].Equals("kill", StringComparison.OrdinalIgnoreCase))
        {
            var payload = new JsonObject();
            if (int.TryParse(args[1], out int pid)) payload["pid"] = pid; else payload["name"] = args[1];
            return Report(await client.RequestAsync("ps.kill", payload));
        }

        var options = new Args(args);
        var response = await client.RequestAsync("ps.list", new JsonObject { ["windowedOnly"] = !options.Flag("all") });
        if (response.Obj("result")?["processes"] is not JsonArray processes) return Report(response);

        Console.WriteLine($"{"pid",-8} {"name",-28} title");
        foreach (var entry in processes.OfType<JsonObject>())
            Console.WriteLine($"{entry.Int("pid"),-8} {Truncate(entry.Str("name") ?? "", 28),-28} {Truncate(entry.Str("title") ?? "", 60)}");
        return 0;
    }

    private static async Task<int> Gamepad(string[] args)
    {
        using var client = await Connect(autoStart: false);
        if (client is null) { Console.Error.WriteLine("Anode is not running."); return 1; }

        string sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "state";
        var options = new Args(args);
        var payload = new JsonObject { ["slot"] = options.Int("slot") ?? 0 };

        switch (sub)
        {
            case "attach": return Report(await client.RequestAsync("gamepad.attach", payload));
            case "detach": return Report(await client.RequestAsync("gamepad.detach", payload));
            case "reset": return Report(await client.RequestAsync("gamepad.reset", payload));
            case "state": return Report(await client.RequestAsync("gamepad.state", payload));
            case "tap":
                if (args.Length < 2) { Console.Error.WriteLine("usage: anode gamepad tap <button> [--ms 80]"); return 2; }
                payload["button"] = args[1];
                payload["ms"] = options.Int("ms") ?? 80;
                return Report(await client.RequestAsync("gamepad.tap", payload));
            case "stick":
            {
                if (args.Length < 4) { Console.Error.WriteLine("usage: anode gamepad stick <left|right> <x> <y>"); return 2; }
                string prefix = args[1].StartsWith('r') ? "r" : "l";
                payload["axes"] = new JsonObject
                {
                    [prefix + "x"] = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                    [prefix + "y"] = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
                };
                return Report(await client.RequestAsync("gamepad.set", payload));
            }
            case "hold":
            case "release":
            {
                if (args.Length < 2) { Console.Error.WriteLine($"usage: anode gamepad {sub} <button>"); return 2; }
                payload["buttons"] = new JsonObject { [args[1]] = sub == "hold" };
                return Report(await client.RequestAsync("gamepad.set", payload));
            }
            default:
                Console.Error.WriteLine("usage: anode gamepad <attach|detach|tap|hold|release|stick|reset|state>");
                return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"anode: unknown command '{command}'. Try `anode help`.");
        return 2;
    }

    // ------------------------------------------------------------------- plumbing

    private static async Task<JsonPipeClient?> Connect(bool autoStart)
    {
        var client = await JsonPipeClient.TryConnectAsync(Env.ControlPipe, 800);
        if (client is not null || !autoStart) return client;

        if (Preconditions.BlockingSummary() is { } blocked)
        {
            Console.Error.WriteLine(blocked);
            return null;
        }

        Console.Error.WriteLine("Anode is not running; starting it...");
        DaemonLauncher.Launch(new[] { "--hidden" });
        client = await WaitForDaemon(TimeSpan.FromSeconds(30));
        if (client is null)
        {
            Console.Error.WriteLine("Could not start Anode. Run `anode up` to see why.");
            return null;
        }

        // Give the seat a chance to finish signing in before the caller's request.
        try { await SeatStartup.WaitAsync(client); }
        catch { client.Dispose(); throw; }
        return client;
    }

    private static async Task<JsonPipeClient?> WaitForDaemon(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var client = await JsonPipeClient.TryConnectAsync(Env.ControlPipe, 500);
            if (client is not null) return client;
            await Task.Delay(300);
        }
        return null;
    }

    private static int Report(JsonObject response)
    {
        if (response.Bool("ok") == true)
        {
            var result = response.Obj("result");
            Console.WriteLine(result is null || result.Count == 0
                ? "ok"
                : result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.Error.WriteLine(response.Str("error") ?? "the request failed");
        return 1;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..Math.Max(0, length - 1)] + "…";

    private static int Help(string? topic)
    {
        Console.WriteLine(
"""
anode - a virtual seat for agents on Windows

A seat is a real second Windows session on this machine: its own desktop, its own
mouse pointer, its own keyboard focus, its own running programs. It signs in as you,
so it can see your files, your installed software and your Steam library. Nothing it
does moves your pointer or steals your focus.

  Set up (once)
    anode doctor                 check whether this machine can host a seat
    anode selftest --quick       verify local behavior without desktop input or capture
    anode setup [--fps 60] [--gpu]
                                 enable Remote Desktop + child sessions (one UAC prompt)

  Run a seat
    anode up [options]           bring a seat up and keep the viewer in the foreground
    anode start                  same, but detached; returns when the seat is ready
    anode status [--json]        what the seat is doing right now
    anode rendering [--restore] check or restore the per-user RDP rendering preference
    anode show | hide            show or hide the viewer window
    anode control [take|give]    let your own mouse and keyboard reach the seat
    anode kill                   sign the seat out and close everything in it
    anode quit                   stop the seat and exit Anode

  Work in the seat
    anode run <program> [args]   start a program inside the seat
    anode steam <appid>          start a Steam game inside the seat
    anode ps [--all]             list programs running in the seat
    anode ps kill <pid|name>     close one program in the seat
    anode shot [file] [--width N] [--jpeg]
    anode windows [--query text] [--pid N] [--json]
    anode inspect <windowId> [--html report.html] [--image screen.png] [--json]
    anode window <windowId> <focus|raise|restore|maximize|minimize|close|move>
    anode element <snapshotId> <elementId> <action> [--value text]

  Drive the seat
    anode click [x y] [--right] [--double]
    anode move <x> <y>
    anode scroll <notches>
    anode key <chord>            e.g. anode key ctrl+shift+esc
    anode type <text>

  Virtual controller (needs the ViGEm bus driver)
    anode gamepad attach [--slot 0]
    anode gamepad tap <button> [--ms 80]
    anode gamepad hold <button> | release <button>
    anode gamepad stick <left|right> <x> <y>     x and y from -1 to 1
    anode gamepad detach

  For agents
    anode configure [auto|codex|claude|both]
                                 register installed CLIs with config backups
    anode mcp                    speak the Model Context Protocol on stdin/stdout

  Develop and test inside the seat
    anode capabilities [--json] [--no-capture]
    anode exec --cwd C:\\project --timeout 120000 --wait 1000 -- dotnet build
    anode job <jobId> [--after cursor] [--wait 1000] [--cancel] [--json]
    anode jobs [--json]          recover job IDs after a disconnected client
    anode wait <windowId> --automation-id SaveButton --state enabled [--wait 10000]
    exec accepts repeated --env NAME=VALUE; -- separates literal executable arguments

  Options for `up` and `start`
    --width N --height N   seat resolution (default 1280x720)
    --scale N              DPI scale percentage for the seat
    --audio                play the seat's sound on this computer (off by default)
    --clipboard            share your clipboard with the seat (off by default)
    --winkeys              send Windows-key shortcuts to the seat, not to you
    --control              start with your input reaching the seat
    --hidden               do not show the viewer window
    --no-background-rendering  leave the per-user RDP rendering preference unchanged
    --sign-in              ask Windows for seat credentials without signing you out
    --state-dir PATH       absolute directory for logs when starting a new daemon
    --keep                 leave the seat running when Anode exits

  Stopping a seat that has frozen
    Press Ctrl+Alt+Shift+K anywhere, use the tray icon, or run `anode kill`.
    All three sign the child session out, which force-closes everything in it.
""");
        return 0;
    }
}

/// <summary>Minimal flag and option reader. Anode's surface is small enough not to need a parser library.</summary>
internal sealed class Args
{
    private readonly string[] _args;
    public Args(string[] args) => _args = args;

    public bool Flag(string name) =>
        _args.Any(a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)
                    || a.Equals("-" + name, StringComparison.OrdinalIgnoreCase));

    public string? Value(string name)
    {
        for (int i = 0; i < _args.Length; i++)
        {
            if (!_args[i].Equals("--" + name, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < _args.Length ? _args[i + 1] : null;
        }
        foreach (string argument in _args)
        {
            string prefix = "--" + name + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return argument[prefix.Length..];
        }
        return null;
    }

    public int? Int(string name) => int.TryParse(Value(name), out int value) ? value : null;
}

/// <summary>
/// Anode is a GUI executable so that bringing a seat up does not flash a console
/// window. That means the CLI has to borrow the console of whatever launched it.
/// </summary>
internal static class ConsoleBridge
{
    private static bool _attached;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;

        if (Console.IsOutputRedirected && Console.IsErrorRedirected) return;
        if (!AttachConsole(unchecked((uint)-1))) return;

        try
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(output);
            var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(error);
        }
        catch
        {
            // Without a console the CLI is silent, which is survivable.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
