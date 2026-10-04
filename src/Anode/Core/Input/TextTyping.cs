using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Input;

/// <summary>
/// Paces literal text so the program that has the keyboard focus keeps up with it.
///
/// <c>SendInput</c> accepts characters far faster than any program reads them, and accepting them proves nothing
/// about delivery. A busy program, such as a browser editing a long field, falls behind, and once a backlog builds
/// it can drop and reorder characters while every call still succeeds. So characters go out at a steady pace, timed
/// with a high-resolution timer (a plain sleep rounds up to the 15.6 ms clock tick), and each one waits until the
/// thread that owns the focus is reading its messages again. A program that stops reading ends the call with the
/// exact count typed, and a call ends while its caller still waits for the reply, because a timed-out input request
/// must never be replayed.
/// </summary>
internal static class TextTyping
{
    /// <summary>
    /// Milliseconds between characters unless the caller says otherwise. A seat's Chrome took 7,938 characters into a
    /// long field intact at <c>perCharMs</c> 3, which then slept a whole clock tick (about 15.5 ms) per character;
    /// unpaced, the same text arrived dropped and out of order after about 5,500 characters.
    /// </summary>
    public const int DefaultPerCharMs = 15;

    /// <summary>Typing time one call may use. MCP and the daemon wait 60 seconds for the whole request.</summary>
    public const int BudgetMs = 45_000;

    /// <summary>How long the focused program may take to read a character. Windows calls a window hung after 5 s.</summary>
    public const int ReadLimitMs = 5_000;

    /// <summary>How long a newline's Enter and a tab's Tab are held, as typing always has.</summary>
    public const int KeyHoldMs = 20;

    /// <summary>
    /// One step of typing: a character, a surrogate pair kept whole, or the key a newline or tab becomes.
    /// <paramref name="Index"/> is where it starts in the text, in UTF-16 code units.
    /// </summary>
    internal readonly record struct Unit(string Text, string? Key, int Index);

    /// <summary>What typing needs from Windows. Quick checks pass stand-ins.</summary>
    internal sealed class Typist
    {
        public required Action<Unit> Send { get; init; }

        /// <summary>
        /// Waits up to the given milliseconds for the focused program to read its messages again: true once it has,
        /// false when it has not, and null when Windows won't let Anode ask.
        /// </summary>
        public required Func<int, bool?> Answered { get; init; }

        public required Action<double> Wait { get; init; }

        /// <summary>Milliseconds on a monotonic clock.</summary>
        public required Func<double> Now { get; init; }

        /// <summary>Set when the seat is stopping; typing ends before the next character.</summary>
        public CancellationToken Stopping { get; init; }
    }

    internal static List<Unit> Units(string text)
    {
        var units = new List<Unit>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\r') continue;
            if (character == '\n') units.Add(new Unit("\n", "enter", i));
            else if (character == '\t') units.Add(new Unit("\t", "tab", i));
            else if (char.IsSurrogatePair(text, i)) { units.Add(new Unit(text.Substring(i, 2), null, i)); i++; }
            else units.Add(new Unit(character.ToString(), null, i));
        }
        return units;
    }

    /// <summary>The least time typing these units takes at this pace.</summary>
    internal static long MinimumMs(IReadOnlyCollection<Unit> units, int perCharMs) =>
        (long)Math.Max(0, units.Count - 1) * perCharMs + (long)units.Count(unit => unit.Key is not null) * KeyHoldMs;

    /// <summary>Why one call can't type this text in its time, or null when it can.</summary>
    public static string? Problem(string text, int? perCharMs)
    {
        int pace = perCharMs ?? DefaultPerCharMs;
        var units = Units(text);
        if (MinimumMs(units, pace) <= BudgetMs) return null;
        string limit = pace > 0
            ? $"{BudgetMs / pace + 1} characters at perCharMs {pace} (fewer with newlines and tabs)"
            : $"{BudgetMs / KeyHoldMs} newlines and tabs";
        return $"One call types at most {limit}, so that it finishes within the request's time; this text has {units.Count}. "
            + "Split it across several calls, each continuing where the last one ended.";
    }

    public static JsonObject Type(string text, int? perCharMs, Typist typist)
    {
        if (Problem(text, perCharMs) is { } problem) return JsonLine.Fail(problem);
        int pace = perCharMs ?? DefaultPerCharMs;
        var units = Units(text);
        double start = typist.Now(), previous = start;
        for (int i = 0; i < units.Count; i++)
        {
            if (typist.Stopping.IsCancellationRequested) return Stopped(units, i, "the seat is stopping");
            if (i > 0)
            {
                // A program that falls behind sets the speed: the next character waits until it reads again.
                if (typist.Answered(ReadLimitMs) == false)
                    return Stopped(units, i, $"the focused program did not read input for {ReadLimitMs / 1000} seconds");
                double wait = previous + pace - typist.Now();
                if (wait > 0) typist.Wait(wait);
            }
            if (typist.Now() - start + (units[i].Key is null ? 0 : KeyHoldMs) > BudgetMs)
                return Stopped(units, i, $"typing took longer than the {BudgetMs / 1000} seconds one call has");
            previous = typist.Now();
            typist.Send(units[i]);
        }
        double elapsed = typist.Now() - start;
        string how = pace > 0 ? $"{pace} ms apart" : "as fast as the program read them";
        return JsonLine.Ok(new JsonObject
        {
            ["typed"] = units.Count,
            ["perCharMs"] = pace,
            ["elapsedMs"] = (long)Math.Round(elapsed),
            ["summary"] = string.Create(CultureInfo.InvariantCulture,
                $"Typed {units.Count} {(units.Count == 1 ? "character" : "characters")}, {how}, in {elapsed / 1000:0.0} s.")
        });
    }

    /// <summary>
    /// Ends a call partway, saying exactly how far it got. The reply gives counts only and never repeats the text,
    /// which may be a secret.
    /// </summary>
    private static JsonObject Stopped(List<Unit> units, int typed, string reason)
    {
        var failure = JsonLine.Fail($"Typed the first {typed} of {units.Count} characters, then stopped: {reason}. Nothing after "
            + $"character {typed} was sent. Check what reached the field before you send the rest (from UTF-16 index {units[typed].Index}).");
        failure["errorCode"] = "typing_stopped";
        failure["typed"] = typed;
        failure["total"] = units.Count;
        failure["nextIndex"] = units[typed].Index;
        return failure;
    }

    // ------------------------------------------------------------ Windows side

    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int ErrorTimeout = 1460;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Asks the thread that owns the keyboard focus, or the foreground window's, whether it reads again.</summary>
    internal static bool? FocusAnswered(int limitMs)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        IntPtr window = GetGUIThreadInfo(0, ref info) && info.Focus != IntPtr.Zero ? info.Focus : Native.Native.GetForegroundWindow();
        return window == IntPtr.Zero ? null : Answered(window, limitMs);
    }

    /// <summary>
    /// Sends WM_NULL, which every window ignores. A thread handles a sent message only between its other messages,
    /// so the reply means it is reading its queue again. False when no reply came in time or Windows reports the
    /// window hung; null when Windows won't deliver the question, for example to a closed window or to a program
    /// running with more privileges.
    /// </summary>
    internal static bool? Answered(IntPtr window, int limitMs)
    {
        if (SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, (uint)Math.Max(1, limitMs), out _) != IntPtr.Zero)
            return true;
        return Marshal.GetLastWin32Error() is 0 or ErrorTimeout ? false : null;
    }

    /// <summary>Waits without rounding up to the clock tick, on Windows 10 1803 and later; elsewhere it sleeps.</summary>
    internal sealed class PreciseTimer : IDisposable
    {
        private readonly IntPtr _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);

        public void Wait(double milliseconds)
        {
            if (milliseconds <= 0) return;
            long due = -Math.Max(1, (long)(milliseconds * 10_000)); // relative, in 100 ns units
            if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)
                && WaitForSingleObject(_timer, (uint)Math.Ceiling(milliseconds) + 1000) == 0) return;
            Thread.Sleep((int)Math.Ceiling(milliseconds));
        }

        public void Dispose()
        {
            if (_timer != IntPtr.Zero) CloseHandle(_timer);
        }
    }
}
