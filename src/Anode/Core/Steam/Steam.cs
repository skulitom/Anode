using System.Diagnostics;
using Microsoft.Win32;

namespace Anode.Core.Steam;

/// <summary>
/// Locating and starting Steam games. Read the caveat: Steam is one instance per
/// Windows user, and the seat runs as the same user as the parent session. Whichever
/// session Steam started in is the session its games open in. So a game launched from
/// the seat lands in the seat only when Steam is not already running outside it.
/// <see cref="Describe"/> reports which case you are in so callers can say so plainly
/// instead of silently launching a game onto the user's own screen.
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
    /// A one-line, truthful account of where a game launched right now would open.
    /// </summary>
    public static string Describe(uint seatSession)
    {
        int[] sessions = RunningSessions();
        if (sessions.Length == 0)
            return "Steam is not running. Starting a game from the seat will start Steam in the seat, and the game opens in the seat.";
        if (sessions.All(id => id == (int)seatSession))
            return $"Steam is running inside the seat (session {seatSession}). Games will open in the seat.";
        return $"Steam is already running in session {string.Join(", ", sessions)}, outside the seat. "
             + "Steam allows one instance per Windows user, so a game launched now opens on your own screen, not in the seat. "
             + "Close Steam first, then launch it from the seat.";
    }

    /// <summary>
    /// Starts Steam itself inside the caller's session. Call this from the seat host
    /// before launching a game, so the seat owns the Steam instance.
    /// </summary>
    public static Process? StartClient(bool silent = true)
    {
        string exe = FindExecutable()
            ?? throw new FileNotFoundException("Steam was not found. Install Steam, or pass the game's executable to `anode run` instead.");

        var info = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        };
        if (silent) info.ArgumentList.Add("-silent");
        return Process.Start(info);
    }

    /// <summary>Launches an app id through the Steam client in the caller's session.</summary>
    public static Process? LaunchApp(int appId, IEnumerable<string>? gameArguments = null)
    {
        string exe = FindExecutable()
            ?? throw new FileNotFoundException("Steam was not found. Install Steam, or pass the game's executable to `anode run` instead.");

        var info = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-applaunch");
        info.ArgumentList.Add(appId.ToString());
        foreach (string argument in gameArguments ?? Array.Empty<string>())
            info.ArgumentList.Add(argument);

        return Process.Start(info);
    }
}
