using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Desktop;
using Anode.Core.Launch;
using Anode.Core.Util;
using Anode.Core.Agents;

namespace Anode.Mcp;

/// <summary>
/// A Model Context Protocol server over stdin and stdout, so an agent can hold a seat
/// the way it holds any other tool.
///
/// It owns nothing. Live tools use the daemon over the control pipe; only seat_start and
/// seat_lease acquisition may start it. Guide discovery is local; status never starts a
/// daemon. That means an agent and a person can both be pointed at the same seat, and the
/// person's stop button still wins.
/// </summary>
internal sealed class McpServer : IDisposable
{
    internal const string NotRunning =
        "Anode is not running. Call seat_lease with action=acquire when the task needs a background desktop; it starts a hidden seat.";
    internal const string LeaseEnded =
        "Anode is not running or your lease ended. Call seat_lease action=acquire, then observe again.";
    internal const string SeatStopped =
        "The seat is stopped. Call seat_lease with action=acquire when the task needs a background desktop; it starts the seat again.";
    internal const string SeatStoppedLease =
        "The seat was stopped, which ended your lease. Call seat_lease action=acquire, then observe again.";
    internal const string TookLease =
        "Anode took the desktop lease for this agent; each desktop action keeps it. Release it with seat_lease action=release when you finish.";
    internal const string LookFirst =
        "Anode took the desktop lease for this agent, but the desktop may have changed since you last looked. "
        + "Observe it (seat_screenshot or seat_observe), then repeat this action.";

    /// <summary>How long an explicit acquire waits in line when the agent does not say.</summary>
    internal const int DefaultWaitSeconds = 30;

    private JsonPipeClient? _daemon;
    private string? _blockedReason, _seatTaken;
    private bool _launchPending;
    private readonly string _agentId;
    // A generated identity cannot be resumed after this process ends.
    private readonly bool _ephemeral;
    private readonly bool _namePinned;
    private string? _agentName;
    private string? _leaseToken;
    private readonly string _controlPipe;
    private readonly Action _launchDaemon;
    private readonly Func<string?> _blockingSummary;
    private readonly SemaphoreSlim ConnectGate = new(1, 1);

    internal McpServer(string? controlPipe = null, Action? launchDaemon = null, Func<string?>? blockingSummary = null, string? agentId = null)
    {
        // Checks use private pipes. They must never reach Task Scheduler or the real readiness probe.
        if (controlPipe is not null && controlPipe != Env.ControlPipe && (launchDaemon is null || blockingSummary is null))
            throw new ArgumentException("A private control pipe requires an injected launcher and readiness check.", nameof(controlPipe));
        _controlPipe = controlPipe ?? Env.ControlPipe;
        _launchDaemon = launchDaemon ?? (() => DaemonLauncher.Launch(new[] { "--hidden" }));
        _blockingSummary = blockingSummary ?? Core.Session.Preconditions.BlockingSummary;
        // An empty value (for example an unset MCPB user_config field) means no stable identity.
        string? configured = Environment.GetEnvironmentVariable("ANODE_AGENT_ID");
        _ephemeral = agentId is null && string.IsNullOrWhiteSpace(configured);
        _agentId = agentId ?? (string.IsNullOrWhiteSpace(configured) ? null : configured) ?? "a_" + Guid.NewGuid().ToString("N");
        string? named = Environment.GetEnvironmentVariable("ANODE_AGENT_NAME")?.Trim();
        if (AgentAccess.ValidName(named)) { _agentName = named; _namePinned = true; }
    }

    /// <summary>Whether this connection holds a cached lease token.</summary>
    internal bool HoldsLease => _leaseToken is not null;

    /// <summary>The name other agents and the viewer see for this agent's lease.</summary>
    internal string? AgentName => _agentName;

    /// <summary>
    /// Names this agent after its MCP client, so status and the viewer can say who holds the desktop.
    /// ANODE_AGENT_NAME takes precedence, for telling apart several sessions of one client.
    /// </summary>
    internal void Identify(string? clientName)
    {
        if (_namePinned || string.IsNullOrWhiteSpace(clientName)) return;
        string name = clientName.Trim() switch
        {
            "claude-code" => "Claude Code",
            "claude-ai" => "Claude",
            "codex-mcp-client" or "codex" => "Codex",
            string other => new string(other.Where(c => !char.IsControl(c)).Take(64).ToArray())
        };
        if (AgentAccess.ValidName(name)) _agentName = name;
    }

    /// <summary>How long a call waits for an existing daemon's pipe before treating Anode as stopped.</summary>
    internal int ConnectTimeoutMs { get; init; } = 1000;

    public static async Task<int> Run(string? agentId = null)
    {
        Log.SetRole("mcp");
        Log.Info("MCP server starting");
        using var server = new McpServer(agentId: agentId);
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try { await new McpSession(server.CallAsync, server.Identify).RunAsync(stdin, stdout).ConfigureAwait(false); }
        finally
        {
            // Nothing can resume a generated identity, so its lease would only keep other agents
            // waiting until it expired. A stable ANODE_AGENT_ID keeps it for a restarted session.
            if (server._ephemeral) await server.ReleaseAsync().ConfigureAwait(false);
            Log.Info("MCP server stopped");
        }
        return 0;
    }

    /// <summary>Hands this agent's lease on when its session ends. Brief and best effort; expiry is the fallback.</summary>
    internal async Task ReleaseAsync()
    {
        if (_leaseToken is null) return;
        try
        {
            using var client = await JsonPipeClient.TryConnectAsync(_controlPipe, 1000).ConfigureAwait(false);
            if (client is null) return;
            // An action the session cancelled may still be finishing in the seat; the release waits briefly for it.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var response = await client.RequestAsync("lease",
                    AgentAccess.Attach(new JsonObject { ["action"] = "release" }, _agentId, _leaseToken), 3000).ConfigureAwait(false);
                if (response.Bool("ok") == true || response.Str("errorCode") is "lease_expired" or "stale_lease")
                {
                    _leaseToken = null;
                    Log.Info("released the desktop lease as the MCP session ended");
                    return;
                }
                if (response.Str("errorCode") != "seat_busy") break;
                await Task.Delay(500).ConfigureAwait(false);
            }
            Log.Warn("could not release the desktop lease as the MCP session ended; it expires on its own");
        }
        catch (Exception ex) { Log.Warn($"could not release the desktop lease as the MCP session ended: {ex.Message}"); }
    }

    internal async Task<JsonObject> CallAsync(string toolName, JsonObject arguments, CancellationToken cancel)
    {
        if (!Tools.TryResolve(toolName, out string op, out bool startsDaemon))
            return TextResult($"Unknown tool '{toolName}'.", isError: true);

        if (toolName == "anode_guide") return TextResult(AgentGuide.Text);
        if (Tools.ValidateArguments(toolName, arguments) is { } invalid) return TextResult(invalid, true);
        if (toolName == "seat_stop") return await StopAsync(arguments, cancel).ConfigureAwait(false);
        string? leaseAction = toolName == "seat_lease" ? arguments.Str("action") ?? "status" : null;
        bool leaseGated = AgentAccess.RequiresLease(op);
        // A missing lease for a desktop tool is taken below when the desktop is free; everything
        // else is checked before any daemon starts.
        bool takesLease = leaseGated && _leaseToken is null;
        var payload = Envelope(arguments, leaseAction);
        payload["op"] = op;
        if (!takesLease && AgentAccess.Validate(payload) is { } invalidOwner) return TextResult(invalidOwner, true);
        payload.Remove("op");
        // Only an explicit start or lease acquisition may create a seat. A cached token must not
        // relaunch a daemon the user quit, only for the new seat to refuse that token.
        bool mayStart = (startsDaemon && !leaseGated) || leaseAction == "acquire";
        var client = await DaemonAsync(mayStart, cancel).ConfigureAwait(false);
        if (client is null)
        {
            if (mayStart)
                return TextResult(_seatTaken ?? (_blockedReason is not null
                    ? _blockedReason + " Tell the user to run `anode setup` in a terminal and approve the administrator prompt; "
                      + "this cannot be done from here."
                    : "Anode is not running and could not be started. Run `anode doctor` in a terminal to see why."), isError: true);
            return Unavailable(toolName, leaseAction, leaseGated, daemonRunning: false);
        }

        string? note = null;
        if (takesLease)
        {
            if (await TakeLeaseAsync(client, toolName, cancel).ConfigureAwait(false) is { } refused) return refused;
            // Input aimed by an earlier look could land on whatever another agent left on screen.
            if (Tools.ActsOnScreen(op)) return TextResult(LookFirst, isError: true);
            note = TookLease;
            payload = Envelope(arguments, leaseAction);
        }

        int timeout = toolName is "steam_launch" or "seat_start" ? 180_000 : 60_000;
        var response = leaseAction == "acquire"
            ? await AcquireInLineAsync(client, payload, arguments.Int("waitSeconds") ?? DefaultWaitSeconds, cancel).ConfigureAwait(false)
            : await client.RequestAsync(op, payload, timeout, cancel).ConfigureAwait(false);

        if (response.Bool("ok") != true)
        {
            // Anode quit since the previous call. Report that rather than a lost connection; the
            // request is never resent, and the agent observes again after acquiring.
            if (!client.IsConnected && !mayStart && await DaemonAsync(mayStart: false, cancel).ConfigureAwait(false) is null)
                return Unavailable(toolName, leaseAction, leaseGated, daemonRunning: false);
            // A person stopped the seat while Anode kept running.
            if (response.Str("errorCode") == "seat_stopped")
                return Unavailable(toolName, leaseAction, leaseGated, daemonRunning: true);
            if (response.Str("errorCode") is "lease_expired" or "stale_lease") _leaseToken = null;
            var error = TextResult(response.Str("error") ?? "The request failed.", isError: true);
            error["structuredContent"] = response.DeepClone();
            return error;
        }

        var result = response.Obj("result");
        if (leaseAction is "acquire" or "renew") _leaseToken = result?.Str("leaseToken");
        if (leaseAction == "release") _leaseToken = null;
        if (toolName == "seat_status" && result is not null)
        {
            result["agentId"] = _agentId;
            result["agentName"] = _agentName;
        }
        // Someone is waiting: say so, so a finished agent hands the desktop on instead of letting it expire.
        if (response.Int("waitingAgents") is int waiting and > 0)
            note = (note is null ? "" : note + " ") + (waiting == 1 ? "Another agent is" : $"{waiting} other agents are")
                + " waiting for the desktop; release it with seat_lease action=release when you finish.";
        return WithNote(Present(toolName, result), note);
    }

    private JsonObject Envelope(JsonObject arguments, string? leaseAction)
    {
        var payload = AgentAccess.Attach(arguments, _agentId, _leaseToken);
        if (leaseAction is not null && _agentName is not null) payload["agentName"] = _agentName;
        return payload;
    }

    /// <summary>
    /// Takes the lease for a desktop tool when the desktop is free, so an agent working alone never
    /// manages it. It never starts a seat and never jumps the line: a busy desktop is reported.
    /// </summary>
    private async Task<JsonObject?> TakeLeaseAsync(JsonPipeClient client, string toolName, CancellationToken cancel)
    {
        var request = AgentAccess.Attach(new JsonObject { ["action"] = "acquire" }, _agentId, null);
        request["startSeat"] = false;
        if (_agentName is not null) request["agentName"] = _agentName;
        var response = await client.RequestAsync("lease", request, 30_000, cancel).ConfigureAwait(false);
        if (response.Bool("ok") == true && response.Obj("result")?.Str("leaseToken") is { } token)
        {
            _leaseToken = token;
            return null;
        }
        if (!client.IsConnected && await DaemonAsync(mayStart: false, cancel).ConfigureAwait(false) is null)
            return Unavailable(toolName, null, leaseGated: true, daemonRunning: false);
        if (response.Str("errorCode") == "seat_stopped") return TextResult(SeatStopped, isError: true);
        var error = TextResult(response.Str("error") ?? "Anode could not take the desktop lease.", isError: true);
        error["structuredContent"] = response.DeepClone();
        return error;
    }

    /// <summary>
    /// Asks for the desktop about once a second until it is granted or the wait ends. Each ask keeps
    /// this agent's place in line; a place is dropped soon after the asking stops, so the desktop is
    /// never handed to an agent that gave up or went away.
    /// </summary>
    private static async Task<JsonObject> AcquireInLineAsync(JsonPipeClient client, JsonObject payload, int waitSeconds, CancellationToken cancel)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            int left = waitSeconds - (int)waited.Elapsed.TotalSeconds;
            payload["waitSeconds"] = Math.Max(0, left);
            // Acquisition may start the seat first.
            var response = await client.RequestAsync("lease", payload, 180_000, cancel).ConfigureAwait(false);
            if (response.Bool("ok") == true || response.Str("errorCode") != "seat_busy" || left <= 0 || !client.IsConnected) return response;
            await Task.Delay(1000, cancel).ConfigureAwait(false);
        }
    }

    private static JsonObject WithNote(JsonObject toolResult, string? note)
    {
        if (note is null || toolResult["content"] is not JsonArray content) return toolResult;
        if (content.OfType<JsonObject>().FirstOrDefault(block => block.Str("type") == "text") is { } text)
            text["text"] = text.Str("text") + "\n" + note;
        else content.Add(new JsonObject { ["type"] = "text", ["text"] = note });
        return toolResult;
    }

    private static JsonObject Present(string toolName, JsonObject? result)
    {
        if (result?.Str("summary") is { } summary)
        {
            var block = new JsonObject { ["type"] = "text", ["text"] = summary };
            // Clients that prefer structuredContent would otherwise receive the summary and its data twice.
            var structured = (JsonObject)result.DeepClone();
            structured.Remove("summary");
            if (structured.Obj("screenshot") is { } screenshot && screenshot.Str("data") is { } image)
            {
                // Some clients read only structuredContent when it is present and would drop the image.
                // The summary carries the capture size, automation IDs and bounds instead. A daemon from
                // an older release omits them, so rebuild its observation text here.
                if (!summary.Contains("(captured at ", StringComparison.Ordinal))
                    block["text"] = result["elements"] is JsonArray ? DesktopPresentation.Summary(result)
                        : DesktopPresentation.Capture(screenshot) is { } capture ? summary + "\n" + capture : summary;
                return new JsonObject
                {
                    ["content"] = new JsonArray(block,
                        new JsonObject { ["type"] = "image", ["data"] = image, ["mimeType"] = screenshot.Str("mimeType") ?? "image/png" })
                };
            }
            return new JsonObject { ["content"] = new JsonArray(block), ["structuredContent"] = structured };
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

    /// <summary>
    /// The answer when no seat can serve a call: Anode is not running, or a person stopped the seat
    /// while Anode kept running. Either way the lease is gone. Diagnostics report this as an
    /// observation and nothing starts a seat.
    /// </summary>
    private JsonObject Unavailable(string toolName, string? leaseAction, bool leaseGated, bool daemonRunning)
    {
        if (leaseGated || leaseAction is "renew" or "release") _leaseToken = null;
        if (leaseGated || leaseAction == "renew") return TextResult(daemonRunning ? SeatStoppedLease : LeaseEnded, isError: true);
        if (leaseAction == "release")
            return TextResult(daemonRunning ? "The seat is stopped, so there is no desktop lease to release."
                : "Anode is not running, so there is no desktop lease to release.");
        if (leaseAction == "status" || toolName is "seat_status" or "seat_capabilities" or "seat_processes" or "steam_status")
        {
            var status = new JsonObject
            {
                ["state"] = "stopped", ["daemonRunning"] = daemonRunning, ["agentId"] = _agentId,
                ["ownerAgentId"] = null, ["summary"] = daemonRunning ? SeatStopped : NotRunning
            };
            var reply = TextResult(status.ToJsonString());
            reply["structuredContent"] = status;
            return reply;
        }
        return TextResult(daemonRunning ? SeatStopped : NotRunning, isError: true);
    }

    private async Task<JsonPipeClient?> DaemonAsync(bool mayStart, CancellationToken cancel)
    {
        if (_daemon is { IsConnected: true }) return _daemon;

        await ConnectGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_daemon is { IsConnected: true }) return _daemon;
            _daemon?.Dispose();
            _daemon = await JsonPipeClient.TryConnectAsync(_controlPipe, ConnectTimeoutMs, cancel).ConfigureAwait(false);
            if (_daemon is not null || !mayStart) return _daemon;

            _seatTaken = null;
            if (_blockingSummary() is { } blocked)
            {
                Log.Warn($"not starting a daemon: {blocked}");
                _blockedReason = blocked;
                return null;
            }

            _blockedReason = null;
            Log.Info("no daemon; starting one");
            cancel.ThrowIfCancellationRequested();
            _launchPending = true;
            try { _launchDaemon(); }
            catch (SeatTakenException ex)
            {
                _launchPending = false;
                Log.Warn($"not starting a daemon: {ex.Message}");
                _seatTaken = ex.Message;
                return null;
            }
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
            if (response.Bool("ok") == true) _leaseToken = null;
            return response.Bool("ok") == true
                ? TextResult(response.Obj("result")?.ToJsonString() ?? "ok")
                : TextResult(response.Str("error") ?? "Could not stop the seat.", true);
        }
        finally { ConnectGate.Release(); }
    }

    private static JsonObject TextResult(string text, bool isError = false) => McpSession.TextResult(text, isError);

    public void Dispose() => _daemon?.Dispose();
}
