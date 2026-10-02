using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anode.Core.Bridge;
using Anode.Core.Capture;
using Anode.Core.Util;

namespace Anode.Core.Android;

/// <summary>An emulator that is running, from the file each one writes while it runs.</summary>
internal sealed record RunningEmulator(string Serial, int Port, string? Avd, int Pid, int? Session);

/// <summary>What a command printed and how it ended.</summary>
internal sealed record CommandResult(int ExitCode, byte[] Output, string Error, bool TimedOut)
{
    public string Text => Encoding.UTF8.GetString(Output).Trim();
}

/// <summary>An emulator this seat host started: its launcher process and the tail of what it printed.</summary>
internal interface ILaunchedEmulator : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    string Tail();
}

/// <summary>
/// Android emulators in the seat, for android_status and android_emulator.
///
/// An emulator the seat host starts is a process in the seat, so its window opens there and never on the user's
/// desktop. Every emulator the user runs shares the machine's adb server with it, which is why everything here that
/// touches a device first checks that the device is an emulator running in the seat: a phone on USB and the user's
/// own emulators stay out of reach. AVDs start read-only by default, so tests leave the user's virtual devices as
/// they were and can run while the same AVD is open on the desktop.
/// </summary>
internal sealed class SeatEmulators
{
    internal const int FirstPort = 5554, LastPort = 5682;

    /// <summary>Everything the emulator code does to the machine, so checks can stand in for it.</summary>
    internal sealed record Machine(
        Func<string, string?> Environment,
        Func<IReadOnlyList<RunningEmulator>> Running,
        Func<int, bool> PortFree,
        Func<string, IReadOnlyList<string>, int, CancellationToken, Task<CommandResult>> Run,
        Func<string, IReadOnlyList<string>, string, ILaunchedEmulator> Start,
        Func<int, bool> Alive,
        Action<int> Kill,
        Func<TimeSpan, CancellationToken, Task> Delay,
        Func<long> Milliseconds)
    {
        public static Machine Real { get; } = new(
            System.Environment.GetEnvironmentVariable,
            () => Discover(RunningFolder(System.Environment.GetEnvironmentVariable), ProcessFacts),
            port => !Listening().Contains(port),
            RunAsync,
            (exe, args, log) => new LaunchedProcess(exe, args, log),
            pid => ProcessFacts(pid).Alive,
            pid => { try { using var process = Process.GetProcessById(pid); process.Kill(entireProcessTree: true); } catch (ArgumentException) { } },
            Task.Delay,
            () => System.Environment.TickCount64);
    }

    private readonly Machine _machine;
    private readonly uint _seat;
    private readonly string _logFolder;
    private readonly ConcurrentDictionary<int, Started> _started = new();

    private sealed record Started(string Avd, ILaunchedEmulator Process, string? Agent, string Log, DateTime StartedUtc);

    public SeatEmulators(uint seat, string logFolder, Machine? machine = null)
    {
        _seat = seat;
        _logFolder = logFolder;
        _machine = machine ?? Machine.Real;
    }

    // ------------------------------------------------------------------ status

    public async Task<JsonObject> StatusAsync(CancellationToken cancel)
    {
        var sdk = AndroidSdk.Find(_machine.Environment);
        var avds = AndroidSdk.Avds(AndroidSdk.AvdHome(_machine.Environment));
        var studios = AndroidSdk.Studios(StudioFolders(_machine.Environment));
        var running = Running();
        bool adbServer = !_machine.PortFree(5037);
        var emulators = new JsonArray();
        foreach (var emulator in running)
        {
            bool inSeat = emulator.Session == (int)_seat;
            var entry = new JsonObject
            {
                ["serial"] = emulator.Serial, ["avd"] = emulator.Avd, ["pid"] = emulator.Pid > 0 ? emulator.Pid : null, ["session"] = emulator.Session, ["inSeat"] = inSeat,
                ["startedBy"] = _started.TryGetValue(emulator.Port, out var started) ? started.Agent : null
            };
            // Asking a device is harmless, but only the seat's own are asked, and never by starting adb's server.
            if (inSeat && adbServer && sdk is not null) entry["booted"] = await BootedAsync(sdk, emulator.Serial, cancel).ConfigureAwait(false);
            emulators.Add(entry);
        }
        var result = new JsonObject
        {
            ["sdk"] = sdk?.Root, ["emulatorVersion"] = sdk?.EmulatorVersion?.ToString(),
            ["adb"] = sdk is not null && File.Exists(sdk.Adb) ? sdk.Adb : null, ["adbServerRunning"] = adbServer,
            ["avds"] = new JsonArray(avds.Select(avd => (JsonNode)avd.ToJson()).ToArray()),
            ["emulators"] = emulators,
            ["studio"] = new JsonArray(studios.Select(studio => (JsonNode)studio.ToJson()).ToArray())
        };
        var text = new StringBuilder();
        if (sdk is null) text.Append("No Android SDK was found in ANDROID_HOME, ANDROID_SDK_ROOT or %LOCALAPPDATA%\\Android\\Sdk. ");
        else text.Append($"Android SDK at {sdk.Root}, emulator {sdk.EmulatorVersion?.ToString() ?? "not installed"}. ");
        text.Append(avds.Count == 0 ? "No virtual devices (AVDs) are defined. "
            : $"AVDs: {string.Join("; ", avds.Select(a => $"{a.Name} (API {a.Api?.ToString() ?? "?"}{(a.PlayStore ? ", Google Play" : "")})"))}. ");
        text.Append(running.Count == 0 ? "No emulator is running."
            : "Running: " + string.Join("; ", running.Select(e => $"{e.Serial} {e.Avd ?? "(unknown AVD)"} "
                + (e.Session == (int)_seat ? "in the seat" : $"outside the seat (session {e.Session?.ToString() ?? "unknown"})"))) + ".");
        if (studios.Count > 0) text.Append($" Android Studio {studios[0].Version ?? ""} is installed; android_studio opens it in the seat.".Replace("  ", " "));
        result["summary"] = text.ToString();
        return result;
    }

    // ------------------------------------------------------------------- start

    public async Task<JsonObject> StartAsync(JsonObject request, CancellationToken cancel)
    {
        var sdk = AndroidSdk.Find(_machine.Environment);
        if (sdk is null || !File.Exists(sdk.Emulator))
            return JsonLine.Fail("The Android emulator was not found. Install it with Android Studio's SDK Manager, or set ANDROID_HOME to the SDK folder.");
        string asked = request.Str("avd")!;
        var avds = AndroidSdk.Avds(AndroidSdk.AvdHome(_machine.Environment));
        var avd = avds.FirstOrDefault(a => a.Name == asked) ?? avds.FirstOrDefault(a => a.Name.Equals(asked, StringComparison.OrdinalIgnoreCase));
        if (avd is null)
            return JsonLine.Fail($"No AVD is named '{asked}'. " + (avds.Count == 0 ? "None are defined; create one in Android Studio's Device Manager."
                : "Available: " + string.Join(", ", avds.Select(a => a.Name)) + "."));

        bool readOnly = request.Bool("readOnly") ?? true;
        var running = Running();
        if (!readOnly && running.FirstOrDefault(e => e.Avd == avd.Name) is { } busy)
            return JsonLine.Fail($"{avd.Name} is already running as {busy.Serial}"
                + (busy.Session == (int)_seat ? " in the seat" : $" outside the seat (session {busy.Session})")
                + ". Start it read-only (the default), which can run alongside, or stop that emulator first.");
        int? port = FreePort(running);
        if (port is not int console) return JsonLine.Fail($"Every emulator port from {FirstPort} to {LastPort} is in use.");
        string serial = $"emulator-{console}";

        // Started before the emulator, the adb server learns of it as it boots, whatever its port.
        if (File.Exists(sdk.Adb)) await _machine.Run(sdk.Adb, new[] { "start-server" }, 20_000, cancel).ConfigureAwait(false);

        string gpu = request.Str("gpu") ?? "auto";
        var args = new List<string> { "-avd", avd.Name, "-port", console.ToString(CultureInfo.InvariantCulture), "-no-boot-anim" };
        if (readOnly) args.Add("-read-only");
        if (request.Bool("coldBoot") == true) args.Add("-no-snapshot-load");
        if (AndroidSdk.GpuFlag(gpu, sdk.EmulatorVersion) is { } flag) args.AddRange(new[] { "-gpu", flag });
        if (request.Bool("audio") != true) args.Add("-no-audio");

        string log = Path.Combine(_logFolder, $"{serial}.log");
        ILaunchedEmulator launched;
        try
        {
            Directory.CreateDirectory(_logFolder);
            launched = _machine.Start(sdk.Emulator, args, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return JsonLine.Fail($"The emulator for {avd.Name} could not start: {ex.Message}");
        }
        _started[console] = new Started(avd.Name, launched, request.Str("agentId"), log, DateTime.UtcNow);
        Log.Info($"seat started emulator {serial} for {avd.Name}: {string.Join(' ', args)}");

        // Reply before the daemon's forwarding deadline, which the caller extends with timeoutMs.
        int budget = Math.Clamp(((request.Int("timeoutMs") ?? 60_000) - 20_000) / 1000, 0, 150);
        int wait = Math.Min(request.Int("waitSeconds") ?? 120, budget);
        long begun = _machine.Milliseconds();
        bool booted = false;
        while (true)
        {
            if (launched.HasExited && !Running().Any(e => e.Port == console))
            {
                _started.TryRemove(console, out _);
                string tail = launched.Tail();
                launched.Dispose();
                return JsonLine.Fail($"The emulator for {avd.Name} exited while starting." + (tail.Length > 0 ? " It printed:\n" + tail : "") + $"\nFull output: {log}");
            }
            if (File.Exists(sdk.Adb) && await BootedAsync(sdk, serial, cancel).ConfigureAwait(false) == true) { booted = true; break; }
            if (_machine.Milliseconds() - begun >= wait * 1000L) break;
            await _machine.Delay(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
        }
        double seconds = Math.Round((_machine.Milliseconds() - begun) / 1000.0, 1);
        var others = Running().Where(e => e.Port != console).ToArray();
        return JsonLine.Ok(new JsonObject
        {
            ["serial"] = serial, ["avd"] = avd.Name, ["pid"] = launched.Id, ["consolePort"] = console, ["booted"] = booted,
            ["waitedSeconds"] = seconds, ["readOnly"] = readOnly, ["gpu"] = gpu, ["log"] = log,
            ["summary"] = $"{avd.DisplayName} started in the seat as {serial}" + (readOnly ? ", read-only, so nothing it does is saved to the AVD" : "")
                + (booted ? $", and finished booting after {seconds} s. " : $"; it had not finished booting after {seconds} s. Check again with android_status; a cold boot can take minutes. ")
                + $"Its window is titled \"Android Emulator - {avd.Name}:{console}\". Use android_emulator with serial {serial}: action=adb to install and drive apps, "
                + "action=screenshot for the device's own pixels, action=stop when done. Never use adb without this serial: the adb server is shared with the user's desktop."
                + (others.Length > 0 ? $" Also running: {string.Join(", ", others.Select(e => $"{e.Serial} ({(e.Session == (int)_seat ? "seat" : "outside the seat")})"))}." : "")
        });
    }

    // -------------------------------------------------------------------- stop

    public async Task<JsonObject> StopAsync(JsonObject request, CancellationToken cancel)
    {
        string serial = request.Str("serial")!;
        if (Refuse(serial, out var emulator) is { } refused) return refused;
        var sdk = AndroidSdk.Find(_machine.Environment);
        string method = "emu kill";
        if (sdk is not null && File.Exists(sdk.Adb)) await _machine.Run(sdk.Adb, new[] { "-s", serial, "emu", "kill" }, 10_000, cancel).ConfigureAwait(false);
        long deadline = _machine.Milliseconds() + 20_000;
        while (Alive(emulator!) && _machine.Milliseconds() < deadline) await _machine.Delay(TimeSpan.FromMilliseconds(500), cancel).ConfigureAwait(false);
        if (Alive(emulator!))
        {
            method = "terminated";
            if (emulator!.Pid > 0) _machine.Kill(emulator.Pid);
            if (_started.TryGetValue(emulator.Port, out var ours)) _machine.Kill(ours.Process.Id);
        }
        if (_started.TryRemove(emulator!.Port, out var started)) started.Process.Dispose();
        Log.Info($"seat stopped emulator {serial} ({method})");
        return JsonLine.Ok(new JsonObject
        {
            ["serial"] = serial, ["stopped"] = true, ["method"] = method,
            ["summary"] = method == "emu kill" ? $"{serial} shut down." : $"{serial} did not shut down within 20 s, so its processes were ended."
        });
    }

    private bool Alive(RunningEmulator emulator) =>
        (emulator.Pid > 0 && _machine.Alive(emulator.Pid)) || (_started.TryGetValue(emulator.Port, out var ours) && !ours.Process.HasExited);

    // -------------------------------------------------------------- screenshot

    public async Task<JsonObject> ScreenshotAsync(JsonObject request, CancellationToken cancel)
    {
        string serial = request.Str("serial")!;
        if (Refuse(serial, out _) is { } refused) return refused;
        var sdk = AndroidSdk.Find(_machine.Environment);
        if (sdk is null || !File.Exists(sdk.Adb)) return JsonLine.Fail("adb was not found in the Android SDK's platform-tools.");
        var capture = await _machine.Run(sdk.Adb, new[] { "-s", serial, "exec-out", "screencap", "-p" }, 30_000, cancel).ConfigureAwait(false);
        if (capture.ExitCode != 0 || capture.Output.Length < 8 || capture.Output[0] != 0x89 || capture.Output[1] != (byte)'P')
            return JsonLine.Fail($"{serial} returned no screenshot. {(capture.TimedOut ? "adb timed out." : capture.Error.Trim())} It may still be booting.".Trim());
        ScreenCapture.Shot shot;
        using (var stream = new MemoryStream(capture.Output))
        using (var picture = new System.Drawing.Bitmap(stream))
            shot = ScreenCapture.Encode(picture, request.Int("maxWidth") ?? 1080, request.Str("format") ?? "png", request.Int("quality") ?? 80);
        return JsonLine.Ok(new JsonObject
        {
            ["serial"] = serial,
            ["screenshot"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(shot.Bytes), ["mimeType"] = shot.MimeType, ["width"] = shot.Width, ["height"] = shot.Height,
                ["sourceWidth"] = shot.SourceWidth, ["sourceHeight"] = shot.SourceHeight
            },
            ["summary"] = $"{serial} screen, {shot.Width}x{shot.Height} (captured at {shot.SourceWidth}x{shot.SourceHeight}). "
                + "adb input coordinates use the captured size: x * sourceWidth / width, y * sourceHeight / height."
        });
    }

    // --------------------------------------------------------------------- adb

    /// <summary>
    /// adb commands that act on the whole adb server or on other devices, which the user's desktop shares, whatever -s
    /// says: among them the device list, the server's state, and raw, which sends any adb service, such as host:kill.
    /// The serial is always the one given, so global options are refused too.
    /// </summary>
    private static readonly HashSet<string> ServerCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "kill-server", "start-server", "reconnect", "connect", "disconnect", "pair", "devices", "mdns", "keygen", "server", "nodaemon",
        "wait-for-any-device", "wait-for-usb-device", "wait-for-local-device", "attach", "detach", "track-devices", "server-status", "raw"
    };

    internal static string? AdbProblem(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return "args needs an adb command, such as [\"shell\", \"input\", \"tap\", \"540\", \"1200\"].";
        if (args[0].StartsWith('-'))
            return $"adb option '{args[0]}' is not allowed: the serial is added for you, and other global options would reach other devices or the adb server.";
        if (ServerCommands.Contains(args[0]))
            return $"adb {args[0]} acts on the adb server or other devices, which the user's desktop shares, so it is not allowed here.";
        // adb runs what follows a wait-for-STATE prefix, as in wait-for-device shell, so that is checked as a command too.
        if (args[0].StartsWith("wait-for-", StringComparison.OrdinalIgnoreCase) && args.Count > 1) return AdbProblem(args.Skip(1).ToArray());
        // The adb server keeps one list of host port forwards for every device, and these list or clear all of it.
        if (args[0] == "forward" && args.Count > 1 && args[1] is "--list" or "--remove-all")
            return $"adb forward {args[1]} acts on every device's forwards in the adb server, which the user's desktop shares, so it is not allowed here. "
                + "Remove this emulator's own forwards one at a time with forward --remove LOCAL.";
        return null;
    }

    /// <summary>
    /// The host end a forward would take from whichever device holds it: forward --remove LOCAL, or forward LOCAL REMOTE,
    /// which moves an existing LOCAL to this device. The adb server keys forwards by that name alone, whatever -s says.
    /// Null for anything else, for --no-rebind, which fails rather than move one, and for tcp:0, which asks for a new port.
    /// </summary>
    internal static string? ForwardedLocal(IReadOnlyList<string> args)
    {
        while (args.Count > 1 && args[0].StartsWith("wait-for-", StringComparison.OrdinalIgnoreCase)) args = args.Skip(1).ToArray();
        if (args.Count < 3 || args[0] != "forward") return null;
        if (args[1] == "--remove") return args[2];
        return args[1].StartsWith('-') || args[1] == "tcp:0" ? null : args[1];
    }

    /// <summary>The device a host end forwards to, from adb forward --list lines ("SERIAL LOCAL REMOTE"), or null.</summary>
    internal static string? ForwardOwner(string list, string local) =>
        list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(fields => fields.Length >= 3 && fields[1] == local)?[0];

    public async Task<JsonObject> AdbAsync(JsonObject request, CancellationToken cancel)
    {
        string serial = request.Str("serial")!;
        if (Refuse(serial, out _) is { } refused) return refused;
        var args = (request["args"] as JsonArray)!.Select(a => a!.GetValue<string>()).ToArray();
        if (AdbProblem(args) is { } problem) return JsonLine.Fail(problem);
        var sdk = AndroidSdk.Find(_machine.Environment);
        if (sdk is null || !File.Exists(sdk.Adb)) return JsonLine.Fail("adb was not found in the Android SDK's platform-tools.");
        if (ForwardedLocal(args) is { } local)
        {
            var forwards = await _machine.Run(sdk.Adb, new[] { "forward", "--list" }, 10_000, cancel).ConfigureAwait(false);
            if (forwards.ExitCode != 0)
                return JsonLine.Fail($"Nothing was run: adb could not list the forwards to check that {local} is not another device's. "
                    + (forwards.TimedOut ? "adb timed out." : forwards.Error.Trim()));
            if (ForwardOwner(forwards.Text, local) is { } owner && owner != serial)
                return JsonLine.Fail($"Nothing was run: {local} forwards to another device, and the adb server, which the user's desktop shares, "
                    + (args.Contains("--remove") ? "would remove that forward. Remove only this emulator's own forwards."
                        : $"would hand it to {serial}. Forward another port, or tcp:0 for a free one; forward --no-rebind never takes one over."));
        }
        int seconds = request.Int("timeoutSeconds") ?? 60;
        var result = await _machine.Run(sdk.Adb, new[] { "-s", serial }.Concat(args).ToArray(), seconds * 1000, cancel).ConfigureAwait(false);
        string output = Bounded(Printable(Encoding.UTF8.GetString(result.Output)), 20_000), error = Bounded(Printable(result.Error), 4_000);
        return JsonLine.Ok(new JsonObject
        {
            ["serial"] = serial, ["exitCode"] = result.ExitCode, ["timedOut"] = result.TimedOut, ["stdout"] = output, ["stderr"] = error,
            ["summary"] = $"adb -s {serial} {string.Join(' ', args.Select(Quote))}: "
                + (result.TimedOut ? $"timed out after {seconds} s and was ended; it may have partly run. Use logcat -d rather than a streaming logcat." : $"exit code {result.ExitCode}.")
                + (output.Trim().Length > 0 ? "\nstdout:\n" + output.TrimEnd() : "") + (error.Trim().Length > 0 ? "\nstderr:\n" + error.TrimEnd() : "")
        });
    }

    private static string Printable(string text) => string.Concat(text.Select(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t' ? ' ' : c));

    private static string Quote(string value) => value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"') ? value : "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Bounded(string text, int max) => text.Length <= max ? text : text[..max] + $"\n[{text.Length - max} more characters not shown]";

    // ------------------------------------------------------------- shared checks

    private static readonly Regex SerialPattern = new(@"^emulator-(\d{4})$", RegexOptions.CultureInvariant);

    /// <summary>A failure unless the serial is an emulator running in the seat.</summary>
    private JsonObject? Refuse(string serial, out RunningEmulator? emulator)
    {
        emulator = null;
        var match = SerialPattern.Match(serial);
        if (!match.Success) return JsonLine.Fail($"'{serial}' is not an emulator serial such as emulator-5554. Anode works only with emulators in the seat, never with phones or other devices.");
        int port = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        emulator = Running().FirstOrDefault(e => e.Port == port);
        if (emulator is null) return JsonLine.Fail($"No emulator {serial} is running. android_status lists the running ones.");
        if (emulator.Session != (int)_seat)
            return JsonLine.Fail($"{serial} runs outside the seat (session {emulator.Session?.ToString() ?? "unknown"}), so Anode leaves it alone. Start one in the seat with android_emulator action=start.");
        return null;
    }

    /// <summary>Running emulators, including ones this host started that have not advertised themselves yet.</summary>
    private IReadOnlyList<RunningEmulator> Running()
    {
        var running = _machine.Running().ToList();
        foreach (var (port, started) in _started)
        {
            if (running.Any(e => e.Port == port)) continue;
            if (started.Process.HasExited) { _started.TryRemove(port, out _); continue; }
            running.Add(new RunningEmulator($"emulator-{port}", port, started.Avd, 0, (int)_seat));
        }
        return running.OrderBy(e => e.Port).ToArray();
    }

    /// <summary>The first even console port whose adb port is free too, and that no emulator claims.</summary>
    private int? FreePort(IReadOnlyList<RunningEmulator> running)
    {
        for (int port = FirstPort; port <= LastPort; port += 2)
            if (!running.Any(e => e.Port == port) && !_started.ContainsKey(port) && _machine.PortFree(port) && _machine.PortFree(port + 1)) return port;
        return null;
    }

    private async Task<bool?> BootedAsync(AndroidSdk sdk, string serial, CancellationToken cancel)
    {
        var result = await _machine.Run(sdk.Adb, new[] { "-s", serial, "shell", "getprop", "sys.boot_completed" }, 10_000, cancel).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Text == "1" : null;
    }

    // ------------------------------------------------------------ the machine

    /// <summary>The folders Android Studio installs into: Program Files\Android, or LOCALAPPDATA\Programs for one user.</summary>
    internal static IEnumerable<string> StudioFolders(Func<string, string?> environment)
    {
        var folders = new List<string>();
        foreach (string variable in new[] { "ProgramFiles", "ProgramW6432" })
            if (environment(variable) is { Length: > 0 } programs) folders.Add(Path.Combine(programs, "Android"));
        if (environment("LOCALAPPDATA") is { Length: > 0 } local) folders.Add(Path.Combine(local, "Programs"));
        return folders.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Where running emulators advertise themselves on Windows, whichever session they run in.</summary>
    internal static string RunningFolder(Func<string, string?> environment) =>
        Path.Combine(environment("LOCALAPPDATA") ?? "", "Temp", "avd", "running");

    internal readonly record struct ProcessFact(bool Alive, string? Name, int? Session);

    /// <summary>
    /// Emulators from their pid_NNNN.ini files. A file whose process has gone, or is no longer an emulator, is left
    /// from one that crashed and is skipped.
    /// </summary>
    internal static IReadOnlyList<RunningEmulator> Discover(string folder, Func<int, ProcessFact> process)
    {
        var found = new List<RunningEmulator>();
        if (!Directory.Exists(folder)) return found;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "pid_*.ini").ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }
        foreach (string file in files)
        {
            var id = Regex.Match(Path.GetFileName(file), @"^pid_(\d+)\.ini$", RegexOptions.IgnoreCase);
            if (!id.Success || !int.TryParse(id.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
            var fact = process(pid);
            if (!fact.Alive || fact.Name is not { } name
                || !(name.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase) || name.StartsWith("emulator", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var values = AndroidSdk.Values(file);
                if (!int.TryParse(values.GetValueOrDefault("port.serial"), NumberStyles.None, CultureInfo.InvariantCulture, out int port)) continue;
                found.Add(new RunningEmulator($"emulator-{port}", port, values.GetValueOrDefault("avd.name"), pid, fact.Session));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private static ProcessFact ProcessFacts(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return new ProcessFact(false, null, null);
            return new ProcessFact(true, process.ProcessName, process.SessionId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessFact(false, null, null);
        }
    }

    private static HashSet<int> Listening() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();

    /// <summary>
    /// Runs a command and collects what it prints. A command that outlives its deadline is ended with its children.
    /// Output is read only briefly after it exits: adb start-server leaves a server behind that may hold it open.
    /// </summary>
    internal static async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, int timeoutMs, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? System.Environment.CurrentDirectory
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}.");
        process.StandardInput.Close();
        var output = new MemoryStream();
        var reading = CopyBoundedAsync(process.StandardOutput.BaseStream, output, 64 * 1024 * 1024);
        var error = process.StandardError.ReadToEndAsync();
        bool timedOut = false;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            deadline.CancelAfter(timeoutMs);
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                cancel.ThrowIfCancellationRequested();
                timedOut = true;
            }
        }
        await Task.WhenAny(Task.WhenAll(reading, error), Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
        string errorText = error.IsCompletedSuccessfully ? error.Result : "";
        return new CommandResult(timedOut ? -1 : SafeExitCode(process), output.ToArray(), errorText, timedOut);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch (InvalidOperationException) { return -1; }
    }

    private static async Task CopyBoundedAsync(Stream source, MemoryStream target, int limit)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            lock (target) { if (target.Length < limit) target.Write(buffer, 0, (int)Math.Min(read, limit - target.Length)); }
    }

    /// <summary>
    /// The emulator's launcher, with what it and the emulator engine print copied to a log file. The copy ends when
    /// both have exited; if the seat host ends first, the emulator carries on without its log.
    /// </summary>
    private sealed class LaunchedProcess : ILaunchedEmulator
    {
        private readonly Process _process;
        private readonly Queue<string> _tail = new();
        private readonly StreamWriter _log;
        private long _written;

        public LaunchedProcess(string executable, IReadOnlyList<string> arguments, string logPath)
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? System.Environment.CurrentDirectory
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            _log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            _log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {executable} {string.Join(' ', arguments)}");
            _process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the Android emulator.");
            _process.OutputDataReceived += (_, line) => Record(line.Data);
            _process.ErrorDataReceived += (_, line) => Record(line.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private void Record(string? line)
        {
            if (line is null) return;
            lock (_tail)
            {
                _tail.Enqueue(line);
                while (_tail.Count > 30) _tail.Dequeue();
                if (_written >= 4 * 1024 * 1024) return;
                _written += line.Length + 2;
                try { _log.WriteLine(line); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }

        public int Id => _process.Id;

        public bool HasExited
        {
            get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
        }

        public string Tail() { lock (_tail) return string.Join("\n", _tail); }

        public void Dispose()
        {
            _process.Dispose();
            lock (_tail) { try { _log.Dispose(); } catch (IOException) { } }
        }
    }
}
