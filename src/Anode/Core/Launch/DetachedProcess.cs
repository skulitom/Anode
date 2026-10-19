using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Anode.Core.Launch;

/// <summary>
/// Starts a program that outlives the command that started it. It inherits none of that command's handles and has no
/// standard handles, as a Task Scheduler start gives it. Process.Start always passes every inheritable handle on, so a
/// program it started held a piped caller's output open: `anode lease acquire | Out-String` waited until a fallback
/// daemon exited, and `anode browser --sign-in ... | Out-Host` until the browser closed.
/// </summary>
internal static class DetachedProcess
{
    /// <summary>Returns the process id. A console program gets no console window; a null directory keeps this one's.</summary>
    public static int Start(string file, IEnumerable<string> arguments, string? directory)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = UseStdHandles };
        // CreateProcessW may write to the command line, so it gets a buffer of its own.
        var commandLine = new StringBuilder(string.Join(' ', arguments.Prepend(file).Select(DaemonLauncher.Quote)));
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, directory,
                ref startup, out var started))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Could not start {Path.GetFileName(file)}: {new Win32Exception(error).Message}");
        }
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
        string? directory, ref StartupInfo startup, out ProcessInformation started);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
