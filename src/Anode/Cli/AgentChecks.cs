using System.Text.Json.Nodes;
using Anode.Core.Agents;
using Anode.Core.Bridge;
using Anode.Mcp;

namespace Anode.Cli;

internal static class AgentChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonObject Lease(string agent, string action, string? token = null) => new JsonObject
    {
        ["op"] = "lease", ["agentId"] = agent, ["action"] = action
    }.WithToken(token);

    private static JsonObject WithToken(this JsonObject request, string? token)
    {
        if (token is not null) request["leaseToken"] = token;
        return request;
    }

    private static JsonObject Input(string agent, string token, string text = "test") => new()
    {
        ["op"] = "input.text", ["agentId"] = agent, ["leaseToken"] = token, ["text"] = text
    };

    public static async Task<string> Ownership()
    {
        long now = 0;
        int invalidations = 0, actions = 0;
        string? cancelledOwner = null;
        var lease = new DesktopLease(() => invalidations++, owner => { cancelledOwner = owner; return 2; }, () => now);
        Task<JsonObject> Dispatch(JsonObject _, CancellationToken token) { actions++; return Task.FromResult(JsonLine.Ok()); }
        Task<JsonObject> Call(JsonObject request) => lease.HandleAsync(request, Dispatch);
        string Token(JsonObject result) => result.Obj("result")!.Str("leaseToken")!;

        var malformed = Lease("A", "acquire"); malformed["ttlSeconds"] = 1;
        Require((await Call(malformed)).Bool("ok") == false, "invalid lease lifetime was accepted");
        var first = await Call(Lease("A", "acquire"));
        string token = Token(first);
        Require(first.Bool("ok") == true && token.Length > 20, "acquire returned no fencing token");
        Require(Token(await Call(Lease("A", "acquire"))) == token, "uncertain acquire could not be recovered");
        Require((await Call(Lease("B", "status"))).Obj("result")?.Str("leaseToken") is null, "status exposed another agent's token");
        Require((await Call(Lease("B", "acquire"))).Str("errorCode") == "seat_busy", "second agent acquired an owned desktop");
        Require((await Call(Input("B", token))).Bool("ok") == false && actions == 0, "another agent's input executed");
        Require((await Call(Input("A", token))).Bool("ok") == true && actions == 1, "owner could not act");
        now = 100000;
        var renewal = Lease("A", "renew", token); renewal["ttlSeconds"] = 10;
        Require((await Call(renewal)).Bool("ok") == true, "owner could not renew");
        now = 110000;
        lease.ExpireIdle();
        Require(invalidations == 1, "idle expiry did not clean up references/input");
        Require((await Call(renewal)).Str("errorCode") == "lease_expired", "renew resurrected an expired lease");
        string next = Token(await Call(Lease("A", "acquire")));
        Require(next != token && (await Call(Input("A", token))).Str("errorCode") == "stale_lease", "reacquisition revived an old request");
        var release = Lease("A", "release", next); release["cancelJobs"] = true;
        Require((await Call(release)).Obj("result")?.Int("cancelledJobs") == 2 && cancelledOwner == "A", "cleanup was not scoped to the releasing agent");
        Require(invalidations == 2 && (await Call(Input("A", next))).Bool("ok") == false, "released observations or token remained usable");
        Require((await Call(Lease("B", "acquire"))).Bool("ok") == true, "handoff failed");

        bool cleanupBlocked = true;
        var failureLease = new DesktopLease(() => { if (cleanupBlocked) throw new InvalidOperationException("simulated held-input cleanup failure"); }, milliseconds: () => now);
        await failureLease.HandleAsync(Lease("A", "acquire"), Dispatch);
        now += 120001;
        bool refused = false;
        try { await failureLease.HandleAsync(Lease("B", "acquire"), Dispatch); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, "failed held-input cleanup allowed ownership transfer");
        cleanupBlocked = false;
        Require((await failureLease.HandleAsync(Lease("B", "acquire"), Dispatch)).Bool("ok") == true, "cleanup recovery could not transfer ownership");

        var activeFailure = new DesktopLease(() => { if (cleanupBlocked) throw new InvalidOperationException("simulated cleanup failure after an action"); }, milliseconds: () => now);
        string activeToken = Token(await activeFailure.HandleAsync(Lease("A", "acquire"), Dispatch));
        cleanupBlocked = true;
        try
        {
            await activeFailure.HandleAsync(Input("A", activeToken), (_, _) => { now += 120001; return Task.FromResult(JsonLine.Ok()); });
            throw new InvalidOperationException("cleanup failure was ignored");
        }
        catch (InvalidOperationException ex) when (ex.Message == "simulated cleanup failure after an action") { }
        cleanupBlocked = false;
        activeToken = Token(await activeFailure.HandleAsync(Lease("B", "acquire"), Dispatch));
        Require((await activeFailure.HandleAsync(Input("B", activeToken), Dispatch).WaitAsync(TimeSpan.FromSeconds(1))).Bool("ok") == true,
            "failed cleanup leaked the interaction gate");
        return "exclusive ownership, recoverable acquire, bounded renewal, idle expiry, stale-token refusal and scoped release";
    }

    public static async Task<string> QueueAndDisconnect()
    {
        long now = 0;
        int actions = 0;
        var entered = Signal(); var finish = Signal();
        var lease = new DesktopLease(milliseconds: () => Interlocked.Read(ref now));
        using var stopping = new CancellationTokenSource();
        async Task<JsonObject> Dispatch(JsonObject request, CancellationToken cancel)
        {
            if (request.Str("op") == "input.text")
            {
                Interlocked.Increment(ref actions);
                entered.TrySetResult();
                await finish.Task.WaitAsync(cancel);
            }
            return JsonLine.Ok();
        }
        string pipe = "anode-selftest-" + Guid.NewGuid().ToString("N");
        using var server = new JsonPipeServer(pipe, request => request.Str("op") == "seat.stop"
            ? Stop() : lease.HandleAsync(request, Dispatch, stopping.Token));
        Task<JsonObject> Stop() { stopping.Cancel(); return Task.FromResult(JsonLine.Ok()); }
        server.Start();
        using var a = await JsonPipeClient.TryConnectAsync(pipe, 2000) ?? throw new InvalidOperationException("private pipe unavailable");
        using var b = await JsonPipeClient.TryConnectAsync(pipe, 2000) ?? throw new InvalidOperationException("private pipe unavailable");
        string token = (await a.RequestAsync("lease", new JsonObject { ["agentId"] = "A", ["action"] = "acquire" })).Obj("result")!.Str("leaseToken")!;
        Task<JsonObject> active = a.RequestAsync("input.text", Input("A", token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // HandleAsync runs synchronously up to its occupied gate, so this request is known to be queued.
            Task<JsonObject> queued = lease.HandleAsync(Input("A", token), Dispatch, stopping.Token);
            Interlocked.Exchange(ref now, 120001);
            Require((await b.RequestAsync("lease", new JsonObject { ["agentId"] = "B", ["action"] = "acquire" })).Str("errorCode") == "seat_busy", "lease transferred during an admitted action");
            finish.TrySetResult();
            Require((await active.WaitAsync(TimeSpan.FromSeconds(2))).Bool("ok") == true, "admitted action did not finish");
            Require((await queued.WaitAsync(TimeSpan.FromSeconds(2))).Bool("ok") == false && actions == 1, "expired queued input was executed");

            string second = (await a.RequestAsync("lease", new JsonObject { ["agentId"] = "A", ["action"] = "acquire" })).Obj("result")!.Str("leaseToken")!;
            a.Dispose();
            Require((await b.RequestAsync("lease", new JsonObject { ["agentId"] = "B", ["action"] = "acquire" })).Str("errorCode") == "seat_busy", "connection loss silently released workflow ownership");
            Interlocked.Exchange(ref now, 240002);
            string third = (await b.RequestAsync("lease", new JsonObject { ["agentId"] = "B", ["action"] = "acquire" })).Obj("result")!.Str("leaseToken")!;
            Require(second != third, "disconnect expiry reused a token");

            entered = Signal(); finish = Signal();
            active = b.RequestAsync("input.text", Input("B", third));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queued = lease.HandleAsync(Input("B", third), Dispatch, stopping.Token);
            using var stop = await JsonPipeClient.TryConnectAsync(pipe, 2000) ?? throw new InvalidOperationException("private pipe unavailable");
            Require((await stop.RequestAsync("seat.stop", timeoutMs: 1000)).Bool("ok") == true, "Stop waited for the owner");
            await active.WaitAsync(TimeSpan.FromSeconds(2));
            try { await queued; throw new InvalidOperationException("Stop admitted queued input"); }
            catch (OperationCanceledException) { }
            Require(actions == 2, "queued input survived Stop");
        }
        finally { stopping.Cancel(); finish.TrySetResult(); }
        return "private clients contend, expired queued input is fenced, disconnect recovers by expiry, and Stop bypasses ownership";
    }

    public static async Task<string> McpOwnership()
    {
        int actions = 0, launches = 0;
        var lease = new DesktopLease();
        string pipe = "anode-selftest-" + Guid.NewGuid().ToString("N");
        using var daemon = new JsonPipeServer(pipe, request => lease.HandleAsync(request, (_, _) =>
        {
            actions++;
            return Task.FromResult(JsonLine.Ok());
        }));
        daemon.Start();
        using var a = new McpServer(pipe, () => launches++, agentId: "A");
        using var b = new McpServer(pipe, () => launches++, agentId: "B");
        Task<JsonObject> Call(McpServer client, string tool, JsonObject args) => client.CallAsync(tool, args, CancellationToken.None);
        var acquire = new JsonObject { ["action"] = "acquire" };
        Require((await Call(a, "seat_type", new JsonObject { ["text"] = "missing lease" })).Bool("isError") == true && actions == 0 && launches == 0,
            "unowned input reached the daemon or started it");
        string token = (await Call(a, "seat_lease", acquire)).Obj("structuredContent")!.Str("leaseToken")!;
        Require((await Call(a, "seat_type", new JsonObject { ["text"] = "owned" })).Bool("isError") != true && actions == 1, "MCP did not attach its cached identity and token");
        Require((await Call(b, "seat_lease", acquire)).Obj("structuredContent")?.Str("errorCode") == "seat_busy", "MCP hid ownership contention");
        using var resumed = new McpServer(pipe, agentId: "A");
        Require((await Call(resumed, "seat_lease", acquire)).Obj("structuredContent")?.Str("leaseToken") == token, "stable MCP identity could not resume ownership");
        await Call(resumed, "seat_lease", new JsonObject { ["action"] = "release" });
        await Call(b, "seat_lease", acquire);
        Require((await Call(a, "seat_type", new JsonObject { ["text"] = "stale" })).Bool("isError") == true && actions == 1, "old MCP connection acted after handoff");
        Require((await Call(b, "seat_type", new JsonObject { ["text"] = "new owner" })).Bool("isError") != true && actions == 2, "new MCP owner could not act");
        return "MCP identities, cached tokens, errors, stable restart recovery and stale-connection refusal";
    }

    public static async Task<string> StartupAndStop()
    {
        int prerequisites = 0, starts = 0;
        using var daemon = new Daemon.AnodeDaemon(new Daemon.SeatOptions(), findChild: () => null,
            blockingSummary: () => { prerequisites++; return "injected unavailable prerequisites"; },
            startHost: () => { starts++; return Task.CompletedTask; }, logoff: _ => null, disconnectViewer: () => { });
        var invalid = Lease("A", "acquire"); invalid["ttlSeconds"] = 601;
        Require((await daemon.HandleControlAsync(invalid)).Bool("ok") == false, "invalid acquisition reached startup");
        invalid = Lease("A", "acquire"); invalid["agentId"] = "invalid agent";
        Require((await daemon.HandleControlAsync(invalid)).Bool("ok") == false, "invalid identity reached startup");
        Require(prerequisites == 0 && starts == 0, "ownership validation ran after prerequisites/startup");
        await daemon.StopSeatAsync("injected ownership test; no live seat");
        Require((await daemon.StartSeatAsync(expectedStopVersion: 0)).Bool("ok") == false && prerequisites == 0,
            "an acquisition queued before Stop restarted the seat");
        Require((await daemon.HandleControlAsync(Lease("A", "status"))).Bool("ok") == false && prerequisites == 0,
            "ownership status tried to start a stopped seat");
        Require((await daemon.HandleControlAsync(Lease("A", "acquire"))).Bool("ok") == false && prerequisites == 1 && starts == 0,
            "explicit acquisition after Stop did not check the injected prerequisites");
        return "daemon validates before startup, pending acquisition cannot undo Stop, and lease status never starts a seat";
    }
}
