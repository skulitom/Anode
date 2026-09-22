using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Util;

namespace Anode.Daemon;

/// <summary>
/// Keeps the Remote Desktop control from moving the user's real pointer.
///
/// When a program in the seat calls SetCursorPos (SDL games do on every switch between
/// relative and absolute mouse mode), the RDP server sends a pointer-position update and
/// mstscax.dll applies it with SetCursorPos on the desktop that hosts the control: the
/// user's. It does so while the control is disabled, hidden or unfocused, and the control
/// has no setting that turns it off.
///
/// mstscax.dll imports SetCursorPos by name, so the guard replaces that one entry in the
/// module's import tables, in this process only, with a gate. The gate forwards to the
/// real function only while the user is driving the seat through the viewer (input
/// enabled, window visible and in the foreground); otherwise it reports success without
/// moving anything. Nothing outside mstscax.dll's own imports is touched.
/// </summary>
internal static unsafe class PointerGuard
{
    private const string ClientModule = "mstscax.dll";
    private const string GuardedImport = "SetCursorPos";

    private const int ImportDirectory = 1;
    private const int DelayImportDirectory = 13;
    private const int MaxEntries = 65536;

    private static readonly object Sync = new();
    private static readonly List<IntPtr> Slots = new();
    private static IntPtr _module;
    private static bool _installationComplete;
    private static delegate* unmanaged<int, int, int> _real;
    private static volatile Viewer[] _viewers = Array.Empty<Viewer>();
    private static long _suppressed, _forwarded;

    private sealed record Viewer(IntPtr Control, bool InputEnabled);

    public static bool Installed { get { lock (Sync) return IsInstalled(); } }
    private static bool IsInstalled() => _installationComplete && Slots.Count > 0 && Slots.All(slot => IsGate(*(IntPtr*)slot));
    public static long Suppressed => Interlocked.Read(ref _suppressed);
    public static long Forwarded => Interlocked.Read(ref _forwarded);

    /// <summary>
    /// Patches mstscax.dll once it is loaded. Safe to call repeatedly: an intact patch is
    /// left alone and an entry something else rewrote is patched again.
    /// </summary>
    public static bool Install()
    {
        lock (Sync)
        {
            _installationComplete = false;
            try
            {
                // Pinned so the recorded import slots stay valid for the life of the process.
                if (!GetModuleHandleEx(GetModuleHandlePin, ClientModule, out IntPtr module) || module == IntPtr.Zero)
                {
                    Log.Warn($"pointer guard: {ClientModule} is not loaded yet; nothing to patch");
                    return false;
                }
                IntPtr real = GetProcAddress(GetModuleHandle("user32.dll"), GuardedImport);
                if (real == IntPtr.Zero)
                {
                    Log.Error($"pointer guard: user32 does not export {GuardedImport}");
                    return false;
                }

                IntPtr hook = (IntPtr)(delegate* unmanaged<int, int, int>)&Gate;
                if (module != _module) Slots.Clear();
                _module = module;
                _real = (delegate* unmanaged<int, int, int>)real;

                var found = FindSlots((byte*)module, real, hook);
                Slots.Clear();
                int patched = 0;
                foreach (IntPtr slot in found)
                {
                    if (*(IntPtr*)slot != hook)
                    {
                        Write(slot, hook);
                        patched++;
                    }
                    // A failed write must never count as an installed guard.
                    Slots.Add(slot);
                }

                if (Slots.Count == 0)
                    Log.Error($"pointer guard: {ClientModule} has no {GuardedImport} import to patch. " +
                        "The viewer cannot connect safely; see docs/TROUBLESHOOTING.md.");
                else if (patched > 0)
                    Log.Info($"pointer guard: patched {patched} {GuardedImport} import(s) of {ClientModule}; " +
                        "the viewer moves the local pointer only while it is focused with control taken");
                _installationComplete = true;
                return IsInstalled();
            }
            catch (Exception ex)
            {
                Log.Error("pointer guard: could not patch the Remote Desktop control", ex);
                return false;
            }
        }
    }

    /// <summary>Records a viewer control and whether the user's input currently reaches it.</summary>
    public static void Track(IntPtr control, bool inputEnabled)
    {
        if (control == IntPtr.Zero) return;
        lock (Sync)
            _viewers = _viewers.Where(v => v.Control != control).Append(new Viewer(control, inputEnabled)).ToArray();
    }

    public static void Untrack(IntPtr control)
    {
        lock (Sync) _viewers = _viewers.Where(v => v.Control != control).ToArray();
    }

    public static JsonObject Status()
    {
        lock (Sync)
        {
            var status = new JsonObject
            {
                ["installed"] = IsInstalled(),
                ["patchedImports"] = Slots.Count(slot => IsGate(*(IntPtr*)slot)),
                ["suppressed"] = Suppressed,
                ["forwarded"] = Forwarded
            };
            // mstscax.dll only attempts a move while the real pointer is over this
            // rectangle, shown or not, so the live test needs to know where it is.
            if (_viewers.FirstOrDefault() is { } viewer && GetClientRect(viewer.Control, out Rect client))
            {
                var corner = new System.Drawing.Point(0, 0);
                if (ClientToScreen(viewer.Control, ref corner))
                    status["viewer"] = new JsonObject
                    {
                        ["x"] = corner.X, ["y"] = corner.Y,
                        ["width"] = client.Right - client.Left, ["height"] = client.Bottom - client.Top
                    };
            }
            return status;
        }
    }

    /// <summary>The whole policy: the user must be driving the seat through a viewer they can see.</summary>
    internal static bool Allows(bool inputEnabled, bool visible, bool foreground) => inputEnabled && visible && foreground;

    /// <summary>
    /// What mstscax.dll would call right now for SetCursorPos, read from its import table
    /// the way its own code does. Lets the self-test exercise the installed gate.
    /// </summary>
    internal static IntPtr[] ImportTargets()
    {
        lock (Sync) return Slots.Select(slot => *(IntPtr*)slot).ToArray();
    }

    internal static bool IsGate(IntPtr target) => target == (IntPtr)(delegate* unmanaged<int, int, int>)&Gate;

    [UnmanagedCallersOnly]
    private static int Gate(int x, int y)
    {
        // Called by mstscax.dll on its own threads. Nothing may throw across this boundary.
        try
        {
            if (!UserIsDriving())
            {
                Interlocked.Increment(ref _suppressed);
                return 1;
            }
            Interlocked.Increment(ref _forwarded);
            return _real(x, y);
        }
        catch { return 1; }
    }

    private static bool UserIsDriving()
    {
        IntPtr foreground = GetForegroundWindow();
        foreach (var viewer in _viewers)
        {
            IntPtr root = GetAncestor(viewer.Control, GetAncestorRoot);
            if (root == IntPtr.Zero) continue;
            if (Allows(viewer.InputEnabled, IsWindowVisible(root) && !IsIconic(root), root == foreground)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------- import tables

    /// <summary>
    /// Every import-table slot of the loaded image that resolves SetCursorPos, matched by
    /// imported name and by resolved address. The name of the DLL an import is listed
    /// under is ignored, so API-set redirection cannot hide it. Delay-load tables are
    /// walked too: an unresolved slot there is matched by name before its first use.
    /// </summary>
    private static List<IntPtr> FindSlots(byte* image, IntPtr real, IntPtr hook)
    {
        var found = new List<IntPtr>();
        if (*(ushort*)image != 0x5A4D) return found;                    // MZ
        byte* headers = image + *(int*)(image + 0x3C);
        if (*(uint*)headers != 0x00004550) return found;                // PE\0\0
        byte* optional = headers + 24;
        if (*(ushort*)optional != 0x20B) return found;                  // PE32+; Anode is x64 only
        uint imageSize = *(uint*)(optional + 56);
        uint directories = *(uint*)(optional + 108);
        uint Rva(int index) => index < directories ? *(uint*)(optional + 112 + index * 8) : 0;

        void Scan(uint names, uint addresses)
        {
            if (addresses == 0 || addresses >= imageSize || names >= imageSize) return;
            for (int i = 0; i < MaxEntries; i++)
            {
                long end = (i + 1L) * IntPtr.Size;
                if (addresses + end > imageSize || names != 0 && names + end > imageSize) break;
                IntPtr* slot = (IntPtr*)(image + addresses) + i;
                ulong name = names != 0 ? *((ulong*)(image + names) + i) : 0;
                if (names != 0 ? name == 0 : *slot == IntPtr.Zero) break;
                bool match = *slot == real || *slot == hook;
                // The high bit marks an import by ordinal, which has no name to compare.
                if (!match && names != 0 && (name & 0x8000000000000000) == 0 && name + 2 < imageSize)
                    match = NameEquals(image + (uint)name + 2, imageSize - (uint)name - 2, GuardedImport);
                if (match) found.Add((IntPtr)slot);
            }
        }

        if (Rva(ImportDirectory) is > 0 and var imports && imports < imageSize)
        {
            // IMAGE_IMPORT_DESCRIPTOR: OriginalFirstThunk, TimeDateStamp, ForwarderChain, Name, FirstThunk.
            for (byte* entry = image + imports; entry + 20 <= image + imageSize; entry += 20)
            {
                uint names = *(uint*)entry, dll = *(uint*)(entry + 12), addresses = *(uint*)(entry + 16);
                if (dll == 0 && addresses == 0) break;
                Scan(names, addresses);
            }
        }

        if (Rva(DelayImportDirectory) is > 0 and var delayed && delayed < imageSize)
        {
            // ImgDelayDescr: grAttrs, rvaDLLName, rvaHmod, rvaIAT, rvaINT, rvaBoundIAT, rvaUnloadIAT, dwTimeStamp.
            for (byte* entry = image + delayed; entry + 32 <= image + imageSize; entry += 32)
            {
                uint attributes = *(uint*)entry, dll = *(uint*)(entry + 4);
                if (dll == 0) break;
                if ((attributes & 1) == 0) continue;                    // pre-RVA format; never produced for x64
                Scan(*(uint*)(entry + 16), *(uint*)(entry + 12));
            }
        }
        return found;
    }

    private static bool NameEquals(byte* text, uint available, string expected)
    {
        if (available <= (uint)expected.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (text[i] != expected[i]) return false;
        return text[expected.Length] == 0;
    }

    private static void Write(IntPtr slot, IntPtr value)
    {
        // Import tables are read-only once the loader has finished with them.
        if (!VirtualProtect(slot, (UIntPtr)IntPtr.Size, PageReadWrite, out uint previous))
            throw new InvalidOperationException($"VirtualProtect failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        try { Interlocked.Exchange(ref *(IntPtr*)slot, value); }
        finally { VirtualProtect(slot, (UIntPtr)IntPtr.Size, previous, out _); }
    }

    // -------------------------------------------------------------------- native

    private const uint GetModuleHandlePin = 0x1;
    private const uint PageReadWrite = 0x04;
    private const uint GetAncestorRoot = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleEx(uint flags, string moduleName, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint previous);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref System.Drawing.Point point);
}
