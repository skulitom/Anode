using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Agents;

/// <summary>Fences queued desktop work and prevents transfer while an admitted action is running.</summary>
internal sealed class DesktopLease
{
    private readonly object _state = new();
    private readonly SemaphoreSlim _interaction = new(1, 1);
    private readonly Func<long> _milliseconds;
    private readonly Action _invalidateReferences;
    private readonly Func<string, int> _cancelJobs;
    private string? _owner, _token;
    private long _expires;
    private bool _active;

    public DesktopLease(Action? invalidateReferences = null, Func<string, int>? cancelJobs = null, Func<long>? milliseconds = null)
    {
        _invalidateReferences = invalidateReferences ?? (() => { });
        _cancelJobs = cancelJobs ?? (_ => 0);
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    }

    private void Expire()
    {
        if (!_active && _owner is not null && _expires <= _milliseconds()) EndLease();
    }

    private void EndLease()
    {
        // Called only with the state lock held and no admitted desktop operation.
        // If cleanup fails, keep the expired owner and refuse transfer until cleanup succeeds.
        _invalidateReferences();
        _owner = null; _token = null;
    }

    public void ExpireIdle() { lock (_state) Expire(); }

    private JsonObject State(string? caller = null, bool includeToken = false) => new()
    {
        ["agentId"] = caller, ["ownerAgentId"] = _owner,
        ["expiresInMs"] = _owner is null ? 0 : Math.Max(0, _expires - _milliseconds()),
        ["operationRunning"] = _active,
        ["leaseToken"] = includeToken ? _token : null,
        ["summary"] = _owner is null ? "The desktop is available. Acquire a lease before desktop work."
            : $"Desktop owned by {_owner}; {Math.Max(0, _expires - _milliseconds())} ms remaining."
    };

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
            return Fail("seat_busy", $"The desktop is busy with agent {_owner}. Wait until it releases the lease or the lease expires.", request.Str("agentId"));
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
                if (_owner is not null && (_owner != agent || _expires <= _milliseconds()))
                    return Fail("seat_busy", $"The desktop is busy with agent {_owner}. Retry after release or expiry; an in-flight action must finish before transfer.", agent);
                if (_owner is null)
                {
                    _owner = agent;
                    _token = "l_" + Guid.NewGuid().ToString("N");
                    _expires = _milliseconds() + (request.Int("ttlSeconds") ?? 120) * 1000L;
                }
                // An uncertain acquire can be recovered without silently extending its lifetime.
                return JsonLine.Ok(State(agent, includeToken: true));
            }
            if (Check(request) is { } error) return error;
            if (action == "renew")
            {
                _expires = _milliseconds() + (request.Int("ttlSeconds") ?? 120) * 1000L;
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
            }
            return await dispatch(request, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            try { if (admitted) lock (_state) { _active = false; Expire(); } }
            finally { _interaction.Release(); }
        }
    }
}
