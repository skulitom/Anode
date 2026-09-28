using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Cli;

internal static partial class Cli
{
    private static async Task<int> AudioCommand(string[] args)
    {
        string action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        if (action is not ("status" or "listen" or "play" or "stop"))
            throw new UsageException("audio takes status, listen <file.wav>, play <file.wav>, or stop.");
        var request = new JsonObject();
        string? target = null;
        bool json = false;
        int index = args.Length == 0 ? 0 : 1;
        if (action is "listen" or "play")
        {
            if (index >= args.Length || args[index].StartsWith("--")) throw new UsageException($"audio {action} requires a WAV file path.");
            target = Path.GetFullPath(args[index++]);
            if (action == "play") request["path"] = target;
        }
        for (; index < args.Length; index++)
        {
            if (args[index] == "--json") { json = true; continue; }
            if (action == "listen" && args[index] == "--ms" && ++index < args.Length && int.TryParse(args[index], out int ms))
            { request["durationMs"] = ms; continue; }
            throw new UsageException("Use audio listen <file.wav> [--ms 5000] [--json], audio play <file.wav> [--json], or audio status|stop [--json].");
        }
        string tool = "seat_audio_" + action;
        if (Mcp.Tools.ValidateArguments(tool, request) is { } error) throw new UsageException(CliError(error));
        var envelope = Core.Agents.AgentAccess.Attach(request, _agentId, _leaseToken);
        envelope["op"] = "audio." + action;
        if (Core.Agents.AgentAccess.Validate(envelope) is { } ownerError) throw new UsageException(ownerError);
        // Reserve the output before taking a lease or recording. Existing recordings are never overwritten.
        using var output = action == "listen" ? new FileStream(target!, FileMode.CreateNew, FileAccess.Write, FileShare.None) : null;
        bool saved = false;
        try
        {
            using var client = await Connect(autoStart: false);
            if (client is null) return NotRunning();
            var response = await RequestAsync(client, "audio." + action, request, 60000);
            if (response.Bool("ok") != true) return Report(response);
            var result = response.Obj("result")!;
            if (output is not null)
            {
                await output.WriteAsync(Convert.FromBase64String(result.Str("data")!));
                await output.FlushAsync();
                saved = true;
                result.Remove("data");
                result["path"] = target;
                result["summary"] = result.Str("summary") + " Saved to " + target;
            }
            Console.WriteLine(json ? result.ToJsonString(Indented) : result.Str("summary"));
            return 0;
        }
        finally
        {
            if (output is not null && !saved)
            {
                output.Dispose();
                File.Delete(target!); // Only the file reserved with CreateNew by this invocation.
            }
        }
    }
}
