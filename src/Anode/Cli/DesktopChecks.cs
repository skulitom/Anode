using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Desktop;
using Anode.Mcp;

namespace Anode.Cli;

internal static class DesktopChecks
{
    private static void Require(bool test, string message) { if (!test) throw new InvalidOperationException(message); }
    private static void Rejected(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; }
        throw new InvalidOperationException("A stale or unknown reference was accepted.");
    }

    public static string References()
    {
        DateTime now = DateTime.UtcNow;
        var references = new DesktopReferences(() => now);
        var first = new WindowTarget(100, 50, 123);
        var reused = new WindowTarget(100, 51, 456);
        string one = references.RememberWindow(first), two = references.RememberWindow(reused);
        Require(one != two && references.Window(one) == first && references.Window(two) == reused, "Reused window handles shared an identity.");
        var elements = new JsonArray(new JsonObject { ["id"] = "e0", ["name"] = "Save" });
        string snapshot = references.RememberObservation(first, elements);
        Require(references.TakeElement(snapshot, "e0").Window == first, "Observation lost its window identity.");
        Rejected(() => references.TakeElement(snapshot, "e0"));
        snapshot = references.RememberObservation(first, elements);
        Rejected(() => references.TakeElement(snapshot, "e1"));
        Rejected(() => references.TakeElement(snapshot, "e0"));
        snapshot = references.RememberObservation(first, elements);
        now = now.AddSeconds(91);
        Rejected(() => references.TakeElement(snapshot, "e0"));
        now = now.AddMinutes(11);
        Rejected(() => references.Window(one));
        Require(Tools.ValidateArguments("seat_observe", new JsonObject { ["windowId"] = one, ["maxElements"] = 10000 }) is not null, "Unbounded inspection was accepted.");
        Require(Tools.ValidateArguments("seat_window", new JsonObject { ["windowId"] = one, ["action"] = "move" }) is not null, "Incomplete window move was accepted.");
        Require(Tools.ValidateArguments("seat_element", new JsonObject { ["snapshotId"] = "s", ["elementId"] = "e0", ["action"] = "invoke", ["value"] = "bad" }) is not null, "Irrelevant action arguments were accepted.");
        Require(Tools.ValidateArguments("seat_element", new JsonObject { ["snapshotId"] = "s", ["elementId"] = "e0", ["action"] = "set_value", ["value"] = "" }) is null, "Clearing a value was rejected.");
        return "window reuse, expired/consumed references, observation limits and action arguments checked without desktop access";
    }

    public static async Task<string> Dispatch()
    {
        int actions = 0, workers = 0;
        var desktop = new DesktopTools((request, _) =>
        {
            workers++;
            return request.Str("op") switch
            {
                "windows" => Task.FromResult(new JsonObject { ["windows"] = new JsonArray(new JsonObject
                    { ["target"] = new WindowTarget(100, 50, 123).ToJson(), ["pid"] = 50, ["process"] = "fixture", ["title"] = "Fixture" }) }),
                "observe" => Task.FromResult(new JsonObject { ["elements"] = new JsonArray(new JsonObject
                    { ["id"] = "e0", ["name"] = "Save", ["path"] = new JsonArray(0), ["runtimeId"] = new JsonArray(1, 2), ["processId"] = 50 }) }),
                "element" => FailedAction(),
                _ => throw new InvalidOperationException("Unexpected worker call")
            };
            Task<JsonObject> FailedAction() { actions++; throw new TimeoutException("Action outcome unknown"); }
        });
        var invalid = await desktop.HandleAsync("desktop.observe", new JsonObject { ["windowId"] = "unknown", ["maxDepth"] = -1 });
        Require(invalid.Bool("ok") == false && workers == 0, "Invalid inspection started a worker.");
        var windows = (await desktop.HandleAsync("desktop.windows", new())).Obj("result")!["windows"]!.AsArray();
        string id = windows[0]!["windowId"]!.GetValue<string>();
        Require(windows[0]!["target"] is null, "Private HWND metadata escaped into the public result.");
        var observation = (await desktop.HandleAsync("desktop.observe", new() { ["windowId"] = id, ["includeScreenshot"] = false })).Obj("result")!;
        Require(observation["elements"]![0]!["path"] is null && observation["elements"]![0]!["runtimeId"] is null, "Private UIA references escaped into the public result.");
        var action = new JsonObject { ["snapshotId"] = observation.Str("snapshotId"), ["elementId"] = "e0", ["action"] = "invoke" };
        try { await desktop.HandleAsync("desktop.element", action); } catch (TimeoutException) { }
        try { await desktop.HandleAsync("desktop.element", action); } catch (InvalidOperationException) { }
        Require(actions == 1, "Timed-out input was replayed.");
        return "validation precedes workers, private references stay private, timed-out actions consume their observation";
    }

    public static async Task<string> WorkerTimeout()
    {
        var info = new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::In.ReadLine() | Out-Null; Start-Sleep -Seconds 30" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start disposable timeout fixture.");
        var watch = Stopwatch.StartNew();
        try
        {
            await DesktopWorker.ExchangeAsync(process, new JsonObject { ["op"] = "element" }, TimeSpan.FromMilliseconds(250), default);
            throw new InvalidOperationException("Hung worker did not time out.");
        }
        catch (TimeoutException error) { Require(error.Message.Contains("may already"), "An uncertain action was reported as definitely unapplied."); }
        Require(process.WaitForExit(2000) && watch.Elapsed < TimeSpan.FromSeconds(4), "Worker was left running after its deadline.");
        return "hung subprocess terminated within deadline; uncertain action reported without replay";
    }

    public static string Presentation()
    {
        var parsed = DesktopWorker.ParseResult("{\"ok\":true,\"result\":{\"windows\":[]}}");
        Require(JsonLine.Ok(parsed).Obj("result") is not null, "Worker result could not be wrapped in the seat response.");
        var result = new JsonObject
        {
            ["window"] = new JsonObject { ["title"] = "<script>alert('title')</script>" },
            ["elements"] = new JsonArray(new JsonObject { ["id"] = "e0", ["role"] = "Edit", ["name"] = "<img onerror=bad>", ["text"] = "</script><script>bad()</script>", ["actions"] = new JsonArray("set_value") })
        };
        string html = DesktopPresentation.Html(result);
        Require(!html.Contains("<img onerror=bad>") && !html.Contains("<script>bad()") && html.Contains("&lt;img"), "Report failed to escape untrusted app text.");
        result["window"]!["title"] = "Unsafe\u001b[2J title";
        result["elements"]![0]!["toggleState"] = "On";
        string summary = DesktopPresentation.Summary(result);
        Require(!summary.Contains('\u001b') && summary.Contains("toggle: On"), "Summary lost control state or exposed terminal escape codes.");
        return "HTML report escapes titles, control names and text; report remains standalone";
    }
}
