using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Agents;

/// <summary>Cooperative ownership within one Windows identity, not an authentication boundary.</summary>
internal static class AgentAccess
{
    public static bool RequiresLease(string op) => op.StartsWith("input.", StringComparison.Ordinal)
        || op.StartsWith("desktop.", StringComparison.Ordinal) && op != "desktop.capabilities"
        || op.StartsWith("gamepad.", StringComparison.Ordinal) && op != "gamepad.state"
        || op is "screenshot" or "run" or "steam.launch" or "ps.kill" or "exec.start";

    public static bool ValidId(string? value) => value is { Length: > 0 and <= 80 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>A readable owner for status and the viewer, such as the MCP client's name.</summary>
    public static bool ValidName(string? value) => value is { Length: > 0 and <= 64 } && !value.Any(char.IsControl);

    public static JsonObject Arguments(JsonObject request)
    {
        var args = (JsonObject)request.DeepClone();
        foreach (string key in new[] { "op", "id", "timeoutMs", "agentId", "leaseToken", "agentName", "startSeat" }) args.Remove(key);
        return args;
    }

    public static string? Validate(JsonObject request)
    {
        string op = request.Str("op") ?? "";
        if (request.ContainsKey("agentId") && (request["agentId"] is not JsonValue agent
            || !agent.TryGetValue<string>(out var id) || !ValidId(id)))
            return "agentId must contain 1-80 ASCII letters, digits, dots, underscores or hyphens.";
        if (request.ContainsKey("leaseToken") && (request["leaseToken"] is not JsonValue lease
            || !lease.TryGetValue<string>(out var token) || token.Length is 0 or > 128))
            return "leaseToken must be a returned lease token.";
        if (request.ContainsKey("agentName") && (request["agentName"] is not JsonValue named
            || !named.TryGetValue<string>(out var name) || !ValidName(name)))
            return "agentName must contain 1-64 printable characters.";
        if (request.ContainsKey("startSeat") && request["startSeat"]?.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            return "startSeat must be true or false.";
        if ((RequiresLease(op) || op is "exec.read" or "lease") && !ValidId(request.Str("agentId")))
            return "An agentId is required. MCP supplies one; CLI users must set ANODE_AGENT_ID or use --agent ID before the command.";
        if ((RequiresLease(op) || op == "lease" && request.Str("action") is "renew" or "release") && request.Str("leaseToken") is null)
            return "Acquire the desktop with seat_lease action=acquire (CLI: anode lease acquire) before desktop work, then send its leaseToken.";
        if (request.ContainsKey("timeoutMs") && (request["timeoutMs"] is not JsonValue timeout
            || !timeout.TryGetValue<int>(out int ms) || ms is < 1 or > 180000))
            return "timeoutMs must be an integer from 1 to 180000.";
        return null;
    }

    public static JsonObject Attach(JsonObject? arguments, string? agentId, string? leaseToken)
    {
        var args = arguments is null ? new JsonObject() : (JsonObject)arguments.DeepClone();
        if (agentId is not null) args["agentId"] = agentId;
        if (leaseToken is not null) args["leaseToken"] = leaseToken;
        return args;
    }
}
