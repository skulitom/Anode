using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Native;

namespace Anode.Core.Processes;

/// <summary>Enumerating and killing programs, scoped to a Windows session.</summary>
internal static class ProcessControl
{
    /// <summary>
    /// Force-terminates every process belonging to <paramref name="sessionId"/> that
    /// this user is allowed to touch. Used as the fallback when a clean session logoff
    /// will not go through. Returns how many processes were killed.
    /// </summary>
    public static int KillSessionProcesses(uint sessionId)
    {
        int killed = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId != (int)sessionId) continue;
                if (process.Id == System.Environment.ProcessId) continue;
                process.Kill(entireProcessTree: true);
                killed++;
            }
            catch
            {
                // Protected or already-gone processes are expected and uninteresting.
            }
            finally
            {
                process.Dispose();
            }
        }
        return killed;
    }

    /// <summary>Lists the visible programs running in a session, newest first.</summary>
    public static JsonArray List(uint sessionId, bool windowedOnly)
    {
        var results = new List<(Process Process, string Title)>();
        foreach (var process in Process.GetProcesses())
        {
            bool keep = false;
            string title = string.Empty;
            try
            {
                if (process.SessionId == (int)sessionId)
                {
                    title = SafeWindowTitle(process);
                    keep = !windowedOnly || title.Length > 0;
                }
            }
            catch { }

            if (keep) results.Add((process, title));
            else process.Dispose();
        }

        var array = new JsonArray();
        foreach (var (process, title) in results.OrderByDescending(entry => SafeStartTime(entry.Process)))
        {
            try
            {
                array.Add(new JsonObject
                {
                    ["pid"] = process.Id,
                    ["name"] = process.ProcessName,
                    ["title"] = title,
                    ["started"] = SafeStartTime(process)?.ToString("o"),
                    ["memoryMb"] = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1)
                });
            }
            catch { }
            finally { process.Dispose(); }
        }
        return array;
    }

    /// <summary>Kills one process tree by pid, refusing to reach outside the given session.</summary>
    public static void Kill(int pid, uint sessionId)
    {
        using var process = Process.GetProcessById(pid);
        if (process.SessionId != (int)sessionId)
            throw new InvalidOperationException(
                $"Process {pid} runs in session {process.SessionId}, not in the seat (session {sessionId}). Refusing to kill it.");
        process.Kill(entireProcessTree: true);
    }

    /// <summary>Kills every process with the given image name inside one session.</summary>
    public static int KillByName(string name, uint sessionId)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        int killed = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.SessionId != (int)sessionId) continue;
                process.Kill(entireProcessTree: true);
                killed++;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return killed;
    }

    private static DateTime? SafeStartTime(Process process)
    {
        try { return process.StartTime; } catch { return null; }
    }

    private static string SafeWindowTitle(Process process)
    {
        try
        {
            if (process.MainWindowHandle == IntPtr.Zero) return string.Empty;
            var buffer = new StringBuilder(512);
            int length = Native.Native.GetWindowText(process.MainWindowHandle, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
