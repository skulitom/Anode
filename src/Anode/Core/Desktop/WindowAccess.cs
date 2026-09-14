using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Session;
using Win = Anode.Core.Native.Native;

namespace Anode.Core.Desktop;

internal sealed record WindowTarget(long Handle, int Pid, long Started)
{
    public JsonObject ToJson() => new() { ["handle"] = Handle, ["pid"] = Pid, ["started"] = Started };
    public static WindowTarget FromJson(JsonObject value) => new(
        value["handle"]!.GetValue<long>(), value["pid"]!.GetValue<int>(), value["started"]!.GetValue<long>());
}

/// <summary>Every HWND is checked against this process's verified seat before use.</summary>
internal static class WindowAccess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(Win.EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);

    public static WindowTarget Identify(IntPtr handle)
    {
        if (!IsWindow(handle)) throw new InvalidOperationException("That window has closed. List seat windows again.");
        GetWindowThreadProcessId(handle, out uint pid);
        using var process = Process.GetProcessById(checked((int)pid));
        if (process.SessionId != (int)ChildSession.CurrentSessionId())
            throw new InvalidOperationException("Refusing a window outside the seat session.");
        return new WindowTarget(handle.ToInt64(), process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    public static IntPtr Verify(WindowTarget target)
    {
        var handle = new IntPtr(target.Handle);
        if (Identify(handle) != target)
            throw new InvalidOperationException("That window reference is stale. List seat windows again.");
        return handle;
    }

    public static JsonArray List()
    {
        var windows = new JsonArray();
        EnumWindows((handle, _) =>
        {
            if (!Win.IsWindowVisible(handle)) return true;
            try
            {
                var target = Identify(handle);
                var window = Describe(target);
                window["target"] = target.ToJson();
                windows.Add(window);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return windows.Count < 256;
        }, IntPtr.Zero);
        return windows;
    }

    public static Rectangle Bounds(WindowTarget target)
    {
        if (!GetWindowRect(Verify(target), out var rect)) throw Win.LastError("Cannot read seat window bounds");
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static JsonObject Describe(WindowTarget target)
    {
        var handle = Verify(target);
        using var process = Process.GetProcessById(target.Pid);
        var title = new StringBuilder(1024);
        Win.GetWindowText(handle, title, title.Capacity);
        var className = new StringBuilder(256);
        GetClassName(handle, className, className.Capacity);
        return new JsonObject
        {
            ["pid"] = target.Pid, ["process"] = process.ProcessName, ["title"] = title.ToString(),
            ["className"] = className.ToString(), ["bounds"] = RectangleJson(Bounds(target)),
            ["foreground"] = Win.GetForegroundWindow() == handle, ["enabled"] = IsWindowEnabled(handle),
            ["minimized"] = IsIconic(handle), ["maximized"] = IsZoomed(handle)
        };
    }

    public static JsonObject RectangleJson(Rectangle value) => new()
        { ["x"] = value.X, ["y"] = value.Y, ["width"] = value.Width, ["height"] = value.Height };

    public static JsonObject Act(WindowTarget target, string action, JsonObject request)
    {
        var handle = Verify(target);
        switch (action)
        {
            case "focus":
                if (IsIconic(handle)) ShowWindowAsync(handle, 9);
                if (!Win.SetForegroundWindow(handle))
                    throw new InvalidOperationException("Windows refused focus in the seat. Inspect the current window or modal dialog.");
                break;
            case "restore": ShowWindowAsync(handle, 9); break;
            case "maximize": ShowWindowAsync(handle, 3); break;
            case "minimize": ShowWindowAsync(handle, 6); break;
            case "close":
                if (!PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero)) throw Win.LastError("Cannot request window close");
                break;
            case "move":
                if (!SetWindowPos(handle, IntPtr.Zero, request["x"]!.GetValue<int>(), request["y"]!.GetValue<int>(),
                        request["width"]!.GetValue<int>(), request["height"]!.GetValue<int>(), 0x0014))
                    throw Win.LastError("Cannot move seat window");
                break;
            default: throw new ArgumentException("Unknown window action.");
        }
        return new JsonObject { ["requested"] = action, ["note"] = "Inspect again to confirm the resulting window state." };
    }
}
