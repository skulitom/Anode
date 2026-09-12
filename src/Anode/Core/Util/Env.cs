using System.Text;

namespace Anode.Core.Util;

/// <summary>Well-known paths, pipe names and a very small log.</summary>
internal static class Env
{
    /// <summary>Control pipe: the daemon serves it, the CLI and the MCP server talk to it.</summary>
    public const string ControlPipe = "anode-control";

    /// <summary>Seat pipe: the seat host inside the child session serves it, the daemon talks to it.</summary>
    public const string SeatPipe = "anode-seat";

    /// <summary>Mutex proving a daemon already owns the seat.</summary>
    public const string DaemonMutex = @"Local\Anode.Daemon";

    public static string StateDirectory { get; } = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "Anode");

    public static string LogPath => Path.Combine(StateDirectory, "anode.log");

    /// <summary>
    /// Full path to anode.exe. The daemon relaunches this same file into the child
    /// session, so it has to be the apphost and not a managed assembly path (which is
    /// empty in a single-file build).
    /// </summary>
    public static string ExecutablePath =>
        System.Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "anode.exe");

    public static void EnsureStateDirectory() => Directory.CreateDirectory(StateDirectory);
}

/// <summary>
/// Append-only log shared by every role. Writes are best effort: a seat that cannot
/// log is still a seat, and losing a log line must never take the seat down.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string _role = "anode";

    /// <summary>When true, lines also go to stderr. The CLI wants this; the daemon does not.</summary>
    public static bool Echo { get; set; }

    public static void SetRole(string role) => _role = role;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    public static void Error(string message, Exception ex) =>
        Write("ERR ", $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{_role}] {message}";
        if (Echo)
        {
            try { Console.Error.WriteLine(line); } catch { /* no console */ }
        }

        try
        {
            lock (Gate)
            {
                Env.EnsureStateDirectory();
                File.AppendAllText(Env.LogPath, line + System.Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is never allowed to be the reason something fails.
        }
    }
}
