using System.Diagnostics;
using Anode.Core.Util;

namespace Anode.Cli;

internal static partial class Cli
{
    private static int Guide(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--json"))
            throw new UsageException("guide takes only --json.");
        Console.WriteLine(args.Length == 0 ? Mcp.AgentGuide.Text : Mcp.AgentGuide.Info().ToJsonString());
        return 0;
    }

    /// <summary>The client and skill choice, or a usage error before anything is registered.</summary>
    internal static (string Client, bool NoSkill) ConfigureArguments(string[] args)
    {
        bool noSkill = args.Contains("--no-skill", StringComparer.Ordinal);
        var positional = args.Where(arg => arg != "--no-skill").ToArray();
        string client = positional.FirstOrDefault()?.ToLowerInvariant() ?? "auto";
        if (positional.Length > 1 || args.Length - positional.Length > 1 || client is not ("auto" or "codex" or "claude" or "both"))
            throw new UsageException("configure takes one client (auto, codex, claude or both) and optionally --no-skill.");
        return (client, noSkill);
    }

    // `configure --help` is answered by the shared help table before this runs.
    private static int Configure(string[] args)
    {
        var (client, noSkill) = ConfigureArguments(args);
        // configure writes the clients' one "anode" entry, which belongs to the main Anode.
        if (!Env.IsMainChannel)
        {
            Console.Error.WriteLine($"configure registers the main Anode, and this is the {Env.Channel} channel. Register a "
                + $"{Env.Channel} build under its own name by hand: {Links.Repository}/blob/main/docs/DEVELOPMENT-TESTING.md#a-separate-dev-anode");
            return 2;
        }
        string script = Path.Combine(AppContext.BaseDirectory, "connect-agents.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("The connector script is missing. Extract the complete release next to anode.exe, "
                + $"or register manually with `{Environment.ProcessPath} mcp`: {Links.Connecting}#3-any-other-mcp-client", script);
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-Client", client, "-Anode", Environment.ProcessPath! })
            start.ArgumentList.Add(argument);
        if (noSkill) start.ArgumentList.Add("-NoSkill");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the connector script.");
        Task.WhenAll(
            process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()),
            process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError())).GetAwaiter().GetResult();
        process.WaitForExit();
        return process.ExitCode;
    }
}
