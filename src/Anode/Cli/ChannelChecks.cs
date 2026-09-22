using System.IO.Pipes;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Core.Util;

namespace Anode.Cli;

/// <summary>
/// Channels keep a development build away from the installed Anode, and the one seat a Windows
/// session allows goes to one channel at a time. The seat checks use a private mutex and made-up
/// pipe lists: claiming the real seat mutex here could stop a daemon of any channel from starting.
/// </summary>
internal static class ChannelChecks
{
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    public static string Names()
    {
        string channel = Env.Channel, pipe = Env.ControlPipe;
        string? variable = Environment.GetEnvironmentVariable("ANODE_CHANNEL");
        try
        {
            Env.SetChannel("Main");
            Require(Env.IsMainChannel && Env.ControlPipe == "anode-control" && Env.SeatPipe == "anode-seat"
                && Env.DaemonMutex == Env.MainDaemonMutex && Path.GetFileName(Env.StateDirectory) == "Anode"
                && Env.ChannelSuffix.Length == 0, "the main channel lost the names earlier versions use");
            Env.SetChannel("dev");
            Require(Env.ControlPipe == "anode-control-dev" && Env.SeatPipe == "anode-seat-dev"
                && Env.DaemonMutex == Env.MainDaemonMutex + ".dev" && Path.GetFileName(Env.StateDirectory) == "Anode-dev"
                && Env.ChannelSuffix == " (dev)", "the dev channel shares a name with the main channel");
            Require(Environment.GetEnvironmentVariable("ANODE_CHANNEL") == (Env.BuildChannel == "dev" ? null : "dev"),
                "programs this process starts would not inherit its channel");
            foreach (string name in new[] { "", " ", "-dev", "dev channel", @"dev\x", "dév", new string('a', 33) })
            {
                bool refused = false;
                try { Env.SetChannel(name); }
                catch (ArgumentException) { refused = true; }
                Require(refused && Env.Channel == "dev", $"the channel name '{name}' was accepted");
            }
        }
        finally
        {
            Env.SetChannel(channel);
            Environment.SetEnvironmentVariable("ANODE_CHANNEL", variable);
        }
        Require(Env.Channel == channel && Env.ControlPipe == pipe, "the check did not restore this process's channel");
        return "main keeps its names; other channels get their own pipes, mutex, logs and title";
    }

    public static string Seat()
    {
        string[] main = { "InitShutdown", "anode-control", "anode-seat", "anode-selftest-1" };
        Require(SeatSlot.OtherChannel("dev", main, mainDaemon: true) == ("main", true), "dev missed a running main Anode");
        Require(SeatSlot.OtherChannel("dev", new[] { "anode-seat" }, false) == ("main", false), "dev missed a seat main kept");
        Require(SeatSlot.OtherChannel("dev", new[] { "anode-seat", "anode-control" }, false) == ("main", true),
            "a kept seat hid a running main Anode");
        Require(SeatSlot.OtherChannel("dev", Array.Empty<string>(), mainDaemon: true) == ("main", true),
            "dev missed a main Anode that serves no pipe yet, or predates channels");
        Require(SeatSlot.OtherChannel("main", new[] { "anode-control-dev", "anode-seat-dev" }, false) == ("dev", true),
            "main missed a running dev Anode");
        Require(SeatSlot.OtherChannel("main", main, mainDaemon: true) is null
            && SeatSlot.OtherChannel("dev", new[] { "anode-control-dev", "anode-seat-dev", "anode-controller", "anode-seat-a b" }, false) is null,
            "a channel counted itself or an unrelated pipe");

        Require(SeatSlot.Taken(("main", true)).StartsWith("The main Anode is running this Windows session's seat.", StringComparison.Ordinal)
            && SeatSlot.Taken(("main", true)).Contains("one seat per session", StringComparison.Ordinal)
            && SeatSlot.Taken(("dev", false)).StartsWith("The dev Anode kept its seat signed in", StringComparison.Ordinal)
            && SeatSlot.Taken(null).StartsWith("Another Anode has", StringComparison.Ordinal), "a refusal does not say who has the seat");

        // Daemons claim on their own threads; a mutex lets its owning thread claim it again.
        string mutex = $@"Local\Anode.Seat.selftest-{Guid.NewGuid():N}";
        IDisposable? OnThread(Func<(string, bool)?> other, out string? blocker, bool release = true)
        {
            IDisposable? claim = null;
            string? reason = null;
            var thread = new Thread(() =>
            {
                claim = SeatSlot.Claim(mutex, other, out reason);
                if (release) claim?.Dispose();
            });
            thread.Start();
            thread.Join();
            blocker = reason;
            return claim;
        }

        using (var first = SeatSlot.Claim(mutex, () => null, out string? free))
        {
            Require(first is not null && free is null, "a free seat was refused");
            Require(OnThread(() => null, out string? held) is null && held?.StartsWith("Another Anode", StringComparison.Ordinal) == true,
                "a claimed seat was claimed again");
        }
        Require(OnThread(() => ("main", true), out string? other) is null && other?.StartsWith("The main Anode", StringComparison.Ordinal) == true,
            "a daemon claimed the seat while another channel ran one");
        Require(OnThread(() => null, out _) is not null, "a refused claim kept the seat");
        OnThread(() => null, out _, release: false); // a daemon that crashed
        Require(OnThread(() => null, out _) is not null, "a seat left by a crashed daemon stayed taken");

        string name = $"anode-selftest-{Guid.NewGuid():N}";
        using (new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            Require(SeatSlot.Pipes().Contains(name, StringComparer.OrdinalIgnoreCase), "Windows' named pipes could not be listed");

        // An agent hears who has the seat, and a later, different problem replaces that answer.
        string? setup = null;
        int launches = 0;
        using var server = new Mcp.McpServer($"anode-selftest-{Guid.NewGuid():N}",
            () => { launches++; throw new SeatTakenException(SeatSlot.Taken(("main", true))); }, () => setup) { ConnectTimeoutMs = 50 };
        var refused = server.CallAsync("seat_start", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
        Require(refused.Bool("isError") == true && launches == 1
            && McpChecks.Text(refused).StartsWith("The main Anode is running", StringComparison.Ordinal), "seat_start hid who has the seat");
        setup = "Child sessions are off.";
        var blocked = server.CallAsync("seat_start", new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();
        Require(blocked.Bool("isError") == true && launches == 1
            && McpChecks.Text(blocked).StartsWith(setup, StringComparison.Ordinal), "a later setup problem repeated the old refusal");
        return "one channel at a time holds the seat; refusals, MCP's included, name the channel that has it";
    }
}
