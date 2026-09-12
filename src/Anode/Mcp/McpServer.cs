using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
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
internal static class McpServer
{
    private const string ServerName = "anode";
    private const string ServerVersion = "0.1.0";
    private const string DefaultProtocol = "2024-11-05";

    private static JsonPipeClient? _daemon;
    private static string? _blockedReason;
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);

    public static async Task<int> Run()
    {
        Log.SetRole("mcp");
        Log.Info("MCP server starting");

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        while (true)
        {
            string? line;
            try { line = await stdin.ReadLineAsync().ConfigureAwait(false); }
            catch (IOException) { break; }
            if (line is null) break;
            if (line.Trim().Length == 0) continue;

            JsonObject? message = JsonLine.Parse(line);
            if (message is null)
            {
                await WriteAsync(stdout, Error(null, -32700, "Parse error")).ConfigureAwait(false);
                continue;
            }

            JsonObject? response;
            try
            {
                response = await HandleAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("MCP dispatch failed", ex);
                response = Error(message["id"], -32603, $"{ex.GetType().Name}: {ex.Message}");
            }

            if (response is not null) await WriteAsync(stdout, response).ConfigureAwait(false);
        }

        Log.Info("MCP server stopped");
        _daemon?.Dispose();
        return 0;
    }

    private static async Task WriteAsync(TextWriter writer, JsonObject message) =>
        await writer.WriteLineAsync(message.ToJsonString(new JsonSerializerOptions { WriteIndented = false })).ConfigureAwait(false);

    private static async Task<JsonObject?> HandleAsync(JsonObject message)
    {
        string method = message.Str("method") ?? string.Empty;
        JsonNode? id = message["id"];

        switch (method)
        {
            case "initialize":
            {
                string protocol = message.Obj("params")?.Str("protocolVersion") ?? DefaultProtocol;
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
                    ["instructions"] =
                        "Anode gives you a seat: a second Windows session on this machine with its own screen, "
                        + "mouse pointer, keyboard focus and programs. Your input never reaches the user's own desktop. "
                        + "Start with seat_status, take a seat_screenshot to see the seat, and use seat_run or "
                        + "steam_launch to start something in it. The user can watch the seat and can stop it at any "
                        + "moment, which closes everything running in it."
                });
            }

            case "notifications/initialized":
            case "notifications/cancelled":
                return null;

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = Tools.Definitions() });

            case "tools/call":
            {
                var parameters = message.Obj("params");
                string name = parameters?.Str("name") ?? string.Empty;
                var arguments = parameters?.Obj("arguments") ?? new JsonObject();
                return Result(id, await CallAsync(name, arguments).ConfigureAwait(false));
            }

            default:
                return id is null ? null : Error(id, -32601, $"Method '{method}' is not implemented.");
        }
    }

    private static async Task<JsonObject> CallAsync(string toolName, JsonObject arguments)
    {
        if (!Tools.TryResolve(toolName, out string op, out bool startsDaemon))
            return TextResult($"Unknown tool '{toolName}'.", isError: true);

        var client = await DaemonAsync(startsDaemon).ConfigureAwait(false);
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
        var response = await client.RequestAsync(op, arguments, timeout).ConfigureAwait(false);

        if (response.Bool("ok") != true)
            return TextResult(response.Str("error") ?? "The request failed.", isError: true);

        var result = response.Obj("result");

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

    private static async Task<JsonPipeClient?> DaemonAsync(bool mayStart)
    {
        if (_daemon is { IsConnected: true }) return _daemon;

        await ConnectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_daemon is { IsConnected: true }) return _daemon;
            _daemon?.Dispose();
            _daemon = await JsonPipeClient.TryConnectAsync(Env.ControlPipe, 1000).ConfigureAwait(false);
            if (_daemon is not null || !mayStart) return _daemon;

            if (Core.Session.Preconditions.BlockingSummary() is { } blocked)
            {
                Log.Warn($"not starting a daemon: {blocked}");
                _blockedReason = blocked;
                return null;
            }

            Log.Info("no daemon; starting one");
            var info = new ProcessStartInfo
            {
                FileName = Env.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            info.ArgumentList.Add("up");
            Process.Start(info);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            while (DateTime.UtcNow < deadline && _daemon is null)
            {
                await Task.Delay(400).ConfigureAwait(false);
                _daemon = await JsonPipeClient.TryConnectAsync(Env.ControlPipe, 500).ConfigureAwait(false);
            }

            if (_daemon is null) return null;

            // Let the seat finish signing in, so the agent's first real call does not
            // land on a session that is still starting Explorer.
            var ready = DateTime.UtcNow + TimeSpan.FromSeconds(150);
            while (DateTime.UtcNow < ready)
            {
                var status = await _daemon.RequestAsync("status").ConfigureAwait(false);
                string state = status.Obj("result")?.Str("state") ?? "?";
                if (state is "ready" or "error" or "logon-error") break;
                await Task.Delay(500).ConfigureAwait(false);
            }

            return _daemon;
        }
        finally
        {
            ConnectGate.Release();
        }
    }

    private static JsonObject TextResult(string text, bool isError = false)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
        };
        if (isError) result["isError"] = true;
        return result;
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
