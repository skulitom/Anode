using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Util;

namespace Anode.Mcp;

/// <summary>Stdio protocol and request scheduling, independent of Windows seat operations.</summary>
internal sealed class McpSession
{
    private const string LatestProtocol = "2025-11-25";
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonObject>> _call;
    private readonly SemaphoreSlim _tools = new(1, 1);
    private readonly SemaphoreSlim _output = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    private sealed class Pending
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public bool IsStop { get; init; }
        public volatile bool SuppressResponse;
    }

    public McpSession(Func<string, JsonObject, CancellationToken, Task<JsonObject>> call) => _call = call;

    public async Task RunAsync(TextReader input, TextWriter output)
    {
        var work = new List<Task>();
        try
        {
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                work.RemoveAll(task => task.IsCompleted);
                JsonNode? node;
                try { node = JsonLine.ParseNode(line); }
                catch (JsonException)
                {
                    await WriteAsync(output, Error(null, -32700, "Parse error")).ConfigureAwait(false);
                    continue;
                }

                if (node is not JsonObject message || String(message["jsonrpc"]) != "2.0"
                    || String(message["method"]) is not { Length: > 0 } method
                    || (message.ContainsKey("id") && !ValidId(message["id"])))
                {
                    await WriteAsync(output, Error(null, -32600, "Invalid request")).ConfigureAwait(false);
                    continue;
                }

                JsonNode? id = message["id"];
                if (id is null)
                {
                    // Notifications never cause a response or dispatch a tool.
                    if (method == "notifications/cancelled" && ValidId(message.Obj("params")?["requestId"]))
                    {
                        string key = message.Obj("params")!["requestId"]!.ToJsonString();
                        if (_pending.TryGetValue(key, out var request)) Cancel(request, suppressResponse: true);
                    }
                    continue;
                }

                if (message.ContainsKey("params") && message["params"] is not JsonObject)
                {
                    await WriteAsync(output, Error(id, -32602, "params must be an object")).ConfigureAwait(false);
                    continue;
                }

                var parameters = message.Obj("params");
                JsonObject? response = null;
                switch (method)
                {
                    case "initialize":
                        if (String(parameters?["protocolVersion"]) is not { Length: > 0 } protocol
                            || parameters?.Obj("capabilities") is null || parameters.Obj("clientInfo") is null)
                        {
                            response = Error(id, -32602, "initialize requires protocolVersion, capabilities and clientInfo");
                            break;
                        }
                        string selected = protocol is "2024-11-05" or "2025-03-26" or "2025-06-18" or LatestProtocol
                            ? protocol : LatestProtocol;
                        response = Result(id, new JsonObject
                        {
                            ["protocolVersion"] = selected,
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject(), ["prompts"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "anode", ["title"] = "Anode background Windows desktop",
                                ["version"] = typeof(McpSession).Assembly.GetName().Version?.ToString(3) ?? "unknown",
                                ["description"] = AgentGuide.Description,
                                ["websiteUrl"] = Links.Repository
                            },
                            ["instructions"] = AgentGuide.Instructions
                        });
                        break;
                    case "ping":
                        response = Result(id, new JsonObject());
                        break;
                    case "tools/list":
                        response = Result(id, new JsonObject { ["tools"] = Tools.Definitions() });
                        break;
                    // Prompts are static guidance: answer them here, never behind a tool or a daemon.
                    case "prompts/list":
                        response = Result(id, new JsonObject { ["prompts"] = AgentGuide.Prompts() });
                        break;
                    case "prompts/get":
                        if (String(parameters?["name"]) is not { Length: > 0 } promptName
                            || (parameters!.ContainsKey("arguments") && parameters["arguments"] is not JsonObject))
                        {
                            response = Error(id, -32602, "prompts/get requires a prompt name and an optional arguments object");
                            break;
                        }
                        response = AgentGuide.Prompt(promptName) is { } prompt
                            ? Result(id, prompt)
                            : Error(id, -32602, $"Unknown prompt '{promptName}'. Use desktop_test or desktop_guide.");
                        break;
                    case "tools/call":
                        if (String(parameters?["name"]) is not { Length: > 0 } name
                            || (parameters!.ContainsKey("arguments") && parameters["arguments"] is not JsonObject))
                        {
                            response = Error(id, -32602, "tools/call requires a tool name and an arguments object");
                            break;
                        }
                        if (!Tools.TryResolve(name, out _, out _))
                        {
                            response = Error(id, -32602, $"Unknown tool '{name}'.");
                            break;
                        }
                        var arguments = parameters.Obj("arguments") ?? new JsonObject();
                        if (Tools.ValidateArguments(name, arguments) is { } invalid)
                        {
                            response = Result(id, TextResult(invalid, isError: true));
                            break;
                        }
                        // Static guidance must not wait for a seat operation or start a daemon.
                        if (name == "anode_guide")
                        {
                            response = Result(id, TextResult(AgentGuide.Text));
                            break;
                        }
                        if (name != "seat_stop" && _pending.Values.Any(p => p.IsStop))
                        {
                            response = Result(id, TextResult("The seat is stopping. Wait for seat_stop to finish before issuing another command.", true));
                            break;
                        }
                        var pending = new Pending { IsStop = name == "seat_stop" };
                        string requestKey = id.ToJsonString();
                        if (!_pending.TryAdd(requestKey, pending))
                        {
                            pending.Cancellation.Dispose();
                            response = Error(id, -32600, "Request id is already in progress");
                            break;
                        }
                        if (pending.IsStop)
                            foreach (var other in _pending.Values.Where(p => !p.IsStop)) Cancel(other, suppressResponse: false);
                        work.Add(RunToolAsync(output, id, requestKey, name, arguments, pending));
                        break;
                    default:
                        response = Error(id, -32601, $"Method '{method}' is not implemented.");
                        break;
                }
                if (response is not null) await WriteAsync(output, response).ConfigureAwait(false);
            }
        }
        catch (IOException) { /* client closed the transport */ }
        finally
        {
            foreach (var request in _pending.Values) Cancel(request, suppressResponse: true);
            await Task.WhenAll(work).ConfigureAwait(false);
        }
    }

    private async Task RunToolAsync(TextWriter output, JsonNode id, string key, string name, JsonObject args, Pending pending)
    {
        bool entered = false;
        try
        {
            if (!pending.IsStop)
            {
                await _tools.WaitAsync(pending.Cancellation.Token).ConfigureAwait(false);
                entered = true;
            }
            // Keep synchronous readiness checks and Task Scheduler COM calls off the
            // stdin loop, which must remain able to read Stop and cancellation.
            await Task.Yield();
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            var result = await _call(name, args, pending.Cancellation.Token).ConfigureAwait(false);
            if (!pending.SuppressResponse) await WriteAsync(output, Result(id, result)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested)
        {
            if (!pending.SuppressResponse)
            {
                try { await WriteAsync(output, Result(id, TextResult("Request cancelled by seat_stop. Actions already sent may have executed.", true))).ConfigureAwait(false); }
                catch (IOException) { }
            }
        }
        catch (IOException) { /* stdout closed */ }
        catch (Exception ex)
        {
            Log.Error($"MCP tool '{name}' failed", ex);
            if (!pending.SuppressResponse)
            {
                try { await WriteAsync(output, Result(id, TextResult(ex.Message, true))).ConfigureAwait(false); }
                catch (IOException) { }
            }
        }
        finally
        {
            if (entered) _tools.Release();
            _pending.TryRemove(key, out _);
            lock (pending) pending.Cancellation.Dispose();
        }
    }

    private static void Cancel(Pending request, bool suppressResponse)
    {
        lock (request)
        {
            request.SuppressResponse |= suppressResponse;
            try { request.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task WriteAsync(TextWriter writer, JsonObject response)
    {
        await _output.WaitAsync().ConfigureAwait(false);
        try { await writer.WriteLineAsync(JsonLine.Serialize(response)).ConfigureAwait(false); }
        finally { _output.Release(); }
    }

    private static string? String(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static bool ValidId(JsonNode? id) => id is JsonValue value
        && (value.TryGetValue<string>(out _) || value.TryGetValue<long>(out _));

    internal static JsonObject TextResult(string text, bool isError = false)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (isError) result["isError"] = true;
        return result;
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
