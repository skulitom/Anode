using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Agents;
using Anode.Core.Bridge;

namespace Anode.Cli;

internal static partial class Cli
{
    private static string? _agentId, _leaseToken, _agentName;

    private static string[] AgentOptions(string[] args)
    {
        // A blank variable is unset, not an invalid identity.
        static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { } value
            && !string.IsNullOrWhiteSpace(value) ? value : null;
        _agentId = Variable("ANODE_AGENT_ID");
        _leaseToken = Variable("ANODE_LEASE_TOKEN");
        _agentName = Variable("ANODE_AGENT_NAME")?.Trim() is { } name && AgentAccess.ValidName(name) ? name : null;
        int index = 0;
        // Prefix options cannot consume literal text or arguments passed to a launched program.
        while (index < args.Length && args[index] is "--agent" or "--lease")
        {
            string option = args[index++];
            if (index == args.Length) throw new ArgumentException($"{option} requires a value before the command.");
            if (option == "--agent") _agentId = args[index++];
            else _leaseToken = args[index++];
        }
        return args[index..];
    }

    private static Task<JsonObject> RequestAsync(JsonPipeClient client, string op, JsonObject? arguments = null,
        int timeoutMs = 60000, CancellationToken cancel = default)
    {
        // Human emergency controls do not depend on an agent's environment or lease.
        var payload = op is "seat.stop" or "quit" ? arguments ?? new JsonObject()
            : AgentAccess.Attach(arguments, _agentId, _leaseToken);
        if (op == "lease" && _agentName is not null) payload["agentName"] = _agentName;
        payload["op"] = op;
        if (AgentAccess.Validate(payload) is { } error) return Task.FromResult(JsonLine.Fail(error));
        payload.Remove("op");
        if (Mcp.Tools.ValidateOperation(op, AgentAccess.Arguments(payload)) is { } invalid)
            return Task.FromResult(JsonLine.Fail(invalid));
        return client.RequestAsync(op, payload, timeoutMs, cancel);
    }

    private static async Task<int> LeaseCommand(string[] args)
    {
        var payload = new JsonObject { ["action"] = args.FirstOrDefault() ?? "status" };
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--cancel-jobs") payload["cancelJobs"] = true;
            else if (args[i] == "--ttl")
            {
                if (++i >= args.Length || !int.TryParse(args[i], out int ttl)) throw new UsageException("--ttl requires a number of seconds.");
                payload["ttlSeconds"] = ttl;
            }
            else if (args[i] == "--wait")
            {
                if (++i >= args.Length || !int.TryParse(args[i], out int seconds)) throw new UsageException("--wait requires a number of seconds.");
                payload["waitSeconds"] = seconds;
            }
            else throw new UsageException($"Unknown lease option '{args[i]}'. Run `anode help lease`.");
        }
        if (Mcp.Tools.ValidateArguments("seat_lease", payload) is { } invalid) throw new UsageException(CliError(invalid));
        var envelope = AgentAccess.Attach(payload, _agentId, _leaseToken);
        envelope["op"] = "lease";
        if (AgentAccess.Validate(envelope) is { } error) throw new UsageException(error);
        bool acquire = payload.Str("action") == "acquire";
        using var client = await Connect(autoStart: acquire);
        // A failed acquisition has already said why the daemon could not start; 3 matches `start`.
        if (client is null) return acquire ? (_startBlocked ? 3 : 1) : NotRunning();
        if (!acquire || payload.Int("waitSeconds") is not int wait || wait == 0)
            return Report(await RequestAsync(client, "lease", payload, acquire ? 180000 : 60000));

        // Ask about once a second; each ask keeps this agent's place in line until the wait ends.
        var waited = Stopwatch.StartNew();
        bool told = false;
        while (true)
        {
            int left = wait - (int)waited.Elapsed.TotalSeconds;
            payload["waitSeconds"] = Math.Max(0, left);
            var response = await RequestAsync(client, "lease", payload, 180000);
            if (response.Bool("ok") == true || response.Str("errorCode") != "seat_busy" || left <= 0 || !client.IsConnected)
                return Report(response);
            if (!told) { Console.Error.WriteLine(response.Str("error")); told = true; }
            await Task.Delay(1000);
        }
    }
}
