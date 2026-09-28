using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Anode.Core.Steam;

/// <summary>The Steam client this Windows user runs: its process, its session and whether an account is signed in.</summary>
internal readonly record struct SteamClient(int Pid, int Session, bool SignedIn);

/// <summary>
/// Locating Steam and its games. The seat shares the parent's Windows user, and Steam keeps one
/// client per user: a client started in one session takes over from the one in another. Games
/// launched through Anode therefore use the client where it already runs (see <see cref="SteamLaunch"/>),
/// and a Steam client starts in the seat only when asked to or when it already runs there.
/// <see cref="Describe"/> says which case applies, so callers can say so plainly.
/// </summary>
internal static class Steam
{
    public static string? FindExecutable()
    {
        foreach (var (hive, path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
                 })
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;

            if (key.GetValue("SteamExe") is string exe && File.Exists(Normalize(exe)))
                return Normalize(exe);

            if (key.GetValue("SteamPath") is string dir)
            {
                string candidate = Path.Combine(Normalize(dir), "steam.exe");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(Normalize(dir), "Steam.exe");
                if (File.Exists(candidate)) return candidate;
            }

            if (key.GetValue("InstallPath") is string install)
            {
                string candidate = Path.Combine(Normalize(install), "steam.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Replace('/', '\\'));

    internal static string? DirectLaunchFailure(string path, uint seatSession, Func<int[]>? readSessions = null)
    {
        string target = path.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) target = uri.LocalPath;
            else if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase)) target = "steam.exe";
        }
        string fileName = Path.GetFileName(target);
        bool steam = fileName.Equals("steam", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("steam.exe", StringComparison.OrdinalIgnoreCase);
        if (!steam) return null;

        int[] elsewhere = (readSessions ?? RunningSessions)().Where(id => id != (int)seatSession).Distinct().ToArray();
        return elsewhere.Length == 0 ? null
            : $"Steam is already running outside the seat in session {string.Join(", ", elsewhere)}. "
            + "A Steam client or Steam URL started in the seat would take Steam over from that session, so the launch was refused. "
            + "Launch Steam games with `anode steam <appid>` (agents: steam_launch): they run in the seat and use the running client.";
    }

    /// <summary>The session ids Steam is currently running in, if any.</summary>
    public static int[] RunningSessions()
    {
        var sessions = new List<int>();
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            try { sessions.Add(process.SessionId); } catch { }
            finally { process.Dispose(); }
        }
        return sessions.Distinct().ToArray();
    }

    /// <summary>
    /// The Steam client this user runs, from the record Steam keeps under HKCU while it runs, or the
    /// running steam.exe when that record is stale. Null when Steam is not running.
    /// </summary>
    public static SteamClient? ActiveClient()
    {
        int pid = 0;
        bool signedIn = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            pid = key?.GetValue("pid") is int id ? id : 0;
            signedIn = key?.GetValue("ActiveUser") is int user && user != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }

        if (pid > 0 && SessionOf(pid) is int session) return new SteamClient(pid, session, signedIn);

        // Steam leaves its last process id behind when it exits, and a client that has not written
        // its record yet has not signed in either.
        SteamClient? found = null;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            try { found ??= new SteamClient(process.Id, process.SessionId, false); }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return found;
    }

    private static int? SessionOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase) ? process.SessionId : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>Where Steam runs and what a launch through Anode will do about it.</summary>
    internal static string Describe(uint seatSession, uint? desktopSession, SteamClient? client)
    {
        if (client is not { } steam)
            return "Steam is not running. A Steam game launched through Anode starts Steam on your desktop, minimized, "
                + "and runs in the seat.";
        if (steam.Session == (int)seatSession)
            return $"Steam is running inside the seat (session {seatSession}), so its games open there. "
                + "To keep Steam on your desktop instead, close it in the seat and start it on your desktop.";
        string place = desktopSession is uint desktop && steam.Session == (int)desktop
            ? $"on your desktop (session {steam.Session})"
            : $"in session {steam.Session}, outside the seat";
        return $"Steam is running {place}. Steam games launched through Anode run in the seat and use that client, "
            + "so Steam stays where it is." + (steam.SignedIn ? "" : " It is not signed in yet; sign in to Steam there first.");
    }

    /// <summary>Starts the Steam client itself, minimized, in the session of whoever runs it.</summary>
    internal static ProcessStartInfo ClientStart(string steamExe) => new()
    {
        FileName = steamExe,
        WorkingDirectory = Path.GetDirectoryName(steamExe)!,
        UseShellExecute = false,
        ArgumentList = { "-silent" }
    };

    /// <summary>Asks the Steam client in the caller's session to launch an app.</summary>
    internal static ProcessStartInfo AppLaunch(string steamExe, int appId, IEnumerable<string> gameArguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = Path.GetDirectoryName(steamExe)!,
            UseShellExecute = false,
            ArgumentList = { "-applaunch", appId.ToString(CultureInfo.InvariantCulture) }
        };
        foreach (string argument in gameArguments) info.ArgumentList.Add(argument);
        return info;
    }
}
