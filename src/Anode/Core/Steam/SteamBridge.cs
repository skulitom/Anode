using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Anode.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace Anode.Core.Steam;

/// <summary>
/// Lets a game in the seat use the Steam client running in another session, so Steam can stay
/// on the user's desktop.
///
/// A Steamworks game finds its client through two named objects, Steam3Master_SharedMemFile and
/// Steam3Master_SharedMemLock. Windows resolves unprefixed names in the caller's own session, so
/// from the seat they name nothing, the game reports that Steam is not running, and a Steam
/// client started there takes over from the user's. Only that handshake is bound to a session:
/// the pipes that follow are handles Steam duplicates into the game, which works across sessions
/// for the same user, and the client's objects grant access to everyone.
///
/// The seat host therefore links a name of Anode's own in the seat's namespace to the client's
/// objects, and a game it starts is told that name through steam_master_ipc_name_override, which
/// Steam's client library reads. Nothing else in the seat resolves differently, and the links
/// disappear with the seat host.
/// </summary>
internal static class SteamBridge
{
    internal const string OverrideVariable = "steam_master_ipc_name_override";
    private const string Master = "Steam3Master";
    private static readonly string[] Objects = { "_SharedMemFile", "_SharedMemLock" };
    private static readonly Dictionary<int, (string Name, NamespaceLink[] Links)> Bridges = new();
    private static readonly object Gate = new();

    /// <summary>
    /// The name games started in <paramref name="ownSession"/> use to reach the Steam client in
    /// <paramref name="steamSession"/>. Links are made once and kept while this process runs.
    /// </summary>
    public static string Connect(uint ownSession, int steamSession)
    {
        lock (Gate)
        {
            if (Bridges.TryGetValue(steamSession, out var existing)) return existing.Name;
            // Steam refuses a backslash in the name; this process's id keeps it unique if a
            // previous seat host's links have not gone yet.
            string name = $"AnodeSteamS{steamSession}P{System.Environment.ProcessId}";
            var links = new List<NamespaceLink>();
            try
            {
                foreach (string suffix in Objects)
                    links.Add(NamespaceLink.Create(NamedObjects(ownSession) + @"\" + name + suffix, NamedObjects(steamSession) + @"\" + Master + suffix));
            }
            catch
            {
                foreach (var link in links) link.Dispose();
                throw;
            }
            Bridges[steamSession] = (name, links.ToArray());
            Log.Info($"linked {name} in session {ownSession} to the Steam client objects in session {steamSession}");
            return name;
        }
    }

    /// <summary>True once the Steam client behind <paramref name="name"/> accepts games.</summary>
    public static bool Listening(string name)
    {
        IntPtr mapping = OpenFileMapping(FileMapRead, false, name + Objects[0]);
        if (mapping == IntPtr.Zero) return false;
        CloseHandle(mapping);
        return true;
    }

    /// <summary>
    /// What Steam would set for a game it launched, so the game does not relaunch itself through a
    /// Steam client in the seat, plus the name that leads to the running client.
    /// </summary>
    public static Dictionary<string, string> Environment(int appId, string name)
    {
        string id = appId.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            ["SteamAppId"] = id,
            ["SteamGameId"] = id,
            ["SteamOverlayGameId"] = id,
            [OverrideVariable] = name
        };
    }

    /// <summary>The object directory behind unprefixed names in a session.</summary>
    internal static string NamedObjects(long session) =>
        session == 0 ? @"\BaseNamedObjects" : $@"\Sessions\{session.ToString(CultureInfo.InvariantCulture)}\BaseNamedObjects";

    private const uint FileMapRead = 0x0004;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMapping(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// An object-manager symbolic link, the kind that makes Global\ and Local\ work. It exists while
/// this handle is open. Win32 has no call for one, so this uses ntdll's NtCreateSymbolicLinkObject,
/// the user-mode form of the documented ZwCreateSymbolicLinkObject; an ordinary user may create
/// one in a session namespace their own programs can create objects in.
/// </summary>
internal sealed class NamespaceLink : IDisposable
{
    private const uint SymbolicLinkAllAccess = 0xF0001;
    private const uint CaseInsensitive = 0x40;
    private readonly LinkHandle _handle;

    private NamespaceLink(LinkHandle handle) => _handle = handle;

    /// <summary>Creates <paramref name="name"/>, a full object path, pointing at <paramref name="target"/>, which need not exist yet.</summary>
    public static unsafe NamespaceLink Create(string name, string target)
    {
        if (name.Length is 0 or > 1024 || target.Length is 0 or > 1024)
            throw new ArgumentException("Object names must have 1-1024 characters.");
        fixed (char* namePointer = name)
        fixed (char* targetPointer = target)
        {
            var objectName = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = (IntPtr)namePointer };
            var targetName = new UnicodeString { Length = (ushort)(target.Length * 2), MaximumLength = (ushort)(target.Length * 2), Buffer = (IntPtr)targetPointer };
            var attributes = new ObjectAttributes
            {
                Length = sizeof(ObjectAttributes),
                ObjectName = (IntPtr)(&objectName),
                Attributes = CaseInsensitive
            };
            int status = NtCreateSymbolicLinkObject(out var handle, SymbolicLinkAllAccess, &attributes, &targetName);
            if (status < 0)
            {
                handle.Dispose();
                throw new Win32Exception(RtlNtStatusToDosError(status), $"Could not link {name} to {target}");
            }
            return new NamespaceLink(handle);
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    private sealed class LinkHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LinkHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => NtClose(handle) >= 0;
    }

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtCreateSymbolicLinkObject(out LinkHandle handle, uint desiredAccess,
        ObjectAttributes* attributes, UnicodeString* target);

    [DllImport("ntdll.dll")]
    private static extern int NtClose(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);
}
