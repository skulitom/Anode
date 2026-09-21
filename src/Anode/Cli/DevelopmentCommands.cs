using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Cli;

internal static partial class Cli
{
    private static async Task<int> DevelopmentCommand(string command, string[] args)
    {
        string tool = command switch { "capabilities" => "seat_capabilities", "exec" => "seat_exec",
            "wait" => "seat_wait", _ => "seat_job" };
        var request = new JsonObject();
        bool json = false;
        int index = 0;
        if (command is "job" or "wait" && args.FirstOrDefault() is { } id && !id.StartsWith("--"))
        { request[command == "job" ? "jobId" : "windowId"] = id; index++; }
        if (command == "jobs") request["action"] = "list";
        var allowed = command switch
        {
            "exec" => new[] { "cwd", "timeout", "wait", "env" },
            "job" => new[] { "after", "wait", "max-chars" },
            "wait" => new[] { "automation-id", "name", "role", "text", "state", "wait" },
            _ => Array.Empty<string>()
        };
        for (; index < args.Length; index++)
        {
            string option = args[index];
            if (option == "--" && command == "exec")
            {
                if (++index >= args.Length) throw new UsageException("exec requires a program after --.");
                request["path"] = args[index++];
                request["args"] = new JsonArray(args.Skip(index).Select(a => (JsonNode)a).ToArray());
                break;
            }
            if (option == "--json") { json = true; continue; }
            if (option == "--no-capture" && command == "capabilities") { request["probeCapture"] = false; continue; }
            if (option == "--cancel" && command == "job") { request["action"] = "cancel"; continue; }
            if (command == "exec" && !option.StartsWith('-'))
                throw new UsageException($"Put -- before the program: anode exec [options] -- {option} [args].");
            if (!option.StartsWith("--") || !allowed.Contains(option[2..])) throw new UsageException($"Unknown {command} option '{option}'. Run `anode help {command}`.");
            if (++index >= args.Length) throw new UsageException($"{option} requires a value.");
            string value = args[index];
            string field = option switch { "--timeout" => "executionTimeoutMs", "--wait" => "waitMs", "--max-chars" => "maxChars",
                "--automation-id" => "automationId", "--text" => "textContains", _ => option[2..] };
            if (field == "env")
            {
                int split = value.IndexOf('=');
                if (split < 1) throw new UsageException("--env requires NAME=VALUE.");
                request["env"] ??= new JsonObject();
                request["env"]![value[..split]] = value[(split + 1)..];
            }
            else if (field is "executionTimeoutMs" or "waitMs" or "maxChars")
            {
                if (!int.TryParse(value, out int number)) throw new UsageException($"{option} requires an integer.");
                request[field] = number;
            }
            else request[field] = value;
        }
        if (command == "exec" && !request.ContainsKey("path")) throw new UsageException("exec requires -- and then the program to run.");
        if (Mcp.Tools.ValidateArguments(tool, request) is { } error) throw new UsageException(CliError(error));
        Mcp.Tools.TryResolve(tool, out string op, out _);
        using var client = await Connect(autoStart: false);
        if (client is null) return NotRunning();
        var response = await RequestAsync(client, op, request, 45000);
        if (response.Bool("ok") != true) return Report(response);
        var result = response.Obj("result")!;
        Console.WriteLine(json ? result.ToJsonString(Indented) : result.Str("summary"));
        if (command == "wait") return result.Bool("matched") == true ? 0 : 3;
        if (command is "exec" or "job") return result.Str("state") switch
        {
            "completed" => result.Int("exitCode") ?? 1, "timed_out" => 124, "cancelled" => 130, "failed" => 1, _ => 0
        };
        return 0;
    }
}
