using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Core.Util;

namespace Anode.Core.Steam;

/// <summary>
/// steam.launch, run by the seat host. The game always runs in the seat; what varies is the Steam
/// client it uses. Normally that is the client outside the seat, usually on the user's desktop,
/// reached through <see cref="SteamBridge"/>, so Steam never moves. When Steam is not running at
/// all, Anode starts it on the desktop first. A client already running in the seat is used as it
/// is, and force launches through a client in the seat even though that takes Steam over from the
/// desktop, as launches did before the bridge.
/// </summary>
internal static class SteamLaunch
{
    private const int PollMs = 1000;
    private const int SeatClientWarmupMs = 4000;
    // Steam fetches the account's licences just after it signs in, and a game checks its own as it starts.
    private const int SettleMs = 5000;

    /// <summary>Everything a launch does to the machine, so checks can stand in for it.</summary>
    internal sealed record Machine(
        Func<SteamClient?> Client,
        Func<string?> SteamExecutable,
        Action<uint, string> StartOnDesktop,
        Func<string, int, string?, SteamGame> FindGame,
        Func<uint, int, string> Connect,
        Func<string, SteamReady> Ready,
        Func<ProcessStartInfo, Process?> Start,
        Action<int> Pause,
        Func<long> Milliseconds)
    {
        public static Machine Real { get; } = new(
            Steam.ActiveClient,
            Steam.FindExecutable,
            // The seat host cannot start a program in another session itself; the Task Scheduler
            // starts it there as this user, as it started the seat host in the seat.
            (session, steamExe) => SeatLauncher.LaunchInSession(session, steamExe, "-silent", Path.GetDirectoryName(steamExe)!,
                purpose: $"start Steam for Anode in session {session}"),
            SteamLibrary.Find,
            SteamBridge.Connect,
            // Only a client whose handshake objects exist is worth asking.
            name => SteamBridge.Listening(name) ? SteamReadiness.Ask(name, timeoutMs: 5000) : SteamReady.Unavailable,
            Process.Start,
            Thread.Sleep,
            () => Environment.TickCount64);
    }

    public static JsonObject Run(JsonObject request, uint seat, uint? desktop, Machine machine)
    {
        int appId = request.Int("appId") ?? throw new ArgumentException("steam.launch needs 'appId'.");
        string? executable = request.Str("exe");
        var extra = new List<string>();
        if (request["args"] is JsonArray arguments)
            foreach (var argument in arguments)
                if (argument is not null) extra.Add(argument.ToString());
        // Reply before the daemon's forwarding deadline, which the caller may extend with timeoutMs.
        long deadline = machine.Milliseconds() + Math.Clamp((request.Int("timeoutMs") ?? 60_000) - 15_000, 2_000, 150_000);
        string? steamExe = machine.SteamExecutable();
        if (steamExe is null)
            return JsonLine.Fail("Steam was not found. Install Steam, or start the game's executable with `anode run` (agents: seat_run).");

        var client = machine.Client();
        if (request.Bool("force") == true) return InSeat(appId, extra, executable, seat, client, steamExe, machine);

        bool started = false;
        if (client is null)
        {
            if (desktop is not uint home)
                return JsonLine.Fail("Steam is not running, and this seat host does not know which session is your desktop. "
                    + "Start Steam on your desktop, then launch the game again.");
            machine.StartOnDesktop(home, steamExe);
            started = true;
            Log.Info($"started Steam on the desktop (session {home}) for steam app {appId}");
            client = Await<SteamClient>(() => machine.Client() is { } c && c.Session != (int)seat ? c : null, deadline, machine);
            if (client is null)
                return JsonLine.Fail("Steam did not start on your desktop. Start it there, then launch the game again.");
        }
        if (client.Value.Session == (int)seat) return InSeat(appId, extra, executable, seat, client, steamExe, machine);
        var steam = client.Value;

        SteamGame game;
        string name;
        try
        {
            game = machine.FindGame(Path.GetDirectoryName(steamExe)!, appId, executable);
            name = machine.Connect(seat, steam.Session);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return JsonLine.Fail(ex.Message);
        }

        // Steam's records claim an account before a game could use it, and keep one after an unclean
        // exit, so ask the client the way the game will, through the bridge.
        SteamReady ready = machine.Ready(name);
        bool waited = false;
        while (ready != SteamReady.Online && machine.Milliseconds() < deadline)
        {
            waited = true;
            machine.Pause(PollMs);
            ready = machine.Ready(name);
        }
        if (ready == SteamReady.Unavailable)
            return JsonLine.Fail(started
                ? "Steam is starting on your desktop but is not ready for games yet. If it asks you to sign in, do that, then launch the game again."
                : $"Steam in session {steam.Session} is not ready for games: it may be starting, updating or signed out. Sign in there if it asks, then launch the game again.");
        if (waited && ready == SteamReady.Online) machine.Pause(SettleMs);

        var info = new ProcessStartInfo
        {
            FileName = game.Executable,
            WorkingDirectory = game.WorkingDirectory,
            UseShellExecute = false,
            Arguments = string.Join(" ", new[] { game.Arguments }.Where(a => a.Length > 0).Concat(extra.Select(DaemonLauncher.Quote)))
        };
        foreach (var (key, value) in SteamBridge.Environment(appId, name)) info.Environment[key] = value;
        using var process = machine.Start(info);
        Log.Info($"seat launched steam app {appId} using the Steam client in session {steam.Session}: {game.Executable}");
        return JsonLine.Ok(new JsonObject
        {
            ["appId"] = appId,
            ["session"] = seat,
            ["pid"] = process?.Id,
            ["path"] = game.Executable,
            ["steamSession"] = steam.Session,
            ["bridged"] = true,
            ["note"] = $"{game.Name} runs in the seat and uses Steam in session {steam.Session}, which stays where it is."
                + (ready == SteamReady.Offline ? " Steam is not connected to the Steam network there, so the game may run offline." : "")
        });
    }

    /// <summary>The launch through a Steam client in the seat, which starts one there if Steam is not running.</summary>
    private static JsonObject InSeat(int appId, List<string> extra, string? executable, uint seat, SteamClient? client,
        string steamExe, Machine machine)
    {
        if (client is null)
        {
            using (machine.Start(Steam.ClientStart(steamExe))) { }
            machine.Pause(SeatClientWarmupMs);
        }
        using (machine.Start(Steam.AppLaunch(steamExe, appId, extra))) { }
        Log.Info($"seat launched steam app {appId} through a Steam client in the seat");

        string note = client is { } other && other.Session != (int)seat
            ? $"Launched through a Steam client in the seat, as force asked. It takes Steam over from session {other.Session}."
            : "Steam runs inside the seat, so the game opens there.";
        if (executable is not null) note += " exe is used only with a Steam client outside the seat, so Steam chose the program.";
        return JsonLine.Ok(new JsonObject
        {
            ["appId"] = appId,
            ["session"] = seat,
            ["steamSession"] = seat,
            ["bridged"] = false,
            ["note"] = note
        });
    }

    private static T? Await<T>(Func<T?> probe, long deadline, Machine machine) where T : struct
    {
        while (true)
        {
            if (probe() is { } found) return found;
            if (machine.Milliseconds() >= deadline) return null;
            machine.Pause(PollMs);
        }
    }
}
