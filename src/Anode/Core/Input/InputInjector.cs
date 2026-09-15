using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Anode.Core.Input;

/// <summary>
/// Keyboard and mouse injection with <c>SendInput</c>.
///
/// The isolation the whole project is built around lives here: this code only ever
/// runs inside the seat host, which lives inside the child session. <c>SendInput</c>
/// posts to the calling process's own session input queue, so it moves the seat's
/// pointer and types into the seat's focused window. The parent session's pointer,
/// focus and foreground window are untouched. There is no cross-session injection
/// anywhere in Anode, by construction.
///
/// Keys go in as scan codes by default, because games commonly read raw input or
/// DirectInput and ignore virtual-key-only synthetic events.
/// </summary>
internal static class InputInjector
{
    // ----------------------------------------------------------------- interop

    private const int InputMouse = 0;
    private const int InputKeyboard = 1;

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHWheel = 0x1000;
    private const uint MouseEventAbsolute = 0x8000;

    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventScanCode = 0x0008;

    private const uint MapVkToVsc = 0;
    private const int WheelDelta = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL, ParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    // --------------------------------------------------------------- geometry

    /// <summary>Size of the seat's desktop, in pixels.</summary>
    public static System.Drawing.Size ScreenSize()
    {
        int width = GetSystemMetrics(0);
        int height = GetSystemMetrics(1);
        if (width > 0 && height > 0) return new System.Drawing.Size(width, height);
        var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return bounds.Size;
    }

    public static System.Drawing.Point CursorPosition() =>
        GetCursorPos(out var point) ? point : System.Drawing.Point.Empty;

    // ------------------------------------------------------------------ mouse

    private static void Send(params Input[] inputs)
    {
        if (Desktop.WindowAccess.InputBlockReason() is { } blocked) throw new InvalidOperationException(blocked);
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
            throw Native.Native.LastError($"SendInput accepted {sent} of {inputs.Length} events");
    }

    private static Input AbsoluteMove(int x, int y)
    {
        var size = ScreenSize();
        int nx = (int)Math.Round(Math.Clamp(x, 0, size.Width - 1) * 65535.0 / Math.Max(1, size.Width - 1));
        int ny = (int)Math.Round(Math.Clamp(y, 0, size.Height - 1) * 65535.0 / Math.Max(1, size.Height - 1));
        return new Input
        {
            Type = InputMouse,
            Union = { Mouse = new MouseInput { Dx = nx, Dy = ny, Flags = MouseEventMove | MouseEventAbsolute } }
        };
    }

    public static void MoveTo(int x, int y) => Send(AbsoluteMove(x, y));

    public static void MoveBy(int dx, int dy) => Send(new Input
    {
        Type = InputMouse,
        Union = { Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = MouseEventMove } }
    });

    private static (uint Down, uint Up, uint Data) ButtonFlags(string button) => button.ToLowerInvariant() switch
    {
        "left" or "l" or "" => (MouseEventLeftDown, MouseEventLeftUp, 0u),
        "right" or "r" => (MouseEventRightDown, MouseEventRightUp, 0u),
        "middle" or "m" => (MouseEventMiddleDown, MouseEventMiddleUp, 0u),
        "x1" or "back" => (MouseEventXDown, MouseEventXUp, 1u),
        "x2" or "forward" => (MouseEventXDown, MouseEventXUp, 2u),
        _ => throw new ArgumentException($"Unknown mouse button '{button}'. Use left, right, middle, x1 or x2.")
    };

    public static void MouseDown(string button, int? x = null, int? y = null)
    {
        var (down, up, data) = ButtonFlags(button);
        var events = new List<Input>();
        if (x.HasValue && y.HasValue) events.Add(AbsoluteMove(x.Value, y.Value));
        events.Add(new Input { Type = InputMouse, Union = { Mouse = new MouseInput { Flags = down, MouseData = data } } });
        HeldMouse.Add((up, data));
        Send(events.ToArray());
    }

    public static void MouseUp(string button, int? x = null, int? y = null)
    {
        var (_, up, data) = ButtonFlags(button);
        var events = new List<Input>();
        if (x.HasValue && y.HasValue) events.Add(AbsoluteMove(x.Value, y.Value));
        events.Add(new Input { Type = InputMouse, Union = { Mouse = new MouseInput { Flags = up, MouseData = data } } });
        Send(events.ToArray());
        HeldMouse.Remove((up, data));
    }

    public static void Click(string button = "left", int? x = null, int? y = null, int count = 1, int holdMs = 20)
    {
        for (int i = 0; i < Math.Max(1, count); i++)
        {
            MouseDown(button, i == 0 ? x : null, i == 0 ? y : null);
            Thread.Sleep(Math.Max(1, holdMs));
            MouseUp(button);
            if (i + 1 < count) Thread.Sleep(40);
        }
    }

    public static void Scroll(int notches, bool horizontal = false) => Send(new Input
    {
        Type = InputMouse,
        Union =
        {
            Mouse = new MouseInput
            {
                MouseData = unchecked((uint)(notches * WheelDelta)),
                Flags = horizontal ? MouseEventHWheel : MouseEventWheel
            }
        }
    });

    public static void Drag(int fromX, int fromY, int toX, int toY, string button = "left", int steps = 24, int stepMs = 8)
    {
        MouseDown(button, fromX, fromY);
        try
        {
            steps = Math.Max(1, steps);
            for (int i = 1; i <= steps; i++)
            {
                int x = fromX + (toX - fromX) * i / steps;
                int y = fromY + (toY - fromY) * i / steps;
                MoveTo(x, y);
                Thread.Sleep(Math.Max(1, stepMs));
            }
        }
        finally
        {
            MouseUp(button);
        }
    }

    // --------------------------------------------------------------- keyboard

    private static Input KeyEvent(ushort vk, bool up, bool useScanCode)
    {
        ushort scan = (ushort)MapVirtualKey(vk, MapVkToVsc);
        uint flags = up ? KeyEventKeyUp : 0;
        if (IsExtendedKey(vk)) flags |= KeyEventExtended;

        if (useScanCode && scan != 0)
        {
            flags |= KeyEventScanCode;
            return new Input { Type = InputKeyboard, Union = { Keyboard = new KeyboardInput { Vk = 0, Scan = scan, Flags = flags } } };
        }

        return new Input { Type = InputKeyboard, Union = { Keyboard = new KeyboardInput { Vk = vk, Scan = scan, Flags = flags } } };
    }

    private static readonly HashSet<(uint Up, uint Data)> HeldMouse = new();
    private static readonly HashSet<(ushort Key, bool Scan)> HeldKeys = new();

    public static void KeyDown(string key, bool useScanCode = true)
    {
        ushort vk = KeyCodes.Resolve(key);
        HeldKeys.Add((vk, useScanCode));
        Send(KeyEvent(vk, false, useScanCode));
    }

    public static void KeyUp(string key, bool useScanCode = true)
    {
        ushort vk = KeyCodes.Resolve(key);
        Send(KeyEvent(vk, true, useScanCode));
        HeldKeys.Remove((vk, useScanCode));
    }

    /// <summary>Release only input issued by this host, under the desktop ownership gate.</summary>
    public static void ReleaseHeld()
    {
        foreach (var button in HeldMouse.ToArray())
        {
            Send(new Input { Type = InputMouse, Union = { Mouse = new MouseInput { Flags = button.Up, MouseData = button.Data } } });
            HeldMouse.Remove(button);
        }
        foreach (var key in HeldKeys.ToArray())
        {
            Send(KeyEvent(key.Key, true, key.Scan));
            HeldKeys.Remove(key);
        }
    }

    /// <summary>
    /// Presses a chord such as <c>ctrl+shift+esc</c> or a single key such as <c>f5</c>,
    /// holding it for <paramref name="holdMs"/> and releasing in reverse order.
    /// </summary>
    public static void Press(string chord, int holdMs = 40, bool useScanCode = true)
    {
        ushort[] keys = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(KeyCodes.Resolve)
            .ToArray();

        if (keys.Length == 0) throw new ArgumentException("No key was given.");

        var down = keys.Select(vk => KeyEvent(vk, false, useScanCode)).ToArray();
        foreach (ushort key in keys) HeldKeys.Add((key, useScanCode));
        try { Send(down); Thread.Sleep(Math.Max(1, holdMs)); }
        finally
        {
            var up = keys.Reverse().Select(vk => KeyEvent(vk, true, useScanCode)).ToArray();
            Send(up);
            foreach (ushort key in keys) HeldKeys.Remove((key, useScanCode));
        }
    }

    /// <summary>Types literal text as Unicode, so layout and dead keys do not interfere.</summary>
    public static void TypeText(string text, int perCharMs = 0)
    {
        foreach (char character in text)
        {
            if (character == '\n')
            {
                Press("enter", 20);
                continue;
            }
            if (character == '\t')
            {
                Press("tab", 20);
                continue;
            }
            if (character == '\r') continue;

            Send(
                new Input { Type = InputKeyboard, Union = { Keyboard = new KeyboardInput { Scan = character, Flags = KeyEventUnicode } } },
                new Input { Type = InputKeyboard, Union = { Keyboard = new KeyboardInput { Scan = character, Flags = KeyEventUnicode | KeyEventKeyUp } } });

            if (perCharMs > 0) Thread.Sleep(perCharMs);
        }
    }

    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,             // PageUp PageDown End Home
        0x25 or 0x26 or 0x27 or 0x28 => true,             // arrows
        0x2D or 0x2E => true,                             // Insert Delete
        0x2C => true,                                     // PrintScreen
        0x90 => true,                                     // NumLock
        0x6F => true,                                     // Divide
        0xA3 or 0xA5 => true,                             // RControl RMenu
        0x5B or 0x5C or 0x5D => true,                     // LWin RWin Apps
        _ => false
    };
}

/// <summary>Key names an agent might reasonably type, mapped to virtual-key codes.</summary>
internal static class KeyCodes
{
    private static readonly Dictionary<string, ushort> Map = Build();

    public static ushort Resolve(string name)
    {
        string key = name.Trim().ToLowerInvariant();
        if (Map.TryGetValue(key, out ushort vk)) return vk;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (key.StartsWith("vk", StringComparison.Ordinal) &&
            ushort.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort raw))
            return raw;

        throw new ArgumentException($"Unknown key '{name}'. Try a letter, a digit, f1-f24, or a name like enter, esc, space, tab, ctrl, alt, shift, win, up, down, left, right.");
    }

    public static IEnumerable<string> Names => Map.Keys.OrderBy(k => k, StringComparer.Ordinal);

    private static Dictionary<string, ushort> Build()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08, ["back"] = 0x08, ["tab"] = 0x09,
            ["clear"] = 0x0C, ["enter"] = 0x0D, ["return"] = 0x0D,
            ["shift"] = 0xA0, ["lshift"] = 0xA0, ["rshift"] = 0xA1,
            ["ctrl"] = 0xA2, ["control"] = 0xA2, ["lctrl"] = 0xA2, ["rctrl"] = 0xA3,
            ["alt"] = 0xA4, ["lalt"] = 0xA4, ["ralt"] = 0xA5, ["menu"] = 0xA4,
            ["pause"] = 0x13, ["capslock"] = 0x14, ["caps"] = 0x14,
            ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20, [" "] = 0x20,
            ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
            ["end"] = 0x23, ["home"] = 0x24,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
            ["insert"] = 0x2D, ["ins"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["win"] = 0x5B, ["lwin"] = 0x5B, ["rwin"] = 0x5C, ["apps"] = 0x5D, ["menukey"] = 0x5D,
            ["numlock"] = 0x90, ["scrolllock"] = 0x91,
            ["multiply"] = 0x6A, ["add"] = 0x6B, ["subtract"] = 0x6D,
            ["decimal"] = 0x6E, ["divide"] = 0x6F,
            ["semicolon"] = 0xBA, [";"] = 0xBA,
            ["plus"] = 0xBB, ["="] = 0xBB,
            ["comma"] = 0xBC, [","] = 0xBC,
            ["minus"] = 0xBD, ["-"] = 0xBD,
            ["period"] = 0xBE, ["."] = 0xBE,
            ["slash"] = 0xBF, ["/"] = 0xBF,
            ["tilde"] = 0xC0, ["`"] = 0xC0,
            ["lbracket"] = 0xDB, ["["] = 0xDB,
            ["backslash"] = 0xDC, ["\\"] = 0xDC,
            ["rbracket"] = 0xDD, ["]"] = 0xDD,
            ["quote"] = 0xDE, ["'"] = 0xDE
        };

        for (int i = 1; i <= 24; i++) map[$"f{i}"] = (ushort)(0x6F + i);       // F1 = 0x70
        for (int i = 0; i <= 9; i++) map[$"num{i}"] = (ushort)(0x60 + i);      // numpad
        for (char c = 'a'; c <= 'z'; c++) map[c.ToString()] = char.ToUpperInvariant(c);
        for (char c = '0'; c <= '9'; c++) map[c.ToString()] = c;

        return map;
    }
}
