using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Capture;

namespace Anode.Core.Desktop;

internal sealed class DesktopTools
{
    private readonly DesktopReferences _references = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<JsonObject, CancellationToken, Task<JsonObject>> _worker;
    public DesktopTools(Func<JsonObject, CancellationToken, Task<JsonObject>>? worker = null) => _worker = worker ?? DesktopWorker.CallAsync;
    public void InvalidateObservations() => _references.InvalidateObservations();

    public static string? ToolName(string op) => op switch
    {
        "desktop.windows" => "seat_windows", "desktop.observe" => "seat_observe",
        "desktop.window" => "seat_window", "desktop.element" => "seat_element",
        "desktop.capabilities" => "seat_capabilities", "desktop.wait" => "seat_wait", _ => null
    };

    public async Task<JsonObject> HandleAsync(string op, JsonObject arguments, CancellationToken cancel = default)
    {
        var args = (JsonObject)arguments.DeepClone();
        args.Remove("op"); args.Remove("id"); args.Remove("timeoutMs");
        string tool = ToolName(op) ?? throw new ArgumentException("Unknown desktop operation.");
        if (Mcp.Tools.ValidateArguments(tool, args) is { } error) return JsonLine.Fail(error);
        if (op == "desktop.wait") return await DesktopWait.RunAsync(args, (r, token) => HandleAsync("desktop.observe", r, token), cancel).ConfigureAwait(false);
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            JsonObject result;
            if (op == "desktop.capabilities")
            {
                args["op"] = "capabilities";
                return JsonLine.Ok(await _worker(args, cancel).ConfigureAwait(false));
            }
            if (op == "desktop.windows")
            {
                result = await _worker(new JsonObject { ["op"] = "windows" }, cancel).ConfigureAwait(false);
                var windows = new JsonArray();
                foreach (var window in result["windows"]!.AsArray().OfType<JsonObject>())
                {
                    var target = WindowTarget.FromJson(window.Obj("target")!);
                    if (args.Int("pid") is int pid && target.Pid != pid) continue;
                    if (args.Str("query") is { } query && !(window.Str("title") ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !(window.Str("process") ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    var item = (JsonObject)window.DeepClone();
                    item.Remove("target");
                    item["windowId"] = _references.RememberWindow(target);
                    windows.Add(item);
                }
                result["windows"] = windows;
            }
            else if (op == "desktop.element")
            {
                var (window, element) = _references.TakeElement(args.Str("snapshotId")!, args.Str("elementId")!);
                // All observations are invalid after an attempted action, including a timeout.
                _references.InvalidateObservations();
                args["target"] = window.ToJson();
                args["element"] = element;
                args["op"] = "element";
                result = await _worker(args, cancel).ConfigureAwait(false);
            }
            else
            {
                string windowId = args.Str("windowId")!;
                var window = _references.Window(windowId);
                args["target"] = window.ToJson();
                args["op"] = op == "desktop.observe" ? "observe" : "window";
                if (op == "desktop.window") _references.InvalidateObservations();
                result = await _worker(args, cancel).ConfigureAwait(false);
                if (op == "desktop.observe")
                {
                    result["windowId"] = windowId;
                    result["snapshotId"] = _references.RememberObservation(window, result["elements"]!.AsArray());
                    result["expiresInSeconds"] = 90;
                    foreach (var element in result["elements"]!.AsArray().OfType<JsonObject>())
                    {
                        element.Remove("path"); element.Remove("runtimeId"); element.Remove("processId");
                    }
                    if (args.Bool("includeScreenshot") ?? true)
                    {
                        try
                        {
                            // UIA remains useful if RDP suppresses the display. Never open the parent viewer here.
                            var shot = ScreenCapture.Capture(args.Int("maxWidth") ?? 1280, "png");
                            result["screenshot"] = new JsonObject
                            {
                                ["data"] = Convert.ToBase64String(shot.Bytes), ["mimeType"] = shot.MimeType,
                                ["width"] = shot.Width, ["height"] = shot.Height,
                                ["sourceWidth"] = shot.SourceWidth, ["sourceHeight"] = shot.SourceHeight
                            };
                        }
                        catch (Exception ex)
                        {
                            result["screenshotError"] = "Seat capture unavailable: " + ex.Message
                                + " No foreground fallback was used. Use control actions from this tree; do not guess pixel coordinates.";
                        }
                    }
                }
            }
            result["summary"] = DesktopPresentation.Summary(result);
            return JsonLine.Ok(result);
        }
        finally { _gate.Release(); }
    }
}
