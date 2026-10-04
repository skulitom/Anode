using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Cli;

internal static partial class Cli
{
    /// <summary>An android command line as the tool call it makes, and where its screenshot goes.</summary>
    internal sealed record AndroidCall(string Tool, string Op, JsonObject Payload, string? File, bool Json);

    /// <summary>
    /// Reads an android command line: status, start, stop, shot, adb or studio. Pure, so argument checks run it
    /// before anything connects. Everything after -- belongs to adb.
    /// </summary>
    internal static AndroidCall AndroidRequest(string[] args)
    {
        int dashes = Array.IndexOf(args, "--");
        string[] head = dashes < 0 ? args : args[..dashes];
        string[] adb = dashes < 0 ? Array.Empty<string>() : args[(dashes + 1)..];
        string action = head.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "status";
        if (action == "screenshot") action = "shot";
        (int words, string[] options) = action switch
        {
            "status" => (1, new[] { "json" }),
            "start" => (2, new[] { "gpu=", "writable", "cold", "audio", "wait#", "json" }),
            "stop" => (2, new[] { "json" }),
            "shot" => (3, new[] { "width#", "jpeg" }),
            "adb" => (2, new[] { "timeout#", "json" }),
            "studio" => (2, new[] { "json" }),
            _ => throw new UsageException($"android takes status, start, stop, shot, adb or studio, not '{action}'.")
        };
        var found = Known("android", head, words, options);
        var flags = new Args(head);
        if (dashes >= 0 && action != "adb") throw new UsageException("Only android adb takes arguments after --.");
        string? Word(int index) => index < found.Count ? found[index] : null;
        var payload = new JsonObject();
        string tool = "android_emulator", op = "android.emulator";
        string? file = null;
        switch (action)
        {
            case "status":
                tool = "android_status";
                op = "android.status";
                break;
            case "start":
                payload["action"] = "start";
                payload["avd"] = Word(1) ?? throw new UsageException("android start requires an AVD name; anode android lists them.");
                if (flags.Value("gpu") is { } gpu) payload["gpu"] = gpu.ToLowerInvariant();
                if (flags.Flag("writable")) payload["readOnly"] = false;
                if (flags.Flag("cold")) payload["coldBoot"] = true;
                if (flags.Flag("audio")) payload["audio"] = true;
                if (flags.Int("wait") is int wait) payload["waitSeconds"] = wait;
                break;
            case "stop":
                payload["action"] = "stop";
                payload["serial"] = Word(1) ?? throw new UsageException("android stop requires an emulator serial, such as emulator-5554.");
                break;
            case "shot":
                payload["action"] = "screenshot";
                payload["serial"] = Word(1) ?? throw new UsageException("android shot requires an emulator serial, such as emulator-5554.");
                file = Word(2);
                // Full size unless asked: a file can hold what a model's image budget cannot.
                payload["maxWidth"] = flags.Int("width") ?? 8192;
                if (flags.Flag("jpeg")) payload["format"] = "jpeg";
                break;
            case "adb":
                payload["action"] = "adb";
                payload["serial"] = Word(1) ?? throw new UsageException("android adb requires an emulator serial, then -- and the adb arguments.");
                if (adb.Length == 0) throw new UsageException("Put the adb arguments after --, such as: anode android adb emulator-5554 -- shell input tap 540 1200");
                payload["args"] = new JsonArray(adb.Select(a => (JsonNode)a).ToArray());
                if (flags.Int("timeout") is int seconds) payload["timeoutSeconds"] = seconds;
                break;
            default:
                tool = "android_studio";
                op = "android.studio";
                if (Word(1) is { } project) payload["project"] = Path.GetFullPath(project);
                break;
        }
        if (Mcp.Tools.ValidateArguments(tool, payload) is { } error) throw new UsageException(CliError(error));
        return new AndroidCall(tool, op, payload, file, flags.Flag("json"));
    }

    private static async Task<int> AndroidCommand(string[] args)
    {
        var call = AndroidRequest(args);
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        var payload = (JsonObject)call.Payload.DeepClone();
        // Booting an emulator can take minutes; the daemon's usual minute would not cover it.
        if (call.Tool == "android_emulator") payload["timeoutMs"] = 175_000;
        var response = await RequestAsync(client, call.Op, payload, 180_000);
        if (response.Bool("ok") != true || response.Obj("result") is not { } result) return Report(response);
        string? action = call.Payload.Str("action");
        if (action == "screenshot" && result.Obj("screenshot") is { } picture)
        {
            string target = call.File ?? Path.Combine(Directory.GetCurrentDirectory(),
                $"{call.Payload.Str("serial")}-{DateTime.Now:yyyyMMdd-HHmmss}.{(call.Payload.Str("format") == "jpeg" ? "jpg" : "png")}");
            File.WriteAllBytes(target, Convert.FromBase64String(picture.Str("data")!));
            Console.WriteLine($"{target}  ({picture.Int("width")}x{picture.Int("height")} of {picture.Int("sourceWidth")}x{picture.Int("sourceHeight")})");
            return 0;
        }
        if (call.Json) Console.WriteLine(result.ToJsonString(Indented));
        else if (action == "adb")
        {
            // Like exec: the program's own output and exit code.
            Console.Write(result.Str("stdout"));
            if (result.Str("stderr") is { Length: > 0 } errors) Console.Error.Write(errors);
            if (result.Bool("timedOut") == true) Console.Error.WriteLine("anode: adb timed out and was ended; it may have partly run.");
        }
        else Console.WriteLine(result.Str("summary"));
        if (action != "adb") return 0;
        return result.Bool("timedOut") == true ? 124 : result.Int("exitCode") ?? 1;
    }

    private static async Task<int> BrowserCommand(string[] args)
    {
        var options = new Args(args);
        if (options.Flag("sign-in")) return SignInHere(options.Flag("edge") ? "edge" : null, SignInAddresses(args));
        var payload = BrowserRequest(args);
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        var response = await RequestAsync(client, "browser.open", payload);
        if (response.Bool("ok") != true || response.Obj("result") is not { } result) return Report(response);
        Console.WriteLine(options.Flag("json") ? result.ToJsonString(Indented) : result.Str("summary"));
        return 0;
    }

    /// <summary>
    /// Opens the seat's browser profile on the user's own desktop, where signing in is easier than through the viewer.
    /// A terminal inside a packaged app, such as an agent's desktop app, would keep the profile's new files private to
    /// that app, where the seat never sees them, so that is refused.
    /// </summary>
    private static int SignInHere(string? choice, IReadOnlyList<string> addresses)
    {
        var browser = Core.Browser.SeatBrowser.Find(choice, Environment.GetEnvironmentVariable, Core.Browser.SeatBrowser.AppPath);
        if (browser is null)
        {
            Console.Error.WriteLine(choice == "edge" ? "Edge was not found." : "Neither Chrome nor Edge was found.");
            return 1;
        }
        string actual = Core.Util.Env.ResolveDirectory(browser.ProfileFolder);
        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(browser.ProfileFolder), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"This terminal runs inside a packaged app, which stores new files for {browser.ProfileFolder} in {actual}, "
                + "where the seat cannot see them. Run the same command from your own terminal, such as Windows Terminal opened from the Start menu.");
            return 1;
        }
        int[] elsewhere = Core.Browser.SeatBrowser.Sessions(browser.Process).Where(session => session != Process.GetCurrentProcess().SessionId).ToArray();
        Core.Browser.SeatBrowser.OpenForSignIn(browser, addresses, Core.Util.Env.StateDirectory, Process.Start);
        Console.WriteLine($"{browser.Name} opened on this desktop with Anode's seat profile ({browser.ProfileFolder}). Sign in to the sites agents "
            + $"should use, such as Play Console, then close that {browser.Name} window: the seat can use the profile only once it is closed here.");
        if (elsewhere.Length > 0)
            Console.WriteLine($"{browser.Name} is also running in session {string.Join(", ", elsewhere)}. If that is the seat's browser on this profile, "
                + "this window cannot open it; close the seat's browser first.");
        return 0;
    }

    /// <summary>The seat_browser arguments a browser command line asks for; its options were checked before dispatch.</summary>
    internal static JsonObject BrowserRequest(string[] args)
    {
        var payload = new JsonObject();
        var words = Words("browser", args);
        if (words.Count > 1) throw new UsageException("browser opens one address in the seat; only --sign-in takes several.");
        if (words.FirstOrDefault() is { } url) payload["url"] = url;
        if (new Args(args).Flag("edge")) payload["browser"] = "edge";
        if (Mcp.Tools.ValidateArguments("seat_browser", payload) is { } error) throw new UsageException(CliError(error));
        return payload;
    }

    /// <summary>
    /// The addresses browser --sign-in opens on this desktop, one tab each, since a first sitting usually covers several
    /// sites. Each is checked as seat_browser checks its url, before anything starts.
    /// </summary>
    internal static string[] SignInAddresses(string[] args)
    {
        var addresses = Words("browser", args);
        foreach (string address in addresses)
            if (Mcp.Tools.ValidateArguments("seat_browser", new JsonObject { ["url"] = address }) is { } error)
                throw new UsageException($"{CliError(error)} Check '{address}'.");
        if (addresses.Sum(address => address.Length + 1) > 30000) throw new UsageException("The addresses exceed the Windows command-line limit.");
        return addresses.ToArray();
    }
}
