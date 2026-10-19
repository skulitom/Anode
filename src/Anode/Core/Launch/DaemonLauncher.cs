using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Anode.Core.Session;
using Anode.Core.Bridge;
using Anode.Core.Util;

namespace Anode.Core.Launch;

/// <summary>Shared by the CLI and MCP so the daemon can outlive either client.</summary>
internal static class DaemonLauncher
{
    /// <summary>
    /// An absent daemon cannot answer later in this probe. Only wait when its mutex says it is
    /// already starting or running; still try the pipe once for compatibility with older hosts.
    /// This is discovery, not the wait after launching a daemon.
    /// </summary>
    public static Task<JsonPipeClient?> ConnectExistingAsync(int timeoutMs, CancellationToken cancel = default) =>
        ConnectExistingAsync(Env.ControlPipe, Env.DaemonMutex, timeoutMs, cancel);

    internal static Task<JsonPipeClient?> ConnectExistingAsync(string pipe, string mutex, int timeoutMs, CancellationToken cancel) =>
        JsonPipeClient.TryConnectAsync(pipe, SeatSlot.Exists(mutex) ? timeoutMs : 0, cancel);

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

        StartWithoutHandles(Env.ExecutablePath, arguments, AppContext.BaseDirectory);
    }

    /// <summary>
    /// Starts a program that inherits none of this process's handles and has no standard handles, as a Task Scheduler
    /// start gives it. Process.Start always passes every inheritable handle on, so a daemon it started kept the caller's
    /// output pipe open, and `anode lease acquire | Out-String` waited until the daemon exited. Returns the process id.
    /// </summary>
    internal static int StartWithoutHandles(string file, string arguments, string directory)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = UseStdHandles };
        // CreateProcessW may write to the command line, so it gets a buffer of its own.
        var commandLine = new StringBuilder(Quote(file) + " " + arguments);
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, directory,
                ref startup, out var started))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the Anode daemon.");
        CloseHandle(started.Thread);
        CloseHandle(started.Process);
        return started.ProcessId;
    }

    private const int UseStdHandles = 0x100;
    private const uint CreateNoWindow = 0x08000000;

    // With UseStdHandles and these left null, the program gets no standard handles at all.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string directory, ref StartupInfo startup, out ProcessInformation started);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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
