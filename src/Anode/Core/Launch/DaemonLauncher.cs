using System.Diagnostics;
using System.Text;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Core.Launch;

/// <summary>Shared by the CLI and MCP so the daemon can outlive either client.</summary>
internal static class DaemonLauncher
{
    /// <exception cref="SeatTakenException">Another channel's Anode has this Windows session's seat.</exception>
    public static void Launch(string[] extra)
    {
        if (SeatSlot.Blocker() is { } taken) throw new SeatTakenException(taken);
        Env.ResolveStateDirectory();
        // The Task Scheduler passes no environment, so the channel travels as an argument.
        string[] launchArguments = new[] { "--channel", Env.Channel, "up", "--state-dir", Env.StateDirectory }.Concat(extra).ToArray();
        string arguments = string.Join(' ', launchArguments.Select(Quote));
        try
        {
            SeatLauncher.LaunchInSession(
                ChildSession.CurrentSessionId(), Env.ExecutablePath, arguments, AppContext.BaseDirectory);
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not start the daemon through the Task Scheduler ({ex.Message}); starting it as a child process");
        }

        var info = new ProcessStartInfo
        {
            FileName = Env.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (string argument in launchArguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start the Anode daemon.");
    }

    // Windows command-line quoting, including empty arguments and backslashes
    // immediately before a quote or the closing delimiter.
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
}
