using System.Diagnostics;

namespace Anode.Cli;

internal static partial class Cli
{
    private static int Guide(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--json"))
        {
            Console.Error.WriteLine("usage: anode guide [--json]");
            return 2;
        }
        Console.WriteLine(args.Length == 0 ? Mcp.AgentGuide.Text : Mcp.AgentGuide.Info().ToJsonString());
        return 0;
    }

    private static int Configure(string[] args)
    {
        const string usage = "usage: anode configure [auto|codex|claude|both] [--no-skill]";
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(usage);
            Console.WriteLine("Registers Anode and its discoverable desktop skill for installed Codex/Claude Code CLIs, with backups. Default: auto.");
            Console.WriteLine("Use --no-skill for MCP registration only. Existing customized skills are preserved.");
            Console.WriteLine("Does not start a seat or change machine settings. Restart your agent afterward.");
            return 0;
        }
        bool noSkill = args.Contains("--no-skill", StringComparer.Ordinal);
        var positional = args.Where(arg => arg != "--no-skill").ToArray();
        string client = positional.FirstOrDefault()?.ToLowerInvariant() ?? "auto";
        if (positional.Length > 1 || args.Length - positional.Length > 1 || client is not ("auto" or "codex" or "claude" or "both"))
        {
            Console.Error.WriteLine(usage);
            return 2;
        }

        string script = Path.Combine(AppContext.BaseDirectory, "connect-agents.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("The connector script is missing. Extract the complete Anode release archive next to anode.exe.", script);
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
