using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using Anode.Core.Bridge;
using Anode.Core.Desktop;
using Anode.Mcp;

namespace Anode.Cli;

internal static class DesktopChecks
{
    private static void Require(bool test, string message) { if (!test) throw new InvalidOperationException(message); }
    private static void Rejected(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; }
        throw new InvalidOperationException("A stale or unknown reference was accepted.");
    }

    public static string References()
    {
        DateTime now = DateTime.UtcNow;
        var references = new DesktopReferences(() => now);
        var first = new WindowTarget(100, 50, 123);
        var reused = new WindowTarget(100, 51, 456);
        string one = references.RememberWindow(first), two = references.RememberWindow(reused);
        Require(one != two && references.Window(one) == first && references.Window(two) == reused, "Reused window handles shared an identity.");
        var elements = new JsonArray(new JsonObject { ["id"] = "e0", ["name"] = "Save" });
        string snapshot = references.RememberObservation(first, elements);
        Require(references.TakeElement(snapshot, "e0").Window == first, "Observation lost its window identity.");
        Rejected(() => references.TakeElement(snapshot, "e0"));
        snapshot = references.RememberObservation(first, elements);
        Rejected(() => references.TakeElement(snapshot, "e1"));
        Rejected(() => references.TakeElement(snapshot, "e0"));
        snapshot = references.RememberObservation(first, elements);
        now = now.AddSeconds(91);
        Rejected(() => references.TakeElement(snapshot, "e0"));
        now = now.AddMinutes(11);
        Rejected(() => references.Window(one));
        Require(Tools.ValidateArguments("seat_observe", new JsonObject { ["windowId"] = one, ["maxElements"] = 10000 }) is not null, "Unbounded inspection was accepted.");
        Require(Tools.ValidateArguments("seat_window", new JsonObject { ["windowId"] = one, ["action"] = "move" }) is not null, "Incomplete window move was accepted.");
        Require(Tools.ValidateArguments("seat_element", new JsonObject { ["snapshotId"] = "s", ["elementId"] = "e0", ["action"] = "invoke", ["value"] = "bad" }) is not null, "Irrelevant action arguments were accepted.");
        Require(Tools.ValidateArguments("seat_element", new JsonObject { ["snapshotId"] = "s", ["elementId"] = "e0", ["action"] = "set_value", ["value"] = "" }) is null, "Clearing a value was rejected.");
        return "window reuse, expired/consumed references, observation limits and action arguments checked without desktop access";
    }

    public static async Task<string> Dispatch()
    {
        int actions = 0, workers = 0;
        var desktop = new DesktopTools((request, _) =>
        {
            workers++;
            return request.Str("op") switch
            {
                "windows" => Task.FromResult(new JsonObject { ["windows"] = new JsonArray(new JsonObject
                    { ["target"] = new WindowTarget(100, 50, 123).ToJson(), ["pid"] = 50, ["process"] = "fixture", ["title"] = "Fixture" }) }),
                "observe" => Task.FromResult(new JsonObject { ["elements"] = new JsonArray(new JsonObject
                    { ["id"] = "e0", ["name"] = "Save", ["path"] = new JsonArray(0), ["runtimeId"] = new JsonArray(1, 2), ["processId"] = 50 }) }),
                "element" => FailedAction(),
                _ => throw new InvalidOperationException("Unexpected worker call")
            };
            Task<JsonObject> FailedAction() { actions++; throw new TimeoutException("Action outcome unknown"); }
        });
        var invalid = await desktop.HandleAsync("desktop.observe", new JsonObject { ["windowId"] = "unknown", ["maxDepth"] = -1 });
        Require(invalid.Bool("ok") == false && workers == 0, "Invalid inspection started a worker.");
        var windows = (await desktop.HandleAsync("desktop.windows", new())).Obj("result")!["windows"]!.AsArray();
        string id = windows[0]!["windowId"]!.GetValue<string>();
        Require(windows[0]!["target"] is null, "Private HWND metadata escaped into the public result.");
        var observation = (await desktop.HandleAsync("desktop.observe", new() { ["windowId"] = id, ["includeScreenshot"] = false })).Obj("result")!;
        Require(observation["elements"]![0]!["path"] is null && observation["elements"]![0]!["runtimeId"] is null, "Private UIA references escaped into the public result.");
        var action = new JsonObject { ["snapshotId"] = observation.Str("snapshotId"), ["elementId"] = "e0", ["action"] = "invoke" };
        try { await desktop.HandleAsync("desktop.element", action); } catch (TimeoutException) { }
        try { await desktop.HandleAsync("desktop.element", action); } catch (InvalidOperationException) { }
        Require(actions == 1, "Timed-out input was replayed.");
        return "validation precedes workers, private references stay private, timed-out actions consume their observation";
    }

    public static async Task<string> WorkerTimeout()
    {
        var info = new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::In.ReadLine() | Out-Null; Start-Sleep -Seconds 30" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start disposable timeout fixture.");
        var watch = Stopwatch.StartNew();
        try
        {
            await DesktopWorker.ExchangeAsync(process, new JsonObject { ["op"] = "element" }, TimeSpan.FromMilliseconds(250), default);
            throw new InvalidOperationException("Hung worker did not time out.");
        }
        catch (TimeoutException error) { Require(error.Message.Contains("may already"), "An uncertain action was reported as definitely unapplied."); }
        Require(process.WaitForExit(2000) && watch.Elapsed < TimeSpan.FromSeconds(4), "Worker was left running after its deadline.");
        return "hung subprocess terminated within deadline; uncertain action reported without replay";
    }

    public static string Presentation()
    {
        var parsed = DesktopWorker.ParseResult("{\"ok\":true,\"result\":{\"windows\":[]}}");
        Require(JsonLine.Ok(parsed).Obj("result") is not null, "Worker result could not be wrapped in the seat response.");
        var result = new JsonObject
        {
            ["window"] = new JsonObject { ["title"] = "<script>alert('title')</script>" },
            ["elements"] = new JsonArray(new JsonObject { ["id"] = "e0", ["role"] = "Edit", ["name"] = "<img onerror=bad>", ["text"] = "</script><script>bad()</script>", ["actions"] = new JsonArray("set_value") })
        };
        string html = DesktopPresentation.Html(result);
        Require(!html.Contains("<img onerror=bad>") && !html.Contains("<script>bad()") && html.Contains("&lt;img"), "Report failed to escape untrusted app text.");
        result["window"]!["title"] = "Unsafe\u001b[2J title";
        result["elements"]![0]!["toggleState"] = "On";
        result["elements"]![0]!["automationId"] = "Name" + (char)27 + "Box";
        result["elements"]![0]!["bounds"] = new JsonObject { ["x"] = -5, ["y"] = 20, ["width"] = 300, ["height"] = 24 };
        result["screenshot"] = new JsonObject { ["width"] = 640, ["height"] = 360, ["sourceWidth"] = 1920, ["sourceHeight"] = 1080 };
        string summary = DesktopPresentation.Summary(result);
        Require(!summary.Contains('\u001b') && summary.Contains("toggle: On"), "Summary lost control state or exposed terminal escape codes.");
        // Clients that receive only text still need wait selectors, pixel fallback and the capture scale.
        Require(summary.Contains("#Name Box @-5,20,300,24") && summary.Contains("Screenshot 640x360 (captured at 1920x1080)"),
            "Summary lost automation IDs, bounds or capture geometry.");
        return "HTML report escapes untrusted app text; text summary keeps automation IDs, bounds and capture geometry";
    }

    /// <summary>A hidden window, never shown, that answers UI Automation with its own provider.</summary>
    private abstract class HiddenWindow : NativeWindow, IRawElementProviderSimple
    {
        private const int WmGetObject = 0x003D, UiaRootObjectId = -25;
        public const int Quit = 0x8002; // WM_APP + 2

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmGetObject && (int)(long)m.LParam == UiaRootObjectId)
            {
                m.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, m.WParam, m.LParam, this);
                return;
            }
            if (m.Msg == Quit) { Application.ExitThread(); return; }
            base.WndProc(ref m);
        }

        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public abstract object? GetPatternProvider(int patternId);
        public abstract object? GetPropertyValue(int propertyId);
        public IRawElementProviderSimple HostRawElementProvider => AutomationInteropProvider.HostProviderFromHandle(Handle);
    }

    /// <summary>A control that says it is an AppBar (UIA control type 50040), as File Explorer's command bar does.</summary>
    private sealed class AppBarWindow : HiddenWindow
    {
        private const int AppBarControlTypeId = 50040;
        public override object? GetPatternProvider(int patternId) => null;
        public override object? GetPropertyValue(int propertyId) =>
            propertyId == AutomationElement.ControlTypeProperty.Id ? AppBarControlTypeId
            : propertyId == AutomationElement.NameProperty.Id ? "Command bar" : null;
    }

    /// <summary>
    /// A drop-down list, or a text field, that takes set_value the way it is told to: applies it, ignores it as Chrome's
    /// &lt;select&gt; does, applies it a moment later as Chrome's text fields do, reformats it, or goes away.
    /// </summary>
    private sealed class ValueWindow : HiddenWindow, IValueProvider
    {
        private volatile string _mode = "apply", _value = "Free";
        private volatile bool _sent, _field;
        public void Use(string mode, bool field = false) { _mode = mode; _field = field; _value = "Free"; _sent = false; }
        public override object? GetPatternProvider(int patternId) => patternId == ValuePattern.Pattern.Id ? this : null;
        public override object? GetPropertyValue(int propertyId) =>
            propertyId == AutomationElement.ControlTypeProperty.Id ? (_field ? ControlType.Edit.Id : ControlType.ComboBox.Id)
            : propertyId == AutomationElement.NameProperty.Id ? "Plan" : null;
        public bool IsReadOnly => false;
        public string Value => _mode == "vanish" && _sent ? throw new ElementNotAvailableException() : _value;
        public void SetValue(string value)
        {
            _sent = true;
            switch (_mode)
            {
                case "ignore": break;
                case "late": _ = Task.Delay(300).ContinueWith(_ => _value = value, TaskScheduler.Default); break;
                case "reformat": _value = value.ToUpperInvariant(); break;
                default: _value = value; break;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Runs a check against a hidden window of this process on its own STA thread. The window is this process's, so it
    /// passes the seat's session checks; the check runs off that thread, since UI Automation must not run on an STA
    /// thread, as in the seat's worker.
    /// </summary>
    private static string Hosted(HiddenWindow window, string caption, Func<WindowTarget, string> check)
    {
        using var created = new ManualResetEventSlim();
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try { window.CreateHandle(new CreateParams { Caption = caption, Style = unchecked((int)0x80000000) }); } // WS_POPUP, never visible
            catch (Exception ex) { failed = ex; created.Set(); return; }
            created.Set();
            Application.Run();
            window.DestroyHandle();
        }) { IsBackground = true, Name = caption };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(created.Wait(5000) && failed is null, "the check's hidden window was not created: " + failed?.Message);
        try
        {
            var target = WindowAccess.Identify(window.Handle);
            return Task.Run(() => check(target)).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        }
        finally
        {
            PostMessage(window.Handle, HiddenWindow.Quit, IntPtr.Zero, IntPtr.Zero);
            thread.Join(5000);
        }
    }

    private static JsonObject Root(JsonObject observation) => observation["elements"]?.AsArray().FirstOrDefault()?.AsObject()
        ?? throw new InvalidOperationException("the observation has no elements: " + observation.ToJsonString());

    public static string UnknownControlTypes()
    {
        Require(AccessibilityReader.Role(ControlType.Button) == "Button", "a known control type lost its name");
        return Hosted(new AppBarWindow(), "Anode unknown control type check", target =>
        {
            var root = Root(AccessibilityReader.Observe(target, new JsonObject { ["maxDepth"] = 1 }));
            string expected = AccessibilityReader.Role(ControlType.LookupById(50040));
            Require(root.Str("role") == expected && root.Str("name") == "Command bar",
                $"an AppBar control was described as {root.Str("role")} \"{root.Str("name")}\", not {expected} \"Command bar\"");
            // Acting on it compares the role again; an action it doesn't offer must be refused after that comparison.
            try
            {
                AccessibilityReader.Act(target, root, new JsonObject { ["action"] = "invoke" });
                throw new InvalidOperationException("an action the control does not offer was performed");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("not offered", StringComparison.Ordinal)) { }
            return $"a control type .NET's client can't name (AppBar, as in File Explorer) is observed as {expected}, and actions on it reach their checks";
        });
    }

    public static string IgnoredValues()
    {
        var window = new ValueWindow();
        return Hosted(window, "Anode set_value check", target =>
        {
            var list = Root(AccessibilityReader.Observe(target, new JsonObject { ["maxDepth"] = 0 }));
            Require(list["actions"]!.AsArray().Any(action => action?.GetValue<string>() == "set_value"), "the list does not offer set_value");
            JsonObject Set(string mode, JsonObject? control = null)
            {
                window.Use(mode, field: control is not null);
                return AccessibilityReader.Act(target, control ?? list, new JsonObject { ["action"] = "set_value", ["value"] = "Team" });
            }
            foreach (string mode in new[] { "apply", "late" })
            {
                var done = Set(mode);
                Require(done.Str("performed") == "set_value" && done["value"] is null && window.Value == "Team",
                    $"a value the control {(mode == "late" ? "applied a moment later" : "applied")} was reported as {done.ToJsonString()}");
            }
            var reformatted = Set("reformat");
            Require(reformatted.Str("performed") == "set_value" && reformatted.Str("value") == "TEAM"
                && DesktopPresentation.Summary(reformatted).EndsWith("It now reads: \"TEAM\"", StringComparison.Ordinal),
                "a value the control reformatted was not reported as it now reads: " + reformatted.ToJsonString());
            var watch = Stopwatch.StartNew();
            try
            {
                var ignored = Set("ignore");
                throw new InvalidOperationException("a value the control ignored was reported as " + ignored.ToJsonString());
            }
            catch (InvalidOperationException error) when (error.Message.Contains("still showed its old value", StringComparison.Ordinal))
            {
                // Errors are logged, so the message must not quote the control's text; a list gets the way to choose.
                Require(!error.Message.Contains("Free", StringComparison.Ordinal) && error.Message.Contains("expand the list", StringComparison.Ordinal),
                    "the failure quoted the control's text or left out the drop-down advice: " + error.Message);
            }
            Require(window.Value == "Free" && watch.ElapsedMilliseconds >= AccessibilityReader.SetValueSettleMs - 100,
                "an ignored value was refused before the control had time to apply it");
            // The value went out before the control went away, so that must not read as "nothing happened".
            var unread = Set("vanish");
            Require(unread.Str("performed") == "set_value" && unread.Str("note")!.Contains("could not be read back", StringComparison.Ordinal),
                "a control that went away after set_value was reported as " + unread.ToJsonString());
            window.Use("ignore", field: true);
            var field = Root(AccessibilityReader.Observe(target, new JsonObject { ["maxDepth"] = 0 }));
            try
            {
                var ignored = Set("ignore", field);
                throw new InvalidOperationException("a value the text field ignored was reported as " + ignored.ToJsonString());
            }
            catch (InvalidOperationException error) when (error.Message.Contains("still showed its old value", StringComparison.Ordinal))
            {
                Require(!error.Message.Contains("expand the list", StringComparison.Ordinal), "a text field was told to expand a list: " + error.Message);
            }
            return "set_value reads the control back: an ignored value (as Chrome's <select>) fails instead of reporting performed; applied, late and reformatted values pass";
        });
    }

    /// <summary>
    /// A window whose tree repeats controls. The window lists A and D; A lists B and C; C lists A, its own parent, so
    /// its children are A and D again (a loop, as Chrome's window appeared below itself while a &lt;select&gt; list
    /// was open); D lists a second provider with B's runtime ID, then E, which says E is its own next sibling.
    /// </summary>
    private sealed class LoopWindow : HiddenWindow, IRawElementProviderFragmentRoot
    {
        public readonly LoopNode A, B, C, D, B2, E;
        public LoopWindow()
        {
            A = new(this, 1, "A"); B = new(this, 2, "B"); C = new(this, 3, "C"); D = new(this, 4, "D");
            B2 = new(this, 2, "B"); E = new(this, 5, "E");
            (A.Parent, A.Next, A.FirstChild, A.LastChild) = (this, D, B, C);
            (D.Parent, D.Previous, D.FirstChild, D.LastChild) = (this, A, B2, E);
            (B.Parent, B.Next) = (A, C);
            (C.Parent, C.Previous, C.FirstChild, C.LastChild) = (A, B, A, A);
            (B2.Parent, B2.Next) = (D, E);
            (E.Parent, E.Previous, E.Next) = (D, B2, E);
        }
        public override object? GetPatternProvider(int patternId) => null;
        public override object? GetPropertyValue(int propertyId) => null;
        public System.Windows.Rect BoundingRectangle => System.Windows.Rect.Empty;
        public IRawElementProviderFragmentRoot FragmentRoot => this;
        public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;
        public int[]? GetRuntimeId() => null; // the window's own, from its handle
        public IRawElementProviderFragment? Navigate(NavigateDirection direction) =>
            direction == NavigateDirection.FirstChild ? A : direction == NavigateDirection.LastChild ? D : null;
        public void SetFocus() { }
        public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y) => null;
        public IRawElementProviderFragment? GetFocus() => null;
    }

    private sealed class LoopNode(LoopWindow window, int id, string name) : IRawElementProviderFragment, IInvokeProvider
    {
        public IRawElementProviderFragment? Parent, FirstChild, LastChild, Next, Previous;
        public int Invoked;
        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public object? GetPatternProvider(int patternId) => patternId == InvokePattern.Pattern.Id ? this : null;
        public object? GetPropertyValue(int propertyId) =>
            propertyId == AutomationElement.ControlTypeProperty.Id ? ControlType.Button.Id
            : propertyId == AutomationElement.NameProperty.Id ? name
            : propertyId == AutomationElement.IsEnabledProperty.Id ? true : null;
        public IRawElementProviderSimple? HostRawElementProvider => null;
        public System.Windows.Rect BoundingRectangle => System.Windows.Rect.Empty;
        public IRawElementProviderFragmentRoot FragmentRoot => window;
        public IRawElementProviderSimple[]? GetEmbeddedFragmentRoots() => null;
        public int[] GetRuntimeId() => new[] { AutomationInteropProvider.AppendRuntimeId, id };
        public IRawElementProviderFragment? Navigate(NavigateDirection direction) => direction switch
        {
            NavigateDirection.Parent => Parent, NavigateDirection.FirstChild => FirstChild, NavigateDirection.LastChild => LastChild,
            NavigateDirection.NextSibling => Next, NavigateDirection.PreviousSibling => Previous, _ => null
        };
        public void SetFocus() { }
        public void Invoke() => Interlocked.Increment(ref Invoked);
    }

    public static string RepeatedElements()
    {
        var window = new LoopWindow();
        return Hosted(window, "Anode repeated elements check", target =>
        {
            // Six controls, and a budget of seven: repeats must not use it up.
            var observation = AccessibilityReader.Observe(target, new JsonObject { ["maxDepth"] = 6, ["maxElements"] = 7, ["includeOffscreen"] = true });
            var elements = observation["elements"]!.AsArray().OfType<JsonObject>().ToList();
            string names = string.Join(",", elements.Skip(1).Select(element => element.Str("name")));
            Require(names == "A,D,B,C,E" && observation.Bool("truncated") == false,
                $"a tree that repeats controls was observed as {names} (truncated: {observation.Bool("truncated")}), not A,D,B,C,E once each");
            Require(observation.Int("skippedRepeats") == 3 && DesktopPresentation.Summary(observation).Contains("Skipped 3 control(s)", StringComparison.Ordinal),
                "the observation did not report the 3 repeats it skipped: " + observation.ToJsonString());
            // Nothing was hidden, so a wait for a control to go away still matches.
            Require(observation["warnings"]!.AsArray().Count == 0
                && DesktopWait.Matches(observation, new JsonObject { ["name"] = "Spinner", ["state"] = "missing" }),
                "skipped repeats stopped a wait for a missing control from matching");
            // E's path counts the repeat before it, so an action still reaches the control that was observed.
            var e = elements.Single(element => element.Str("name") == "E");
            AccessibilityReader.Act(target, e, new JsonObject { ["action"] = "invoke" });
            Require(window.E.Invoked == 1 && new[] { window.A, window.B, window.B2, window.C, window.D }.All(node => node.Invoked == 0),
                "an action reached the wrong control");
            return "a control the app lists again, below itself (Chrome with a <select> open) or under a second parent, is observed once, without using the budget; waits and actions still work";
        });
    }
}
