using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Agents;

/// <summary>
/// Fences queued desktop work and prevents transfer while an admitted action is running.
/// Agents waiting for the desktop form a first-come line. A place is kept only while its agent
/// keeps asking, so the desktop is never granted to an agent that has gone away.
/// </summary>
internal sealed class DesktopLease
{
    /// <summary>How long a waiting agent keeps its place after it last asked for the desktop.</summary>
    internal const int PlaceHoldMs = 5000;
    private const int DefaultTtlMs = 120_000;

    private sealed class Waiter(string agent, string? name, long seen)
    {
        public string Agent { get; } = agent;
        public string? Name { get; set; } = name;
        public long Seen { get; set; } = seen;
    }

    private readonly object _state = new();
    private readonly SemaphoreSlim _interaction = new(1, 1);
    private readonly Func<long> _milliseconds;
    private readonly Action _invalidateReferences;
    private readonly Func<string, int> _cancelJobs;
    private readonly List<Waiter> _line = new();
    private string? _owner, _ownerName, _token;
    private long _expires, _ttl = DefaultTtlMs;
    private bool _active;

    public DesktopLease(Action? invalidateReferences = null, Func<string, int>? cancelJobs = null, Func<long>? milliseconds = null)
    {
        _invalidateReferences = invalidateReferences ?? (() => { });
        _cancelJobs = cancelJobs ?? (_ => 0);
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    }

    private void Expire()
    {
        long now = _milliseconds();
        _line.RemoveAll(waiter => now - waiter.Seen > PlaceHoldMs);
        if (!_active && _owner is not null && _expires <= now) EndLease();
    }

    private void EndLease()
    {
        // Called only with the state lock held and no admitted desktop operation.
        // If cleanup fails, keep the expired owner and refuse transfer until cleanup succeeds.
        _invalidateReferences();
        _owner = null; _ownerName = null; _token = null;
    }

    public void ExpireIdle() { lock (_state) Expire(); }

    private static string Label(string agent, string? name) =>
        name is null ? agent : $"{name} ({(agent.Length > 10 ? agent[..10] : agent)})";

    private JsonObject State(string? caller = null, bool includeToken = false)
    {
        long remaining = _owner is null ? 0 : Math.Max(0, _expires - _milliseconds());
        int position = caller is null ? 0 : _line.FindIndex(waiter => waiter.Agent == caller) + 1;
        var line = new JsonArray(_line.Select(waiter => (JsonNode)new JsonObject { ["agentId"] = waiter.Agent, ["agentName"] = waiter.Name }).ToArray());
        string waiting = _line.Count switch { 0 => "", 1 => " 1 agent is waiting.", int n => $" {n} agents are waiting." };
        string place = position > 0 ? $" You are number {position} in line; ask again within {PlaceHoldMs / 1000} s to keep your place." : waiting;
        string summary = _owner is null
            ? (_line.Count == 0 ? "The desktop is available." : "The desktop is being handed to the next agent in line." + place)
            : _owner == caller
                ? $"You hold the desktop lease; {remaining / 1000} s left, extended by each desktop action." + (_line.Count > 0 ? waiting + " Release it when you finish." : "")
                : $"Desktop in use by {Label(_owner, _ownerName)}; {remaining / 1000} s left." + place;
        return new JsonObject
        {
            ["agentId"] = caller, ["ownerAgentId"] = _owner, ["ownerName"] = _ownerName,
            ["expiresInMs"] = remaining,
            ["operationRunning"] = _active,
            ["waiting"] = line,
            ["queuePosition"] = position > 0 ? position : null,
            ["leaseToken"] = includeToken ? _token : null,
            ["summary"] = summary
        };
    }

    private JsonObject Fail(string code, string message, string? caller = null)
    {
        var failure = JsonLine.Fail(message);
        failure["errorCode"] = code;
        failure["result"] = State(caller);
        return failure;
    }

    private JsonObject? Check(JsonObject request)
    {
        Expire();
        if (_token is null || _expires <= _milliseconds())
            return Fail("lease_expired", "The desktop lease is absent or expired. Acquire again and observe the desktop before acting.", request.Str("agentId"));
        if (_owner != request.Str("agentId"))
            return Fail("seat_busy", $"The desktop is busy with {Label(_owner!, _ownerName)}. Wait until it releases the lease or the lease expires.", request.Str("agentId"));
        if (_token != request.Str("leaseToken"))
            return Fail("stale_lease", "This request belongs to an earlier lease. Acquire again and observe before issuing new actions; do not replay uncertain input.", request.Str("agentId"));
        return null;
    }

    private JsonObject Manage(JsonObject request)
    {
        lock (_state)
        {
            Expire();
            string agent = request.Str("agentId")!;
            string action = request.Str("action") ?? "status";
            if (action == "status") return JsonLine.Ok(State(agent));
            if (action == "acquire")
            {
                long now = _milliseconds();
                // An uncertain acquire can be recovered without silently extending its lifetime.
                if (_owner == agent && _expires > now) return JsonLine.Ok(State(agent, includeToken: true));
                int place = _line.FindIndex(waiter => waiter.Agent == agent);
                if (_owner is not null || place > 0 || (place < 0 && _line.Count > 0))
                {
                    // Waiting keeps (or takes) a place in line; the desktop goes to the first agent still asking.
                    if (request.Int("waitSeconds") is > 0)
                    {
                        if (place < 0) _line.Add(new Waiter(agent, request.Str("agentName"), now));
                        else { _line[place].Seen = now; _line[place].Name = request.Str("agentName") ?? _line[place].Name; }
                    }
                    var state = State(agent);
                    return Fail("seat_busy", state.Str("summary") + (state["queuePosition"] is null ? " Acquire with waitSeconds to wait in line." : ""), agent);
                }
                _owner = agent;
                _ownerName = request.Str("agentName") ?? (place == 0 ? _line[0].Name : null);
                _line.RemoveAll(waiter => waiter.Agent == agent);
                _token = "l_" + Guid.NewGuid().ToString("N");
                _ttl = (request.Int("ttlSeconds") ?? DefaultTtlMs / 1000) * 1000L;
                _expires = now + _ttl;
                return JsonLine.Ok(State(agent, includeToken: true));
            }
            if (Check(request) is { } error) return error;
            if (action == "renew")
            {
                _ttl = (request.Int("ttlSeconds") ?? DefaultTtlMs / 1000) * 1000L;
                _expires = _milliseconds() + _ttl;
                return JsonLine.Ok(State(agent, includeToken: true));
            }
            if (_active) return Fail("seat_busy", "Wait for this agent's in-flight desktop operation to finish before releasing its lease.", agent);
            int cancelled = request.Bool("cancelJobs") == true ? _cancelJobs(agent) : 0;
            _expires = _milliseconds();
            EndLease();
            var result = State(agent);
            result["cancelledJobs"] = cancelled;
            return JsonLine.Ok(result);
        }
    }

    public async Task<JsonObject> HandleAsync(JsonObject request,
        Func<JsonObject, CancellationToken, Task<JsonObject>> dispatch, CancellationToken cancel = default)
    {
        if (AgentAccess.Validate(request) is { } invalid) return JsonLine.Fail(invalid);
        string op = request.Str("op") ?? "";
        if (Mcp.Tools.ValidateOperation(op, AgentAccess.Arguments(request)) is { } badArgs) return JsonLine.Fail(badArgs);
        cancel.ThrowIfCancellationRequested();
        if (op == "lease") return Manage(request);
        if (!AgentAccess.RequiresLease(op)) return await dispatch(request, cancel).ConfigureAwait(false);
        lock (_state) { if (Check(request) is { } error) return error; }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(request.Int("timeoutMs") ?? 60000);
        await _interaction.WaitAsync(deadline.Token).ConfigureAwait(false);
        bool admitted = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            lock (_state)
            {
                // Check again after queueing. A released, replaced or expired token never gets input.
                if (Check(request) is { } error) return error;
                _active = admitted = true;
                // Working keeps the lease: each admitted action extends it to a full lifetime from its
                // start, so only an idle owner needs renew. It never revives an expired lease.
                _expires = Math.Max(_expires, _milliseconds() + _ttl);
            }
            var response = await dispatch(request, deadline.Token).ConfigureAwait(false);
            // Tell the owner someone is waiting, so it can hand the desktop on when it finishes.
            lock (_state) { if (_line.Count > 0) response["waitingAgents"] = _line.Count; }
            return response;
        }
        finally
        {
            try { if (admitted) lock (_state) { _active = false; Expire(); } }
            finally { _interaction.Release(); }
        }
    }
}
