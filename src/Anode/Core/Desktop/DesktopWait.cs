using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Desktop;

internal static class DesktopWait
{
    public static async Task<JsonObject> RunAsync(JsonObject args,
        Func<JsonObject, CancellationToken, Task<JsonObject>> observe, CancellationToken cancel)
    {
        int milliseconds = args.Int("waitMs") ?? 10000;
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // Zero requests a single observation, with the normal worker deadline.
        if (milliseconds > 0) deadline.CancelAfter(milliseconds);
        JsonObject? result = null;
        bool matched = false;
        try
        {
            do
            {
                var envelope = await observe(new JsonObject { ["windowId"] = args.Str("windowId"),
                    ["includeScreenshot"] = false, ["maxElements"] = 500, ["maxDepth"] = 20 }, deadline.Token).ConfigureAwait(false);
                if (envelope.Bool("ok") != true) return envelope;
                result = (JsonObject)envelope.Obj("result")!.DeepClone();
                matched = Matches(result, args);
                if (matched || milliseconds == 0) break;
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            } while (watch.ElapsedMilliseconds < milliseconds);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        result ??= new JsonObject();
        result["matched"] = matched;
        result["waitElapsedMs"] = watch.ElapsedMilliseconds;
        // A timed-out observation is evidence, but not a fresh actionable reference.
        if (!matched)
        {
            result.Remove("snapshotId"); result.Remove("expiresInSeconds");
            if (result["elements"] is JsonArray) result["summary"] = DesktopPresentation.Summary(result);
        }
        result["summary"] = (matched ? "Condition matched. " : "Condition did not match before the wait ended. ")
            + (result.Str("summary") ?? "No complete observation was available.");
        return JsonLine.Ok(result);
    }

    internal static bool Matches(JsonObject result, JsonObject args)
    {
        bool Select(JsonObject item)
        {
            foreach (string field in new[] { "automationId", "name", "role" })
                if (args.Str(field) is { } expected && item.Str(field) != expected) return false;
            if (args.Str("textContains") is { } text && !new[] { "name", "text" }.Any(f => (item.Str(f) ?? "").Contains(text, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }
        var selected = (result["elements"] as JsonArray ?? new()).OfType<JsonObject>().Where(Select).ToArray();
        return (args.Str("state") ?? "exists") switch
        {
            "missing" => selected.Length == 0 && result.Bool("truncated") == false
                && result["warnings"] is JsonArray { Count: 0 } && args.Str("textContains") is null,
            "enabled" => selected.Any(e => e.Bool("enabled") == true),
            "disabled" => selected.Any(e => e.Bool("enabled") == false),
            _ => selected.Length > 0
        };
    }
}
