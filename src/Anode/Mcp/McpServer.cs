using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Core.Util;

namespace Anode.Mcp;

/// <summary>
/// A Model Context Protocol server over stdin and stdout, so an agent can hold a seat
/// the way it holds any other tool.
///
/// It owns nothing. Every tool call is forwarded to the running daemon over the control
/// pipe, and the daemon is started on the first call if it is not up yet. That means an
/// agent and a person can both be pointed at the same seat, and the person's stop button
/// still wins.
/// </summary>
internal sealed class McpServer : IDisposable
{
    private JsonPipeClient? _daemon;
    private string? _blockedReason;
    private bool _launchPending;
    private readonly string _controlPipe;
    private readonly SemaphoreSlim ConnectGate = new(1, 1);

    internal McpServer(string controlPipe = Env.ControlPipe) => _controlPipe = controlPipe;

    public static async Task<int> Run()
    {
        Log.SetRole("mcp");
        Log.Info("MCP server starting");
        using var server = new McpServer();
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try { await new McpSession(server.CallAsync).RunAsync(stdin, stdout).ConfigureAwait(false); }
        finally
        {
            Log.Info("MCP server stopped");
        }
        return 0;
    }

    internal async Task<JsonObject> CallAsync(string toolName, JsonObject arguments, CancellationToken cancel)
    {
        if (!Tools.TryResolve(toolName, out string op, out bool startsDaemon))
            return TextResult($"Unknown tool '{toolName}'.", isError: true);

        if (toolName == "seat_stop") return await StopAsync(arguments, cancel).ConfigureAwait(false);
        var client = await DaemonAsync(startsDaemon, cancel).ConfigureAwait(false);
        if (client is null)
        {
            string reason = _blockedReason is not null
                ? _blockedReason + " Tell the user to run `anode setup` in a terminal and approve the administrator prompt; "
                  + "this cannot be done from here."
                : startsDaemon
                    ? "Anode is not running and could not be started. Run `anode doctor` in a terminal to see why."
                    : $"Anode is not running, so there is no seat for '{toolName}' to act on. Call seat_start first.";
            return TextResult(reason, isError: true);
        }

        int timeout = toolName is "steam_launch" or "seat_start" ? 180_000 : 60_000;
        var response = await client.RequestAsync(op, arguments, timeout, cancel).ConfigureAwait(false);

        if (response.Bool("ok") != true)
            return TextResult(response.Str("error") ?? "The request failed.", isError: true);

        var result = response.Obj("result");

        if (toolName is "seat_windows" or "seat_observe" or "seat_window" or "seat_element" && result is not null)
        {
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Str("summary") ?? Core.Desktop.DesktopPresentation.Summary(result) });
            var structured = (JsonObject)result.DeepClone();
            if (structured.Obj("screenshot") is { } screenshot)
            {
                if (screenshot.Str("data") is { } image)
                    content.Add(new JsonObject { ["type"] = "image", ["data"] = image, ["mimeType"] = screenshot.Str("mimeType") ?? "image/png" });
                screenshot.Remove("data");
            }
            return new JsonObject { ["content"] = content, ["structuredContent"] = structured };
        }

        // A screenshot comes back as an image block so the model can actually look at it.
        if (toolName == "seat_screenshot" && result?.Str("data") is { } base64)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = base64,
                        ["mimeType"] = result.Str("mimeType") ?? "image/png"
                    },
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Seat screen, {result.Int("width")}x{result.Int("height")} "
                                 + $"(captured at {result.Int("sourceWidth")}x{result.Int("sourceHeight")}). "
                                 + "Click coordinates use the captured size, not the scaled image."
                    })
            };
        }

        string text = result is null || result.Count == 0
            ? "ok"
            : result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return TextResult(text);
    }

    private async Task<JsonPipeClient?> DaemonAsync(bool mayStart, CancellationToken cancel)
    {
        if (_daemon is { IsConnected: true }) return _daemon;

        await ConnectGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_daemon is { IsConnected: true }) return _daemon;
            _daemon?.Dispose();
            _daemon = await JsonPipeClient.TryConnectAsync(_controlPipe, 1000, cancel).ConfigureAwait(false);
            if (_daemon is not null || !mayStart) return _daemon;

            if (Core.Session.Preconditions.BlockingSummary() is { } blocked)
            {
                Log.Warn($"not starting a daemon: {blocked}");
                _blockedReason = blocked;
                return null;
            }

            _blockedReason = null;
            Log.Info("no daemon; starting one");
            cancel.ThrowIfCancellationRequested();
            _launchPending = true;
            try { DaemonLauncher.Launch(new[] { "--hidden" }); }
            catch { _launchPending = false; throw; }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            while (DateTime.UtcNow < deadline && _daemon is null)
            {
                await Task.Delay(400, cancel).ConfigureAwait(false);
                _daemon = await JsonPipeClient.TryConnectAsync(_controlPipe, 500, cancel).ConfigureAwait(false);
            }

            if (_daemon is null) return null;
            _launchPending = false;

            // Let the seat finish signing in, so the agent's first real call does not
            // land on a session that is still starting Explorer.
            await SeatStartup.WaitAsync(_daemon, cancel).ConfigureAwait(false);

            return _daemon;
        }
        finally
        {
            ConnectGate.Release();
        }
    }

    private async Task<JsonObject> StopAsync(JsonObject arguments, CancellationToken cancel)
    {
        // McpSession cancels normal requests first. Wait for any scheduler hand-off
        // to finish, then use a separate connection so Stop bypasses busy pipe I/O.
        await ConnectGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            int waitMs = _launchPending ? 40_000 : 1000;
            using var client = await JsonPipeClient.TryConnectAsync(_controlPipe, waitMs, cancel).ConfigureAwait(false);
            if (client is null)
                return TextResult(_launchPending
                    ? "The daemon was launched but did not answer Stop. Run `anode status` and `anode kill` to check it."
                    : "Anode is not running; there is no seat to stop.", _launchPending);
            _launchPending = false;
            var response = await client.RequestAsync("seat.stop", arguments, 60_000, cancel).ConfigureAwait(false);
            return response.Bool("ok") == true
                ? TextResult(response.Obj("result")?.ToJsonString() ?? "ok")
                : TextResult(response.Str("error") ?? "Could not stop the seat.", true);
        }
        finally { ConnectGate.Release(); }
    }

    private static JsonObject TextResult(string text, bool isError = false) => McpSession.TextResult(text, isError);

    public void Dispose() => _daemon?.Dispose();
}
