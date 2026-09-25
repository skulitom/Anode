using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Anode.Core.Gamepad;

/// <summary>
/// Finds programs outside the seat that make virtual controllers of their own, such as DS4Windows.
/// While one runs, a new pad could be theirs, so <see cref="PadIsolation"/> keeps only Anode's own
/// pads in the seat. A program counts when it has ViGEm's client library loaded or is a known
/// ViGEm program; modules are read when a process is first seen, again as it ages, then every minute.
/// </summary>
internal sealed class ViGEmClients
{
    /// <summary>Programs that link ViGEm's client in, so no module gives them away (names without .exe).</summary>
    internal static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "DS4Windows", "BetterJoy", "BetterJoyForCemu", "DSX", "DualSenseX", "JoyShockMapper", "x360ce", "x360ce_x64",
        "InputMapper", "sunshine", "sunshinesvc", "parsecd"
    };

    private static readonly long[] RecheckAges = { 2_000, 5_000, 10_000, 20_000, 40_000 };
    private const long RecheckPeriod = 60_000;

    private readonly uint _seat;
    private readonly Func<long> _clock;
    private readonly Func<int, bool> _loadsViGEm;
    private readonly Dictionary<(int Pid, string Name), (long First, long Due, bool Client)> _seen = new();

    public ViGEmClients(uint seat, Func<long>? clock = null, Func<int, bool>? loadsViGEm = null)
    {
        _seat = seat;
        _clock = clock ?? (() => Environment.TickCount64);
        _loadsViGEm = loadsViGEm ?? LoadsViGEm;
    }

    public static bool IsClientModule(string module) => module.Contains("vigemclient", StringComparison.OrdinalIgnoreCase);

    /// <summary>Descriptions of the ViGEm programs running outside the seat, from a process list.</summary>
    public IReadOnlyList<string> Scan() => Scan(Processes());

    internal IReadOnlyList<string> Scan(IEnumerable<(int Pid, string Name, int Session)> processes)
    {
        long now = _clock();
        var clients = new List<string>();
        var alive = new HashSet<(int, string)>();
        foreach (var (pid, name, session) in processes)
        {
            // Anode's own processes may load ViGEm on the desktop, for doctor or the self-test.
            if (session == _seat || pid is 0 or 4 || name.Equals("anode", StringComparison.OrdinalIgnoreCase)) continue;
            string label = $"{name}.exe (PID {pid}, session {session})";
            if (Known.Contains(name)) { clients.Add(label); continue; }
            if (session == 0) continue;
            var key = (pid, name);
            alive.Add(key);
            if (!_seen.TryGetValue(key, out var entry)) entry = (now, now, false);
            if (!entry.Client && now >= entry.Due)
            {
                bool client = _loadsViGEm(pid);
                long age = now - entry.First;
                long later = RecheckAges.FirstOrDefault(a => a > age);
                entry = (entry.First, client ? long.MaxValue : later > 0 ? entry.First + later : now + RecheckPeriod, client);
            }
            _seen[key] = entry;
            if (entry.Client) clients.Add(label);
        }
        foreach (var gone in _seen.Keys.Where(key => !alive.Contains(key)).ToList()) _seen.Remove(gone);
        return clients;
    }

    private static List<(int Pid, string Name, int Session)> Processes()
    {
        var list = new List<(int, string, int)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { list.Add((process.Id, process.ProcessName, process.SessionId)); }
                catch (InvalidOperationException) { }
            }
        }
        return list;
    }

    private static bool LoadsViGEm(int pid)
    {
        IntPtr handle = OpenProcess(QueryLimitedInformation | VmRead, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(handle, modules, modules.Length * IntPtr.Size, out int needed, ListAll)) return false;
            var name = new StringBuilder(260);
            for (int i = 0, count = Math.Min(needed / IntPtr.Size, modules.Length); i < count; i++)
            {
                name.Clear();
                if (GetModuleBaseName(handle, modules[i], name, name.Capacity) > 0 && IsClientModule(name.ToString())) return true;
            }
            return false;
        }
        finally { CloseHandle(handle); }
    }

    private const uint QueryLimitedInformation = 0x1000, VmRead = 0x0010, ListAll = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, uint filter);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleBaseName(IntPtr process, IntPtr module, StringBuilder name, int size);
}
