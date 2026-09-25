using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Anode.Core.Gamepad;

/// <summary>What Anode reads and changes in HidHide's configuration.</summary>
internal interface IHidHide : IDisposable
{
    IReadOnlyList<string> Blacklist();
    void SetBlacklist(IReadOnlyCollection<string> entries);
    IReadOnlyList<string> Whitelist();
    bool Active();
    void SetActive(bool active);
    bool Inverse();
}

/// <summary>
/// HidHide's control device, <c>\\.\HidHide</c>. HidHide (by the ViGEmBus author) is a filter driver
/// that fails every attempt to open a device on its list, except by the System process and the
/// programs on its allow list. An entry written <c>instance path!n</c> also lets every program in
/// Windows session n open the device, which is how Anode keeps virtual controllers inside the seat.
/// Any user may change the lists; the device takes one handle at a time, so each change opens it
/// briefly. See https://github.com/nefarius/HidHide.
/// </summary>
internal sealed class HidHideDevice : IHidHide
{
    private const string DevicePath = @"\\.\HidHide";

    // CTL_CODE(32769, function, METHOD_BUFFERED, FILE_READ_DATA), from HidHide's IOCTL contract.
    private static uint Code(uint function) => (32769u << 16) | (1u << 14) | (function << 2);
    private static readonly uint GetWhitelistCode = Code(2048), GetBlacklistCode = Code(2050), SetBlacklistCode = Code(2051),
        GetActiveCode = Code(2052), SetActiveCode = Code(2053), GetInverseCode = Code(2054);

    private readonly SafeFileHandle _handle;

    private HidHideDevice(SafeFileHandle handle) => _handle = handle;

    /// <summary>
    /// Opens the control device, waiting up to <paramref name="waitMs"/> while another program has it.
    /// Null when HidHide is not installed or its driver is not loaded.
    /// </summary>
    public static HidHideDevice? Open(int waitMs = 2000)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var handle = CreateFile(DevicePath, GenericRead, FileShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return new HidHideDevice(handle);
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
            // The device admits one handle: access denied means another program holds it.
            if (error != ErrorAccessDenied || waited.ElapsedMilliseconds >= waitMs)
                throw new Win32Exception(error, error == ErrorAccessDenied
                    ? "HidHide is busy: another program, such as its configuration window, has it open."
                    : $"HidHide could not be opened (Win32 error {error}: {new Win32Exception(error).Message}).");
            Thread.Sleep(50);
        }
    }

    public IReadOnlyList<string> Blacklist() => FromMultiString(Read(GetBlacklistCode));

    public IReadOnlyList<string> Whitelist() => FromMultiString(Read(GetWhitelistCode));

    public void SetBlacklist(IReadOnlyCollection<string> entries)
    {
        // No input clears the list; otherwise a REG_MULTI_SZ, which the driver stores as it is.
        byte[] input = entries.Count == 0 ? Array.Empty<byte>() : ToMultiString(entries);
        Control(SetBlacklistCode, input, null, out _);
    }

    public bool Active() => ReadFlag(GetActiveCode);

    public void SetActive(bool active) => Control(SetActiveCode, new[] { active ? (byte)1 : (byte)0 }, null, out _);

    public bool Inverse() => ReadFlag(GetInverseCode);

    public void Dispose() => _handle.Dispose();

    private bool ReadFlag(uint code)
    {
        var value = new byte[1];
        Control(code, null, value, out int returned);
        if (returned != 1) throw new InvalidOperationException("HidHide returned an unexpected setting.");
        return value[0] != 0;
    }

    private byte[] Read(uint code)
    {
        // The first call reports the size, the second fills it.
        Control(code, null, null, out int needed);
        if (needed <= 0) return Array.Empty<byte>();
        var buffer = new byte[needed];
        Control(code, null, buffer, out int returned);
        return returned == buffer.Length ? buffer : buffer[..Math.Clamp(returned, 0, buffer.Length)];
    }

    private void Control(uint code, byte[]? input, byte[]? output, out int returned)
    {
        if (!DeviceIoControl(_handle, code, input, input?.Length ?? 0, output, output?.Length ?? 0, out returned, IntPtr.Zero))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"HidHide refused a request (Win32 error {error}: {new Win32Exception(error).Message}).");
        }
    }

    internal static List<string> FromMultiString(byte[] bytes)
    {
        string all = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        return all.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    internal static byte[] ToMultiString(IEnumerable<string> entries)
    {
        var text = new StringBuilder();
        foreach (string entry in entries)
        {
            if (entry.Length == 0 || entry.Contains('\0')) throw new ArgumentException("A HidHide entry cannot be empty or contain NUL.");
            text.Append(entry).Append('\0');
        }
        return Encoding.Unicode.GetBytes(text.Append('\0').ToString());
    }

    private const uint GenericRead = 0x80000000, FileShareAll = 0x7, OpenExisting = 3;
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize,
        byte[]? output, int outputSize, out int returned, IntPtr overlapped);
}
