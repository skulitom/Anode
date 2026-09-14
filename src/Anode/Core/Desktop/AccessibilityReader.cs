using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Anode.Core.Bridge;

namespace Anode.Core.Desktop;

/// <summary>Runs only in a disposable MTA worker inside the verified child session.</summary>
internal static class AccessibilityReader
{
    private static readonly TreeWalker Walker = TreeWalker.ControlViewWalker;
    private static readonly (AutomationPattern Pattern, string[] Actions)[] Patterns =
    {
        (InvokePattern.Pattern, new[] { "invoke" }), (ValuePattern.Pattern, new[] { "set_value" }),
        (TogglePattern.Pattern, new[] { "toggle" }), (SelectionItemPattern.Pattern, new[] { "select" }),
        (ExpandCollapsePattern.Pattern, new[] { "expand", "collapse" }),
        (ScrollItemPattern.Pattern, new[] { "scroll_into_view" }),
        (ScrollPattern.Pattern, new[] { "scroll" }), (RangeValuePattern.Pattern, new[] { "set_range" })
    };

    public static JsonObject Observe(WindowTarget target, JsonObject request)
    {
        var root = AutomationElement.FromHandle(WindowAccess.Verify(target));
        int maximum = request.Int("maxElements") ?? 200;
        int depthLimit = request.Int("maxDepth") ?? 8;
        int textBudget = request.Int("maxTextChars") ?? 6000;
        bool offscreen = request.Bool("includeOffscreen") ?? false;
        var elements = new JsonArray();
        var warnings = new JsonArray();
        var watch = Stopwatch.StartNew();
        var queue = new Queue<(AutomationElement Element, int[] Path, string? Parent)>();
        queue.Enqueue((root, Array.Empty<int>(), null));
        bool truncated = false;
        int visited = 0;
        while (queue.Count > 0 && visited < maximum && watch.ElapsedMilliseconds < 4000)
        {
            var current = queue.Dequeue();
            visited++;
            try
            {
                var node = Describe(current.Element, current.Path, current.Parent, ref textBudget);
                string? parent = current.Parent;
                if (offscreen || node.Bool("offscreen") != true || current.Path.Length == 0)
                {
                    node["id"] = "e" + elements.Count;
                    parent = node.Str("id");
                    elements.Add(node);
                }
                if (current.Path.Length >= depthLimit) { truncated = true; continue; }
                int index = 0;
                var child = Walker.GetFirstChild(current.Element);
                while (child is not null && queue.Count + visited < maximum && watch.ElapsedMilliseconds < 4000)
                {
                    queue.Enqueue((child, current.Path.Append(index++).ToArray(), parent));
                    child = Walker.GetNextSibling(child);
                }
                if (child is not null) truncated = true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                if (warnings.Count < 3) warnings.Add("A control changed or did not expose readable accessibility information.");
            }
        }
        truncated |= queue.Count > 0;
        return new JsonObject
        {
            ["window"] = WindowAccess.Describe(target), ["elements"] = elements,
            ["truncated"] = truncated, ["warnings"] = warnings,
            ["observedAt"] = DateTime.UtcNow.ToString("o"), ["elapsedMs"] = watch.ElapsedMilliseconds
        };
    }

    private static JsonObject Describe(AutomationElement element, int[] path, string? parent, ref int textBudget)
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        foreach (var property in new[]
        {
            AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.AutomationIdProperty,
            AutomationElement.ClassNameProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.IsKeyboardFocusableProperty, AutomationElement.HasKeyboardFocusProperty,
            AutomationElement.IsPasswordProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.ProcessIdProperty
        }) cache.Add(property);
        var data = element.GetUpdatedCache(cache).Cached;
        VerifyElementSession(data.ProcessId);
        bool password = data.IsPassword;
        var supported = element.GetSupportedPatterns();
        var actions = new List<string>();
        if (!password)
            foreach (var pattern in Patterns)
                if (supported.Contains(pattern.Pattern)) actions.AddRange(pattern.Actions);
        if (data.IsKeyboardFocusable && !password) actions.Add("focus");
        var bounds = data.BoundingRectangle;
        var node = new JsonObject
        {
            ["parent"] = parent, ["depth"] = path.Length,
            ["role"] = data.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            ["name"] = Clip(data.Name, 500), ["automationId"] = Clip(data.AutomationId, 256),
            ["className"] = Clip(data.ClassName, 128), ["enabled"] = data.IsEnabled,
            ["offscreen"] = data.IsOffscreen, ["focused"] = data.HasKeyboardFocus, ["password"] = password,
            ["bounds"] = bounds.IsEmpty ? null : WindowAccess.RectangleJson(new Rectangle(
                (int)bounds.X, (int)bounds.Y, Math.Max(0, (int)bounds.Width), Math.Max(0, (int)bounds.Height))),
            ["actions"] = new JsonArray(actions.Select(value => (JsonNode)value).ToArray()),
            // Private references are retained by the host and removed from public results.
            ["path"] = new JsonArray(path.Select(value => (JsonNode)value).ToArray()),
            ["runtimeId"] = new JsonArray(element.GetRuntimeId().Select(value => (JsonNode)value).ToArray()),
            ["processId"] = data.ProcessId
        };
        if (!password)
        {
            string? text = null;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            {
                var pattern = (ValuePattern)value;
                node["readOnly"] = pattern.Current.IsReadOnly;
                if (textBudget > 0) text = pattern.Current.Value;
                if (pattern.Current.IsReadOnly) actions.Remove("set_value");
            }
            else if (textBudget > 0 && element.TryGetCurrentPattern(TextPattern.Pattern, out var document))
            {
                var pattern = (TextPattern)document;
                text = pattern.DocumentRange.GetText(Math.Min(textBudget, 3000));
                var selected = pattern.GetSelection().FirstOrDefault()?.GetText(Math.Min(textBudget, 1000));
                if (!string.IsNullOrEmpty(selected))
                {
                    node["selectedText"] = Clip(selected, textBudget);
                    textBudget -= node.Str("selectedText")!.Length;
                }
            }
            if (!string.IsNullOrEmpty(text))
            {
                string limited = Clip(text, Math.Min(textBudget, 3000));
                node["text"] = limited;
                textBudget -= limited.Length;
            }
        }
        if (!password && element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            node["toggleState"] = ((TogglePattern)toggle).Current.ToggleState.ToString();
        if (!password && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            node["selected"] = ((SelectionItemPattern)selection).Current.IsSelected;
        if (!password && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            node["expandState"] = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString();
        if (!password && element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range))
        {
            var state = ((RangeValuePattern)range).Current;
            node["range"] = new JsonObject
            {
                ["value"] = Finite(state.Value), ["minimum"] = Finite(state.Minimum), ["maximum"] = Finite(state.Maximum),
                ["smallChange"] = Finite(state.SmallChange), ["largeChange"] = Finite(state.LargeChange), ["readOnly"] = state.IsReadOnly
            };
            if (state.IsReadOnly) actions.Remove("set_range");
        }
        if (!password && element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrolling))
        {
            var state = ((ScrollPattern)scrolling).Current;
            node["scroll"] = new JsonObject
            {
                ["horizontal"] = state.HorizontallyScrollable, ["vertical"] = state.VerticallyScrollable,
                ["horizontalPercent"] = Finite(state.HorizontalScrollPercent), ["verticalPercent"] = Finite(state.VerticalScrollPercent)
            };
        }
        node["actions"] = new JsonArray(actions.Select(value => (JsonNode)value).ToArray());
        return node;
    }

    public static JsonObject Act(WindowTarget target, JsonObject reference, JsonObject request)
    {
        var root = AutomationElement.FromHandle(WindowAccess.Verify(target));
        var element = root;
        foreach (int index in reference["path"]!.AsArray().Select(value => value!.GetValue<int>()))
        {
            element = Walker.GetFirstChild(element);
            for (int i = 0; i < index && element is not null; i++) element = Walker.GetNextSibling(element);
            if (element is null) throw Stale();
        }
        int[] runtimeId = reference["runtimeId"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        if (!element.GetRuntimeId().SequenceEqual(runtimeId)) throw Stale();
        var state = element.Current;
        VerifyElementSession(state.ProcessId);
        if (state.ProcessId != reference.Int("processId") || Clip(state.Name, 500) != reference.Str("name")
            || state.ControlType.ProgrammaticName.Replace("ControlType.", "") != reference.Str("role")
            || Clip(state.AutomationId, 256) != reference.Str("automationId")) throw Stale();
        if (state.IsPassword) throw new InvalidOperationException("Password controls require the user's direct interaction.");
        if (!state.IsEnabled) throw new InvalidOperationException("The control is disabled. Inspect again.");
        WindowAccess.Verify(target);
        string action = request.Str("action") ?? throw new ArgumentException("An element action is required.");
        if (reference["actions"] is not JsonArray actions || !actions.Any(value => value?.GetValue<string>() == action))
            throw new InvalidOperationException("That action was not offered by the observed control. Inspect again.");
        T Pattern<T>(AutomationPattern id) where T : class => element.TryGetCurrentPattern(id, out var value)
            ? (T)value : throw Stale();
        switch (action)
        {
            case "invoke": Pattern<InvokePattern>(InvokePattern.Pattern).Invoke(); break;
            case "focus": element.SetFocus(); break;
            case "set_value": Pattern<ValuePattern>(ValuePattern.Pattern).SetValue(request.Str("value")!); break;
            case "toggle": Pattern<TogglePattern>(TogglePattern.Pattern).Toggle(); break;
            case "select": Pattern<SelectionItemPattern>(SelectionItemPattern.Pattern).Select(); break;
            case "expand": Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern).Expand(); break;
            case "collapse": Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern).Collapse(); break;
            case "scroll_into_view": Pattern<ScrollItemPattern>(ScrollItemPattern.Pattern).ScrollIntoView(); break;
            case "scroll":
                var amount = request.Str("amount") == "large" ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement;
                if (request.Str("direction") is "up" or "left") amount = request.Str("amount") == "large" ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement;
                Pattern<ScrollPattern>(ScrollPattern.Pattern).Scroll(
                    request.Str("direction") is "left" or "right" ? amount : ScrollAmount.NoAmount,
                    request.Str("direction") is "up" or "down" ? amount : ScrollAmount.NoAmount);
                break;
            case "set_range": Pattern<RangeValuePattern>(RangeValuePattern.Pattern).SetValue(request["number"]!.GetValue<double>()); break;
            default: throw new ArgumentException("Unknown element action.");
        }
        return new JsonObject { ["performed"] = action, ["note"] = "Observation consumed. Inspect again before the next action." };
    }

    private static InvalidOperationException Stale() => new("The control changed since observation. Inspect again; the action was not replayed.");
    private static void VerifyElementSession(int pid)
    {
        using var process = Process.GetProcessById(pid);
        if (process.SessionId != (int)Core.Session.ChildSession.CurrentSessionId())
            throw new InvalidOperationException("Refusing an accessibility element outside the seat session.");
    }
    private static string Clip(string? value, int maximum) => string.IsNullOrEmpty(value) || maximum <= 0 ? "" : value[..Math.Min(value.Length, maximum)];
    private static JsonNode? Finite(double value) => double.IsFinite(value) ? JsonValue.Create(value) : null;
}
