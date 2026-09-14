using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Anode.Core.Bridge;
using Anode.Mcp;

namespace Anode.Cli;

internal static class McpChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Request(int id, string method, JsonNode? parameters = null) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject()
    }.ToJsonString();

    private static JsonObject Tool(string name, JsonNode? arguments) => new()
    {
        ["name"] = name, ["arguments"] = arguments
    };

    public static async Task<string> Validation()
    {
        int calls = 0;
        var session = new McpSession((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(McpSession.TextResult("unexpected action"));
        });
        string[] requests =
        {
            Request(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2099-01-01", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "selftest", ["version"] = "1" }
            }),
            "{",
            "[]",
            "{\"jsonrpc\":\"1.0\",\"id\":2,\"method\":\"ping\"}",
            Request(3, "tools/call", new JsonArray()),
            Request(4, "tools/call", Tool("seat_click", null)),
            Request(5, "tools/call", Tool("seat_click", new JsonObject { ["x"] = "bad", ["y"] = 10 })),
            Request(6, "tools/call", Tool("seat_run", new JsonObject())),
            Request(7, "tools/call", Tool("seat_click", new JsonObject { ["x"] = 10 })),
            Request(8, "tools/call", Tool("seat_move", new JsonObject())),
            Request(9, "tools/call", Tool("seat_key", new JsonObject { ["keys"] = "a", ["holdMs"] = -1 })),
            Request(10, "tools/call", Tool("seat_run", new JsonObject { ["path"] = "test", ["args"] = new JsonArray(42) })),
            Request(11, "tools/call", Tool("seat_click", new JsonObject { ["X"] = 10, ["Y"] = 10 })),
            "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"name\":\"seat_stop\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}",
            Request(12, "ping")
        };
        using var output = new StringWriter();
        await session.RunAsync(new StringReader(string.Join('\n', requests)), output);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonLine.Parse(line)!).ToArray();
        Require(calls == 0, "invalid request or notification dispatched an action");
        Require(replies.Length == 14, "invalid requests or notifications produced the wrong number of replies");
        Require(replies[0].Obj("result")?.Str("protocolVersion") == "2025-11-25", "unsupported protocol was echoed back");
        Require(replies[1].Obj("error")?.Int("code") == -32700, "malformed JSON was not a parse error");
        Require(replies[2].Obj("error")?.Int("code") == -32600 && replies[3].Obj("error")?.Int("code") == -32600,
            "invalid envelopes were accepted");
        foreach (int id in new[] { 3, 4 })
            Require(replies.Single(r => r.Int("id") == id).Obj("error")?.Int("code") == -32602, "invalid params were accepted");
        for (int id = 5; id <= 11; id++)
            Require(replies.Single(r => r.Int("id") == id).Obj("result")?.Bool("isError") == true, $"invalid arguments for request {id} were accepted");
        Require(replies[^1].Obj("result") is { Count: 0 }, "server did not recover after invalid requests");
        return "protocol negotiation, envelopes, notifications and tool arguments validated before actions";
    }

    public static async Task<string> StopAndCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int actions = 0;
        var session = new McpSession(async (name, _, cancel) =>
        {
            if (name == "seat_stop") return McpSession.TextResult("stopped");
            Interlocked.Increment(ref actions);
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancel); }
            finally { cancelled.TrySetResult(); }
            return McpSession.TextResult("finished");
        });
        using var input = new Input();
        using var output = new Output();
        Task running = session.RunAsync(input, output);
        try
        {
            input.Send(Request(1, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "first" })));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            input.Send(Request(2, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "queued" })));
            input.Send(Request(3, "ping"));
            Require((await output.Next()).Int("id") == 3, "ping waited behind an active tool");
            input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2}}");
            input.Send(Request(4, "tools/call", Tool("seat_stop", new JsonObject())));
            JsonObject reply;
            do { reply = await output.Next(); } while (reply.Int("id") != 4);
            Require(reply.Obj("result")?.Bool("isError") != true, "stop failed while another command was active");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            input.Complete();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Require(actions == 1, "a queued command was executed after cancellation or stop");
        Require(output.Lines.All(line => JsonLine.Parse(line)?.Int("id") != 2), "cancelled request received a response");
        return "ping and Stop bypass busy tools; cancelled queued commands never execute";
    }

    public static async Task<string> Disconnect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        var session = new McpSession(async (_, _, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = token.IsCancellationRequested; }
            return McpSession.TextResult("finished");
        });
        using var input = new Input();
        using var output = new Output();
        Task running = session.RunAsync(input, output);
        input.Send(Request(1, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "first" })));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        input.Complete();
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        Require(cancelled && output.Lines.IsEmpty, "disconnect failed to cancel pending work silently");
        return "closing stdin cancels outstanding requests and exits promptly";
    }

    public static async Task<string> BusyDaemon()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string pipeName = $"anode-selftest-{Guid.NewGuid():N}";
        using var daemon = new JsonPipeServer(pipeName, async request =>
        {
            if (request.Str("op") == "input.text")
            {
                entered.TrySetResult();
                await release.Task;
            }
            return JsonLine.Ok(new JsonObject { ["op"] = request.Str("op") });
        });
        daemon.Start();
        using var backend = new McpServer(pipeName);
        var session = new McpSession(backend.CallAsync);
        using var input = new Input();
        using var output = new Output();
        Task running = session.RunAsync(input, output);
        try
        {
            input.Send(Request(1, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "blocked" })));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            input.Send(Request(2, "tools/call", Tool("seat_stop", new JsonObject())));
            JsonObject reply;
            do { reply = await output.Next(); } while (reply.Int("id") != 2);
            Require(reply.Obj("result")?.Bool("isError") != true, "stop could not reach a busy daemon");
            Require(!release.Task.IsCompleted, "stop waited for the blocked daemon handler");
            var recovered = await backend.CallAsync("seat_show", new JsonObject(), CancellationToken.None);
            Require(recovered.Bool("isError") != true, "MCP did not reconnect after its previous request was cancelled");
        }
        finally
        {
            release.TrySetResult();
            input.Complete();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        return "Stop reaches a busy daemon on a separate pipe; subsequent tools reconnect";
    }

    public static async Task<string> OrderedCommands()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();
        var session = new McpSession(async (_, args, token) =>
        {
            string text = args.Str("text")!;
            seen.Enqueue(text);
            if (text == "first") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return McpSession.TextResult(text);
        });
        using var input = new Input();
        using var output = new Output();
        Task running = session.RunAsync(input, output);
        try
        {
            input.Send(Request(1, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "first" })));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            input.Send(Request(2, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "second" })));
            input.Send(Request(3, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "third" })));
            input.Send(Request(4, "ping"));
            Require((await output.Next()).Int("id") == 4 && seen.Count == 1, "normal tools ran concurrently");
            release.TrySetResult();
            for (int id = 1; id <= 3; id++) Require((await output.Next()).Int("id") == id, "tool results lost their order");
        }
        finally
        {
            release.TrySetResult();
            input.Complete();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Require(seen.SequenceEqual(new[] { "first", "second", "third" }), "input commands executed out of order");
        return "normal tools preserve arrival order while protocol messages stay responsive";
    }

    private sealed class Input : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public void Send(string line) => _lines.Writer.TryWrite(line);
        public void Complete() => _lines.Writer.TryComplete();
        public override async Task<string?> ReadLineAsync()
        {
            try { return await _lines.Reader.ReadAsync(); }
            catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class Output : StringWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public ConcurrentQueue<string> Lines { get; } = new();
        public override Task WriteLineAsync(string? value)
        {
            Lines.Enqueue(value!);
            _lines.Writer.TryWrite(value!);
            return Task.CompletedTask;
        }
        public async Task<JsonObject> Next() => JsonLine.Parse(await _lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)))!;
    }
}
