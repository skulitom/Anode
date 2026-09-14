using System.ComponentModel;
using System.Runtime.InteropServices;
using Anode.Core.Native;
using Anode.Core.Util;

namespace Anode.Core.Session;

/// <summary>
/// Thin, honest wrapper over the Windows child-session APIs.
///
/// A child session is a loopback Remote Desktop session tied to the signed-in
/// user's own session. It is a full second Windows session: its own desktop, its
/// own input queue, its own focus and foreground window, its own running programs.
/// It shares the user's account, so it sees the same drives, the same installed
/// software and the same Steam library. Exactly one may exist at a time, and it
/// dies with the parent session.
/// </summary>
internal static class ChildSession
{
    /// <summary>True when the machine has had child sessions turned on.</summary>
    public static bool IsFeatureEnabled()
    {
        if (!Native.Native.WTSIsChildSessionsEnabled(out bool enabled))
            throw Native.Native.LastError("Could not read the child-session setting");
        return enabled;
    }

    /// <summary>Same as <see cref="IsFeatureEnabled"/> but never throws.</summary>
    public static bool IsFeatureEnabledSafe()
    {
        try { return IsFeatureEnabled(); }
        catch { return false; }
    }

    /// <summary>
    /// Turns the feature on machine-wide. Requires an elevated token; callers that
    /// are not elevated get a <see cref="Win32Exception"/> with ERROR_ACCESS_DENIED.
    /// </summary>
    public static void EnableFeature()
    {
        if (!Native.Native.WTSEnableChildSessions(true))
            throw Native.Native.LastError("WTSEnableChildSessions(TRUE) failed");
    }

    /// <summary>Turns the feature back off machine-wide. Requires elevation.</summary>
    public static void DisableFeature()
    {
        if (!Native.Native.WTSEnableChildSessions(false))
            throw Native.Native.LastError("WTSEnableChildSessions(FALSE) failed");
    }

    /// <summary>The calling session's child id, or null when there is none. Resolve this in the parent daemon.</summary>
    public static uint? TryGetId()
    {
        if (!Native.Native.WTSGetChildSessionId(out uint id)) return null;
        return id == Native.Native.NoChildSession ? null : id;
    }

    /// <summary>The session this process is running in.</summary>
    public static uint CurrentSessionId()
    {
        return Native.Native.ProcessIdToSessionId((uint)System.Environment.ProcessId, out uint id)
            ? id
            : uint.MaxValue;
    }

    /// <summary>
    /// Waits for a child session to appear. The RDP control creates it asynchronously,
    /// so the id shows up a little after the control reports "connected".
    /// </summary>
    public static async Task<uint?> WaitForIdAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            uint? id = TryGetId();
            if (id.HasValue) return id;
            await Task.Delay(200, cancel).ConfigureAwait(false);
        }
        return TryGetId();
    }

    /// <summary>
    /// The panic button. Logs the child session off, which force-terminates every
    /// program running in it, however wedged those programs are. Returns the id that
    /// was logged off, or null when there was nothing to log off.
    /// </summary>
    public static uint? Logoff(bool wait = true)
    {
        uint? id = TryGetId();
        if (id is null) return null;
        if (id == 0 || id == CurrentSessionId())
            throw new InvalidOperationException("Refusing to log off a system or parent session as the child.");

        if (!Native.Native.WTSLogoffSession(Native.Native.CurrentServer, id.Value, wait))
        {
            var error = Native.Native.LastError($"Could not log off child session {id.Value}");
            return HandleLogoffFailure(id.Value, error, () => Exists(id.Value),
                () => Processes.ProcessControl.KillSessionProcesses(id.Value));
        }

        return id;
    }

    internal static uint? HandleLogoffFailure(uint id, Win32Exception error, Func<bool?> exists, Func<int> killProcesses)
    {
        // WTSGetChildSessionId can retain a reserved ID after authentication fails.
        // Only ignore a not-found error when enumeration confirms it is absent.
        if (error.NativeErrorCode is 2 or 1168 or 7022 && exists() is false)
        {
            Log.Info($"child session {id} is already absent; nothing to log off");
            return null;
        }
        Log.Warn(error.Message + " - falling back to terminating its processes directly");
        int killed = killProcesses();
        Log.Info($"terminated {killed} process(es) in session {id}");
        if (killed == 0) throw error;
        return id;
    }

    /// <summary>Null means enumeration failed, not that the session is absent.</summary>
    internal static bool? Exists(uint id)
    {
        if (!Native.Native.WTSEnumerateSessions(Native.Native.CurrentServer, 0, 1, out var sessions, out uint count))
            return null;
        try
        {
            int size = Marshal.SizeOf<Native.Native.WtsSessionInfo>();
            for (int i = 0; i < count; i++)
                if (Marshal.PtrToStructure<Native.Native.WtsSessionInfo>(IntPtr.Add(sessions, checked(i * size))).SessionId == id)
                    return true;
            return false;
        }
        finally { Native.Native.WTSFreeMemory(sessions); }
    }
}
