using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Core.Util;

namespace Anode.Cli;

/// <summary>Regression checks using private test pipes and an MCP subprocess; no seat required.</summary>
internal static class TransportChecks
{
    private static string PipeName() => $"anode-selftest-{Guid.NewGuid():N}";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    public static async Task<string> QueueDeadline()
    {
        var entered = Signal();
        var release = Signal();
        int handled = 0;
        string name = PipeName();
        using var server = new JsonPipeServer(name, async request =>
        {
            Interlocked.Increment(ref handled);
            if (request.Str("op") == "slow")
            {
                entered.TrySetResult();
                await release.Task;
            }
            return JsonLine.Ok(new JsonObject { ["echo"] = request.Str("op") });
        });
        server.Start();
        using var client = await JsonPipeClient.TryConnectAsync(name, 4000)
            ?? throw new InvalidOperationException("could not connect to the test pipe");
        Task<JsonObject> first = client.RequestAsync("slow", timeoutMs: 5000);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = await client.RequestAsync("queued", timeoutMs: 100).WaitAsync(TimeSpan.FromSeconds(2));
            Require(queued.Bool("ok") == false && queued.Str("error")!.Contains("timed out"), "queued request did not time out");
            Require(!first.IsCompleted && client.IsConnected, "queue timeout interrupted the active request");
        }
        finally { release.TrySetResult(); }
        Require((await first).Obj("result")?.Str("echo") == "slow", "active request lost its reply");
        var next = await client.RequestAsync("next", timeoutMs: 4000);
        Require(next.Obj("result")?.Str("echo") == "next" && handled == 2, "cancelled queued request was sent or corrupted the connection");
        return "queued requests time out without interrupting or replaying other commands";
    }

    public static async Task<string> LateReply()
    {
        string name = PipeName();
        using var server = new JsonPipeServer(name, async request =>
        {
            if (request.Str("op") == "slow") await Task.Delay(300);
            return JsonLine.Ok(new JsonObject { ["echo"] = request.Str("op") });
        });
        server.Start();
        using var client = await JsonPipeClient.TryConnectAsync(name, 4000)
            ?? throw new InvalidOperationException("could not connect to the test pipe");
        var timedOut = await client.RequestAsync("slow", timeoutMs: 100);
        Require(timedOut.Bool("ok") == false && !client.IsConnected, "timed-out connection stayed reusable");
        var next = await client.RequestAsync("next", timeoutMs: 4000);
        Require(next.Bool("ok") == false, "a late reply was accepted as the next result");
        using var fresh = await JsonPipeClient.TryConnectAsync(name, 4000)
            ?? throw new InvalidOperationException("could not reconnect to the test pipe");
        Require((await fresh.RequestAsync("fresh")).Obj("result")?.Str("echo") == "fresh", "fresh connection failed");
        return "timed-out connections close; a fresh connection gets its own reply";
    }

    public static async Task<string> ReplyId()
    {
        string name = PipeName();
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync();
            using var reader = new StreamReader(pipe, JsonLine.Utf8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, JsonLine.Utf8, 1024, leaveOpen: true) { AutoFlush = true };
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("{\"ok\":true,\"id\":-1}");
        });
        using var client = await JsonPipeClient.TryConnectAsync(name, 4000)
            ?? throw new InvalidOperationException("could not connect to the test pipe");
        var response = await client.RequestAsync("test", timeoutMs: 4000);
        await serving.WaitAsync(TimeSpan.FromSeconds(3));
        Require(response.Bool("ok") == false && !client.IsConnected, "mismatched reply id was accepted");
        return "replies must match the request id";
    }

    public static async Task<string> MalformedMessages()
    {
        string name = PipeName();
        int calls = 0;
        using var server = new JsonPipeServer(name, _ => { calls++; return Task.FromResult(JsonLine.Ok()); });
        server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(4000);
        using var reader = new StreamReader(client, JsonLine.Utf8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, JsonLine.Utf8, 1024, leaveOpen: true) { AutoFlush = true };
        foreach (string request in new[] {
            """{"op":"first","op":"second"}""",
            """{"op":"test","args":[{"name":"first","name":"second"}]}""",
            """{"op":"test","args":{"text":"\uD800"}}""" })
        {
            await writer.WriteLineAsync(request);
            string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Require(line is not null && JsonLine.Parse(line)?.Bool("ok") == false && calls == 0,
                "ambiguous JSON dispatched a command or closed the pipe");
        }
        await writer.WriteLineAsync("""{"op":"valid","id":7,"args":{"Name":1,"name":2}}""");
        var reply = JsonLine.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))!);
        Require(reply?.Bool("ok") == true && reply.Int("id") == 7 && calls == 1,
            "pipe did not recover after malformed input or rejected distinct property names");
        return "duplicate fields and invalid Unicode are refused without dispatch; the same pipe remains usable";
    }

    public static async Task<string> CloseDuringRequest()
    {
        var entered = Signal();
        var release = Signal();
        string name = PipeName();
        using var server = new JsonPipeServer(name, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return JsonLine.Ok();
        });
        server.Start();
        using var client = await JsonPipeClient.TryConnectAsync(name, 4000)
            ?? throw new InvalidOperationException("could not connect to the test pipe");
        Task<JsonObject> request = client.RequestAsync("slow", timeoutMs: 4000);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            client.Dispose();
            Require((await request.WaitAsync(TimeSpan.FromSeconds(2))).Bool("ok") == false,
                "closing an active client did not return a failure");
            Require(!client.IsConnected, "closed client reports connected");
        }
        finally { release.TrySetResult(); }
        return "closing an active connection unblocks its request without throwing";
    }

    public static string LaunchArguments()
    {
        string[] arguments = { "", "plain", "two words", "tab\tvalue", "a\"b", @"C:\a b\", "a\\\"b", @"C:\plain\" };
        string command = "anode.exe " + string.Join(' ', arguments.Select(DaemonLauncher.Quote));
        IntPtr argv = CommandLineToArgvW(command, out int count);
        if (argv == IntPtr.Zero) throw new InvalidOperationException("could not parse the Windows command line");
        try
        {
            Require(count == arguments.Length + 1, "launch argument count changed");
            for (int i = 0; i < arguments.Length; i++)
                Require(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, (i + 1) * IntPtr.Size)) == arguments[i],
                    $"launch argument {i} did not round trip");
        }
        finally { LocalFree(argv); }
        return "Windows launch preserves empty arguments, whitespace, quotes and trailing slashes";
    }

    public static async Task<string> ServerShutdown()
    {
        string name = PipeName();
        var entered = Signal();
        var release = Signal();
        using var server = new JsonPipeServer(name, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return JsonLine.Ok();
        });
        server.Start();
        using var client = await JsonPipeClient.TryConnectAsync(name, 4000)
            ?? throw new InvalidOperationException("could not connect to the test pipe");
        var pending = client.RequestAsync("blocked", timeoutMs: 10_000);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            server.Dispose();
            Require((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Bool("ok") == false,
                "server disposal left a client waiting for its blocked handler");
        }
        finally { release.TrySetResult(); }
        return "server disposal closes active clients even while a handler is blocked";
    }

    public static async Task<string> PipeAccess()
    {
        string name = PipeName();
        using var server = new JsonPipeServer(name, _ => Task.FromResult(JsonLine.Ok()));
        server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(4000);
        var security = client.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Require(rules.Any(r => r.AccessControlType == AccessControlType.Allow), "pipe has no allow rule");
        Require(rules.Where(r => r.AccessControlType == AccessControlType.Allow)
            .All(r => r.IdentityReference.Equals(identity.Owner)), "pipe grants access beyond the creating identity");
        return "pipe ACL grants access only to the creating Windows identity";
    }

    public static async Task<string> McpStdio()
    {
        var info = new ProcessStartInfo(Env.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("mcp");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start MCP");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            string[] messages =
            {
                "{",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"selftest\",\"version\":\"1\"}}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"ping\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"unknown-selftest-tool\",\"arguments\":{}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"unknown-selftest-method\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"anode_guide\",\"arguments\":{}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"prompts/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"prompts/get\",\"params\":{\"name\":\"desktop_guide\"}}"
            };
            foreach (string message in messages) await process.StandardInput.WriteLineAsync(message);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(process.ExitCode == 0, $"MCP exited with {process.ExitCode}: {await stderr}");
            var replies = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonLine.Parse(line) ?? throw new InvalidOperationException("MCP wrote non-JSON output")).ToArray();
            Require(replies.Length == 10, "unexpected MCP reply count (including notification responses)");
            Require(replies[0].Obj("error")?.Int("code") == -32700, "malformed JSON did not return a parse error");
            for (int i = 1; i < replies.Length; i++)
                Require(replies[i].Int("id") == i && replies[i].Str("jsonrpc") == "2.0", "MCP reply lost its id or version");
            Require(replies[1].Obj("result")?.Obj("serverInfo")?.Str("name") == "anode"
                && replies[1].Obj("result")?.Obj("capabilities")?.Obj("prompts") is not null, "MCP initialization failed");
            var listed = replies[2].Obj("result")?["tools"] as JsonArray;
            Require(listed is { Count: 32 } && JsonNode.DeepEquals(replies[2]["result"], replies[3]["result"]), "MCP tool discovery is incomplete or unstable");
            Require(listed!.OfType<JsonObject>().Select(t => t.Str("name")).Distinct().Count() == listed!.Count, "duplicate tool names");
            foreach (string name in new[] { "seat_lease", "seat_windows", "seat_observe", "seat_window", "seat_element", "seat_capabilities", "seat_exec", "seat_job", "seat_wait" })
                Require(listed!.OfType<JsonObject>().Any(tool => tool.Str("name") == name), $"Missing desktop tool {name}");
            Require(replies[4].Obj("result") is { Count: 0 }, "MCP ping failed");
            Require(replies[5].Obj("error")?.Int("code") == -32602, "unknown tool was not reported as a protocol error");
            Require(replies[6].Obj("error")?.Int("code") == -32601, "unknown method was not reported as an error");
            Require((replies[7].Obj("result")?["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.Str("text") == Mcp.AgentGuide.Text,
                "published MCP guide differs from the embedded skill");
            Require(replies[8].Obj("result")?["prompts"] is JsonArray { Count: 2 }
                && (replies[9].Obj("result")?["messages"]?[0]?["content"] as JsonObject)?.Str("text") == Mcp.AgentGuide.Text,
                "MCP prompts are missing or differ from the embedded skill");
            return "initialize, 32 tools, embedded guide, prompts, notifications, ping, malformed JSON and errors over stdio";
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
