using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

    public static string StateDirectory { get; private set; } = DefaultStateDirectory();
    private static bool _stateDirectoryResolved;

    private static string DefaultStateDirectory()
    {
        string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "AppData", "Local");
        return Path.GetFullPath(Path.Combine(local, "Anode"));
    }

    public static void SetStateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("--state-dir must be an absolute path.");
        StateDirectory = Path.GetFullPath(path);
        _stateDirectoryResolved = false;
    }

    public static string LogPath => Path.Combine(StateDirectory, "anode.log");

    /// <summary>
    /// Full path to anode.exe. The daemon relaunches this same file into the child
    /// session, so it has to be the apphost and not a managed assembly path (which is
    /// empty in a single-file build).
    /// </summary>
    public static string ExecutablePath =>
        System.Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "anode.exe");

    public static void EnsureStateDirectory()
    {
        Directory.CreateDirectory(StateDirectory);
    }

    public static void ResolveStateDirectory()
    {
        if (_stateDirectoryResolved) return;
        StateDirectory = ResolveDirectory(StateDirectory);
        _stateDirectoryResolved = true;
    }

    internal static string ResolveDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        // Packaged callers can see a redirected AppData folder. Task Scheduler
        // does not inherit that package identity, so forward the actual directory.
        // Resolve a writable file: MSIX can merge directory views while redirecting
        // only the files beneath them, so a directory handle is not sufficient.
        // A disposable marker also works when another process has locked the log.
        using var file = File.OpenHandle(Path.Combine(directory, $".path-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, FileOptions.DeleteOnClose);
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= path.Capacity) throw new PathTooLongException("The resolved Anode state directory is too long.");
        string resolved = path.ToString();
        resolved = resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + resolved[8..]
            : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
        return Path.GetDirectoryName(resolved) ?? throw new IOException("The Anode state file has no parent directory.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}

/// <summary>
/// Append-only log shared by every role. Writes are best effort: a seat that cannot
/// log is still a seat, and losing a log line must never take the seat down.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string _role = "anode";
    private static string? _lastWriteError;
    private static int _reportedFailure;

    public static string? LastWriteError => Volatile.Read(ref _lastWriteError);

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
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{_role}] pid={System.Environment.ProcessId} {message}";
        if (Echo)
        {
            try { Console.Error.WriteLine(line); } catch { /* no console */ }
        }

        try
        {
            lock (Gate)
            {
                Env.EnsureStateDirectory();
                Append(Env.LogPath, line);
                Volatile.Write(ref _lastWriteError, null);
            }
        }
        catch (Exception ex)
        {
            string error = $"Cannot write Anode log '{Env.LogPath}': {ex.GetType().Name}: {ex.Message}";
            Volatile.Write(ref _lastWriteError, error);
            // A detached process may have no stderr. Status also exposes this error.
            if (Interlocked.Exchange(ref _reportedFailure, 1) == 0)
                try { Console.Error.WriteLine(error); } catch { }
        }
    }

    internal static void Append(string path, string line)
    {
        // Every role opens the same file. Retry sharing violations at open time,
        // before writing any bytes, so a collision neither loses nor repeats a line.
        FileStream file;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                break;
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 32 or 33)
            {
                Thread.Sleep(20);
            }
        }
        using (file)
        using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.WriteLine(line);
    }
}
