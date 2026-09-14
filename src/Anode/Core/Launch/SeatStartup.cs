using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Launch;

internal static class SeatStartup
{
    internal static string? Failure(JsonObject response)
    {
        if (response.Bool("ok") != true) return response.Str("error") ?? "Lost contact with the daemon during startup.";
        var status = response.Obj("result");
        string? state = status?.Str("state");
        if (state is "error" or "logon-error" or "stopped" or "detached")
            return status?.Str("lastError") is { Length: > 0 } error ? error : $"Seat startup ended in state '{state}'. Run `anode status`.";
        return status is null ? "The daemon returned no startup status." : null;
    }

    public static async Task<JsonObject> WaitAsync(JsonPipeClient client, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(150);
        JsonObject? latest = null;
        while (watch.Elapsed < timeout)
        {
            int remaining = Math.Max(1, (int)(timeout - watch.Elapsed).TotalMilliseconds);
            var response = await client.RequestAsync("status", timeoutMs: Math.Min(5000, remaining), cancel: cancel).ConfigureAwait(false);
            if (Failure(response) is { } error) throw new InvalidOperationException(error);
            var status = response.Obj("result")!;
            latest = status;
            if (status.Str("state") == "ready") return status;
            await Task.Delay(300, cancel).ConfigureAwait(false);
        }
        throw new TimeoutException(latest?.Bool("signInPrompt") == true
            ? "Windows has not finished signing in. Complete the credential dialog in Anode, then check `anode status`."
            : "The seat did not become ready in time. Run `anode status`.");
    }
}
