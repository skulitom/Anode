using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Android;
using Anode.Core.Bridge;
using Anode.Core.Browser;
using Anode.Mcp;

namespace Anode.Cli;

/// <summary>
/// Android and seat-browser support against folders made for the check and stand-ins for every process. Nothing here
/// starts an emulator, adb, Android Studio or a browser; the one look at this machine only reads its SDK folders.
/// </summary>
internal static class AndroidChecks
{
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    /// <summary>A disposable folder holding an SDK, AVDs, Android Studio installs and a LOCALAPPDATA.</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "anode-selftest-android-" + Guid.NewGuid().ToString("N"));
        public string Sdk => Path.Combine(Root, "Local", "Android", "Sdk");
        public string Local => Path.Combine(Root, "Local");
        public string AvdHome => Path.Combine(Root, "Profile", ".android", "avd");
        public string ProgramFiles => Path.Combine(Root, "Program Files");
        public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Fixture(string emulatorRevision = "36.2.12")
        {
            Write(Path.Combine(Sdk, "emulator", "emulator.exe"), "");
            Write(Path.Combine(Sdk, "emulator", "source.properties"), $"Pkg.UserSrc=false\nPkg.Revision={emulatorRevision}\nPkg.Path=emulator\n");
            Write(Path.Combine(Sdk, "platform-tools", "adb.exe"), "");
            string avd = Path.Combine(AvdHome, "Pixel_9_Pro.avd");
            Write(Path.Combine(AvdHome, "Pixel_9_Pro.ini"), $"avd.ini.encoding=UTF-8\npath={avd}\npath.rel=avd\\Pixel_9_Pro.avd\ntarget=android-36\n");
            Write(Path.Combine(avd, "config.ini"), "AvdId=Pixel_9_Pro\nPlayStore.enabled=true\nabi.type=x86_64\navd.ini.displayname=Pixel 9 Pro\n"
                + "hw.lcd.density=480\nhw.lcd.height=2856\nhw.lcd.width=1280\nimage.sysdir.1=system-images\\android-36\\google_apis_playstore\\x86_64\\\n"
                + "tag.display=Google Play\ntag.id=google_apis_playstore\ntarget=android-36\n");
            // A pointer whose AVD folder has gone is skipped.
            Write(Path.Combine(AvdHome, "Gone.ini"), $"path={Path.Combine(AvdHome, "Gone.avd")}\n");
            // An install that lost its product-info.json does not start and is skipped; the real one is found.
            Write(Path.Combine(ProgramFiles, "Android", "Android Studio", "bin", "studio64.exe"), "");
            Write(Path.Combine(ProgramFiles, "Android", "Android Studio1", "bin", "studio64.exe"), "");
            Write(Path.Combine(ProgramFiles, "Android", "Android Studio1", "product-info.json"),
                "{\"name\":\"Android Studio\",\"dataDirectoryName\":\"AndroidStudio2025.2.1\"}");
            Variables["LOCALAPPDATA"] = Local;
            Variables["USERPROFILE"] = Path.Combine(Root, "Profile");
            Variables["ProgramFiles"] = ProgramFiles;
        }

        public string? Get(string name) => Variables.TryGetValue(name, out var value) ? value : null;

        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string Sdk()
    {
        using var fixture = new Fixture();
        var sdk = AndroidSdk.Find(fixture.Get);
        Require(sdk?.Root == Path.GetFullPath(fixture.Sdk) && sdk.EmulatorVersion == new Version(36, 2, 12), "the SDK under LOCALAPPDATA was not found");
        // ANDROID_HOME comes first, and a folder without the emulator or adb is not an SDK.
        fixture.Variables["ANDROID_SDK_ROOT"] = fixture.Root;
        Require(AndroidSdk.Find(fixture.Get)?.Root == Path.GetFullPath(fixture.Sdk), "a folder without platform tools was taken for the SDK");
        fixture.Variables["ANDROID_HOME"] = fixture.Sdk;
        Require(AndroidSdk.Find(fixture.Get)?.Root == Path.GetFullPath(fixture.Sdk), "ANDROID_HOME was ignored");
        Require(AndroidSdk.Find(_ => null) is null, "an SDK was found without any folder to look in");

        Require(AndroidSdk.AvdHome(fixture.Get) == fixture.AvdHome, "the AVD home is not USERPROFILE\\.android\\avd");
        fixture.Variables["ANDROID_USER_HOME"] = Path.Combine(fixture.Root, "user");
        Require(AndroidSdk.AvdHome(fixture.Get) == Path.Combine(fixture.Root, "user", "avd"), "ANDROID_USER_HOME was ignored");
        fixture.Variables["ANDROID_AVD_HOME"] = fixture.AvdHome;
        Require(AndroidSdk.AvdHome(fixture.Get) == fixture.AvdHome, "ANDROID_AVD_HOME did not come first");

        var avds = AndroidSdk.Avds(fixture.AvdHome);
        Require(avds.Count == 1 && avds[0] is { Name: "Pixel_9_Pro", DisplayName: "Pixel 9 Pro", Api: 36, Image: "Google Play", Abi: "x86_64", PlayStore: true }
            && avds[0].Screen == "1280x2856 at 480 dpi", "the AVD was not read as its config.ini describes it");

        Require(AndroidSdk.GpuFlag("auto", new Version(36, 2, 12)) is null && AndroidSdk.GpuFlag("host", null) == "host"
            && AndroidSdk.GpuFlag("software", new Version(36, 2, 12)) == "swiftshader_indirect" && AndroidSdk.GpuFlag("software", null) == "swiftshader_indirect"
            && AndroidSdk.GpuFlag("software", new Version(36, 4, 9)) == "software" && AndroidSdk.GpuFlag("software", new Version(37, 1, 11)) == "software",
            "software rendering is not named as the installed emulator names it");

        var studios = AndroidSdk.Studios(SeatEmulators.StudioFolders(fixture.Get));
        Require(studios.Count == 1 && studios[0].Version == "2025.2.1" && studios[0].Launcher.Contains("Android Studio1"),
            "Android Studio was not found, or the remains of an old install were taken for one");

        string running = Path.Combine(fixture.Root, "running");
        Fixture.Write(Path.Combine(running, "pid_100.ini"), "port.serial=5554\nport.adb=5555\navd.name=Pixel_9_Pro\ngrpc.token=secret\n");
        Fixture.Write(Path.Combine(running, "pid_200.ini"), "port.serial=5556\navd.name=Gone\n");
        Fixture.Write(Path.Combine(running, "pid_300.ini"), "port.serial=5558\navd.name=Reused\n");
        Fixture.Write(Path.Combine(running, "notes.txt"), "not an emulator");
        var found = SeatEmulators.Discover(running, pid => pid switch
        {
            100 => new SeatEmulators.ProcessFact(true, "qemu-system-x86_64", 3),
            300 => new SeatEmulators.ProcessFact(true, "notepad", 3),
            _ => new SeatEmulators.ProcessFact(false, null, null)
        });
        Require(found.Count == 1 && found[0] == new RunningEmulator("emulator-5554", 5554, "Pixel_9_Pro", 100, 3),
            "running emulators were not read from their files, or a crashed or reused one was listed");
        return "SDK, AVD home and AVDs found as the SDK finds them; broken installs and stale emulator files skipped; software rendering named per emulator version";
    }

    /// <summary>A seat emulator's machine: fake processes, ports, adb replies and a clock that runs as it waits.</summary>
    private sealed class StandIn
    {
        public long Now;
        public readonly List<string> Commands = new();
        public readonly List<RunningEmulator> Running = new();
        public readonly HashSet<int> Busy = new() { 5037 };
        public readonly HashSet<int> Dead = new();
        public readonly List<int> Killed = new();
        public string? StartedArgs;
        public int BootPolls = 2;
        public bool LauncherExits, EmuKillWorks = true;
        /// <summary>A reply for particular adb arguments; null falls through to the usual ones.</summary>
        public Func<string[], CommandResult?>? Adb;

        public sealed class Launched : ILaunchedEmulator
        {
            public int Id { get; init; } = 4242;
            public bool HasExited { get; set; }
            public string Tail() => "PANIC: Missing emulator engine program";
            public void Dispose() { }
        }

        public Launched? Last;

        public SeatEmulators.Machine Machine(Fixture fixture) => new(
            fixture.Get,
            () => Running.ToArray(),
            port => !Busy.Contains(port),
            (exe, args, timeout, cancel) =>
            {
                lock (Commands) Commands.Add(string.Join(' ', args));
                var list = args.ToArray();
                if (Adb?.Invoke(list) is { } custom) return Task.FromResult(custom);
                if (list.SequenceEqual(new[] { "start-server" })) return Task.FromResult(Result(""));
                if (list.Length >= 4 && list[2] == "shell" && list[^1] == "sys.boot_completed")
                    return Task.FromResult(BootPolls-- > 0 ? new CommandResult(1, Array.Empty<byte>(), "error: device offline", false) : Result("1\n"));
                if (list.Length >= 4 && list[2] == "emu" && list[3] == "kill")
                {
                    if (EmuKillWorks)
                    {
                        foreach (var emulator in Running.Where(e => e.Serial == list[1]).ToArray()) Dead.Add(emulator.Pid);
                        // The launcher waits for the emulator engine and exits with it.
                        if (Last is not null) Last.HasExited = true;
                    }
                    return Task.FromResult(Result("OK"));
                }
                return Task.FromResult(Result("done\n"));
            },
            (exe, args, log) =>
            {
                StartedArgs = string.Join(' ', args);
                Last = new Launched { HasExited = LauncherExits };
                return Last;
            },
            pid => !Dead.Contains(pid),
            pid => { Killed.Add(pid); Dead.Add(pid); },
            (delay, cancel) => { Now += (long)delay.TotalMilliseconds; return Task.CompletedTask; },
            () => Now);

        public static CommandResult Result(string text) => new(0, Encoding.UTF8.GetBytes(text), "", false);
    }

    public static async Task<string> Emulators()
    {
        using var fixture = new Fixture();
        var none = CancellationToken.None;
        JsonObject Start(string avd, JsonObject? extra = null)
        {
            var request = new JsonObject { ["action"] = "start", ["avd"] = avd, ["agentId"] = "agent-1", ["timeoutMs"] = 175_000 };
            foreach (var (key, value) in extra ?? new JsonObject()) request[key] = value?.DeepClone();
            return request;
        }

        // The user's own emulator on the desktop takes 5554; the seat's gets the next free even port.
        var machine = new StandIn();
        machine.Running.Add(new RunningEmulator("emulator-5554", 5554, "Pixel_9_Pro", 900, 1));
        var seat = new SeatEmulators(3, Path.Combine(fixture.Root, "logs"), machine.Machine(fixture));
        var started = await seat.StartAsync(Start("Pixel_9_Pro", new JsonObject { ["gpu"] = "software" }), none);
        var result = started.Obj("result");
        Require(started.Bool("ok") == true && result?.Str("serial") == "emulator-5556" && result.Bool("booted") == true && result.Bool("readOnly") == true,
            "the emulator did not start on a free port, read-only, and wait for boot: " + started.ToJsonString());
        Require(machine.StartedArgs == "-avd Pixel_9_Pro -port 5556 -no-boot-anim -read-only -gpu swiftshader_indirect -no-audio"
            && machine.Commands[0] == "start-server", "the emulator was started with other arguments, or before adb's server: " + machine.StartedArgs);
        Require(result!.Str("summary")!.Contains("Never use adb without this serial"), "the start result does not warn about the shared adb server");

        // Unknown AVDs are named; a writable start of an AVD already running is refused, since the emulator locks it.
        var unknown = await seat.StartAsync(Start("Pixel_10"), none);
        Require(unknown.Bool("ok") == false && unknown.Str("error")!.Contains("Available: Pixel_9_Pro"), "an unknown AVD did not list the real ones");
        var writable = await seat.StartAsync(Start("Pixel_9_Pro", new JsonObject { ["readOnly"] = false }), none);
        Require(writable.Bool("ok") == false && writable.Str("error")!.Contains("already running as emulator-5554"), "a writable start of a running AVD was attempted");

        // The emulator the seat started counts as the seat's until it advertises itself.
        machine.Running.Add(new RunningEmulator("emulator-5556", 5556, "Pixel_9_Pro", 901, 3));
        var status = await seat.StatusAsync(none);
        var listed = status["emulators"] as JsonArray;
        Require(listed?.Count == 2 && listed.OfType<JsonObject>().Single(e => e.Str("serial") == "emulator-5556") is var mine
            && mine.Bool("inSeat") == true && mine.Bool("booted") == true && mine.Str("startedBy") == "agent-1"
            && listed.OfType<JsonObject>().Single(e => e.Str("serial") == "emulator-5554") is var theirs && theirs.Bool("inSeat") == false && !theirs.ContainsKey("booted"),
            "status did not tell the seat's emulator from the desktop's, or asked the desktop's: " + status.ToJsonString());
        Require(status.Str("summary")!.Contains("outside the seat (session 1)") && status["studio"] is JsonArray { Count: 1 }, "status summary lacks placement or Studio");

        // Nothing outside the seat, and nothing but an emulator, is ever stopped, captured or driven.
        foreach (var (serial, expected) in new[] { ("emulator-5554", "runs outside the seat"), ("R58M12345", "not an emulator serial"), ("emulator-5560", "No emulator") })
        {
            foreach (string action in new[] { "stop", "screenshot", "adb" })
            {
                var request = new JsonObject { ["action"] = action, ["serial"] = serial, ["args"] = new JsonArray("shell", "true") };
                var refused = action switch
                {
                    "stop" => await seat.StopAsync(request, none),
                    "screenshot" => await seat.ScreenshotAsync(request, none),
                    _ => await seat.AdbAsync(request, none)
                };
                Require(refused.Bool("ok") == false && refused.Str("error")!.Contains(expected), $"{action} on {serial} was not refused: {refused.ToJsonString()}");
            }
        }
        Require(!machine.Commands.Any(command => command.Contains("emulator-5554") || command.Contains("R58M")), "adb was run against a device outside the seat");

        // adb runs for the seat's serial only, and never acts on the server or other devices.
        var adb = await seat.AdbAsync(new JsonObject { ["serial"] = "emulator-5556", ["args"] = new JsonArray("shell", "input", "tap", "540", "1200") }, none);
        Require(adb.Bool("ok") == true && machine.Commands.Last() == "-s emulator-5556 shell input tap 540 1200" && adb.Obj("result")?.Str("stdout") == "done\n",
            "adb did not run for the seat's serial: " + adb.ToJsonString());
        foreach (var args in new[] { new[] { "kill-server" }, new[] { "-s", "emulator-5554", "shell" }, new[] { "devices" }, new[] { "connect", "10.0.0.2" }, Array.Empty<string>() })
            Require(SeatEmulators.AdbProblem(args) is not null, $"adb {string.Join(' ', args)} was allowed");

        // A screenshot is the device's own PNG, scaled and re-encoded like a seat screenshot.
        using (var picture = new Bitmap(1280, 2856))
        using (var png = new MemoryStream())
        {
            picture.Save(png, ImageFormat.Png);
            byte[] bytes = png.ToArray();
            machine.Adb = args => args.Contains("screencap") ? new CommandResult(0, bytes, "", false) : null;
            var shot = await seat.ScreenshotAsync(new JsonObject { ["serial"] = "emulator-5556", ["maxWidth"] = 540 }, none);
            var image = shot.Obj("result")?.Obj("screenshot");
            Require(shot.Bool("ok") == true && image?.Int("width") == 540 && image.Int("sourceWidth") == 1280 && image.Int("sourceHeight") == 2856
                && shot.Obj("result")!.Str("summary")!.Contains("(captured at 1280x2856)"), "the device screenshot lost its size: " + shot.ToJsonString());
            machine.Adb = args => args.Contains("screencap") ? new CommandResult(1, Array.Empty<byte>(), "error: closed", false) : null;
            Require((await seat.ScreenshotAsync(new JsonObject { ["serial"] = "emulator-5556" }, none)).Str("error")!.Contains("returned no screenshot"),
                "a failed capture was not reported");
            machine.Adb = null;
        }

        // Stop asks the emulator to shut down, and ends it only if it does not.
        var stopped = await seat.StopAsync(new JsonObject { ["serial"] = "emulator-5556" }, none);
        Require(stopped.Obj("result")?.Str("method") == "emu kill" && machine.Killed.Count == 0, "stop did not shut the emulator down gently");
        machine.Running.Add(new RunningEmulator("emulator-5558", 5558, "Pixel_9_Pro", 902, 3));
        machine.EmuKillWorks = false;
        var ended = await seat.StopAsync(new JsonObject { ["serial"] = "emulator-5558" }, none);
        Require(ended.Obj("result")?.Str("method") == "terminated" && machine.Killed.Contains(902), "an emulator that ignored emu kill was left running");

        // An emulator that exits while starting says why.
        var broken = new StandIn { LauncherExits = true };
        var failed = await new SeatEmulators(3, Path.Combine(fixture.Root, "logs"), broken.Machine(fixture)).StartAsync(Start("Pixel_9_Pro"), none);
        Require(failed.Bool("ok") == false && failed.Str("error")!.Contains("PANIC: Missing emulator engine program"), "a failed start hid the emulator's own message");

        // A boot that outlasts the wait returns the serial, not booted, within the caller's deadline.
        var slow = new StandIn { BootPolls = int.MaxValue };
        var waiting = await new SeatEmulators(3, Path.Combine(fixture.Root, "logs"), slow.Machine(fixture))
            .StartAsync(Start("Pixel_9_Pro", new JsonObject { ["waitSeconds"] = 30, ["coldBoot"] = true, ["audio"] = true }), none);
        Require(waiting.Bool("ok") == true && waiting.Obj("result")?.Bool("booted") == false && slow.Now is >= 30_000 and < 34_000
            && slow.StartedArgs == "-avd Pixel_9_Pro -port 5554 -no-boot-anim -read-only -no-snapshot-load", "a slow boot was not bounded by waitSeconds: " + slow.StartedArgs);
        return "free ports, read-only starts, boot waits, seat-only stop/screenshot/adb, refused server commands and failed starts, all with stand-ins";
    }

    public static string Validation()
    {
        using var fixture = new Fixture();
        (string Tool, JsonObject Arguments)[] accepted =
        {
            ("android_status", new()), ("android_emulator", new() { ["action"] = "start", ["avd"] = "Pixel_9_Pro" }),
            ("android_emulator", new() { ["action"] = "start", ["avd"] = "Pixel_3a_API_34_extension_level_7_x86_64", ["gpu"] = "software", ["readOnly"] = false, ["coldBoot"] = true, ["waitSeconds"] = 0 }),
            ("android_emulator", new() { ["action"] = "stop", ["serial"] = "emulator-5554" }),
            ("android_emulator", new() { ["action"] = "screenshot", ["serial"] = "emulator-5682", ["maxWidth"] = 540, ["format"] = "jpeg" }),
            ("android_emulator", new() { ["action"] = "adb", ["serial"] = "emulator-5556", ["args"] = new JsonArray("install", "-r", @"C:\work\app.apk"), ["timeoutSeconds"] = 150 }),
            ("android_studio", new()), ("android_studio", new() { ["project"] = fixture.Root }),
            ("seat_browser", new()), ("seat_browser", new() { ["url"] = "https://play.google.com/console", ["browser"] = "edge" }),
            ("seat_browser", new() { ["url"] = "about:blank" })
        };
        foreach (var (tool, args) in accepted)
            Require(Tools.ValidateArguments(tool, args) is null, $"{tool} {args.ToJsonString()} was refused: {Tools.ValidateArguments(tool, args)}");
        (string Tool, JsonObject Arguments)[] rejected =
        {
            ("android_status", new() { ["avd"] = "x" }), ("android_emulator", new()), ("android_emulator", new() { ["action"] = "start" }),
            ("android_emulator", new() { ["action"] = "start", ["avd"] = "bad name" }), ("android_emulator", new() { ["action"] = "start", ["avd"] = "A", ["serial"] = "emulator-5554" }),
            ("android_emulator", new() { ["action"] = "stop" }), ("android_emulator", new() { ["action"] = "stop", ["serial"] = "R58M12345" }),
            ("android_emulator", new() { ["action"] = "stop", ["serial"] = "emulator-5554", ["gpu"] = "host" }),
            ("android_emulator", new() { ["action"] = "adb", ["serial"] = "emulator-5554" }),
            ("android_emulator", new() { ["action"] = "adb", ["serial"] = "emulator-5554", ["args"] = new JsonArray("kill-server") }),
            ("android_emulator", new() { ["action"] = "adb", ["serial"] = "emulator-5554", ["args"] = new JsonArray("-e", "shell") }),
            ("android_emulator", new() { ["action"] = "screenshot", ["serial"] = "emulator-5554", ["args"] = new JsonArray("x") }),
            ("android_emulator", new() { ["action"] = "start", ["avd"] = "A", ["gpu"] = "vulkan" }),
            ("android_studio", new() { ["project"] = "relative\\path" }), ("android_studio", new() { ["project"] = Path.Combine(fixture.Root, "missing") }),
            ("seat_browser", new() { ["url"] = "file:///C:/Windows/win.ini" }), ("seat_browser", new() { ["url"] = "javascript:alert(1)" }),
            ("seat_browser", new() { ["browser"] = "firefox" })
        };
        foreach (var (tool, args) in rejected)
            Require(Tools.ValidateArguments(tool, args) is not null, $"{tool} {args.ToJsonString()} was accepted");

        Require(!Core.Agents.AgentAccess.RequiresLease("android.status") && Core.Agents.AgentAccess.RequiresLease("android.emulator")
            && Core.Agents.AgentAccess.RequiresLease("android.studio") && Core.Agents.AgentAccess.RequiresLease("browser.open"),
            "only android_status may run without the desktop lease");

        var start = Cli.AndroidRequest(new[] { "start", "Pixel_9_Pro", "--gpu", "software", "--writable", "--wait", "30" });
        Require(start.Op == "android.emulator" && JsonNode.DeepEquals(start.Payload, new JsonObject
            { ["action"] = "start", ["avd"] = "Pixel_9_Pro", ["gpu"] = "software", ["readOnly"] = false, ["waitSeconds"] = 30 }), "the CLI start asked for something else");
        var adb = Cli.AndroidRequest(new[] { "adb", "emulator-5554", "--timeout", "90", "--", "shell", "am", "start", "-n", "com.example/.Main" });
        Require(JsonNode.DeepEquals(adb.Payload["args"], new JsonArray("shell", "am", "start", "-n", "com.example/.Main")) && adb.Payload.Int("timeoutSeconds") == 90,
            "arguments after -- did not reach adb unchanged");
        Require(Cli.AndroidRequest(Array.Empty<string>()).Tool == "android_status" && Cli.AndroidRequest(new[] { "shot", "emulator-5554", "a.png" }).File == "a.png"
            && Cli.AndroidRequest(new[] { "studio" }).Tool == "android_studio", "the CLI mapped a command to the wrong tool");
        return $"{accepted.Length} accepted and {rejected.Length} refused before any lease or daemon; the CLI maps to the same arguments";
    }

    public static string Guards()
    {
        int launches = 0;
        Process? Launch(ProcessStartInfo _) { launches++; return null; }
        int[] Sessions(string name) => name switch { "chrome" => new[] { 1, 3 }, "msedge" => new[] { 3 }, _ => Array.Empty<int>() };
        JsonObject Run(string path, params string[] args) => Seat.SeatHost.RunProgram(
            new JsonObject { ["path"] = path, ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()) }, 3, Launch,
            () => Array.Empty<int>(), Sessions, () => "ChromeHTML");

        foreach (var (path, args, refused, text) in new[]
                 {
                     (@"C:\Program Files\Google\Chrome\Application\chrome.exe", new[] { "https://play.google.com" }, true, "Chrome is running outside the seat (session 1)"),
                     ("chrome", Array.Empty<string>(), true, "seat_browser"),
                     ("https://play.google.com/console", Array.Empty<string>(), true, "The link would open in Chrome, the default browser, which is running outside the seat"),
                     ("chrome.exe", new[] { @"--user-data-dir=C:\Users\you\AppData\Local\AnodeChrome", "https://x.test" }, false, ""),
                     ("msedge.exe", Array.Empty<string>(), false, ""),
                     (@"C:\Program Files\Android\Android Studio1\bin\studio64.exe", new[] { @"C:\work\app" }, true, "android_studio"),
                     ("notepad.exe", Array.Empty<string>(), false, "")
                 })
        {
            var reply = Run(path, args);
            Require((reply.Bool("ok") == false) == refused && (!refused || reply.Str("error")!.Contains(text)),
                $"run {path} {string.Join(' ', args)} was {(refused ? "allowed" : "refused")}: {reply.ToJsonString()}");
        }
        Require(launches == 3, "allowed launches did not reach the process launcher");
        var firefox = Seat.SeatHost.RunProgram(new JsonObject { ["path"] = "https://example.com" }, 3, Launch, () => Array.Empty<int>(), Sessions, () => "FirefoxURL-308046B0AF4A39CB");
        Require(firefox.Bool("ok") == true && launches == 4, "a link for another default browser was refused");
        return "Chrome and Edge on their usual profile, links they would open and Android Studio are refused before launch; everything else starts";
    }

    public static string Studio()
    {
        using var fixture = new Fixture();
        ProcessStartInfo? seen = null;
        var project = Directory.CreateDirectory(Path.Combine(fixture.Root, "work", "App")).FullName;
        var opened = SeatStudio.Open(new JsonObject { ["project"] = project }, fixture.Get, info => { seen = info; return null; });
        string profile = Path.Combine(fixture.Local, "AnodeAndroidStudio");
        string properties = Path.Combine(profile, "studio.properties");
        Require(opened.Bool("ok") == true && opened.Obj("result")?.Bool("firstStart") == true && seen?.FileName.EndsWith(@"Android Studio1\bin\studio64.exe") == true
            && seen.ArgumentList.SequenceEqual(new[] { project }) && seen.Environment["STUDIO_PROPERTIES"] == properties
            && seen.Environment["ANDROID_HOME"] == Path.GetFullPath(fixture.Sdk), "Android Studio was not started on its own profile: " + opened.ToJsonString());
        string text = File.ReadAllText(properties);
        foreach (string folder in new[] { "config", "system", "plugins", "log" })
            Require(text.Contains($"idea.{folder}.path={Path.Combine(profile, folder).Replace('\\', '/')}") && Directory.Exists(Path.Combine(profile, folder)),
                $"the seat profile does not move Studio's {folder} folder");
        Require(text.Contains("disable.android.first.run=true"), "the setup wizard is not skipped");
        Require(SeatStudio.Open(new JsonObject(), fixture.Get, _ => null).Obj("result")?.Bool("firstStart") == false, "a second start still counted as the first");

        // With ANDROID_HOME set, Studio inherits it rather than getting one from Anode.
        fixture.Variables["ANDROID_HOME"] = fixture.Sdk;
        SeatStudio.Open(new JsonObject(), fixture.Get, info => { seen = info; return null; });
        Require((seen!.Environment.TryGetValue("ANDROID_HOME", out var home) ? home : null) == Environment.GetEnvironmentVariable("ANDROID_HOME"),
            "a set ANDROID_HOME was replaced");
        Require(SeatStudio.Open(new JsonObject { ["project"] = Path.Combine(fixture.Root, "missing") }, fixture.Get, _ => null).Bool("ok") == false,
            "a missing project was opened");
        fixture.Variables["ProgramFiles"] = Path.Combine(fixture.Root, "nothing");
        Require(SeatStudio.Open(new JsonObject(), fixture.Get, _ => null).Str("error")!.Contains("was not found"), "a missing Android Studio was not reported");
        return "Android Studio starts on its own config, system, plugin and log folders with the setup wizard skipped and the SDK found";
    }

    public static string Browser()
    {
        using var fixture = new Fixture();
        string chrome = Path.Combine(fixture.ProgramFiles, @"Google\Chrome\Application\chrome.exe");
        Fixture.Write(chrome, "");
        ProcessStartInfo? seen = null;
        var opened = SeatBrowser.Open(new JsonObject { ["url"] = "https://play.google.com/console" }, fixture.Get, _ => null, info => { seen = info; return null; });
        string profile = Path.Combine(fixture.Local, "AnodeChrome");
        Require(opened.Bool("ok") == true && opened.Obj("result")?.Bool("newProfile") == true && seen?.FileName == chrome
            && seen.ArgumentList.SequenceEqual(new[] { $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--new-window", "https://play.google.com/console" }),
            "Chrome was not opened on the seat profile: " + opened.ToJsonString());
        Require(opened.Obj("result")!.Str("summary")!.Contains("never enter a password"), "the result does not forbid signing in");
        Directory.CreateDirectory(Path.Combine(profile, "Default"));
        Require(SeatBrowser.Open(new JsonObject(), fixture.Get, _ => null, info => { seen = info; return null; }).Obj("result")?.Bool("newProfile") == false
            && seen!.ArgumentList.Last() == "about:blank", "a used profile was reported as new");

        string edge = Path.Combine(fixture.Root, "Edge", "msedge.exe");
        Fixture.Write(edge, "");
        var viaAppPath = SeatBrowser.Open(new JsonObject { ["browser"] = "edge" }, fixture.Get, file => file == "msedge.exe" ? edge : null, info => { seen = info; return null; });
        Require(viaAppPath.Obj("result")?.Str("profile") == Path.Combine(fixture.Local, "AnodeEdge") && seen?.FileName == edge, "Edge was not found through App Paths");
        File.Delete(chrome);
        Require(SeatBrowser.Open(new JsonObject(), fixture.Get, _ => null, _ => null).Str("error") == "Neither Chrome nor Edge was found.", "a missing browser was not reported");
        return "Chrome and Edge open on Anode's persistent seat profiles, with first-run pages off and a new profile reported";
    }

    /// <summary>What this machine has, read the way the seat host reads it: folders only, nothing started.</summary>
    public static string ThisMachine()
    {
        var sdk = AndroidSdk.Find(Environment.GetEnvironmentVariable);
        var avds = AndroidSdk.Avds(AndroidSdk.AvdHome(Environment.GetEnvironmentVariable));
        var studios = AndroidSdk.Studios(SeatEmulators.StudioFolders(Environment.GetEnvironmentVariable));
        var running = SeatEmulators.Discover(SeatEmulators.RunningFolder(Environment.GetEnvironmentVariable), _ => new SeatEmulators.ProcessFact(false, null, null));
        return sdk is null ? "no Android SDK on this machine; nothing to read"
            : $"SDK with emulator {sdk.EmulatorVersion?.ToString() ?? "absent"}, {avds.Count} AVD(s), {studios.Count} Android Studio install(s); read only";
    }
}
