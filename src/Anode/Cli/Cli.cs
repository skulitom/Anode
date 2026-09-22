using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal const string NotRunningText =
        "Anode is not running. Start it with `anode start --hidden` (agents: `anode lease acquire` starts it).";

    public static int Run(string[] args)
    {
        bool bare = args.Length == 0;
        try { args = AgentOptions(args); }
        catch (ArgumentException ex) { ConsoleBridge.Attach(); Console.Error.WriteLine($"anode: {ex.Message}"); return 2; }
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();

        // Help and malformed arguments are answered from the arguments alone, before anything
        // resolves state, connects, launches or elevates: `anode kill --help` must not stop a seat.
        if (!command.StartsWith("__", StringComparison.Ordinal))
        {
            if (!IsHelpCommand(command) && AsksHelp(command, rest))
            {
                ConsoleBridge.Attach();
                // MCP stdout carries only protocol messages, so its help goes to stderr.
                return Help(command, command == "mcp" ? Console.Error : Console.Out);
            }
            try { CheckArguments(command, rest); }
            catch (UsageException ex) { ConsoleBridge.Attach(); return UsageError(command, ex.Message); }
            catch (Exception ex) { ConsoleBridge.Attach(); Console.Error.WriteLine($"anode: {ex.Message}"); return 1; }
        }

        // Resolve before any role logs, including scheduled and elevated launches.
        if (command is "up" or "start" or "rendering" or "__seat-host" or "__apply-setup" or "__log-probe")
        {
            try
            {
                if (new Args(rest).Value("state-dir") is { } directory) Env.SetStateDirectory(directory);
                if (command != "__log-probe") Env.ResolveStateDirectory();
            }
            catch (Exception ex)
            {
                if (!command.StartsWith("__", StringComparison.Ordinal)) ConsoleBridge.Attach();
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
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
            case "__viewer-preview":
                return ViewerPreview.Run(rest);
            case "__log-probe":
                return DiagnosticsChecks.LogProbe(rest);
            case "__cursor-probe":
                return PointerIsolation.Probe(rest);
            case "__pointer-watch":
                return PointerIsolation.Watch(rest);
            case "__apply-setup":
                return ApplySetupElevated(rest);
            case "mcp":
                return Mcp.McpServer.Run(_agentId).GetAwaiter().GetResult();
        }

        ConsoleBridge.Attach();
        Log.SetRole("cli");
        // The Start menu shortcut and Explorer start anode.exe without a console to print help into.
        if (bare && !ConsoleBridge.HasConsole && !ConsoleBridge.TryAttachParent()) return Open();

        try
        {
            return command switch
            {
                "help" or "--help" or "-h" or "/?" or "-?" => Help(rest.FirstOrDefault()),
                "version" or "--version" or "-v" => Version(),
                "doctor" => Doctor(rest),
                "configure" => Configure(rest),
                "guide" => Guide(rest),
                "rendering" => Rendering(rest),
                "selftest" or "self-test" => SelfTest.Run(rest),
                "setup" => Setup(rest),
                "up" => Up(rest),
                "start" => StartDetached(rest).GetAwaiter().GetResult(),
                "status" => Status(rest).GetAwaiter().GetResult(),
                "lease" => LeaseCommand(rest).GetAwaiter().GetResult(),
                "kill" or "stop" => Simple("seat.stop").GetAwaiter().GetResult(),
                "quit" => Quit().GetAwaiter().GetResult(),
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
        catch (UsageException ex)
        {
            return UsageError(command, ex.Message);
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
        var words = Words(command, args);
        string? At(int index) => index < words.Count ? words[index] : null;
        // Integer options were validated before dispatch.
        int? Integer(string name) => options.Value(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : null;
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
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
                        throw new UsageException("--number must be a finite number using a decimal point.");
                    payload["number"] = parsed;
                }
                break;
        }
        if (Mcp.Tools.ValidateArguments(tool, payload) is { } error) throw new UsageException(CliError(error));
        Mcp.Tools.TryResolve(tool, out string op, out _);
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        var response = await RequestAsync(client, op, payload, 20_000);
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
        if (options.Flag("json")) Console.WriteLine(result.ToJsonString(Indented));
        else Console.WriteLine(result.Str("summary") ?? Core.Desktop.DesktopPresentation.Summary(result));
        return 0;
    }

    internal static string VersionText() => typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Exactly one line; installers and scripts match `^anode \d+\.\d+\.\d+$`.</summary>
    internal static string VersionLine() => $"anode {VersionText()}";

    private static int Version()
    {
        Console.WriteLine(VersionLine());
        return 0;
    }

    internal static JsonObject DoctorJson(IReadOnlyList<Check> checks) => new()
    {
        ["ready"] = !Preconditions.AnyFailed(checks),
        ["version"] = VersionText(),
        ["checks"] = new JsonArray(checks.Select(c => (JsonNode)c.ToJson()).ToArray())
    };

    private static int Doctor(string[] args)
    {
        var checks = Preconditions.Run();
        bool failed = Preconditions.AnyFailed(checks);
        if (new Args(args).Flag("json"))
        {
            Console.WriteLine(DoctorJson(checks).ToJsonString(Indented));
            return failed ? 1 : 0;
        }

        Console.WriteLine($"Anode {VersionText()} readiness on {Preconditions.ProductName()}\n");
        foreach (var check in checks)
        {
            string mark = check.State switch
            {
                CheckLevel.Pass => "ok  ",
                CheckLevel.Warn => "warn",
                _ => "FAIL"
            };
            Console.WriteLine($"  [{mark}] {check.Name,-23} {check.Detail}");
            if (check.Fix is not null && check.State != CheckLevel.Pass)
                Console.WriteLine($"           -> {check.Fix}");
        }

        Console.WriteLine();
        Console.WriteLine(failed
            ? $"Not ready. Fix the FAIL lines, then run `anode doctor` again.\nHelp: {Links.Troubleshooting}#the-seat-will-not-come-up"
            : "Ready. Next: `anode configure` to register Codex/Claude Code (once), then `anode start --hidden`.");
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
            int readiness = undo ? 0 : Doctor(Array.Empty<string>());
            return code != 0 ? code : readiness;
        }

        int applied = ApplySetupElevated(args);
        if (!undo) Console.WriteLine("\nNow run `anode doctor` from a normal (not administrator) terminal.");
        return applied;
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
        if (await Connect(autoStart: false) is { } already)
        {
            using (already)
            {
                if (options.Flag("sign-in"))
                {
                    Console.Error.WriteLine("Anode is already running. Open its viewer from the tray icon or with `anode show`, then click Sign in in the header.");
                    return 2;
                }
                return Report(await RequestAsync(already, "seat.start", timeoutMs: 180_000));
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
            Console.Error.WriteLine(DidNotStart());
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

    private static string DidNotStart() =>
        $"Anode did not start. Run `anode up | Out-Host` to see the error, or read {Env.LogPath}.";

    /// <summary>The shape MCP seat_status returns without a daemon, so scripts can parse either.</summary>
    internal static JsonObject StoppedStatus(string? agentId) => new()
    {
        ["state"] = "stopped", ["daemonRunning"] = false, ["agentId"] = agentId,
        ["ownerAgentId"] = null, ["summary"] = NotRunningText
    };

    private static async Task<int> Status(string[] args)
    {
        bool json = new Args(args).Flag("json");
        using var client = await Connect(autoStart: false);
        if (client is null)
        {
            Console.WriteLine(json ? StoppedStatus(_agentId).ToJsonString(Indented) : NotRunningText);
            return 1;
        }

        var response = await RequestAsync(client, "status");
        var result = response.Obj("result");
        if (result is null) return Report(response);

        if (json)
        {
            Console.WriteLine(result.ToJsonString(Indented));
            return 0;
        }

        Console.WriteLine($"state         {result.Str("state")}");
        Console.WriteLine($"seat session  {result.Int("session")?.ToString() ?? "none"}");
        Console.WriteLine($"seat host     {(result.Bool("agentReady") == true ? "ready" : "not ready")}");
        Console.WriteLine($"viewer        {(result.Int("viewerConnection") == 1 ? "connected" : "disconnected")}, {(result.Bool("viewerVisible") == true ? "visible" : "hidden")}, {(result.Bool("viewOnly") == true ? "view only" : "you have control")}");

        if (result.Obj("pointerGuard") is { } guard)
            Console.WriteLine(guard.Bool("installed") == true
                ? $"your pointer  guarded; {guard["suppressed"]} seat pointer move(s) kept off your desktop"
                : "your pointer  NOT guarded: a program in the seat that moves its cursor can move yours. See the log.");

        if (result.Obj("seat") is { } seat)
        {
            var screen = seat.Obj("screen");
            Console.WriteLine($"seat screen   {screen?.Int("width")}x{screen?.Int("height")}");
        }
        if (result.Obj("steam") is { } steam)
            Console.WriteLine($"steam         {steam.Str("summary")}");
        if (result.Obj("lease") is { } desk)
            Console.WriteLine($"desktop       {desk.Str("summary")}");
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
            if (op == "seat.show")
            {
                Console.Error.WriteLine("Anode is not running. `anode start` opens the viewer.");
                return 1;
            }
            Console.WriteLine("Anode is not running, so there is nothing to do.");
            return 0;
        }
        return Report(await RequestAsync(client, op));
    }

    /// <summary>
    /// Returns once Anode has exited, so a following `anode start` reaches a new daemon that
    /// applies its options instead of the one still shutting down.
    /// </summary>
    private static async Task<int> Quit()
    {
        using (var client = await Connect(autoStart: false))
        {
            if (client is null)
            {
                Console.WriteLine("Anode is not running, so there is nothing to do.");
                return 0;
            }
            if (Report(await RequestAsync(client, "quit")) is var code and not 0) return code;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var probe = await JsonPipeClient.TryConnectAsync(Env.ControlPipe, 300);
            if (probe is null) return 0;
            await Task.Delay(300);
        }
        Console.Error.WriteLine("Anode accepted quit but is still running after 60 seconds. Check `anode status`.");
        return 1;
    }

    private static async Task<int> Control(string[] args)
    {
        bool viewOnly = !(args.FirstOrDefault()?.Equals("take", StringComparison.OrdinalIgnoreCase) ?? false);
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        return Report(await RequestAsync(client, "seat.control", new JsonObject { ["viewOnly"] = viewOnly }));
    }

    private static async Task<int> RunProgram(string[] args)
    {
        var payload = new JsonObject { ["path"] = args[0] };
        if (args.Length > 1) payload["args"] = new JsonArray(args.Skip(1).Select(a => (JsonNode)a!).ToArray());

        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        return Report(await RequestAsync(client, "run", payload));
    }

    private static async Task<int> SteamCommand(string[] args)
    {
        var words = Words("steam", args);
        bool status = words.Count == 0 || words[0].Equals("status", StringComparison.OrdinalIgnoreCase);
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();

        if (status) return Report(await RequestAsync(client, "steam.status"));
        var payload = new JsonObject
        {
            ["appId"] = int.Parse(words[0], CultureInfo.InvariantCulture), ["force"] = new Args(args).Flag("force")
        };
        return Report(await RequestAsync(client, "steam.launch", payload, 120_000));
    }

    private static async Task<int> Screenshot(string[] args)
    {
        var options = new Args(args);
        string? target = Words("shot", args).FirstOrDefault();
        target ??= Path.Combine(Directory.GetCurrentDirectory(),
            $"anode-{DateTime.Now:yyyyMMdd-HHmmss}.{(options.Flag("jpeg") ? "jpg" : "png")}");

        var payload = new JsonObject { ["format"] = options.Flag("jpeg") ? "jpeg" : "png" };
        if (options.Int("width") is int width) payload["maxWidth"] = width;

        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();

        var response = await RequestAsync(client, "screenshot", payload, 30_000);
        var result = response.Obj("result");
        if (response.Bool("ok") != true || result?.Str("data") is not { } base64) return Report(response);

        File.WriteAllBytes(target, Convert.FromBase64String(base64));
        Console.WriteLine($"{target}  ({result.Int("width")}x{result.Int("height")} of {result.Int("sourceWidth")}x{result.Int("sourceHeight")})");
        return 0;
    }

    private static async Task<int> Click(string[] args)
    {
        var options = new Args(args);
        var point = Words("click", args);
        var payload = new JsonObject
        {
            ["button"] = options.Flag("right") ? "right" : options.Flag("middle") ? "middle" : "left",
            ["count"] = options.Flag("double") ? 2 : 1
        };
        if (point.Count == 2)
        {
            payload["x"] = int.Parse(point[0], CultureInfo.InvariantCulture);
            payload["y"] = int.Parse(point[1], CultureInfo.InvariantCulture);
        }

        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        return Report(await RequestAsync(client, "input.click", payload));
    }

    private static async Task<int> Move(string[] args)
    {
        var point = Words("move", args);
        var payload = new JsonObject
        {
            ["x"] = int.Parse(point[0], CultureInfo.InvariantCulture), ["y"] = int.Parse(point[1], CultureInfo.InvariantCulture)
        };
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        return Report(await RequestAsync(client, "input.move", payload));
    }

    private static async Task<int> Scroll(string[] args)
    {
        var words = Words("scroll", args);
        int amount = words.Count == 1 ? int.Parse(words[0], CultureInfo.InvariantCulture) : -3;
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        return Report(await RequestAsync(client, "input.scroll", new JsonObject { ["amount"] = amount }));
    }

    private static async Task<int> Key(string[] args)
    {
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        return Report(await RequestAsync(client, "input.key", new JsonObject { ["keys"] = args[0] }));
    }

    private static async Task<int> TypeText(string[] args)
    {
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        return Report(await RequestAsync(client, "input.text", new JsonObject { ["text"] = string.Join(' ', args) }));
    }

    private static async Task<int> Processes(string[] args)
    {
        var words = Words("ps", args);
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();

        if (words.Count == 2)
        {
            var payload = new JsonObject();
            if (int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) payload["pid"] = pid; else payload["name"] = words[1];
            return Report(await RequestAsync(client, "ps.kill", payload));
        }

        var options = new Args(args);
        var response = await RequestAsync(client, "ps.list", new JsonObject { ["windowedOnly"] = !options.Flag("all") });
        if (response.Obj("result")?["processes"] is not JsonArray processes) return Report(response);

        Console.WriteLine($"{"pid",-8} {"name",-28} title");
        foreach (var entry in processes.OfType<JsonObject>())
            Console.WriteLine($"{entry.Int("pid"),-8} {Truncate(entry.Str("name") ?? "", 28),-28} {Truncate(entry.Str("title") ?? "", 60)}");
        return 0;
    }

    private static async Task<int> Gamepad(string[] args)
    {
        // Actions, buttons and numbers were validated before dispatch.
        var words = Words("gamepad", args);
        string action = words.Count > 0 ? words[0].ToLowerInvariant() : "state";
        var options = new Args(args);
        var payload = new JsonObject { ["slot"] = options.Int("slot") ?? 0 };

        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();

        switch (action)
        {
            case "tap":
                payload["button"] = words[1];
                payload["ms"] = options.Int("ms") ?? 80;
                return Report(await RequestAsync(client, "gamepad.tap", payload));
            case "stick":
            {
                string prefix = words[1].Equals("right", StringComparison.OrdinalIgnoreCase) ? "r" : "l";
                payload["axes"] = new JsonObject
                {
                    [prefix + "x"] = double.Parse(words[2], CultureInfo.InvariantCulture),
                    [prefix + "y"] = double.Parse(words[3], CultureInfo.InvariantCulture)
                };
                return Report(await RequestAsync(client, "gamepad.set", payload));
            }
            case "hold":
            case "release":
                payload["buttons"] = new JsonObject { [words[1]] = action == "hold" };
                return Report(await RequestAsync(client, "gamepad.set", payload));
            default:
                return Report(await RequestAsync(client, "gamepad." + action, payload));
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"anode: unknown command '{command}'. {Hint(command, forHelp: false)}");
        return 2;
    }

    // ------------------------------------------------------------------- plumbing

    private static int NotRunning()
    {
        Console.Error.WriteLine(NotRunningText);
        return 1;
    }

    private static int UsageError(string command, string message)
    {
        Console.Error.WriteLine($"anode: {message}");
        if (Usage(command) is { } usage) Console.Error.WriteLine(usage);
        return 2;
    }

    /// <summary>Set when Connect refused to start Anode because machine prerequisites are missing.</summary>
    private static bool _startBlocked;

    private static async Task<JsonPipeClient?> Connect(bool autoStart)
    {
        var client = await JsonPipeClient.TryConnectAsync(Env.ControlPipe, 800);
        if (client is not null || !autoStart) return client;

        if (Preconditions.BlockingSummary() is { } blocked)
        {
            Console.Error.WriteLine(blocked);
            Console.Error.WriteLine("Run `anode doctor` for the full picture.");
            _startBlocked = true;
            return null;
        }

        Console.Error.WriteLine("Anode is not running; starting it...");
        DaemonLauncher.Launch(new[] { "--hidden" });
        client = await WaitForDaemon(TimeSpan.FromSeconds(30));
        if (client is null)
        {
            Console.Error.WriteLine(DidNotStart());
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
                : result.ToJsonString(Indented));
            return 0;
        }

        Console.Error.WriteLine(response.Str("error") ?? "the request failed");
        return 1;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..Math.Max(0, length - 1)] + "…";

    // --------------------------------------------------------- help and arguments

    /// <summary>
    /// One public command. The same row prints the full help, `anode help &lt;command&gt;`,
    /// `&lt;command&gt; --help` and usage errors. When <see cref="Options"/> is set it is also the
    /// command's grammar: the options it reads (a trailing '=' takes a value, '#' an integer)
    /// and at most <see cref="Words"/> positional words. Commands that take free text or parse
    /// their own options leave it null.
    /// </summary>
    private sealed record CommandHelp(string Group, string[] Names, (string Usage, string Text)[] Lines,
        string? Notes, int Words, string[]? Options);

    private const string SeatOptionList =
        "hidden width# height# scale# no-scaling audio clipboard winkeys control sign-in no-background-rendering state-dir= keep";

    private static CommandHelp Row(string group, string names, string? options, int words, string? notes,
        params (string Usage, string Text)[] lines) =>
        new(group, names.Split(' '), lines, notes, words, options?.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static readonly CommandHelp[] Commands =
    {
        Row("Start here", "doctor", "json", 0,
            "Exits 1 while a FAIL line remains. --json prints {ready, version, checks}.",
            ("anode doctor [--json]", "check what this machine needs for a seat; changes nothing")),
        Row("Start here", "setup", "undo fps= fps60 gpu", 0,
            "--fps 60 raises the seat's frame cap to 60 fps and --gpu lets the seat render on the graphics adapter; "
            + "both take effect after a reboot. --undo also signs the seat out and leaves Remote Desktop enabled. "
            + "Run the other commands from a normal (not administrator) terminal. What setup changes: "
            + Links.Repository + "/blob/main/docs/SECURITY.md#what-anode-setup-changes",
            ("anode setup [--fps 60] [--gpu]", "enable Remote Desktop and child sessions (one UAC prompt)"),
            ("anode setup --undo", "turn child sessions back off (one UAC prompt)")),
        Row("Start here", "configure", null, 0,
            "Registers the MCP server and the anode-desktop skill for the installed Codex and Claude Code CLIs, "
            + "with backups; the default is auto. --no-skill registers only the MCP server; customized skills are kept. "
            + "Starts no seat and changes no machine settings. Start a new agent session afterward. Other MCP clients: "
            + Links.Connecting,
            ("anode configure [auto|codex|claude|both] [--no-skill]", "register Codex/Claude Code and the skill; restart the agent")),
        Row("Start here", "start", SeatOptionList, 0,
            "If Anode is already running, start brings its seat up if needed and ignores the options. --sign-in needs a "
            + "visible viewer and a new daemon.",
            ("anode start --hidden", "start the seat in the background; returns when it is ready"),
            ("anode start [options]", "the same, with the viewer open unless --hidden")),
        Row("Start here", "capabilities", null, 0,
            "--no-capture skips the test capture. Needs no lease. A ready report does not prove that an application received input.",
            ("anode capabilities [--json] [--no-capture]", "report seat capture and known input blockers")),

        Row("Diagnose", "selftest self-test", "quick no-gamepad no-scheduler", 0,
            "--no-scheduler skips the desktop capture and scheduled-task checks; --no-gamepad skips the gamepad.",
            ("anode selftest --quick", "check Anode itself; no seat, capture or input"),
            ("anode selftest [--no-gamepad] [--no-scheduler]",
                "the full suite also captures this desktop, runs a scheduled task and attaches a machine-wide virtual gamepad")),
        Row("Diagnose", "status", "json", 0,
            "Never starts anything. Exits 1 when Anode is not running; --json then prints {\"state\": \"stopped\", \"daemonRunning\": false, ...}.",
            ("anode status [--json]", "what the seat is doing right now")),
        Row("Diagnose", "version --version -v", "", 0, null,
            ("anode version", "print the version as one line: anode x.y.z")),

        Row("Run a seat", "up", SeatOptionList, 0,
            "Runs until Anode quits. In PowerShell, append | Out-Host to see its errors.",
            ("anode up [options]", "run Anode in this process; the viewer opens unless --hidden")),
        Row("Run a seat", "show hide", "", 0,
            "Keep the viewer hidden during background automation unless someone wants to watch.",
            ("anode show | hide", "show or hide the viewer window; the seat keeps running")),
        Row("Run a seat", "control", "", 1,
            "Without an argument, control returns the viewer to view only.",
            ("anode control [take|give]", "take: your mouse and keyboard reach the seat; give: view only")),
        Row("Run a seat", "rendering", "restore state-dir=", 0,
            "A running daemon must be restarted to pick up a change.",
            ("anode rendering [--restore]", "check or restore the per-user RDP rendering preference")),
        Row("Run a seat", "kill stop", "", 0,
            "Unsaved work in the seat is lost. Ctrl+Alt+Shift+K and the tray icon do the same.",
            ("anode kill", "sign the seat out and close every program in it, for every agent; ignores leases")),
        Row("Run a seat", "quit", "", 0,
            "Every program in the seat closes, unless Anode was started with --keep. Returns once Anode has exited.",
            ("anode quit", "stop the seat and exit Anode")),

        Row("Desktop leases", "lease", null, 0,
            "--ttl is 10-600 seconds after your last desktop command (default 120); each desktop command extends the lease. "
            + "--wait is 0-300 seconds in a first-come line while another agent has the desktop. --cancel-jobs also cancels your "
            + "running command jobs. Commands that need the lease: run, steam <appid>, shot, windows, inspect, window, element, "
            + "wait, click, move, scroll, key, type, ps kill, exec and gamepad (except state).",
            ("anode --agent ID lease acquire [--ttl 120] [--wait 60]",
                "take exclusive use of the desktop and print its leaseToken, waiting in line if another agent has it; starts a hidden seat if needed"),
            ("anode --agent ID --lease TOKEN lease renew [--ttl 120]", "extend the lease while you are not using the desktop"),
            ("anode --agent ID --lease TOKEN lease release [--cancel-jobs]", "hand the desktop to the next agent in line"),
            ("anode --agent ID lease status", "who holds the desktop and who is waiting; starts nothing"),
            ("", "Set ANODE_AGENT_ID and ANODE_LEASE_TOKEN instead of repeating the prefix options, and ANODE_AGENT_NAME to "
                + "name this agent in status and the viewer. Desktop commands need the lease; job reads and cancellation need "
                + "their agent ID. MCP keeps its own token and takes the lease when the desktop is free; set ANODE_AGENT_ID for "
                + "restart recovery.")),

        Row("Work in the seat", "run", null, 0,
            "Everything after the program goes to it unchanged. Use a full path, a shortcut or anything Windows can open.",
            ("anode run <program> [args]", "start a program inside the seat")),
        Row("Work in the seat", "steam", "force", 1,
            "The app id is the number in the game's Steam store URL. If Steam already runs outside the seat, a launch fails "
            + "unless --force, which can disrupt that client or open the game on your screen.",
            ("anode steam <appid> [--force]", "start a Steam game inside the seat"),
            ("anode steam [status]", "report where Steam is running")),
        Row("Work in the seat", "ps", "all", 2, null,
            ("anode ps [--all]", "list windowed programs in the seat; --all lists every process"),
            ("anode ps kill <pid|name>", "close one program in the seat")),
        Row("Work in the seat", "shot screenshot", "width# jpeg", 1,
            "The default file is anode-<date>-<time>.png (.jpg with --jpeg) in the current folder. Click and move "
            + "coordinates use the seat's full size even when --width shrinks the image.",
            ("anode shot [file] [--width N] [--jpeg]", "save a picture of the seat's screen")),
        Row("Work in the seat", "windows", "query= pid# json", 0, null,
            ("anode windows [--query TEXT] [--pid N] [--json]", "list the seat's windows and their IDs")),
        Row("Work in the seat", "inspect", "html= image= max-elements# max-depth# max-text# offscreen json", 1,
            "It prints a snapshotId and element IDs for `anode element`; they expire after 90 seconds. "
            + "--html writes a report and --image the screenshot.",
            ("anode inspect <windowId> [--html FILE] [--image FILE] [--json]", "read a window's accessible controls without focusing it"),
            ("", "Limits: --max-elements 1-500 (default 200), --max-depth 0-20 (default 8), --max-text 0-20000 characters "
                + "(default 6000). --offscreen adds controls outside the visible area.")),
        Row("Work in the seat", "window", "x# y# width# height# json", 2,
            "raise brings the window forward without keyboard focus. close may leave an unsaved-changes dialog; inspect again.",
            ("anode window <windowId> <focus|raise|restore|maximize|minimize|close>", "act on one seat window"),
            ("anode window <windowId> move --x N --y N --width N --height N", "move and resize it, in seat pixels")),
        Row("Work in the seat", "element", "value= number= direction= amount= json", 3,
            "Use an action the control lists. --number uses a decimal point. --json prints the result as JSON. "
            + "Never replay an action that timed out; inspect again.",
            ("anode element <snapshotId> <elementId> <action> [--value TEXT] [--number N] [--direction D] [--amount A]",
                "act on a control from inspect; this uses up the snapshot"),
            ("", "Actions: invoke, focus, toggle, select, expand, collapse, scroll_into_view, set_value with --value, "
                + "set_range with --number, and scroll with --direction up|down|left|right and optional --amount small|large.")),

        Row("Drive the seat", "click", "right middle double", 2,
            "Coordinates are seat pixels at full size, with 0,0 at the top left.",
            ("anode click [x y] [--right|--middle] [--double]", "click at x y, or where the seat's pointer is")),
        Row("Drive the seat", "move", "", 2, null,
            ("anode move <x> <y>", "move the seat's pointer; yours stays put")),
        Row("Drive the seat", "scroll", "", 1, null,
            ("anode scroll [notches]", "turn the wheel; negative scrolls down (default -3)")),
        Row("Drive the seat", "key", null, 0,
            "Pass one token and join keys with +. Keys go out as scan codes so games see them.",
            ("anode key <chord>", "press a key or chord, e.g. anode key ctrl+shift+esc")),
        Row("Drive the seat", "type", null, 0,
            "Arguments are joined with single spaces; quote text to keep it exact.",
            ("anode type <text>", "type text into whatever has focus in the seat")),

        Row("Virtual controller (needs the ViGEm bus driver)", "gamepad pad", "slot# ms#", 4,
            "Buttons: a, b, x, y, lb, rb, back, start, guide, ls, rs, up, down, left, right; tap also takes lt and rt. "
            + "Every action takes --slot N (0-3, default 0). The controller is machine-wide, so programs on your own "
            + "desktop see it too.",
            ("anode gamepad attach [--slot N]", "plug in a virtual Xbox 360 controller"),
            ("anode gamepad tap <button> [--ms 80]", "press and release a button"),
            ("anode gamepad hold <button> | release <button>", "keep a button down, or let it go"),
            ("anode gamepad stick <left|right> <x> <y>", "x and y from -1 to 1"),
            ("anode gamepad reset | state | detach", "center everything, read the state, or unplug")),

        Row("Develop and test inside the seat", "exec", null, 0,
            "There is no shell: name powershell.exe or cmd.exe to use one. --env repeats, and everything after -- is passed "
            + "unchanged. --cwd must be an existing absolute folder. --timeout is 100-1800000 ms (default 120000) and --wait "
            + "0-10000 ms (default 1000). --json prints the whole result. The exit code is the program's own when it "
            + "completed, 124 when it timed out, 130 when cancelled, and 0 while it is still running (read it with `anode job`).",
            ("anode exec [--cwd DIR] [--timeout MS] [--wait MS] [--env NAME=VALUE]... -- <program> [args]",
                "start a command job in the seat; prints early output and a jobId"),
            ("", @"Example: anode exec --cwd C:\project --timeout 120000 --wait 1000 -- dotnet build")),
        Row("Develop and test inside the seat", "job", null, 0,
            "Pass the printed cursor as --after to skip output you already read. --wait is 0-10000 ms and --max-chars "
            + "1-20000 (default 12000). Exit codes match exec.",
            ("anode job <jobId> [--after CURSOR] [--wait MS] [--max-chars N] [--cancel] [--json]", "read a job's new output, or cancel it")),
        Row("Develop and test inside the seat", "jobs", null, 0, null,
            ("anode jobs [--json]", "list your job IDs, e.g. after a client disconnected")),
        Row("Develop and test inside the seat", "wait", null, 0,
            "It never focuses or clicks. --json prints the match with a fresh observation.",
            ("anode wait <windowId> [--automation-id ID] [--name N] [--role R] [--text T] [--state S] [--wait MS] [--json]",
                "wait for an accessible control"),
            ("", "Selectors combine; give at least one. --state is exists (default), missing, enabled or disabled; missing "
                + "cannot use --text. --wait is 0-30000 ms (default 10000). Exits 3 when unmatched.")),

        Row("For agents", "guide", null, 0,
            "The same guide the MCP server's anode_guide tool returns.",
            ("anode guide [--json]", "when to use Anode and how to choose its tools; needs no setup")),
        Row("For agents", "mcp", null, 0,
            "An MCP client starts it and speaks JSON-RPC on stdin/stdout; it is not interactive. Register it with "
            + "`anode configure`, or see " + Links.Connecting,
            ("anode mcp", "serve the Model Context Protocol on stdin/stdout"))
    };

    private static readonly (string Option, string Text)[] SeatOptionHelp =
    {
        ("--hidden", "keep the viewer closed"),
        ("--width N --height N", "seat resolution (default 1280x720)"),
        ("--scale N", "DPI scale percentage for the seat"),
        ("--no-scaling", "show the seat at its own size instead of fitting it to the viewer"),
        ("--audio", "play the seat's sound on this computer (off by default)"),
        ("--clipboard", "share your clipboard with the seat (off by default)"),
        ("--winkeys", "send Windows-key shortcuts to the seat, not to you"),
        ("--control", "start with your input reaching the seat"),
        ("--sign-in", "ask Windows for seat credentials without signing you out"),
        ("--no-background-rendering", "leave the per-user RDP rendering preference unchanged"),
        ("--state-dir PATH", "absolute directory for logs when starting a new daemon"),
        ("--keep", "leave the seat running when Anode exits")
    };

    private const int HelpWidth = 100, UsageColumn = 29;

    internal static string FullHelp()
    {
        var text = new StringBuilder("anode - a background Windows desktop for AI agents\n\n");
        Wrap(text, "", "Anode runs apps in a Windows child session (the seat) with its own screen, pointer and keyboard focus, "
            + "so an agent can automate native UI, take screenshots and run headed app/browser tests while you keep working. "
            + "The seat signs in as you and shares your files, account and network; it is not a security sandbox.", "");
        foreach (var group in Commands.GroupBy(c => c.Group))
        {
            text.Append("\n  ").Append(group.Key).Append('\n');
            foreach (var command in group) Render(text, command, "    ");
        }
        text.Append("\n  Options for up and start (used only when a new daemon starts)\n");
        RenderSeatOptions(text, "    ");
        text.Append("\n  MCP tools and commands\n");
        Wrap(text, "    ", "Most tools match a command: seat_exec is exec and gamepad_tap is gamepad tap. The others: "
            + "seat_observe = inspect, seat_screenshot = shot, seat_processes = ps, seat_kill_process = ps kill, "
            + "seat_stop = kill, steam_launch = steam, steam_status = steam status, seat_job = job and jobs, "
            + "gamepad_set = gamepad hold|release|stick, anode_guide = guide. seat_drag is MCP-only.", "    ");
        text.Append("\n  Stopping a seat that has frozen\n");
        Wrap(text, "    ", "Press Ctrl+Alt+Shift+K anywhere, use the tray icon, or run `anode kill`. All three sign the "
            + "child session out, which force-closes everything in it.", "    ");
        text.Append("\nPowerShell: append | Out-Host so the prompt waits for output and $LASTEXITCODE.\n")
            .Append("Run `anode help <command>` for one command. Agents: anode guide.\n")
            .Append("Docs: ").Append(Links.Readme).Append('\n')
            .Append("Problems: ").Append(Links.Troubleshooting).Append('\n');
        Wrap(text, "", "Exit codes: 0 ok, 1 failed, 2 usage or already running, 3 start blocked by prerequisites or wait "
            + "unmatched, 1223 setup prompt declined. exec and job return 124 on timeout, 130 when cancelled, 0 while the "
            + "job is still running, and otherwise the program's own code, so 1 or 2 may come from the program; read stderr.", "");
        return text.ToString();
    }

    /// <summary>Help for one command or alias, or null when there is no such command.</summary>
    internal static string? TopicHelp(string topic)
    {
        if (Find(topic) is not { } command) return null;
        var text = new StringBuilder();
        Render(text, command, "");
        if (command.Notes is { } notes)
        {
            text.Append('\n');
            Wrap(text, "", notes, "");
        }
        if (command.Names[0] is "start" or "up")
        {
            text.Append("\nOptions (used only when a new daemon starts):\n");
            RenderSeatOptions(text, "  ");
        }
        text.Append("\nAll commands: anode help\nReference: ").Append(Links.Usage).Append("#command-reference\n");
        return text.ToString();
    }

    /// <summary>The usage lines every usage error prints, or null for an unknown command.</summary>
    internal static string? Usage(string command) => Find(command) is { } row
        ? "usage: " + string.Join("\n       ", row.Lines.Where(l => l.Usage.Length > 0).Select(l => l.Usage))
        : null;

    private static int Help(string? topic, TextWriter? output = null)
    {
        output ??= Console.Out;
        if (topic is null || IsHelpCommand(topic.ToLowerInvariant()))
        {
            output.Write(FullHelp());
            return 0;
        }
        if (TopicHelp(topic) is { } text)
        {
            output.Write(text);
            return 0;
        }
        Console.Error.WriteLine($"anode: no help for '{topic}'. {Hint(topic, forHelp: true)}");
        return 2;
    }

    private static void Render(StringBuilder text, CommandHelp command, string indent)
    {
        // Names that no usage line spells out are aliases.
        var aliases = command.Names.Where(name => !command.Lines.Any(line => line.Usage.Split(' ').Contains(name))).ToArray();
        string column = indent + new string(' ', UsageColumn);
        // Wrapped usage continues under the first argument.
        string Continued(string usage) => indent + new string(' ', string.Join(' ', usage.Split(' ').Take(2)).Length + 1);
        for (int i = 0; i < command.Lines.Length; i++)
        {
            var (usage, summary) = command.Lines[i];
            if (i == 0 && aliases.Length > 0) summary += $" ({(aliases.Length == 1 ? "alias" : "aliases")}: {string.Join(", ", aliases)})";
            if (usage.Length == 0) Wrap(text, indent, summary, indent);
            else if (usage.Length < UsageColumn) Wrap(text, indent + usage.PadRight(UsageColumn), summary, column);
            else
            {
                Wrap(text, indent, usage, Continued(usage));
                Wrap(text, column, summary, column);
            }
        }
    }

    private static void RenderSeatOptions(StringBuilder text, string indent)
    {
        foreach (var (option, meaning) in SeatOptionHelp)
            text.Append(indent).Append(option.Length < 23 ? option.PadRight(23) : option + "  ").Append(meaning).Append('\n');
    }

    // Fills lines up to HelpWidth. The first line starts with `first`, later ones with `indent`.
    // Text breaks only outside brackets, so [--option VALUE], <word> and (asides) stay whole.
    private static void Wrap(StringBuilder text, string first, string content, string indent)
    {
        var line = new StringBuilder(first);
        int start = first.Length;
        foreach (string word in Tokens(content))
        {
            if (line.Length > start && line.Length + 1 + word.Length > HelpWidth)
            {
                text.Append(line).Append('\n');
                line.Clear().Append(indent);
                start = indent.Length;
            }
            if (line.Length > start) line.Append(' ');
            line.Append(word);
        }
        text.Append(line.ToString().TrimEnd()).Append('\n');
    }

    private static List<string> Tokens(string content)
    {
        var tokens = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i <= content.Length; i++)
        {
            if (i == content.Length || content[i] == ' ' && depth == 0)
            {
                if (i > start) tokens.Add(content[start..i]);
                start = i + 1;
            }
            else if (content[i] is '[' or '<' or '(') depth++;
            else if (content[i] is ']' or '>' or ')') depth = Math.Max(0, depth - 1);
        }
        return tokens;
    }

    private static CommandHelp? Find(string name) =>
        Commands.FirstOrDefault(c => c.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

    private static bool IsHelpCommand(string command) => command is "help" or "--help" or "-h" or "/?" or "-?";

    private static bool IsHelp(string argument) => argument is "/?" or "-?"
        || argument.Equals("--help", StringComparison.OrdinalIgnoreCase) || argument.Equals("-h", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a command line asks for help instead of the command. Free text never counts:
    /// run, type and key read literal text after their first argument, exec's program starts
    /// after "--", and the value of a free-text option is data.
    /// </summary>
    internal static bool AsksHelp(string command, string[] rest)
    {
        if (command is "run" or "type" or "key") return rest.Length > 0 && IsHelp(rest[0]);
        for (int i = 0; i < rest.Length && rest[i] != "--"; i++)
        {
            if (i > 0 && rest[i - 1].ToLowerInvariant() is "--value" or "--query" or "--text" or "--name" or "--env") continue;
            if (IsHelp(rest[i])) return true;
        }
        return false;
    }

    /// <summary>
    /// Rejects a command line before the command acts: unknown options, missing or malformed
    /// values, extra words and malformed positions for input. It only reads its arguments.
    /// </summary>
    internal static void CheckArguments(string command, string[] args)
    {
        if (Find(command) is not { } row) return;
        string name = row.Names[0];
        if (row.Options is null)
        {
            // Free text and commands with their own parsers need only a count here.
            if (name is "run" or "type" && args.Length == 0)
                throw new UsageException(name == "run" ? "run requires a program." : "type requires text.");
            if (name == "key" && args.Length != 1)
                throw new UsageException("key takes one chord, such as ctrl+shift+esc; join keys with + and no spaces.");
            return;
        }

        var words = Known(command, args, row.Words, row.Options);
        var options = new Args(args);
        switch (name)
        {
            case "setup" when options.Value("fps") is { } fps && fps != "60":
                throw new UsageException("--fps accepts only 60.");
            case "start" or "up" when options.Flag("sign-in") && options.Flag("hidden"):
                throw new UsageException("--sign-in requires a visible viewer; remove --hidden.");
            case "control" when words.Count == 1 && words[0].ToLowerInvariant() is not ("take" or "give"):
                throw new UsageException($"control takes take or give, not '{words[0]}'.");
            case "click" when words.Count == 1 || !words.All(IsInteger):
                throw new UsageException("click takes both x and y as integers, or no position to click where the pointer is.");
            case "click" when options.Flag("right") && options.Flag("middle"):
                throw new UsageException("Choose --right or --middle, not both.");
            case "move" when words.Count != 2 || !words.All(IsInteger):
                throw new UsageException("move requires x and y as integers.");
            case "scroll" when words.Count == 1 && !IsInteger(words[0]):
                throw new UsageException($"scroll takes a whole number of notches, not '{words[0]}'; negative scrolls down.");
            case "ps" when words.Count > 0 && !words[0].Equals("kill", StringComparison.OrdinalIgnoreCase):
                throw new UsageException($"Unexpected ps argument '{words[0]}'.");
            case "ps" when words.Count == 1:
                throw new UsageException("ps kill requires a process id or name.");
            case "steam":
                if (words.Count == 0 || words[0].Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    if (options.Flag("force")) throw new UsageException("--force applies only when launching a game.");
                }
                else if (!int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out int app) || app < 1)
                    throw new UsageException($"steam takes status or a Steam app id (the number in the game's store URL), not '{words[0]}'.");
                break;
            case "gamepad":
                CheckGamepad(words, options);
                break;
        }
    }

    private static void CheckGamepad(List<string> words, Args options)
    {
        string action = words.Count > 0 ? words[0].ToLowerInvariant() : "state";
        int expected = action switch
        {
            "attach" or "detach" or "reset" or "state" => 1, "tap" or "hold" or "release" => 2, "stick" => 4, _ => 0
        };
        if (expected == 0) throw new UsageException($"Unknown gamepad action '{words[0]}'.");
        static bool Axis(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number is >= -1 and <= 1;
        if (action == "stick" && (words.Count != 4 || words[1].ToLowerInvariant() is not ("left" or "right") || !Axis(words[2]) || !Axis(words[3])))
            throw new UsageException("gamepad stick requires left or right, then x and y from -1 to 1.");
        if (words.Count > 0 && words.Count != expected)
            throw new UsageException(expected == 2 ? $"gamepad {action} requires one button." : $"gamepad {action} takes no other arguments.");
        if (options.Value("slot") is { } slot && int.Parse(slot, CultureInfo.InvariantCulture) is < 0 or > 3)
            throw new UsageException("--slot must be 0-3.");
        if (options.Value("ms") is not null && action != "tap") throw new UsageException("--ms applies only to tap.");
    }

    private static bool IsInteger(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Checks every option against the names a command reads and returns its positional words.
    /// A trailing '=' marks an option that takes a value and '#' one that takes an integer.
    /// Numbers such as -3 are words. Mirrors <see cref="Args"/>: flags are -name or --name,
    /// values --name VALUE or --name=VALUE.
    /// </summary>
    internal static List<string> Known(string command, string[] args, int words, IReadOnlyCollection<string> options)
    {
        var found = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Length < 2 || arg[0] != '-' || double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                found.Add(arg);
                continue;
            }
            bool full = arg.StartsWith("--", StringComparison.Ordinal);
            int equals = full ? arg.IndexOf('=') : -1;
            string name = full ? (equals > 0 ? arg[2..equals] : arg[2..]) : arg[1..];
            string? spec = options.FirstOrDefault(o => o.TrimEnd('=', '#').Equals(name, StringComparison.OrdinalIgnoreCase));
            if (spec is null) throw new UsageException($"Unknown {command} option '{arg}'. Run `anode help {command}`.");
            bool takesValue = spec[^1] is '=' or '#';
            if (takesValue && !full) throw new UsageException($"Write --{name} with two hyphens.");
            if (!takesValue && equals > 0) throw new UsageException($"--{name} takes no value.");
            if (!takesValue) continue;
            string? value = equals > 0 ? arg[(equals + 1)..] : ++i < args.Length ? args[i] : null;
            if (value is null) throw new UsageException($"--{name} requires a value.");
            if (spec[^1] == '#' && !IsInteger(value)) throw new UsageException($"--{name} requires an integer.");
        }
        if (found.Count > words)
            throw new UsageException(words == 0
                ? $"{command} takes no arguments; remove '{found[0]}'."
                : $"Unexpected {command} argument '{found[words]}'.");
        return found;
    }

    /// <summary>Positional words of a command whose arguments <see cref="CheckArguments"/> accepted.</summary>
    private static List<string> Words(string command, string[] args) =>
        Find(command) is { Options: { } options } row ? Known(command, args, row.Words, options) : args.ToList();

    /// <summary>Tool validation names MCP arguments; show the CLI spelling instead.</summary>
    internal static string CliError(string error) =>
        System.Text.RegularExpressions.Regex.Replace(error, @"\barguments\.(\w+)", match => match.Groups[1].Value switch
        {
            "windowId" or "snapshotId" or "elementId" or "jobId" or "action" => $"<{match.Groups[1].Value}>",
            "path" => "<program>",
            "waitMs" => "--wait", "executionTimeoutMs" => "--timeout", "maxChars" => "--max-chars",
            "automationId" => "--automation-id", "textContains" => "--text", "maxElements" => "--max-elements",
            "maxDepth" => "--max-depth", "maxTextChars" => "--max-text", "ttlSeconds" => "--ttl",
            "cancelJobs" => "--cancel-jobs", "waitSeconds" => "--wait",
            var field => "--" + field
        });

    private static string Hint(string name, bool forHelp)
    {
        string lower = name.ToLowerInvariant();
        if (lower is "drag" or "seat_drag") return "Dragging is available only through MCP (seat_drag).";
        if (Suggestion(lower) is not { } match) return "Run `anode help`.";
        return forHelp ? $"Did you mean `anode help {match.Split(' ')[0]}`?" : $"Did you mean `anode {match}`?";
    }

    /// <summary>The command an MCP tool name or a small typo most likely meant, or null.</summary>
    internal static string? Suggestion(string name)
    {
        name = name.ToLowerInvariant();
        string bare = name.StartsWith("seat_", StringComparison.Ordinal) ? name[5..] : name;
        string? mapped = bare switch
        {
            "observe" => "inspect", "processes" => "ps", "kill_process" => "ps kill", "anode_guide" => "guide",
            "steam_launch" => "steam", "steam_status" => "steam status", "gamepad_set" => "gamepad hold|release|stick",
            "gamepad_attach" or "gamepad_detach" or "gamepad_reset" or "gamepad_tap" => "gamepad " + bare[8..],
            _ => null
        };
        if (mapped is not null) return mapped;
        if (Find(bare) is not null) return bare;
        return Commands.SelectMany(c => c.Names).Where(n => !n.StartsWith('-'))
            .Select(n => (Name: n, Distance: Distance(bare, n)))
            .Where(c => c.Distance <= (c.Name.Length <= 4 ? 1 : 2))
            .OrderBy(c => c.Distance).Select(c => c.Name).FirstOrDefault();
    }

    // Edit distance counting a swap of neighbors as one typo, so `stauts` is closer to status than start.
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j], d[i, j - 1]) + 1, d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}

/// <summary>A command line the CLI cannot act on. It is reported with the command's usage and exit code 2.</summary>
internal sealed class UsageException(string message) : ArgumentException(message);

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
    private const int StdOutput = -11, StdError = -12;
    private static bool _attached;

    /// <summary>True when output can reach someone: a borrowed console, or a pipe or file.</summary>
    public static bool HasConsole { get; private set; }

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        HasConsole = Present(StdOutput) || Present(StdError);

        if (Console.IsOutputRedirected && Console.IsErrorRedirected) return;
        if (!AttachConsole(unchecked((uint)-1))) return;
        HasConsole = true;
        UseConsole();
    }

    /// <summary>
    /// Second attempt for a bare `anode` that has no usable handles at all. Only the
    /// no-argument path uses it; the daemon, MCP and every other command never do.
    /// </summary>
    public static bool TryAttachParent()
    {
        if (!AttachConsole(unchecked((uint)-1))) return false;
        UseConsole();
        return HasConsole = true;
    }

    private static void UseConsole()
    {
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

    // Explorer starts a GUI executable with no standard handles; a console, pipe or file has a type.
    private static bool Present(int which)
    {
        IntPtr handle = GetStdHandle(which);
        return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
}
