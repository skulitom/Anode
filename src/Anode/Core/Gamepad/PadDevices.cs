using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anode.Core.Gamepad;

/// <summary>A virtual controller on a ViGEm bus and the devices Windows made under it.</summary>
internal sealed record VirtualPad(string Id, int? Serial, IReadOnlyList<string> Descendants);

/// <summary>What <see cref="PadIsolation"/> reads from Plug and Play.</summary>
internal interface IPadTree
{
    /// <summary>The pads plugged into every ViGEm bus now.</summary>
    IReadOnlyList<VirtualPad> Present();

    /// <summary>Every device Windows has recorded under each pad name, present or not.</summary>
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> History();
}

/// <summary>
/// ViGEm's virtual controllers as Plug and Play sees them. ViGEm names a pad after its product and
/// the lowest free serial, <c>USB\VID_045E&amp;PID_028E\01</c> for the first Xbox 360 pad. Windows then
/// adds devices under it, among them the HID device DirectInput and raw input read, named after a
/// counter that restarts at boot. The device registry keeps every name a pad's devices have had, and
/// a pad with the same serial and counter value gets the same names again.
/// </summary>
internal sealed class PadDevices : IPadTree
{
    public const string Xbox360 = @"USB\VID_045E&PID_028E";
    public const string DualShock4 = @"USB\VID_054C&PID_05C4";
    public static readonly string[] Products = { Xbox360, DualShock4 };

    /// <summary>The interfaces XInput and HID readers open.</summary>
    public static readonly Guid XInputInterface = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    public static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public static string PathFor(string product, int serial) => $@"{product}\{serial:D2}";

    /// <summary>A default ViGEm pad name, or a device Windows made under one.</summary>
    public static bool IsPadDevice(string id) => Products.Any(product =>
        id.StartsWith(product, StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("HID" + product[3..], StringComparison.OrdinalIgnoreCase));

    public static int? SerialOf(string padId) =>
        int.TryParse(padId[(padId.LastIndexOf('\\') + 1)..], System.Globalization.NumberStyles.None, null, out int serial) ? serial : null;

    private IReadOnlyList<string> _buses = Array.Empty<string>();
    private long _busesAt = long.MinValue;

    public IReadOnlyList<VirtualPad> Present()
    {
        // Finding the bus by its service reads every device, so its name is kept for a minute.
        long now = Environment.TickCount64;
        if (_busesAt == long.MinValue || now - _busesAt >= 60_000)
        {
            _buses = DeviceIds("ViGEmBus", FilterService | FilterPresent | DoNotGenerate);
            _busesAt = now;
        }
        var pads = new List<VirtualPad>();
        foreach (string bus in _buses)
        {
            if (CM_Locate_DevNodeW(out uint node, bus, LocateNormal) != Success) { _busesAt = long.MinValue; continue; }
            foreach (uint child in Children(node))
            {
                if (DeviceId(child) is not { } id) continue;
                var descendants = new List<string>();
                Collect(child, descendants);
                pads.Add(new VirtualPad(id, SerialOf(id), descendants));
            }
        }
        return pads;
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> History()
    {
        // Parents of every recorded device named after a pad product, then the trees under each pad name.
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string enumerator in new[] { "USB", "HID" })
            foreach (string id in DeviceIds(enumerator, FilterEnumerator))
                if (IsPadDevice(id) && SerialOf(id) is null && ParentOf(id) is { } parent)
                    parents[id] = parent;
        var history = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string pad in parents.Values.Where(parent => IsPadDevice(parent) && SerialOf(parent) is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var next = new Queue<string>(new[] { pad });
            while (next.TryDequeue(out string? node))
                foreach (var (child, parent) in parents)
                    if (parent.Equals(node, StringComparison.OrdinalIgnoreCase) && found.Add(child)) next.Enqueue(child);
            history[pad] = found;
        }
        return history;
    }

    /// <summary>The XInput and HID interfaces a present device offers readers.</summary>
    public static IReadOnlyList<string> Interfaces(string deviceId)
    {
        var paths = new List<string>();
        foreach (Guid kind in new[] { XInputInterface, HidInterface })
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (CM_Get_Device_Interface_List_SizeW(out int length, kind, deviceId, InterfacesPresent) != Success || length <= 1) break;
                var buffer = new char[length];
                int result = CM_Get_Device_Interface_ListW(kind, deviceId, buffer, length, InterfacesPresent);
                if (result == BufferSmall) continue;
                if (result == Success) paths.AddRange(Split(buffer));
                break;
            }
        }
        return paths;
    }

    /// <summary>
    /// Whether this process may open a device interface the way XInput does: false when the open is
    /// refused (HidHide), null when the result says nothing either way, such as a device that left.
    /// </summary>
    public static bool? CanOpen(string interfacePath)
    {
        using SafeFileHandle handle = CreateFile(interfacePath, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return true;
        return Marshal.GetLastPInvokeError() switch { 5 => false, 32 => true, _ => null };
    }

    /// <summary>
    /// Calls <paramref name="arrived"/> on a thread-pool thread whenever an XInput or HID interface
    /// appears, so new devices under a pad are hidden as soon as Windows announces them.
    /// </summary>
    public static IDisposable WatchArrivals(Action arrived) => new Arrivals(arrived);

    private sealed class Arrivals : IDisposable
    {
        private readonly CmNotifyCallback _callback;
        private readonly List<IntPtr> _registrations = new();

        public Arrivals(Action arrived)
        {
            // Kept in a field: the delegate must outlive every callback Windows makes.
            _callback = (_, _, action, _, _) =>
            {
                if (action == InterfaceArrival) { try { arrived(); } catch { } }
                return 0;
            };
            foreach (Guid kind in new[] { XInputInterface, HidInterface })
            {
                var filter = new CmNotifyFilter { Size = Marshal.SizeOf<CmNotifyFilter>(), FilterType = FilterDeviceInterface, ClassGuid = kind };
                if (CM_Register_Notification(filter, IntPtr.Zero, _callback, out IntPtr registration) == Success) _registrations.Add(registration);
            }
        }

        public void Dispose()
        {
            // Waits for callbacks in progress, so none runs after this returns.
            foreach (IntPtr registration in _registrations) CM_Unregister_Notification(registration);
            _registrations.Clear();
        }
    }

    private const int FilterDeviceInterface = 0, InterfaceArrival = 0;

    /// <summary>CM_NOTIFY_FILTER for a device interface class; the union is 400 bytes wide.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CmNotifyFilter
    {
        public int Size;
        public uint Flags;
        public int FilterType;
        public uint Reserved;
        public Guid ClassGuid;
        public fixed byte Rest[384];
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CmNotifyCallback(IntPtr registration, IntPtr context, int action, IntPtr eventData, int eventDataSize);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Register_Notification(in CmNotifyFilter filter, IntPtr context, CmNotifyCallback callback, out IntPtr registration);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Unregister_Notification(IntPtr registration);

    private static List<string> DeviceIds(string filter, uint flags)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out int length, filter, flags) != Success || length <= 1) return new List<string>();
            var buffer = new char[length];
            int result = CM_Get_Device_ID_ListW(filter, buffer, length, flags);
            if (result == Success) return Split(buffer);
            if (result != BufferSmall) break;
        }
        return new List<string>();
    }

    private static List<string> Split(char[] multiString) =>
        new string(multiString).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static List<uint> Children(uint node)
    {
        var children = new List<uint>();
        if (CM_Get_Child(out uint child, node, 0) != Success) return children;
        do children.Add(child);
        while (CM_Get_Sibling(out child, child, 0) == Success);
        return children;
    }

    private static void Collect(uint node, List<string> into)
    {
        foreach (uint child in Children(node))
        {
            if (DeviceId(child) is { } id) into.Add(id);
            Collect(child, into);
        }
    }

    private static string? DeviceId(uint node)
    {
        var buffer = new char[MaxDeviceIdLength + 1];
        return CM_Get_Device_IDW(node, buffer, buffer.Length, 0) == Success ? new string(buffer).TrimEnd('\0') : null;
    }

    private static string? ParentOf(string id)
    {
        if (CM_Locate_DevNodeW(out uint node, id, LocatePhantom) != Success) return null;
        int size = 0;
        CM_Get_DevNode_PropertyW(node, ParentKey, out _, null, ref size, 0);
        if (size <= 2) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(node, ParentKey, out uint type, buffer, ref size, 0) != Success || type != PropertyString) return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
    }

    private const int Success = 0, BufferSmall = 0x1A, MaxDeviceIdLength = 200;
    private const uint FilterEnumerator = 0x1, FilterService = 0x2, FilterPresent = 0x100, DoNotGenerate = 0x10000040;
    private const uint LocateNormal = 0, LocatePhantom = 1, InterfacesPresent = 0, PropertyString = 0x12;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareReadWrite = 0x3, OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Category;
        public uint Id;
    }

    /// <summary>DEVPKEY_Device_Parent.</summary>
    private static readonly DevPropKey ParentKey = new() { Category = new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), Id = 8 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Child(out uint child, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Sibling(out uint sibling, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, in DevPropKey key, out uint type, [Out] byte[]? buffer, ref int size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int length, in Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(in Guid interfaceClass, string? deviceId, [Out] char[] buffer, int length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
