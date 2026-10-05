using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Input;
using Anode.Mcp;

namespace Anode.Cli;

/// <summary>Typing's pace, limits and waits, with stand-ins for the keyboard. Nothing here injects input.</summary>
internal static class InputChecks
{
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    // a, b, Enter (the \r is dropped), Tab, c, an emoji kept whole, d: 7 characters.
    private const string Sample = "ab\r\n\tc\U0001F600d";

    private sealed class Recorder
    {
        public double Now;
        public readonly List<TextTyping.Unit> Sent = new();
        public readonly List<double> Waits = new();
        public readonly List<int> Limits = new();
        public int Asked;

        /// <summary>How late each wait ends, as a real timer is by a fraction of a millisecond.</summary>
        public double WaitLateMs;

        public TextTyping.Typist Typist(Func<int, bool?> answer, double sendMs = 1, CancellationToken cancel = default) => new()
        {
            Send = unit => { Sent.Add(unit); Now += sendMs; },
            Responsive = limit =>
            {
                Require(limit > 0 && limit <= TextTyping.BusyLimitMs, "typing asked the program with a limit out of range: " + limit);
                Limits.Add(limit);
                bool? responded = answer(++Asked);
                if (responded == false) Now += limit; // A program that doesn't respond uses up the whole wait.
                return responded;
            },
            Wait = milliseconds => { Waits.Add(milliseconds); Now += milliseconds + WaitLateMs; },
            Now = () => Now,
            Cancel = cancel
        };
    }

    public static string Pacing()
    {
        var paced = new Recorder();
        var done = TextTyping.Type(Sample, null, paced.Typist(_ => true));
        Require(done.Bool("ok") == true && done.Obj("result")?.Int("typed") == 7 && done.Obj("result")?.Int("perCharMs") == TextTyping.DefaultPerCharMs,
            "typing did not report 7 characters at the default pace: " + done.ToJsonString());
        Require(string.Concat(paced.Sent.Select(unit => unit.Text)) == "ab\n\tc\U0001F600d"
            && paced.Sent[2].Key == "enter" && paced.Sent[3].Key == "tab" && paced.Sent.Count(unit => unit.Key is not null) == 2,
            "newlines, tabs or the surrogate pair were not typed as single steps");
        Require(paced.Asked == 6, $"the focused program was asked {paced.Asked} times, not once before each character after the first");
        Require(paced.Waits.Count == 6 && paced.Waits.All(wait => Math.Abs(wait - (TextTyping.DefaultPerCharMs - 1)) < 1e-9),
            "characters did not go out the default pace apart: " + string.Join(", ", paced.Waits));
        Require(done.Obj("result")?.Str("summary") is { } summary && summary.StartsWith("Typed 7 characters, 15 ms apart", StringComparison.Ordinal),
            "unexpected summary: " + done.ToJsonString());

        var unpaced = new Recorder();
        Require(TextTyping.Type(Sample, 0, unpaced.Typist(_ => true)).Bool("ok") == true && unpaced.Waits.Count == 0 && unpaced.Asked == 6,
            "perCharMs 0 waited between characters, or stopped checking the program");

        var unknown = new Recorder();
        Require(TextTyping.Type(Sample, 5, unknown.Typist(_ => null)).Bool("ok") == true && unknown.Sent.Count == 7,
            "a program Windows won't let Anode ask stopped typing");

        var stalled = new Recorder();
        var stopped = TextTyping.Type(Sample, null, stalled.Typist(asked => asked < 3));
        Require(stopped.Bool("ok") == false && stopped.Str("errorCode") == "typing_stopped" && stopped.Int("typed") == 3
            && stopped.Int("total") == 7 && stopped.Int("nextIndex") == 4 && stalled.Sent.Count == 3,
            "a program that stopped responding did not end typing with the exact count: " + stopped.ToJsonString());
        string error = stopped.Str("error") ?? "";
        Require(error.Contains("first 3 of 7", StringComparison.Ordinal)
            && !error.Contains("ab\n", StringComparison.Ordinal) && !error.Contains("\U0001F600", StringComparison.Ordinal),
            "the stop message lacks the count or repeats the text: " + error);

        var slow = new Recorder();
        var late = TextTyping.Type(Sample, null, slow.Typist(_ => true, sendMs: 20_000));
        Require(late.Bool("ok") == false && late.Str("errorCode") == "typing_stopped" && late.Int("typed") == 3 && slow.Sent.Count == 3,
            "typing ran past the time a call has: " + late.ToJsonString());

        // A text that just fits the 45 s plan must survive a timer that ends each wait half a millisecond late.
        var lateTimer = new Recorder { WaitLateMs = 0.5 };
        var full = TextTyping.Type(new string('x', 3001), null, lateTimer.Typist(_ => true));
        Require(full.Bool("ok") == true && full.Obj("result")?.Int("typed") == 3001,
            "text that fits the plan stopped short when the timer ran a little late: " + full.ToJsonString());

        // A request with its own deadline plans for three quarters of it.
        var shortDeadline = new Recorder();
        Require(TextTyping.Type(new string('x', 251), null, shortDeadline.Typist(_ => true), timeoutMs: 5000).Bool("ok") == true
            && TextTyping.Type(new string('x', 252), null, new Recorder().Typist(_ => true), timeoutMs: 5000).Bool("ok") == false,
            "a request's own deadline did not bound the text one call types");

        // With little time left, the busy check waits only that long, so the reply still beats the deadline.
        var hung = new Recorder();
        var cutShort = TextTyping.Type("ab", null, hung.Typist(_ => false), timeoutMs: 1000);
        Require(cutShort.Bool("ok") == false && cutShort.Int("typed") == 1 && hung.Limits.Single() < TextTyping.BusyLimitMs
            && hung.Now <= 1000 * 7 / 8 + 1 && (cutShort.Str("error") ?? "").Contains("time this call has", StringComparison.Ordinal),
            $"a busy check outlasted a short request's time ({hung.Now} ms): " + cutShort.ToJsonString());
        // A slow first busy check leaves too little time for the next pause, so typing stops before it, not after it.
        var lateStart = new Recorder();
        var beforeWait = TextTyping.Type("abc", 700, lateStart.Typist(asked => { if (asked == 1) lateStart.Now += 1450; return true; }), timeoutMs: 2000);
        Require(beforeWait.Bool("ok") == false && beforeWait.Int("typed") == 2 && lateStart.Waits.Count == 0 && lateStart.Now <= 2000 * 7 / 8
            && (beforeWait.Str("error") ?? "").Contains("time this call has", StringComparison.Ordinal),
            $"a pause that would cross the stop was waited out ({lateStart.Now} ms of {2000 * 7 / 8}): " + beforeWait.ToJsonString());
        Require(new Recorder() is var busy && TextTyping.Type("ab", null, busy.Typist(_ => false)).Str("error") is { } busyError
            && busyError.Contains("did not respond for 5 seconds", StringComparison.Ordinal) && busy.Limits.Single() == TextTyping.BusyLimitMs,
            "a hung program was not given the full busy limit, or the stop message misnamed the cause");

        using (var duringWait = new CancellationTokenSource())
        {
            var waiting = new Recorder();
            var typist = waiting.Typist(_ => true, cancel: duringWait.Token);
            var halted = TextTyping.Type("ab", null, new TextTyping.Typist
            {
                Send = typist.Send, Responsive = typist.Responsive, Now = typist.Now, Cancel = typist.Cancel,
                Wait = milliseconds => { typist.Wait(milliseconds); duringWait.Cancel(); }
            });
            Require(halted.Bool("ok") == false && halted.Int("typed") == 1 && waiting.Sent.Count == 1,
                "a request cancelled during the wait still sent the next character: " + halted.ToJsonString());
        }

        using (var cancel = new CancellationTokenSource())
        {
            var halting = new Recorder();
            var typist = halting.Typist(_ => true, cancel: cancel.Token);
            var halted = TextTyping.Type(Sample, null, new TextTyping.Typist
            {
                Send = unit => { typist.Send(unit); if (halting.Sent.Count == 2) cancel.Cancel(); },
                Responsive = typist.Responsive, Wait = typist.Wait, Now = typist.Now, Cancel = typist.Cancel
            });
            Require(halted.Bool("ok") == false && halted.Int("typed") == 2 && halting.Sent.Count == 2
                && (halted.Str("error") ?? "").Contains("cancelled", StringComparison.Ordinal),
                "typing went on after its request was cancelled: " + halted.ToJsonString());
        }

        var refused = new Recorder();
        Require(TextTyping.Type(new string('x', 3002), null, refused.Typist(_ => true)).Bool("ok") == false && refused.Sent.Count == 0,
            "text too long for one call was partly typed before being refused");
        return "15 ms apart by default, pauses for a busy program, stops with an exact count for a hung one, a deadline or a cancel, keeps Enter, Tab and emoji whole";
    }

    public static string Limits()
    {
        string? Problem(string text, int? pace)
        {
            var arguments = new JsonObject { ["text"] = text };
            if (pace is int value) arguments["perCharMs"] = value;
            return Tools.ValidateArguments("seat_type", arguments);
        }
        Require(Problem(new string('x', 3001), null) is null, "3001 characters at the default pace were refused");
        Require(Problem(new string('x', 3002), null) is { } longText && longText.Contains("3001 characters at perCharMs 15", StringComparison.Ordinal)
            && longText.Contains("3002", StringComparison.Ordinal), "3002 characters at the default pace were accepted, or the refusal lacks the limit");
        Require(Problem(new string('x', 46), 1000) is null && Problem(new string('x', 47), 1000) is not null, "the limit ignores perCharMs");
        Require(Problem(new string('x', 50_000), 0) is null, "perCharMs 0 limited plain text");
        // A held Enter overlaps the pace that counts from its start: 1,000 lines of "a" take about 35 s, not 50.
        Require(Problem(string.Concat(Enumerable.Repeat("a\n", 1000)), null) is null, "Enter holds were counted on top of the pace");
        Require(Problem(new string('\n', 2251), 0) is { } keys && keys.Contains("2250 newlines and tabs", StringComparison.Ordinal),
            "Enter presses were not counted against the call's time");
        Require(Tools.ValidateOperation("input.text", new JsonObject { ["text"] = new string('x', 3002) }) is not null,
            "the daemon and the seat host would accept text the MCP tool refuses");
        Require(TextTyping.Units("a\r\nb\U0001F600").Count == 4, "\\r\\n or a surrogate pair counted as more than one character");
        return "one call fits 45 s: 3001 characters at 15 ms, fewer when slower or with Enter presses; refused before typing";
    }

    private sealed class MessageWindow : NativeWindow
    {
        public const int Busy = 0x8001, Quit = 0x8002; // WM_APP + n
        public readonly ManualResetEventSlim Working = new();

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Busy) { Working.Set(); Thread.Sleep((int)m.WParam); return; }
            if (m.Msg == Quit) { Application.ExitThread(); return; }
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    public static string Waits()
    {
        double median;
        bool precise;
        using (var timer = new TextTyping.PreciseTimer())
        {
            precise = timer.HighResolution;
            var times = new List<double>();
            for (int i = 0; i < 21; i++)
            {
                var clock = Stopwatch.StartNew();
                timer.Wait(5);
                times.Add(clock.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            median = times[times.Count / 2];
            // Before Windows 10 1803 there is no high-resolution timer, and the fallback sleep only has to be long enough.
            Require(times[0] >= 4 && (!precise || median < 10), string.Create(CultureInfo.InvariantCulture,
                $"5 ms waits took {times[0]:0.0} to {times[^1]:0.0} ms (median {median:0.0}); a plain sleep rounds up to the 15.6 ms clock tick"));
        }

        // A message-only window on a thread of its own stands in for the focused program: it is never shown or focused.
        var window = new MessageWindow();
        using var created = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            window.CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            created.Set();
            Application.Run();
            window.DestroyHandle();
        }) { IsBackground = true, Name = "typing check window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(created.Wait(5000), "the check's message-only window was not created");
        IntPtr handle = window.Handle;
        try
        {
            Require(TextTyping.Responsive(handle, 2000) == true, "a thread waiting for messages did not respond");
            PostMessage(handle, MessageWindow.Busy, 1500, IntPtr.Zero);
            Require(window.Working.Wait(2000), "the window's thread never got busy");
            var clock = Stopwatch.StartNew();
            Require(TextTyping.Responsive(handle, 300) == false && clock.ElapsedMilliseconds >= 250, "a thread busy in a handler counted as responsive");
            Require(TextTyping.Responsive(handle, 5000) == true, "the thread did not respond once it was free again");
        }
        finally
        {
            PostMessage(handle, MessageWindow.Quit, IntPtr.Zero, IntPtr.Zero);
            thread.Join(5000);
        }
        Require(TextTyping.Responsive(handle, 500) is null, "a closed window counted as a program that stopped responding");
        return string.Create(CultureInfo.InvariantCulture,
            $"5 ms waits take {median:0.0} ms (median, {(precise ? "high-resolution timer" : "fallback sleep")}); a busy thread holds the next character, a free one releases it");
    }
}
