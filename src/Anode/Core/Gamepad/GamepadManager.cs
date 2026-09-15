using System.Text.Json.Nodes;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Anode.Core.Bridge;

namespace Anode.Core.Gamepad;

/// <summary>
/// Virtual Xbox 360 controllers, backed by the ViGEm bus driver.
///
/// One caveat worth stating plainly: a ViGEm pad is a real HID device plugged into
/// the machine, not into a session. Every session sees it, exactly as it would see a
/// controller you plugged into a USB port. That is what makes it work for a game in
/// the seat, and it also means a game running on your own screen can read it too.
/// Anode attaches the pad when the seat comes up and detaches it when the seat goes
/// down, so it does not outlive the agent that asked for it.
///
/// State is sticky: <see cref="Apply"/> changes only the fields it is given and
/// resubmits the whole report, so holding W while nudging a stick works the way a
/// caller expects.
/// </summary>
internal sealed class GamepadManager : IDisposable
{
    private readonly object _gate = new();
    private ViGEmClient? _client;
    private readonly Dictionary<int, IXbox360Controller> _pads = new();

    public const int MaxSlots = 4;

    private ViGEmClient Client()
    {
        if (_client is not null) return _client;
        try
        {
            _client = new ViGEmClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The ViGEm bus driver did not respond, so no virtual controller can be created. "
                + "Install ViGEmBus from https://github.com/nefarius/ViGEmBus/releases and try again. "
                + $"({ex.GetType().Name}: {ex.Message})", ex);
        }
        return _client;
    }

    public JsonObject Attach(int slot)
    {
        Validate(slot);
        lock (_gate)
        {
            if (_pads.ContainsKey(slot))
                return new JsonObject { ["slot"] = slot, ["attached"] = true, ["note"] = "already attached" };

            var pad = Client().CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
            pad.ResetReport();
            pad.SubmitReport();
            _pads[slot] = pad;
            return new JsonObject { ["slot"] = slot, ["attached"] = true };
        }
    }

    public JsonObject Detach(int slot)
    {
        Validate(slot);
        lock (_gate)
        {
            if (!_pads.Remove(slot, out var pad))
                return new JsonObject { ["slot"] = slot, ["attached"] = false, ["note"] = "was not attached" };

            try { pad.ResetReport(); pad.SubmitReport(); pad.Disconnect(); } catch { }
            return new JsonObject { ["slot"] = slot, ["attached"] = false };
        }
    }

    public void DetachAll(bool requireSuccess = false)
    {
        lock (_gate)
        {
            Exception? failure = null;
            foreach (var (slot, pad) in _pads.ToArray())
            {
                try
                {
                    pad.ResetReport(); pad.SubmitReport(); pad.Disconnect();
                    _pads.Remove(slot);
                }
                catch (Exception ex)
                {
                    if (requireSuccess) failure ??= ex;
                    else _pads.Remove(slot);
                }
            }
            if (failure is not null) throw new InvalidOperationException("Could not detach an owned controller; desktop lease transfer is paused until cleanup succeeds.", failure);
        }
    }

    public JsonObject Reset(int slot)
    {
        var pad = Require(slot);
        lock (_gate)
        {
            pad.ResetReport();
            pad.SubmitReport();
        }
        return new JsonObject { ["slot"] = slot, ["reset"] = true };
    }

    /// <summary>
    /// Applies a partial controller state. Recognised keys:
    /// <c>buttons</c> (object of name -> bool), <c>axes</c> (lx, ly, rx, ry as -1..1),
    /// <c>triggers</c> (lt, rt as 0..1).
    /// </summary>
    public JsonObject Apply(int slot, JsonObject spec)
    {
        var pad = Require(slot);
        var applied = new JsonArray();

        lock (_gate)
        {
            if (spec.Obj("buttons") is { } buttons)
            {
                foreach (var (name, value) in buttons)
                {
                    bool pressed = value is not null && (value.GetValueKind() == System.Text.Json.JsonValueKind.True
                        || (bool.TryParse(value.ToString(), out bool parsed) && parsed));
                    pad.SetButtonState(ResolveButton(name), pressed);
                    applied.Add($"{name}={pressed}");
                }
            }

            if (spec.Obj("axes") is { } axes)
            {
                foreach (var (name, value) in axes)
                {
                    double magnitude = ReadNumber(value);
                    pad.SetAxisValue(ResolveAxis(name), ToAxis(magnitude));
                    applied.Add($"{name}={magnitude:0.###}");
                }
            }

            if (spec.Obj("triggers") is { } triggers)
            {
                foreach (var (name, value) in triggers)
                {
                    double magnitude = ReadNumber(value);
                    pad.SetSliderValue(ResolveTrigger(name), ToTrigger(magnitude));
                    applied.Add($"{name}={magnitude:0.###}");
                }
            }

            pad.SubmitReport();
        }

        return new JsonObject { ["slot"] = slot, ["applied"] = applied };
    }

    /// <summary>Presses a button (or pulls a trigger) for a moment and releases it.</summary>
    public JsonObject Tap(int slot, string name, int milliseconds)
    {
        var pad = Require(slot);
        milliseconds = Math.Clamp(milliseconds, 1, 10_000);

        bool isTrigger = name.ToLowerInvariant() is "lt" or "rt" or "lefttrigger" or "righttrigger";

        lock (_gate)
        {
            if (isTrigger) pad.SetSliderValue(ResolveTrigger(name), byte.MaxValue);
            else pad.SetButtonState(ResolveButton(name), true);
            pad.SubmitReport();
        }

        Thread.Sleep(milliseconds);

        lock (_gate)
        {
            if (isTrigger) pad.SetSliderValue(ResolveTrigger(name), 0);
            else pad.SetButtonState(ResolveButton(name), false);
            pad.SubmitReport();
        }

        return new JsonObject { ["slot"] = slot, ["tapped"] = name, ["ms"] = milliseconds };
    }

    public JsonArray Slots()
    {
        lock (_gate)
        {
            var array = new JsonArray();
            foreach (int slot in _pads.Keys.OrderBy(k => k))
                array.Add(new JsonObject { ["slot"] = slot, ["type"] = "xbox360" });
            return array;
        }
    }

    private IXbox360Controller Require(int slot)
    {
        Validate(slot);
        lock (_gate)
        {
            if (_pads.TryGetValue(slot, out var pad)) return pad;
        }
        throw new InvalidOperationException($"No virtual controller in slot {slot}. Attach one first (gamepad_attach).");
    }

    private static void Validate(int slot)
    {
        if (slot < 0 || slot >= MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot must be 0..{MaxSlots - 1}.");
    }

    private static double ReadNumber(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<double>(); }
        catch
        {
            return double.TryParse(node.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;
        }
    }

    private static short ToAxis(double value) =>
        (short)Math.Round(Math.Clamp(value, -1.0, 1.0) * (value < 0 ? 32768.0 : 32767.0));

    private static byte ToTrigger(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);

    public static Xbox360Button ResolveButton(string name) => name.Trim().ToLowerInvariant() switch
    {
        "a" => Xbox360Button.A,
        "b" => Xbox360Button.B,
        "x" => Xbox360Button.X,
        "y" => Xbox360Button.Y,
        "lb" or "leftshoulder" or "l1" => Xbox360Button.LeftShoulder,
        "rb" or "rightshoulder" or "r1" => Xbox360Button.RightShoulder,
        "back" or "select" or "view" => Xbox360Button.Back,
        "start" or "menu" => Xbox360Button.Start,
        "guide" or "home" or "xbox" => Xbox360Button.Guide,
        "ls" or "leftthumb" or "l3" => Xbox360Button.LeftThumb,
        "rs" or "rightthumb" or "r3" => Xbox360Button.RightThumb,
        "up" or "dpadup" => Xbox360Button.Up,
        "down" or "dpaddown" => Xbox360Button.Down,
        "left" or "dpadleft" => Xbox360Button.Left,
        "right" or "dpadright" => Xbox360Button.Right,
        _ => throw new ArgumentException(
            $"Unknown gamepad button '{name}'. Use a, b, x, y, lb, rb, back, start, guide, ls, rs, up, down, left, right, lt or rt.")
    };

    public static Xbox360Axis ResolveAxis(string name) => name.Trim().ToLowerInvariant() switch
    {
        "lx" or "leftx" or "leftthumbx" => Xbox360Axis.LeftThumbX,
        "ly" or "lefty" or "leftthumby" => Xbox360Axis.LeftThumbY,
        "rx" or "rightx" or "rightthumbx" => Xbox360Axis.RightThumbX,
        "ry" or "righty" or "rightthumby" => Xbox360Axis.RightThumbY,
        _ => throw new ArgumentException($"Unknown gamepad axis '{name}'. Use lx, ly, rx or ry.")
    };

    public static Xbox360Slider ResolveTrigger(string name) => name.Trim().ToLowerInvariant() switch
    {
        "lt" or "lefttrigger" or "l2" => Xbox360Slider.LeftTrigger,
        "rt" or "righttrigger" or "r2" => Xbox360Slider.RightTrigger,
        _ => throw new ArgumentException($"Unknown trigger '{name}'. Use lt or rt.")
    };

    public void Dispose()
    {
        DetachAll();
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}
