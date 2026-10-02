using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Display;

namespace Anode.Cli;

internal static partial class Cli
{
    /// <summary>Show the seat's display, or change it: a size, a scale or both, or reset. Arguments were checked before dispatch.</summary>
    private static async Task<int> DisplayCommand(string[] args)
    {
        var options = new Args(args);
        var request = DisplayRequest(Words("display", args), options);
        bool json = options.Flag("json");
        if (request.Count == 0) return await ShowDisplay(json);

        string? shot = options.Value("shot") is { } file ? Path.GetFullPath(file) : null;
        if (shot is not null)
        {
            request["screenshot"] = true;
            request["maxWidth"] = DisplayMode.MaxSide;
        }
        if (Mcp.Tools.ValidateArguments("seat_display", request) is { } invalid) throw new UsageException(CliError(invalid));

        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        // A change Windows does not make live reconnects the viewer, which the daemon's usual minute may not cover.
        request["timeoutMs"] = 175_000;
        var response = await RequestAsync(client, "display.set", request, 180_000);
        if (response.Bool("ok") != true || response.Obj("result") is not { } result)
        {
            // Windows may apply something other than what was asked; say what the seat shows now.
            if (!json || response.Obj("result") is null) return Report(response);
            Console.Error.WriteLine(response.Str("error"));
            Console.WriteLine(response.Obj("result")!.ToJsonString(Indented));
            return 1;
        }
        if (shot is not null && result.Obj("screenshot") is { } picture)
        {
            File.WriteAllBytes(shot, Convert.FromBase64String(picture.Str("data")!));
            picture.Remove("data");
            picture["path"] = shot;
            result["summary"] = result.Str("summary") + " Screenshot: " + shot;
        }
        else if (shot is not null && result.Str("screenshotError") is { } error) Console.Error.WriteLine(error);
        Console.WriteLine(json ? result.ToJsonString(Indented) : result.Str("summary"));
        return 0;
    }

    /// <summary>The seat_display arguments a display command line asks for; empty when it only shows the display.</summary>
    internal static JsonObject DisplayRequest(List<string> words, Args options)
    {
        var request = new JsonObject();
        if (words.Count == 1 && words[0].Equals("reset", StringComparison.OrdinalIgnoreCase)) request["reset"] = true;
        else if (words.Count == 1 && DisplayMode.TryParseSize(words[0], out int width, out int height))
        {
            request["width"] = width;
            request["height"] = height;
        }
        if (options.Int("scale") is int scale) request["scale"] = scale;
        return request;
    }

    private static async Task<int> ShowDisplay(bool json)
    {
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        var status = (await RequestAsync(client, "status")).Obj("result");
        if (status is null) return 1;
        var current = DisplayMode.FromJson(status.Obj("seat")?.Obj("screen"));
        var startup = DisplayMode.FromJson(status.Obj("startupDisplay"));
        if (json)
        {
            Console.WriteLine(new JsonObject
            {
                ["state"] = status.Str("state"), ["display"] = current?.ToJson(), ["startup"] = startup?.ToJson()
            }.ToJsonString(Indented));
            return current is null ? 1 : 0;
        }
        if (current is not { } display)
        {
            Console.Error.WriteLine($"The seat is not ready ({status.Str("state")}), so it has no display to report. Check `anode status`.");
            return 1;
        }
        Console.WriteLine($"display   {display}" + (display.Scale == 100 ? "" : $" ({display.EffectiveWidth}x{display.EffectiveHeight} effective)"));
        Console.WriteLine($"startup   {startup?.ToString() ?? "unknown"}" + (startup == display ? "" : "; releasing the lease that changed it restores it"));
        return 0;
    }
}
