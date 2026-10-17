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
        using (var stopping = new CancellationTokenSource())
        {
            // The same stop, with the seat stopping during the busy check: the reply names the cancellation.
            var lateCancel = new Recorder();
            var named = TextTyping.Type("abc", 700, lateCancel.Typist(asked =>
            {
                if (asked == 1) lateCancel.Now += 1450; else stopping.Cancel();
                return true;
            }, cancel: stopping.Token), timeoutMs: 2000);
            Require(named.Int("typed") == 2 && (named.Str("error") ?? "").Contains("cancelled", StringComparison.Ordinal),
                "a cancellation before a pause that would cross the stop was reported as running out of time: " + named.ToJsonString());
        }
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

        // Windows refusing a character still ends the call with the exact count. partlySent says part of that character
        // went out first, as when an Enter's press went through and its release was refused.
        foreach (var (at, partly) in new[] { (0, false), (3, true), (4, false) })
        {
            var blocked = new Recorder();
            var typist = blocked.Typist(_ => true);
            var refusal = new TextTyping.Refused("stand-in refusal: SendInput accepted 0 of 2 events.", partly);
            var partway = TextTyping.Type(Sample, null, new TextTyping.Typist
            {
                Send = unit => { if (blocked.Sent.Count == at) throw refusal; typist.Send(unit); },
                Responsive = typist.Responsive, Wait = typist.Wait, Now = typist.Now
            });
            string why = partway.Str("error") ?? "";
            int next = TextTyping.Units(Sample)[at].Index;
            Require(partway.Bool("ok") == false && partway.Str("errorCode") == "typing_stopped" && partway.Int("typed") == at
                && partway.Int("total") == 7 && partway.Int("nextIndex") == next && blocked.Sent.Count == at
                && (partway["partlySent"] is null) != partly
                && why.Contains("Windows refused the input (stand-in refusal: SendInput accepted 0 of 2 events)", StringComparison.Ordinal)
                && why.Contains(partly ? $"Part of character {at + 1} went out first" : at == 0 ? "Nothing was sent." : $"Nothing after character {at} was sent.",
                    StringComparison.Ordinal)
                && !why.Contains("ab\n", StringComparison.Ordinal) && !why.Contains("\U0001F600", StringComparison.Ordinal),
                $"a refusal at character {at + 1} (partly sent: {partly}) did not end typing with the exact count: " + partway.ToJsonString());
        }
        Require(stopped["partlySent"] is null && late["partlySent"] is null,
            "a stop before a character went out said part of it was sent: " + stopped.ToJsonString());
        // Any other exception is a fault in Anode itself, and fails loudly instead of passing for a refusal.
        foreach (Exception fault in new Exception[] { new InvalidOperationException("stand-in fault"), new ObjectDisposedException("stand-in") })
        {
            var faulty = new Recorder().Typist(_ => true);
            bool surfaced = false;
            try
            {
                TextTyping.Type("ab", null, new TextTyping.Typist
                {
                    Send = _ => throw fault, Responsive = faulty.Responsive, Wait = faulty.Wait, Now = faulty.Now
                });
            }
            catch (Exception caught) when (ReferenceEquals(caught, fault)) { surfaced = true; }
            Require(surfaced, $"typing turned a fault in Anode's own code ({fault.GetType().Name}) into a typing_stopped reply");
        }

        var refused = new Recorder();
        Require(TextTyping.Type(new string('x', 3002), null, refused.Typist(_ => true)).Bool("ok") == false && refused.Sent.Count == 0,
            "text too long for one call was partly typed before being refused");
        return "15 ms apart by default, pauses for a busy program, stops with an exact count for a hung one, a deadline, a cancel or input Windows refuses partway, keeps Enter, Tab and emoji whole";
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

    public static string Holds()
    {
        // A hold longer than the request's deadline cost the daemon its seat-host connection and detached the seat.
        JsonObject Key(int holdMs) => new() { ["keys"] = "a", ["holdMs"] = holdMs };
        JsonObject Tap(int ms) => new() { ["button"] = "a", ["ms"] = ms };
        Require(Tools.ValidateArguments("seat_key", Key(Tools.MaxHoldMs)) is null && Tools.ValidateArguments("gamepad_tap", Tap(Tools.MaxHoldMs)) is null,
            "the longest allowed hold was refused");
        Require(Tools.ValidateArguments("seat_key", Key(Tools.MaxHoldMs + 1)) is not null && Tools.ValidateArguments("gamepad_tap", Tap(Tools.MaxHoldMs + 1)) is not null
            && Tools.ValidateOperation("input.key", Key(60_000)) is not null && Tools.ValidateOperation("gamepad.tap", Tap(60_000)) is not null,
            "a hold the request's deadline can't fit was accepted");
        Require(Tools.MaxHoldMs * 2 <= Core.Agents.DesktopLease.StopAfterMs(60_000), "the longest hold leaves the request's deadline no margin");

        var sent = new List<(string Keys, bool Up)>();
        void Send(ushort[] keys, bool up) => sent.Add((string.Join(",", keys), up));
        ushort[] chord = { 0x11, 0x41 }; // ctrl+a
        var clock = Stopwatch.StartNew();
        InputInjector.Press(chord, 30, CancellationToken.None, Send);
        Require(clock.ElapsedMilliseconds >= 15 && sent.SequenceEqual(new[] { ("17,65", false), ("65,17", true) }),
            "a key press did not hold for its time, or did not release in reverse order");

        sent.Clear();
        using (var cut = new CancellationTokenSource(100))
        {
            clock.Restart();
            bool stopped = false;
            try { InputInjector.Press(chord, Tools.MaxHoldMs, cut.Token, Send); }
            catch (OperationCanceledException) { stopped = true; }
            Require(stopped && clock.ElapsedMilliseconds < 3000 && sent.SequenceEqual(new[] { ("17,65", false), ("65,17", true) }),
                $"a press held past its deadline ({clock.ElapsedMilliseconds} ms), or was not reported as cut short with its keys released");
        }

        // Through the lease, as in the seat host: a long hold answers timed_out before the caller gives up.
        var lease = new Core.Agents.DesktopLease();
        string token = lease.HandleAsync(new JsonObject { ["op"] = "lease", ["agentId"] = "A", ["action"] = "acquire" }, (_, _) => Task.FromResult(JsonLine.Ok()))
            .GetAwaiter().GetResult().Obj("result")!.Str("leaseToken")!;
        var request = Key(Tools.MaxHoldMs);
        request["op"] = "input.key"; request["agentId"] = "A"; request["leaseToken"] = token; request["timeoutMs"] = 2000;
        sent.Clear();
        clock.Restart();
        var reply = lease.HandleAsync(request, (r, cancel) =>
        {
            InputInjector.Press(chord, r.Int("holdMs") ?? 40, cancel, Send);
            return Task.FromResult(JsonLine.Ok());
        }).GetAwaiter().GetResult();
        Require(reply.Str("errorCode") == "timed_out" && reply.Bool("started") == true && clock.ElapsedMilliseconds < 2000 && sent.Count == 2 && sent[1].Up,
            $"a hold longer than its request answered after the caller's deadline ({clock.ElapsedMilliseconds} ms of 2000), or kept its keys down: {reply.ToJsonString()}");

        GamepadChecks.TapCutShort();
        return $"holds of 0-{Tools.MaxHoldMs} ms only; a key press or gamepad tap stopped by its deadline lets go and answers timed_out in time";
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

    /// <summary>
    /// Everyday key names, the names agents tried for the ` key (grave, oem3, backtick, Oem3: all refused before), Windows'
    /// constant names, the vk&lt;hex&gt; range, and chords with an empty part, which pressed what was left. MCP, the daemon and
    /// the CLI refuse a bad chord before it reaches the seat.
    /// </summary>
    public static string KeyChords()
    {
        (string Name, ushort Expected)[] cases =
        {
            ("a", 0x41), ("Z", 0x5A), ("5", 0x35), ("enter", 0x0D), ("esc", 0x1B), ("space", 0x20), ("f5", 0x74), ("f12", 0x7B),
            ("ctrl", 0xA2), ("shift", 0xA0), ("alt", 0xA4), ("win", 0x5B), ("up", 0x26), ("num7", 0x67), ("tab", 0x09),
            ("grave", 0xC0), ("backtick", 0xC0), ("oem3", 0xC0), ("Oem3", 0xC0), ("VK_OEM_3", 0xC0), ("`", 0xC0), ("tilde", 0xC0),
            ("equals", 0xBB), ("VK_OEM_PLUS", 0xBB), ("plus", 0xBB), ("hyphen", 0xBD), ("apostrophe", 0xDE), ("oem102", 0xE2),
            ("VK_RETURN", 0x0D), ("page_up", 0x21), ("caps-lock", 0x14), ("vk_5", 0x35), ("vkC0", 0xC0), ("vk5", 0x05), ("vkFE", 0xFE)
        };
        foreach (var (name, expected) in cases)
        {
            ushort actual = KeyCodes.Resolve(name);
            Require(actual == expected, $"'{name}' resolved to 0x{actual:X2}, expected 0x{expected:X2}");
        }
        foreach (string name in new[] { "grav", "vk0", "vkFF", "vk100", "vkadd", "vk", "vk_", "vk_nosuch", "-_-" })
        {
            string? error = null;
            try { KeyCodes.Resolve(name); }
            catch (ArgumentException ex) { error = ex.Message; }
            Require(error is not null, $"'{name}' was accepted as a key");
            Require(error!.Contains("grave", StringComparison.Ordinal) && error.Contains("vk<hex>", StringComparison.Ordinal),
                "the unknown-key error doesn't name the punctuation keys and vk<hex>: " + error);
        }

        Require(KeyCodes.Chord(" ctrl + shift + grave ").SequenceEqual(new ushort[] { 0xA2, 0xA0, 0xC0 }), "a chord lost or reordered its keys");
        foreach (string chord in new[] { "ctrl++", "ctrl+", "+a", "ctrl+ +s", "+", "", " " })
            Require(KeyCodes.ChordProblem(chord) is not null, $"'{chord}' was accepted, though part of it names no key");
        Require(KeyCodes.ChordProblem("ctrl++")!.Contains("ctrl+plus", StringComparison.Ordinal), "the empty-part error doesn't name the plus key");
        Require(KeyCodes.ChordProblem("ctrl+bogus")!.StartsWith("Unknown key 'bogus'", StringComparison.Ordinal), "an unknown key in a chord wasn't named");

        Require(Tools.ValidateArguments("seat_key", new JsonObject { ["keys"] = "ctrl+grave" }) is null, "seat_key refused ctrl+grave");
        Require(Tools.ValidateArguments("seat_key", new JsonObject { ["keys"] = "ctrl++" }) is not null, "seat_key accepted ctrl++");
        Require(Tools.ValidateOperation("input.key", new JsonObject { ["keys"] = "ctrl+oem33" }) is not null, "the daemon would send an unknown key to the seat");
        Cli.CheckArguments("key", new[] { "VK_OEM_3" });
        bool refused = false;
        try { Cli.CheckArguments("key", new[] { "ctrl++" }); }
        catch (UsageException) { refused = true; }
        Require(refused, "anode key accepted ctrl++");
        return $"{cases.Length} names resolve; unknown names, out-of-range codes and empty chord parts are refused before the seat";
    }
}
