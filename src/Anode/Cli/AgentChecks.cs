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

    public static async Task<string> Line()
    {
        long now = 0;
        var lease = new DesktopLease(milliseconds: () => now);
        Task<JsonObject> Call(JsonObject request) => lease.HandleAsync(request, (_, _) => Task.FromResult(JsonLine.Ok()));
        string Token(JsonObject result) => result.Obj("result")!.Str("leaseToken")!;
        int? Place(JsonObject result) => result.Obj("result")?.Int("queuePosition");
        JsonObject Waiting(string agent, string? name = null)
        {
            var request = Lease(agent, "acquire");
            request["waitSeconds"] = 30;
            if (name is not null) request["agentName"] = name;
            return request;
        }

        var named = Lease("A", "acquire"); named["agentName"] = "Claude Code";
        string a = Token(await Call(named));
        var second = await Call(Waiting("B", "Codex"));
        var third = await Call(Waiting("C"));
        Require(second.Str("errorCode") == "seat_busy" && Place(second) == 1 && Place(third) == 2 && second.Str("error")!.Contains("Claude Code"),
            "waiting agents did not line up in arrival order behind a named owner");
        var status = (await Call(Lease("D", "status"))).Obj("result")!;
        Require(status.Str("ownerName") == "Claude Code" && status["waiting"] is JsonArray { Count: 2 } line && line[0]!.AsObject().Str("agentName") == "Codex",
            "status did not name the owner and the line");
        Require((await Call(Input("A", a))).Int("waitingAgents") == 2, "the owner was not told that agents are waiting");
        Require((await Call(Lease("D", "acquire"))).Str("errorCode") == "seat_busy" && Place(await Call(Lease("D", "status"))) is null,
            "an acquire without waitSeconds joined or jumped the line");

        // The first agent still asking gets the released desktop; the rest keep their order.
        now = 1000;
        await Call(Waiting("B")); await Call(Waiting("C"));
        await Call(Lease("A", "release", a));
        Require((await Call(Waiting("C"))).Str("errorCode") == "seat_busy", "a later agent jumped the line");
        var granted = await Call(Waiting("B"));
        Require(granted.Bool("ok") == true && granted.Obj("result")?.Str("ownerName") == "Codex" && Place(await Call(Lease("C", "status"))) == 1,
            "the first agent in line did not get the desktop, or the line did not move up");
        string b = Token(granted);

        // A place lasts only while its agent keeps asking, so a departed agent never gets the desktop.
        now += DesktopLease.PlaceHoldMs + 1;
        lease.ExpireIdle();
        Require(Place(await Call(Lease("C", "status"))) is null, "an agent that stopped asking kept its place");

        // Working keeps the lease for a full lifetime from each action; only idleness lets it lapse.
        now += 100_000;
        Require((await Call(Input("B", b))).Bool("ok") == true, "the owner could not act");
        now += 100_000;
        Require((await Call(Input("B", b))).Bool("ok") == true, "a working owner's lease lapsed after its first lifetime");
        now += 120_001;
        lease.ExpireIdle();
        Require((await Call(Input("B", b))).Str("errorCode") == "lease_expired", "an idle owner kept the desktop");
        return "first-come line, places kept only while asking, owner names, waiting counts and leases extended by work";
    }

    /// <summary>
    /// Every session of one client shares its name, so an agent is named after its client and project
    /// folder; an agent reading status learns when the desktop is its own; the log records hand-overs.
    /// </summary>
    public static async Task<string> Names()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (folder, expected) in new (string?, string?)[]
                 {
                     (@"C:\Projects\WebShop", "WebShop"), (@"C:\Projects\DroneSim\", "DroneSim"), (@"D:\", null), (profile, null),
                     (Path.Combine(profile, "Projects", "Game"), "Game"), (Path.Combine(local, "OpenAI", "Codex", "bin"), null),
                     (Path.GetTempPath(), null), (Environment.SystemDirectory, null), (AppContext.BaseDirectory, null), ("", null), (null, null)
                 })
            Require(McpServer.Workspace(folder) == expected, $"the workspace of '{folder}' was '{McpServer.Workspace(folder)}', not '{expected}'");

        string pipe = "anode-selftest-" + Guid.NewGuid().ToString("N");
        using var claude = McpChecks.Isolated(pipe, "A", @"C:\Projects\WebShop");
        using var codex = McpChecks.Isolated(pipe, "B", @"C:\Projects\DroneSim");
        using var other = McpChecks.Isolated(pipe, "C", @"C:\DEV\" + new string('x', 80));
        using var bare = McpChecks.Isolated(pipe, "D");
        claude.Identify("claude-code");
        codex.Identify("codex-mcp-client");
        other.Identify("harness");
        bare.Identify("claude-code");
        Require(claude.AgentName == "Claude Code in WebShop" && codex.AgentName == "Codex in DroneSim" && bare.AgentName == "Claude Code"
            && other.AgentName is { Length: 64 } truncated && truncated.StartsWith("harness in xxx", StringComparison.Ordinal),
            $"agents were named '{claude.AgentName}', '{codex.AgentName}', '{other.AgentName}' and '{bare.AgentName}'");

        long now = 0;
        var record = new List<string>();
        var lease = new DesktopLease(milliseconds: () => now, record: record.Add);
        Task<JsonObject> Call(JsonObject request) => lease.HandleAsync(request, (_, _) => Task.FromResult(JsonLine.Ok()));
        var take = Lease("A", "acquire"); take["agentName"] = claude.AgentName;
        string token = (await Call(take)).Obj("result")!.Str("leaseToken")!;
        Require((await Call(Lease("A", "status"))).Obj("result")!.Str("summary")!.StartsWith("You hold the desktop lease", StringComparison.Ordinal)
            && (await Call(Lease("B", "status"))).Obj("result")!.Str("summary")!.Contains("Desktop in use by Claude Code in WebShop (A)"),
            "status did not tell the owner the desktop is its own, or did not name it to others");
        await Call(Lease("A", "release", token));
        var again = Lease("B", "acquire"); again["agentName"] = codex.AgentName;
        await Call(again);
        now += 600_001;
        lease.ExpireIdle();
        Require(record.SequenceEqual(new[]
            {
                "desktop lease taken by Claude Code in WebShop (A)", "desktop lease released by Claude Code in WebShop (A)",
                "desktop lease taken by Codex in DroneSim (B)", "desktop lease of Codex in DroneSim (B) expired"
            }), "desktop hand-overs were not recorded: " + string.Join(" | ", record));
        return "agents are named after their client and project folder; status says when the desktop is yours; hand-overs are logged";
    }

    public static async Task<string> McpLeaseHelper()
    {
        int actions = 0;
        var lease = new DesktopLease();
        string pipe = "anode-selftest-" + Guid.NewGuid().ToString("N");
        using var daemon = new JsonPipeServer(pipe, request => lease.HandleAsync(request, (_, _) =>
        {
            Interlocked.Increment(ref actions);
            return Task.FromResult(JsonLine.Ok(new JsonObject { ["summary"] = "Observed." }));
        }));
        daemon.Start();
        using var first = McpChecks.Isolated(pipe, "first");
        using var second = McpChecks.Isolated(pipe, "second");
        first.Identify("claude-code");
        second.Identify("codex-mcp-client");
        Task<JsonObject> Call(McpServer client, string tool, JsonObject? args = null) => client.CallAsync(tool, args ?? new JsonObject(), CancellationToken.None);

        // A desktop tool takes the free desktop, so an agent working alone never calls seat_lease.
        var observed = await Call(first, "seat_windows");
        Require(observed.Bool("isError") != true && first.HoldsLease && McpChecks.Text(observed).Contains(McpServer.TookLease) && actions == 1,
            "a desktop tool did not take the free desktop for its agent");
        Require(first.AgentName == "Claude Code" && second.AgentName == "Codex", "MCP clients were not named");
        var busy = await Call(second, "seat_click", new JsonObject { ["x"] = 1, ["y"] = 1 });
        Require(busy.Bool("isError") == true && McpChecks.Text(busy).Contains("Claude Code") && !second.HoldsLease && actions == 1,
            "input from an agent without the desktop ran or did not name the owner");

        // Waiting in line: the owner hears someone is waiting, and the waiter gets the desktop on release.
        var waiting = Call(second, "seat_lease", new JsonObject { ["action"] = "acquire", ["waitSeconds"] = 10 });
        await Task.Delay(1500);
        Require(McpChecks.Text(await Call(first, "seat_windows")).Contains("Another agent is waiting"), "the owner was not told another agent is waiting");
        await Call(first, "seat_lease", new JsonObject { ["action"] = "release" });
        var granted = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Require(granted.Bool("isError") != true && second.HoldsLease, "the waiting agent did not get the released desktop");

        // After a handoff, input aimed by an earlier look waits for a fresh observation.
        await Call(second, "seat_lease", new JsonObject { ["action"] = "release" });
        int before = actions;
        var blind = await Call(first, "seat_type", new JsonObject { ["text"] = "blind" });
        Require(blind.Bool("isError") == true && McpChecks.Text(blind) == McpServer.LookFirst && first.HoldsLease && actions == before,
            "input acted blind after Anode took the lease");
        Require((await Call(first, "seat_type", new JsonObject { ["text"] = "seen" })).Bool("isError") != true && actions == before + 1,
            "input under a held lease did not run");

        // A session that ends hands the desktop on at once rather than after expiry.
        await first.ReleaseAsync();
        Require(!first.HoldsLease && (await Call(second, "seat_lease", new JsonObject { ["action"] = "acquire", ["waitSeconds"] = 0 })).Bool("isError") != true,
            "an ended session kept the desktop until expiry");
        return "desktop tools take a free desktop, busy desktops name their owner, waiting agents get it on release, input looks first after a handoff, ended sessions hand it on";
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
        // Readiness passes, so an unexpected start attempt reaches (and fails in) the counting launcher.
        void Launch() { launches++; throw new InvalidOperationException("an MCP ownership check tried to launch a daemon"); }
        using var a = new McpServer(pipe, Launch, () => null, agentId: "A");
        using var b = new McpServer(pipe, Launch, () => null, agentId: "B");
        Task<JsonObject> Call(McpServer client, string tool, JsonObject args) => client.CallAsync(tool, args, CancellationToken.None);
        var acquire = new JsonObject { ["action"] = "acquire" };
        Require((await Call(a, "seat_type", new JsonObject { ["text"] = "missing lease" })).Bool("isError") == true && actions == 0 && launches == 0,
            "unowned input reached the daemon or started it");
        string token = (await Call(a, "seat_lease", acquire)).Obj("structuredContent")!.Str("leaseToken")!;
        Require((await Call(a, "seat_type", new JsonObject { ["text"] = "owned" })).Bool("isError") != true && actions == 1, "MCP did not attach its cached identity and token");
        Require((await Call(b, "seat_lease", new JsonObject { ["action"] = "acquire", ["waitSeconds"] = 0 })).Obj("structuredContent")?.Str("errorCode") == "seat_busy",
            "MCP hid ownership contention");
        using var resumed = McpChecks.Isolated(pipe, "A");
        Require((await Call(resumed, "seat_lease", acquire)).Obj("structuredContent")?.Str("leaseToken") == token, "stable MCP identity could not resume ownership");
        await Call(resumed, "seat_lease", new JsonObject { ["action"] = "release" });
        await Call(b, "seat_lease", acquire);
        Require((await Call(a, "seat_type", new JsonObject { ["text"] = "stale" })).Bool("isError") == true && actions == 1, "old MCP connection acted after handoff");
        Require((await Call(b, "seat_type", new JsonObject { ["text"] = "new owner" })).Bool("isError") != true && actions == 2, "new MCP owner could not act");
        Require(launches == 0, "MCP ownership started a daemon");

        // The user quits Anode while an agent still caches its token: desktop calls must not relaunch it.
        int staleLaunches = 0, staleReadiness = 0;
        string stalePipe = "anode-selftest-" + Guid.NewGuid().ToString("N");
        var staleLease = new DesktopLease();
        using var staleDaemon = new JsonPipeServer(stalePipe, request => staleLease.HandleAsync(request, (_, _) => Task.FromResult(JsonLine.Ok())));
        staleDaemon.Start();
        using var stale = new McpServer(stalePipe,
            () => { staleLaunches++; throw new InvalidOperationException("a cached lease token launched a daemon"); },
            () => { staleReadiness++; return null; }, "C");
        Require((await Call(stale, "seat_lease", acquire)).Bool("isError") != true && stale.HoldsLease, "stale-token fixture could not acquire");
        staleDaemon.Dispose();
        // The first call discovers the closed connection and reports the quit; it is never replayed.
        var quit = await Call(stale, "seat_screenshot", new JsonObject());
        Require(quit.Bool("isError") == true && McpChecks.Text(quit) == McpServer.LeaseEnded && !stale.HoldsLease,
            "a lease-gated call after quit kept its token or hid the acquire hint");
        var next = await Call(stale, "seat_type", new JsonObject { ["text"] = "after quit" });
        Require(next.Bool("isError") == true && McpChecks.Text(next).Contains("seat_lease action=acquire"), "a call after quit did not ask for a new lease");
        Require(staleLaunches == 0 && staleReadiness == 0, "a stale lease token started or prepared to start a daemon");

        // A person stops the seat while Anode keeps running: desktop calls drop the token and ask
        // for a new lease, and diagnostics report the stopped seat instead of failing.
        bool seatStopped = false;
        string stoppedPipe = "anode-selftest-" + Guid.NewGuid().ToString("N");
        var stoppedLease = new DesktopLease();
        using var stoppedDaemon = new JsonPipeServer(stoppedPipe, request =>
        {
            if (!seatStopped) return stoppedLease.HandleAsync(request, (_, _) => Task.FromResult(JsonLine.Ok()));
            var refused = JsonLine.Fail("The seat is stopped.");
            refused["errorCode"] = "seat_stopped";
            refused["state"] = "stopped";
            return Task.FromResult(refused);
        });
        stoppedDaemon.Start();
        using var stopped = McpChecks.Isolated(stoppedPipe, "D");
        Require((await Call(stopped, "seat_lease", acquire)).Bool("isError") != true && stopped.HoldsLease, "stopped-seat fixture could not acquire");
        seatStopped = true;
        var gated = await Call(stopped, "seat_screenshot", new JsonObject());
        Require(gated.Bool("isError") == true && McpChecks.Text(gated) == McpServer.SeatStoppedLease && !stopped.HoldsLease,
            "a lease-gated call on a stopped seat kept its token or hid the acquire hint");
        var diagnostics = (await Call(stopped, "seat_capabilities", new JsonObject())).Obj("structuredContent");
        Require(diagnostics?.Str("state") == "stopped" && diagnostics.Bool("daemonRunning") == true && diagnostics.Str("summary") == McpServer.SeatStopped,
            "diagnostics on a stopped seat failed instead of reporting it");
        return "MCP identities, cached tokens, errors, stable restart recovery, stale-connection refusal, no relaunch after quit and stopped-seat reporting";
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
        var onBehalf = Lease("A", "acquire"); onBehalf["startSeat"] = false;
        Require((await daemon.HandleControlAsync(onBehalf)).Bool("ok") == false && prerequisites == 0 && starts == 0,
            "an acquisition made on an agent's behalf started a stopped seat");
        Require((await daemon.HandleControlAsync(Lease("A", "acquire"))).Bool("ok") == false && prerequisites == 1 && starts == 0,
            "explicit acquisition after Stop did not check the injected prerequisites");
        return "daemon validates before startup, pending acquisition cannot undo Stop, and neither lease status nor helper acquisition starts a seat";
    }
}
