using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Anode.Core.Bridge;
using Anode.Core.Desktop;
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

    private static string PipeName() => $"anode-selftest-{Guid.NewGuid():N}";
    internal static string Text(JsonObject result) =>
        (result["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c.Str("type") == "text")?.Str("text") ?? "";

    /// <summary>
    /// An MCP backend on a private pipe that refuses to start anything. McpServer rejects private
    /// pipes without injected startup hooks, so no check can reach Task Scheduler by omission.
    /// </summary>
    internal static McpServer Isolated(string pipe, string? agentId = null) => new(pipe,
        () => throw new InvalidOperationException("a self-test tried to launch a daemon"),
        () => "self-test: daemon startup is disabled", agentId);

    public static async Task<string> Discovery()
    {
        string surface = await Surface();
        string shape = await ResultShape();
        int launches = 0, readinessChecks = 0, daemonCalls = 0;
        string pipe = PipeName();
        bool guarded = false;
        try { new McpServer(pipe).Dispose(); }
        catch (ArgumentException) { guarded = true; }
        Require(guarded, "a private-pipe MCP backend accepted the real daemon launcher");
        // Nothing listens on this pipe until the ready phase, so a short connect wait is exact.
        using var backend = new McpServer(pipe,
            () => { launches++; throw new InvalidOperationException("Discovery attempted a daemon launch"); },
            () => { readinessChecks++; return "setup missing"; }) { ConnectTimeoutMs = 50 };
        var guide = await backend.CallAsync("anode_guide", new JsonObject(), CancellationToken.None);
        Require(guide.Bool("isError") != true && AgentGuide.Text.Length > 100, "embedded guide unavailable before setup");
        foreach (string diagnostic in new[] { "seat_status", "seat_capabilities", "seat_processes", "steam_status" })
        {
            var stopped = await backend.CallAsync(diagnostic, new JsonObject(), CancellationToken.None);
            Require(stopped.Bool("isError") != true && stopped.Obj("structuredContent") is { } state && state.Str("state") == "stopped"
                && state.Bool("daemonRunning") == false && state.ContainsKey("ownerAgentId") && state.Str("summary") == McpServer.NotRunning,
                $"no-daemon {diagnostic} must be a successful stopped observation");
        }
        var leaseStatus = await backend.CallAsync("seat_lease", new JsonObject(), CancellationToken.None);
        Require(leaseStatus.Obj("structuredContent")?.Str("state") == "stopped", "no-daemon lease status must be a stopped observation");
        foreach (string viewer in new[] { "seat_show", "seat_hide", "seat_job" })
        {
            var args = viewer == "seat_job" ? new JsonObject { ["jobId"] = "job-1" } : new JsonObject();
            Require(Text(await backend.CallAsync(viewer, args, CancellationToken.None)) == McpServer.NotRunning, $"no-daemon {viewer} did not say how to start");
        }
        Require(launches == 0 && readinessChecks == 0, "discovery or diagnostics triggered setup checks or startup");
        foreach (var (tool, args) in new[] { ("seat_start", new JsonObject()), ("seat_lease", new JsonObject { ["action"] = "acquire" }) })
        {
            var blocked = await backend.CallAsync(tool, args, CancellationToken.None);
            Require(blocked.Bool("isError") == true && Text(blocked).Contains("anode setup"), $"{tool} hid the setup blocker");
        }
        Require(launches == 0 && readinessChecks == 2, "only seat_start and lease acquisition may consider starting a seat");

        using var daemon = new JsonPipeServer(pipe, request =>
        {
            daemonCalls++;
            Require(request.Str("op") == "status", "status sent an action to the daemon");
            return Task.FromResult(JsonLine.Ok(new JsonObject { ["state"] = "ready" }));
        });
        daemon.Start();
        using var reader = Isolated(pipe);
        var ready = await reader.CallAsync("seat_status", new JsonObject(), CancellationToken.None);
        Require(ready.Bool("isError") != true && daemonCalls == 1 && launches == 0,
            "status did not read the existing daemon");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var session = new McpSession(async (_, _, token) =>
        {
            calls++;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return McpSession.TextResult("unreachable");
        });
        using var input = new Input();
        using var output = new Output();
        Task running = session.RunAsync(input, output);
        try
        {
            input.Send(Request(1, "tools/call", Tool("seat_type", new JsonObject { ["text"] = "busy" })));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            input.Send(Request(2, "tools/call", Tool("anode_guide", new JsonObject())));
            var response = await output.Next();
            Require(response.Int("id") == 2 && response.Obj("result")?.Bool("isError") != true && calls == 1,
                "guide waited behind or dispatched through an action handler");
            input.Send(Request(3, "tools/call", Tool("anode_guide", new JsonObject { ["start"] = true })));
            Require((await output.Next()).Obj("result")?.Bool("isError") == true && calls == 1,
                "guide accepted unsupported arguments");

            // Prompts are slash commands: they must answer before setup and while a tool is busy.
            input.Send(Request(4, "prompts/list"));
            var listed = await output.Next();
            Require(listed.Int("id") == 4 && (listed.Obj("result")?["prompts"] as JsonArray)?.OfType<JsonObject>().Select(p => p.Str("name"))
                .SequenceEqual(new[] { "desktop_test", "desktop_guide" }) == true, "prompts were not listed while a tool was busy");
            input.Send(Request(5, "prompts/get", new JsonObject { ["name"] = "desktop_test" }));
            var message = (await output.Next()).Obj("result")?["messages"]?[0] as JsonObject;
            Require(message?.Str("role") == "user" && message.Obj("content")?.Str("text") is { } workflow
                && workflow.Contains("seat_lease with action=acquire") && workflow.Contains("action=release"), "desktop_test prompt lost the lease workflow");
            input.Send(Request(6, "prompts/get", new JsonObject { ["name"] = "desktop_guide", ["arguments"] = new JsonObject() }));
            Require(((await output.Next()).Obj("result")?["messages"]?[0]?["content"] as JsonObject)?.Str("text") == AgentGuide.Text,
                "desktop_guide prompt differs from the embedded guide");
            input.Send(Request(7, "prompts/get", new JsonObject { ["name"] = "unknown_prompt" }));
            Require((await output.Next()).Obj("error")?.Int("code") == -32602 && calls == 1, "unknown prompt was not an invalid-params error");
        }
        finally { input.Complete(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
        return $"guide and prompts answer during busy tools; only start/acquire consider startup; {surface}; {shape}";
    }

    /// <summary>The agent-facing surface: text limits, names, titles, annotations, schemas and the skill.</summary>
    private static async Task<string> Surface()
    {
        Require(Encoding.UTF8.GetByteCount(AgentGuide.Instructions) <= 2048 && AgentGuide.Instructions.Contains("seat_lease"),
            "server instructions exceed 2 KB or omit the lease workflow");
        var definitions = Tools.Definitions().OfType<JsonObject>().ToArray();
        var texts = new List<string>
        {
            AgentGuide.Instructions, AgentGuide.Text, McpServer.NotRunning, McpServer.LeaseEnded,
            McpServer.SeatStopped, McpServer.SeatStoppedLease
        };
        foreach (var prompt in AgentGuide.Prompts().OfType<JsonObject>())
            texts.Add(AgentGuide.Prompt(prompt.Str("name")!)!["messages"]![0]!["content"]!["text"]!.GetValue<string>());
        texts.AddRange(definitions.Select(d => d.Str("description")!));
        foreach (Match token in texts.SelectMany(text => ToolToken.Matches(text)))
            Require(Tools.TryResolve(token.Value, out _, out _), $"guidance names unknown tool {token.Value}");

        string[] readOnly = { "anode_guide", "seat_status", "seat_windows", "seat_observe", "seat_screenshot", "seat_wait", "seat_capabilities", "seat_processes", "steam_status" };
        string[] additive = { "seat_start", "seat_hide" };
        string[] seatContent = { "seat_windows", "seat_observe", "seat_screenshot", "seat_wait" };
        var titles = new HashSet<string>();
        foreach (var definition in definitions)
        {
            string name = definition.Str("name")!, title = definition.Str("title") ?? "";
            Require(Encoding.UTF8.GetByteCount(definition.Str("description")!) <= 2048, $"{name} description exceeds 2 KB");
            Require(title.StartsWith("Anode: ", StringComparison.Ordinal) && title != "Anode: " + name.Replace('_', ' ') && titles.Add(title),
                $"{name} has a missing, generic or duplicate title");
            bool isReadOnly = readOnly.Contains(name), isAdditive = additive.Contains(name), isAction = !isReadOnly && !isAdditive;
            var expected = new JsonObject
            {
                ["title"] = title, ["readOnlyHint"] = isReadOnly, ["destructiveHint"] = isAction,
                ["idempotentHint"] = !isAction, ["openWorldHint"] = isAction || seatContent.Contains(name)
            };
            Require(JsonNode.DeepEquals(definition["annotations"], expected), $"{name} annotations differ from the published table");
            Tools.TryResolve(name, out _, out bool startsDaemon);
            Require(startsDaemon == (name == "seat_start"), $"{name} may start a seat; only seat_start and lease acquisition can");
            Require((definition.Obj("_meta")?.Bool("anthropic/requiresUserInteraction") == true) == (name == "seat_stop"),
                $"{name} has the wrong per-call confirmation marker");
            CheckBounds(name, definition.Obj("inputSchema")!);
        }

        var session = new McpSession((_, _, _) => throw new InvalidOperationException("initialize dispatched a tool"));
        using var output = new StringWriter();
        await session.RunAsync(new StringReader(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "selftest", ["version"] = "1" }
        })), output);
        var initialized = JsonLine.Parse(output.ToString().Trim())?.Obj("result") ?? new JsonObject();
        var info = initialized.Obj("serverInfo") ?? new JsonObject();
        Require(new[] { "name", "title", "version", "description", "websiteUrl" }.All(key => !string.IsNullOrWhiteSpace(info.Str(key)))
            && info.Str("name") == "anode" && info.Str("description") == AgentGuide.Description
            && initialized.Obj("capabilities")?.Obj("prompts") is not null && initialized.Str("instructions") == AgentGuide.Instructions,
            "initialize lost serverInfo fields, the prompts capability or the instructions");

        var problems = SkillProblems(AgentGuide.Source).ToArray();
        Require(problems.Length == 0, "SKILL.md frontmatter: " + string.Join("; ", problems));
        string openAi = Path.Combine(AppContext.BaseDirectory, "skills", "anode-desktop", "agents", "openai.yaml");
        if (File.Exists(openAi))
            Require(File.ReadLines(openAi).Any(line => line.TrimStart().StartsWith("default_prompt:", StringComparison.Ordinal) && line.Contains("$anode-desktop")),
                "agents/openai.yaml has no default_prompt that invokes $anode-desktop");
        return $"{definitions.Length} titled tools, annotation table, schema bounds, {Encoding.UTF8.GetByteCount(AgentGuide.Instructions)}-byte instructions and skill frontmatter";
    }

    private static readonly Regex ToolToken = new(@"\b(seat|anode|steam|gamepad)_[a-z]+(?:_[a-z]+)*", RegexOptions.CultureInvariant);

    /// <summary>Every integer bound is accepted at its limit and rejected one past it; every enum value passes.</summary>
    private static void CheckBounds(string tool, JsonObject schema)
    {
        foreach (var (field, node) in schema.Obj("properties") ?? new JsonObject())
        {
            if (node is not JsonObject property) continue;
            if (property["maximum"] is { } maximum)
            {
                // Parse numbers as a client sends them, so int.MaxValue + 1 arrives as an out-of-range JSON number.
                JsonNode Number(long value) => JsonNode.Parse(value.ToString(CultureInfo.InvariantCulture))!;
                long max = maximum.GetValue<int>(), min = property["minimum"]!.GetValue<int>();
                Require(Tools.ValidateArguments(tool, Sample(tool, field, Number(max))) is null
                    && Tools.ValidateArguments(tool, Sample(tool, field, Number(min))) is null, $"{tool}.{field} rejected its own limits");
                Require(Tools.ValidateArguments(tool, Sample(tool, field, Number(max + 1))) is not null
                    && Tools.ValidateArguments(tool, Sample(tool, field, Number(min - 1))) is not null, $"{tool}.{field} accepted a value outside its range");
            }
            if (property["enum"] is JsonArray values)
            {
                // Parsed like client input, so enum matching does not depend on how a value was built.
                foreach (var value in values)
                    Require(Tools.ValidateArguments(tool, Sample(tool, field, JsonNode.Parse(value!.ToJsonString())!)) is null, $"{tool}.{field} rejected published value {value}");
                Require(Tools.ValidateArguments(tool, Sample(tool, field, JsonValue.Create("not-a-choice"))) is not null, $"{tool}.{field} accepted an unpublished value");
            }
        }
    }

    /// <summary>Minimal valid arguments for a tool, with one field set and its dependent fields adjusted.</summary>
    private static JsonObject Sample(string tool, string field, JsonNode value)
    {
        var args = tool switch
        {
            "seat_lease" => new JsonObject { ["action"] = "acquire" },
            "seat_job" => new JsonObject { ["jobId"] = "job-1" },
            "seat_exec" => new JsonObject { ["path"] = "cmd.exe" },
            "seat_wait" => new JsonObject { ["windowId"] = "w1", ["name"] = "OK" },
            "seat_observe" => new JsonObject { ["windowId"] = "w1" },
            "seat_window" => new JsonObject { ["windowId"] = "w1", ["action"] = "move", ["x"] = 0, ["y"] = 0, ["width"] = 800, ["height"] = 600 },
            "seat_element" => new JsonObject { ["snapshotId"] = "s1", ["elementId"] = "e1", ["action"] = "scroll", ["direction"] = "down" },
            "seat_run" => new JsonObject { ["path"] = "notepad.exe" },
            "seat_key" => new JsonObject { ["keys"] = "enter" },
            "seat_type" => new JsonObject { ["text"] = "a" },
            "steam_launch" => new JsonObject { ["appId"] = 1 },
            "gamepad_tap" => new JsonObject { ["button"] = "a" },
            _ => new JsonObject()
        };
        args[field] = value;
        string? choice = value is JsonValue text && text.TryGetValue<string>(out var s) ? s : null;
        if (tool == "seat_job" && field == "action" && choice == "list") args = new JsonObject { ["action"] = "list" };
        if (tool == "seat_window" && field == "action" && choice != "move")
            foreach (string key in new[] { "x", "y", "width", "height" }) args.Remove(key);
        if (tool == "seat_element" && field == "action")
        {
            args.Remove("direction");
            if (choice == "scroll") args["direction"] = "down";
            if (choice == "set_value") args["value"] = "text";
            if (choice == "set_range") args["number"] = 1.5;
        }
        return args;
    }

    /// <summary>Minimal line-based Agent Skills frontmatter validator (.NET has no YAML parser).</summary>
    private static IEnumerable<string> SkillProblems(string source)
    {
        string text = source.Replace("\r\n", "\n");
        int end = text.StartsWith("---\n", StringComparison.Ordinal) ? text.IndexOf("\n---\n", 3, StringComparison.Ordinal) : -1;
        if (end < 0)
        {
            yield return "missing --- delimiters";
            yield break;
        }
        string[] allowed = { "name", "description", "license", "compatibility", "metadata", "allowed-tools" };
        var values = new Dictionary<string, string>();
        string? key = null;
        foreach (string line in (end >= 4 ? text[4..end] : "").Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line[0] is ' ' or '\t')
            {
                if (key != "metadata") yield return $"unexpected indented line under {key}";
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                yield return $"'{line}' is not a key: value line";
                continue;
            }
            key = line[..colon];
            string value = line[(colon + 1)..].Trim();
            if (!allowed.Contains(key)) yield return $"unsupported key '{key}'";
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
                if (value.Contains('"') || value.Contains('\\')) yield return $"{key} uses escapes; reword it";
            }
            else if (value.Contains(": ") || value.Contains(" #") || value.Length > 0 && "\"'[]{}>|*&!%@`#,?-".Contains(value[0]))
                yield return $"{key} must be double-quoted";
            if (!values.TryAdd(key, value)) yield return $"duplicate key '{key}'";
        }
        if (!values.TryGetValue("name", out var name) || name != "anode-desktop" || !Regex.IsMatch(name, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            yield return "name must be anode-desktop";
        if (!values.TryGetValue("description", out var description) || description.Length is 0 or > 1024)
            yield return "description must have 1-1024 characters";
        if (values.TryGetValue("compatibility", out var compatibility) && compatibility.Length is 0 or > 500)
            yield return "compatibility must have 1-500 characters";
    }

    /// <summary>What clients receive: images without structuredContent, and no duplicated summaries.</summary>
    private static async Task<string> ResultShape()
    {
        var observation = new JsonObject
        {
            ["window"] = new JsonObject { ["title"] = "Fixture" }, ["windowId"] = "w1", ["snapshotId"] = "s1",
            ["elements"] = new JsonArray(new JsonObject
            {
                ["id"] = "e0", ["role"] = "Button", ["name"] = "Save", ["automationId"] = "SaveButton",
                ["bounds"] = new JsonObject { ["x"] = 10, ["y"] = 20, ["width"] = 30, ["height"] = 40 }, ["actions"] = new JsonArray("invoke")
            }),
            ["screenshot"] = new JsonObject
            {
                ["data"] = "iVBORw0KGgo=", ["mimeType"] = "image/png", ["width"] = 640, ["height"] = 360, ["sourceWidth"] = 1280, ["sourceHeight"] = 720
            }
        };
        observation["summary"] = DesktopPresentation.Summary(observation);
        // A daemon from an older release summarizes observations without geometry or automation IDs.
        var legacy = (JsonObject)observation.DeepClone();
        legacy["summary"] = "Fixture — w1\nSnapshot s1; expires in 90 seconds and is consumed by an action.\n[e0] Button \"Save\" {invoke}";
        var job = new JsonObject { ["jobId"] = "job-1", ["state"] = "exited", ["stdout"] = "built", ["summary"] = "job-1: exited, exit code 0.\nstdout:\nbuilt" };
        string pipe = PipeName();
        using var daemon = new JsonPipeServer(pipe, request => Task.FromResult(request.Str("op") switch
        {
            "lease" => JsonLine.Ok(new JsonObject { ["leaseToken"] = "shape-token", ["summary"] = "Desktop owned by shape." }),
            "desktop.observe" => JsonLine.Ok((JsonObject)(request.Str("windowId") == "legacy" ? legacy : observation).DeepClone()),
            "exec.read" => JsonLine.Ok((JsonObject)job.DeepClone()),
            _ => JsonLine.Fail("unexpected operation")
        }));
        daemon.Start();
        using var backend = Isolated(pipe, "shape");
        var lease = await backend.CallAsync("seat_lease", new JsonObject { ["action"] = "acquire" }, CancellationToken.None);
        Require(lease.Obj("structuredContent") is { } owned && owned.Str("leaseToken") == "shape-token" && !owned.ContainsKey("summary"),
            "lease result lost its token or duplicated its summary");
        var observed = await backend.CallAsync("seat_observe", new JsonObject { ["windowId"] = "w1" }, CancellationToken.None);
        Require(observed["structuredContent"] is null && (observed["content"] as JsonArray)?.OfType<JsonObject>()
            .Any(c => c.Str("type") == "image" && c.Str("data") == "iVBORw0KGgo=") == true,
            "an image result carried structuredContent, which some clients read instead of the image");
        string text = Text(observed);
        Require(text.Contains("(captured at 1280x720)") && text.Contains("#SaveButton") && text.Contains("@10,20,30,40"),
            "image result text lost capture geometry, automation IDs or bounds");
        string rebuilt = Text(await backend.CallAsync("seat_observe", new JsonObject { ["windowId"] = "legacy" }, CancellationToken.None));
        Require(rebuilt.Contains("(captured at 1280x720)") && rebuilt.Contains("#SaveButton") && rebuilt.Contains("@10,20,30,40"),
            "an older daemon's image result lost capture geometry, automation IDs or bounds");
        var read = await backend.CallAsync("seat_job", new JsonObject { ["jobId"] = "job-1" }, CancellationToken.None);
        Require(read.Obj("structuredContent") is { } structured && !structured.ContainsKey("summary") && structured.Str("stdout") == "built"
            && Text(read).Contains("built"), "job result lost its output or duplicated its summary");
        return "image results omit structuredContent; summaries are not duplicated";
    }

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
            """{"jsonrpc":"2.0","id":20,"method":"ping","method":"tools/call"}""",
            """{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"seat_stop","arguments":{},"arguments":{}}}""",
            """{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"seat_type","arguments":{"text":"first","text":"second"}}}""",
            """{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"seat_exec","arguments":{"path":"unused-test-command","env":{"A":"1","\u0041":"2"}}}}""",
            """{"jsonrpc":"2.0","id":24,"method":"ping","unused":[{"x":1,"x":2}]}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"requestId":2}}""",
            """{"jsonrpc":"2.0","id":25,"method":"\uD800"}""",
            """{"jsonrpc":"2.0","id":26,"method":"tools/call","params":{"name":"seat_type","arguments":{"text":"\uDC00"}}}""",
            """{"jsonrpc":"2.0","id":27,"method":"ping","params":{"\uD800":0}}""",
            Request(12, "ping")
        };
        using var output = new StringWriter();
        await session.RunAsync(new StringReader(string.Join('\n', requests)), output);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonLine.Parse(line)!).ToArray();
        Require(calls == 0, "invalid request or notification dispatched an action");
        Require(replies.Length == 23, "invalid requests or notifications produced the wrong number of replies");
        Require(replies[0].Obj("result")?.Str("protocolVersion") == "2025-11-25", "unsupported protocol was echoed back");
        Require(replies[1].Obj("error")?.Int("code") == -32700, "malformed JSON was not a parse error");
        Require(replies[2].Obj("error")?.Int("code") == -32600 && replies[3].Obj("error")?.Int("code") == -32600,
            "invalid envelopes were accepted");
        foreach (int id in new[] { 3, 4 })
            Require(replies.Single(r => r.Int("id") == id).Obj("error")?.Int("code") == -32602, "invalid params were accepted");
        for (int id = 5; id <= 11; id++)
            Require(replies.Single(r => r.Int("id") == id).Obj("result")?.Bool("isError") == true, $"invalid arguments for request {id} were accepted");
        Require(replies[^1].Obj("result") is { Count: 0 }, "server did not recover after invalid requests");
        Require(replies.Count(r => r.Obj("error")?.Int("code") == -32700) == 10, "ambiguous or invalid Unicode JSON was not rejected before dispatch");
        return "protocol negotiation, duplicate fields, invalid Unicode, envelopes, notifications and arguments validated before actions";
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
            if (request.Str("op") == "lease") return JsonLine.Ok(new JsonObject { ["leaseToken"] = "busy-test-token" });
            if (request.Str("op") == "input.text")
            {
                entered.TrySetResult();
                await release.Task;
            }
            return JsonLine.Ok(new JsonObject { ["op"] = request.Str("op") });
        });
        daemon.Start();
        using var backend = Isolated(pipeName);
        await backend.CallAsync("seat_lease", new JsonObject { ["action"] = "acquire" }, CancellationToken.None);
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
