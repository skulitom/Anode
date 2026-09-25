using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Session;

namespace Anode.Core.Gamepad;

/// <summary>Thrown when HidHide is installed but cannot keep a controller inside the seat.</summary>
internal sealed class PadIsolationException(string message) : InvalidOperationException(message);

/// <summary>What became of the pad Anode just plugged in.</summary>
/// <param name="Required">HidHide is installed, so the pad must not stay where the desktop can open it.</param>
/// <param name="Verified">Whether the daemon, in the user's session, was refused every device of the pad; null when not checked.</param>
internal sealed record Adoption(string? PadId, bool Required, bool Isolated, bool? Verified, string Summary);

/// <summary>
/// Keeps virtual controllers inside the seat.
///
/// A ViGEm pad is a device on the machine, not in a session, so a game on the user's desktop reads the
/// input an agent sends to a game in the seat. With HidHide installed, every device of a seat pad goes
/// on HidHide's list jailed to the seat's session: programs in the seat open it as usual, and every
/// program outside it (XInput and DirectInput games, raw input, Steam, the Xbox Game Bar) is refused.
///
/// HidHide checks each open, not handles already open, so a device has to be listed before a desktop
/// program opens it. The pad names ViGEm hands out next, the lowest free serials, are listed ahead of
/// time together with every name Windows has recorded under them. A device whose name Windows has never
/// used before is listed the moment Windows announces it.
///
/// Anode's own pads always belong to the seat. So does any other pad plugged in while the seat runs,
/// such as a seat program's own ViGEm pad, unless a program outside the seat uses ViGEm (see
/// <see cref="ViGEmClients"/>): a new pad could then be that program's, and only Anode's are listed.
/// Pads that were plugged in before this seat host started are never listed.
/// </summary>
internal sealed class PadIsolation : IDisposable
{
    private const int ReservedSerials = 4, AnodeReservation = 2, PeriodMs = 500;
    private const long ClientCheckMs = 3_000, HistoryMs = 60_000, RecheckMs = 30_000, MissingCheckMs = 10_000;

    public const string NotInstalled = "HidHide is not installed, so this controller is machine-wide: a game on the user's desktop reads "
        + "its input too. Installing HidHide (https://github.com/nefarius/HidHide/releases) keeps Anode's controllers inside the seat.";

    private readonly uint _seat;
    private readonly IPadTree _tree;
    private readonly Func<IHidHide?> _open;
    private readonly Func<IReadOnlyList<string>> _clientsOutside;
    private readonly Func<IReadOnlyList<string>, JsonObject?> _desktopProbe;
    private readonly Func<uint, bool> _sessionExists;
    private readonly Func<long> _clock;
    private readonly Action<string> _record;
    private readonly object _gate = new();
    private readonly HashSet<string> _anodePads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _earlierPads = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What the user's session answered for each seat pad: true when refused, false when it could open it.</summary>
    private readonly Dictionary<string, bool?> _verified = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VirtualPad> _pads = Array.Empty<VirtualPad>();
    private IReadOnlyDictionary<string, IReadOnlyCollection<string>> _history = new Dictionary<string, IReadOnlyCollection<string>>();
    private IReadOnlyList<string> _clients = Array.Empty<string>();
    private long _historyAt = long.MinValue, _clientsAt = long.MinValue, _appliedAt = long.MinValue;
    private int _reserveForAnode, _ticking;
    private HidHideState _hidHide = HidHideState.Unknown;
    private System.Threading.Timer? _timer;
    private IDisposable? _arrivals;
    private bool _disposed;

    /// <param name="Failure">HidHide is there but could not be read or changed, for example while another program holds it.</param>
    private sealed record HidHideState(bool Installed, bool Active, bool Inverse, bool Failure, IReadOnlyList<string> Everywhere, string? Problem)
    {
        public static readonly HidHideState Unknown = new(false, false, false, false, Array.Empty<string>(), "not checked yet");
        public static readonly HidHideState Missing = new(false, false, false, false, Array.Empty<string>(), null);
        public static HidHideState Failed(string problem) => new(true, false, false, true, Array.Empty<string>(), problem);

        public bool IsMissing => !Installed && Problem is null;

        public bool Usable => Installed && Active && !Inverse && Problem is null;

        public string Label => IsMissing ? "not installed" : !Installed ? "not checked yet" : Failure ? "unavailable"
            : Inverse ? "inverted" : !Active ? "switched off" : "active";
    }

    public PadIsolation(uint seat, IPadTree? tree = null, Func<IHidHide?>? openHidHide = null,
        Func<IReadOnlyList<string>>? clientsOutside = null, Func<IReadOnlyList<string>, JsonObject?>? desktopProbe = null,
        Func<uint, bool>? sessionExists = null, Func<long>? clock = null, Action<string>? record = null)
    {
        _seat = seat;
        _tree = tree ?? new PadDevices();
        _open = openHidHide ?? (() => HidHideDevice.Open());
        _clientsOutside = clientsOutside ?? new ViGEmClients(seat).Scan;
        _desktopProbe = desktopProbe ?? (_ => null);
        _sessionExists = sessionExists ?? SessionExists;
        _clock = clock ?? (() => Environment.TickCount64);
        _record = record ?? (_ => { });
        // A pad plugged in before the seat host started belongs to whoever plugged it in.
        foreach (var pad in _tree.Present()) _earlierPads.Add(pad.Id);
    }

    /// <summary>Keeps the list current as pads come and go, starting at once in the background.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // The first look at other programs reads every process's modules, so it stays off the seat's startup.
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, PeriodMs);
            // A new device is listed at once instead of at the next check. A disposed timer throws, which the watch ignores.
            try { _arrivals = PadDevices.WatchArrivals(() => Volatile.Read(ref _timer)?.Change(0, PeriodMs)); }
            catch (Exception ex) { _record($"device arrivals are not watched, so new devices wait for the next check: {ex.Message}"); }
        }
    }

    private void Tick()
    {
        // A check waiting on a busy HidHide must not have more queue up behind it.
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try { Refresh(); }
        catch (Exception ex) { SetState(HidHideState.Failed(ex.Message)); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    /// <summary>One periodic check: the list, then the user's session asked about newly hidden pads.</summary>
    internal void Refresh()
    {
        Reconcile();
        VerifyNewPads();
    }

    /// <summary>
    /// Asks the user's session once about each pad the seat has hidden since the last check, a seat program's
    /// own pads included. Anode cannot unplug another program's pad, so one the desktop can open is logged
    /// and reported by <see cref="Describe"/>.
    /// </summary>
    private void VerifyNewPads()
    {
        List<VirtualPad> pending;
        lock (_gate)
        {
            foreach (string gone in _verified.Keys.Where(id => !_pads.Any(pad => pad.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList())
                _verified.Remove(gone);
            if (!_hidHide.Usable) return;
            // Anode's own pads are checked while they are plugged in.
            pending = _pads.Where(pad => Belongs(pad) && !_anodePads.Contains(pad.Id) && Covered(pad) && !_verified.ContainsKey(pad.Id)).ToList();
        }
        foreach (var pad in pending)
        {
            bool? refused = Verify(pad);
            lock (_gate) _verified[pad.Id] = refused;
            if (refused == false)
                _record($"a program on the user's desktop can still open {pad.Id}, which a program in the seat plugged in; Anode cannot unplug it");
        }
    }

    /// <summary>Brings HidHide's list in line with the pads plugged in now.</summary>
    public void Reconcile()
    {
        lock (_gate)
        {
            if (_disposed) return;
            long now = _clock();
            // Without HidHide there is nothing to keep; it is looked for again now and then.
            if (_hidHide.IsMissing && now - _appliedAt < MissingCheckMs) return;
            _pads = _tree.Present();
            // A failed look keeps the previous answer rather than stopping the seat's controllers.
            if (_clientsAt == long.MinValue || now - _clientsAt >= ClientCheckMs)
            {
                _clientsAt = now;
                try
                {
                    var clients = _clientsOutside();
                    if (clients.Count > 0 != _clients.Count > 0)
                        _record(clients.Count > 0
                            ? $"another program uses ViGEm outside the seat ({string.Join(", ", clients)}); only Anode's own controllers are kept in the seat"
                            : "no program outside the seat uses ViGEm; every new virtual controller is kept in the seat");
                    _clients = clients;
                }
                catch (Exception ex) { _record($"could not look for ViGEm programs outside the seat: {ex.Message}"); }
            }
            if (_historyAt == long.MinValue || now - _historyAt >= HistoryMs)
            {
                _historyAt = now;
                try { _history = _tree.History(); }
                catch (Exception ex) { _record($"could not read the device names Windows recorded for virtual pads: {ex.Message}"); }
            }
            var desired = Desired();
            // Left alone while nothing changes; looked at again now and then, in case someone edited the list.
            if (_hidHide.Usable && desired.SetEquals(_applied) && now - _appliedAt < RecheckMs) return;
            Apply(desired, now);
        }
    }

    /// <summary>
    /// Lists the pad names ViGEm will hand out next, so the pad Anode is about to plug in is seat-only from
    /// its first moment, and returns the pads present before it. Throws when HidHide is installed but cannot
    /// keep the pad in the seat.
    /// </summary>
    public IReadOnlyCollection<string> BeforePlugIn()
    {
        lock (_gate)
        {
            _reserveForAnode = AnodeReservation;
            _appliedAt = long.MinValue;
            Reconcile();
            if (_hidHide.Installed && !_hidHide.Usable)
            {
                _reserveForAnode = 0;
                throw new PadIsolationException("No controller was plugged in, because it would reach the user's desktop. " + Problem());
            }
            return _pads.Select(pad => pad.Id).ToList();
        }
    }

    /// <summary>
    /// Claims the pad Anode just plugged in, waits up to <paramref name="wait"/> for the devices Windows adds
    /// under it, lists each one, and asks the user's session whether it can still open any of them.
    /// </summary>
    public Adoption Adopt(IReadOnlyCollection<string> before, TimeSpan wait)
    {
        VirtualPad? pad;
        lock (_gate)
        {
            _reserveForAnode = 0;
            _pads = _tree.Present();
            // ViGEm hands out the lowest free serial, so the lowest new one is Anode's.
            pad = _pads.Where(p => !before.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.Serial ?? int.MaxValue).FirstOrDefault();
            if (pad is not null) _anodePads.Add(pad.Id);
            if (!_hidHide.Installed) return new Adoption(pad?.Id, false, false, null, NotInstalled);
        }
        if (pad is null)
            return new Adoption(null, true, false, null, "Anode could not find the controller it plugged in, so it cannot keep it inside the seat.");

        // The HID device DirectInput and raw input read arrives a moment after the pad.
        var waited = Stopwatch.StartNew();
        bool covered;
        while (true)
        {
            lock (_gate)
            {
                Reconcile();
                pad = _pads.FirstOrDefault(p => p.Id.Equals(pad.Id, StringComparison.OrdinalIgnoreCase)) ?? pad;
                covered = _hidHide.Usable && Covered(pad);
            }
            if (covered && pad.Descendants.Any(IsHidDevice) || waited.Elapsed >= wait) break;
            Thread.Sleep(25);
        }
        if (!covered) return new Adoption(pad.Id, true, false, null, Problem());

        bool? verified = Verify(pad);
        lock (_gate) _verified[pad.Id] = verified;
        if (verified == false)
        {
            _record($"the desktop could still open {pad.Id} after HidHide listed it");
            return new Adoption(pad.Id, true, false, false,
                "HidHide lists the controller, but a program on the user's desktop could still open it, so it would read this input. "
                + "HidHide may need the restart its installer asked for.");
        }
        return new Adoption(pad.Id, true, true, verified, verified == true
            ? "It stays inside the seat: programs on the user's desktop cannot open it (checked from the desktop)."
            : "It stays inside the seat: HidHide refuses it to programs outside the seat. The desktop could not be asked to confirm.");
    }

    /// <summary>Anode unplugged this pad; its devices stop being listed unless the seat still needs them.</summary>
    public void Release(string padId)
    {
        lock (_gate) _anodePads.Remove(padId);
    }

    /// <summary>The plug-in <see cref="BeforePlugIn"/> prepared for did not happen.</summary>
    public void PlugInFailed()
    {
        lock (_gate) _reserveForAnode = 0;
    }

    public JsonObject Describe()
    {
        lock (_gate)
        {
            _pads = _tree.Present();
            var pads = new JsonArray();
            foreach (var pad in _pads)
                pads.Add(new JsonObject
                {
                    ["device"] = pad.Id,
                    ["owner"] = _anodePads.Contains(pad.Id) ? "anode" : Belongs(pad) ? "seat" : "outside",
                    ["seatOnly"] = _hidHide.Usable && Belongs(pad) && Covered(pad),
                    ["verifiedFromDesktop"] = _verified.GetValueOrDefault(pad.Id)
                });
            return new JsonObject
            {
                ["hidHide"] = _hidHide.Label,
                ["seatSession"] = (int)_seat,
                ["pads"] = pads,
                ["otherViGEmPrograms"] = new JsonArray(_clients.Select(client => (JsonNode)client).ToArray()),
                ["allowedEverywhere"] = new JsonArray(_hidHide.Everywhere.Select(path => (JsonNode)path).ToArray()),
                ["summary"] = Summary()
            };
        }
    }

    /// <summary>
    /// Stops keeping the list. Entries stay: a seat pad that is still plugged in would otherwise become
    /// visible to the desktop until the seat is signed out. The daemon removes them once the seat has
    /// ended (<see cref="ClearEnded"/>).
    /// </summary>
    public void Dispose()
    {
        IDisposable? arrivals;
        System.Threading.Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            arrivals = _arrivals;
            timer = _timer;
            _arrivals = null;
            _timer = null;
        }
        arrivals?.Dispose();
        timer?.Dispose();
    }

    /// <summary>
    /// Removes pad entries jailed to <paramref name="ended"/> or to a session that no longer exists, and
    /// returns how many. The daemon calls it after signing a seat out and when it starts, since a seat host
    /// ends with its session and cannot clean up after it.
    /// </summary>
    public static int ClearEnded(uint? ended, Func<IHidHide?>? openHidHide = null, Func<uint, bool>? sessionExists = null)
    {
        var exists = sessionExists ?? SessionExists;
        using var hidHide = (openHidHide ?? (() => HidHideDevice.Open()))();
        if (hidHide is null) return 0;
        var current = hidHide.Blacklist();
        var kept = current.Where(entry => !(Jail(entry) is { } jail && PadDevices.IsPadDevice(jail.Device)
            && (jail.Session == ended || !exists(jail.Session)))).ToList();
        if (kept.Count != current.Count) hidHide.SetBlacklist(kept);
        return current.Count - kept.Count;
    }

    /// <summary>
    /// Run by the daemon in the user's session: tries each listed pad device's XInput and HID interfaces the
    /// way a reader opens them. Only default ViGEm pad devices are tried.
    /// </summary>
    public static JsonObject DesktopView(IEnumerable<string> devices)
    {
        var results = new JsonArray();
        foreach (string id in devices.Where(PadDevices.IsPadDevice).Distinct(StringComparer.OrdinalIgnoreCase).Take(64))
        {
            var interfaces = new JsonArray();
            foreach (string path in PadDevices.Interfaces(id))
                interfaces.Add(new JsonObject { ["path"] = path, ["opens"] = PadDevices.CanOpen(path) });
            results.Add(new JsonObject { ["device"] = id, ["interfaces"] = interfaces });
        }
        return new JsonObject { ["session"] = (int)ChildSession.CurrentSessionId(), ["devices"] = results };
    }

    /// <summary>A list entry "device!session" split in two; null for an entry without a jail session.</summary>
    internal static (string Device, uint Session)? Jail(string entry)
    {
        int bang = entry.LastIndexOf('!');
        return bang > 0 && uint.TryParse(entry.AsSpan(bang + 1), System.Globalization.NumberStyles.None, null, out uint session) && session > 0
            ? (entry[..bang], session) : null;
    }

    private string Entry(string device) => $"{device}!{_seat}";

    /// <summary>Entries this seat maintains: pad devices jailed to it, or to a session that has ended.</summary>
    private bool Mine(string entry) => Jail(entry) is { } jail && PadDevices.IsPadDevice(jail.Device)
        && (jail.Session == _seat || !_sessionExists(jail.Session));

    private bool Belongs(VirtualPad pad) => _anodePads.Contains(pad.Id) || _clients.Count == 0 && !_earlierPads.Contains(pad.Id);

    private bool Covered(VirtualPad pad) => _applied.Contains(Entry(pad.Id)) && pad.Descendants.All(device => _applied.Contains(Entry(device)));

    private static bool IsHidDevice(string device) => device.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyCollection<string> HistoryOf(string pad) =>
        _history.TryGetValue(pad, out var devices) ? devices : Array.Empty<string>();

    private HashSet<string> Desired()
    {
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pad in _pads.Where(Belongs))
        {
            devices.Add(pad.Id);
            devices.UnionWith(pad.Descendants);
            devices.UnionWith(HistoryOf(pad.Id));
        }
        // The names the next pads will take: a few for anyone while every new pad is the seat's, else Anode's next one.
        int reserve = _clients.Count > 0 ? _reserveForAnode : Math.Max(ReservedSerials, _reserveForAnode);
        var used = _pads.Select(pad => pad.Serial).OfType<int>().ToHashSet();
        for (int serial = 1, reserved = 0; reserved < reserve && serial <= 99; serial++)
        {
            if (used.Contains(serial)) continue;
            reserved++;
            foreach (string product in PadDevices.Products)
            {
                string id = PadDevices.PathFor(product, serial);
                devices.Add(id);
                devices.UnionWith(HistoryOf(id));
            }
        }
        return devices.Select(Entry).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void Apply(HashSet<string> desired, long now)
    {
        _appliedAt = now;
        IHidHide? hidHide;
        try { hidHide = _open(); }
        catch (Exception ex)
        {
            SetState(HidHideState.Failed(ex.Message));
            return;
        }
        if (hidHide is null)
        {
            _applied.Clear();
            SetState(HidHideState.Missing);
            return;
        }
        using (hidHide)
        {
            try
            {
                var current = hidHide.Blacklist();
                // The seat's entries go first, since HidHide goes by the first entry naming a device; everyone
                // else's stay, in their order.
                var next = desired.Order(StringComparer.OrdinalIgnoreCase).ToList();
                next.AddRange(current.Where(entry => !Mine(entry) && !desired.Contains(entry)));
                if (!next.SequenceEqual(current, StringComparer.OrdinalIgnoreCase)) hidHide.SetBlacklist(next);
                _applied = desired;

                bool active = hidHide.Active(), inverse = hidHide.Inverse();
                int others = next.Count(entry => !desired.Contains(entry));
                // A fresh HidHide starts switched off. Switching it on also hides whatever else it lists, so only
                // do that when the list holds nothing but the seat's pads.
                if (!active && desired.Count > 0 && others == 0)
                {
                    hidHide.SetActive(true);
                    active = true;
                    _record("switched HidHide on to keep virtual controllers inside the seat");
                }
                var everywhere = hidHide.Whitelist().Where(path => !IsHidHideTool(path)).ToList();
                string? problem = inverse
                    ? "HidHide's application list is inverted, so every program not on it can open hidden devices. Anode will not change that setting."
                    : !active
                        ? $"HidHide is switched off and also lists {others} device(s) of yours, so Anode will not switch it on. Switch it on in HidHide's configuration."
                        : null;
                SetState(new HidHideState(true, active, inverse, false, everywhere, problem));
            }
            catch (Exception ex) { SetState(HidHideState.Failed(ex.Message)); }
        }
    }

    private bool? Verify(VirtualPad pad)
    {
        JsonObject? view;
        try { view = _desktopProbe(new[] { pad.Id }.Concat(pad.Descendants).ToList()); }
        catch (Exception ex)
        {
            _record($"could not ask the desktop about {pad.Id}: {ex.Message}");
            return null;
        }
        var opens = (view?["devices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .SelectMany(device => (device["interfaces"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Select(entry => entry.Bool("opens")).ToList();
        if (opens.Any(opened => opened == true)) return false;
        return opens.Any(opened => opened == false) ? true : null;
    }

    private void SetState(HidHideState state)
    {
        HidHideState was;
        lock (_gate) { was = _hidHide; _hidHide = state; }
        if (was.Label == state.Label && was.Problem == state.Problem) return;
        _record(state.Usable
            ? $"HidHide is active: virtual controllers of the seat can be opened only in session {_seat}"
            : !state.Installed && state.Problem is null
                ? "HidHide is not installed: virtual controllers are machine-wide"
                : $"HidHide cannot keep virtual controllers in the seat: {state.Problem ?? state.Label}");
    }

    private string Problem() => _hidHide.Problem ?? "HidHide could not hide every device of the controller from the user's desktop.";

    private string Summary()
    {
        if (!_hidHide.Installed)
            return _hidHide.Problem is null
                ? "HidHide is not installed, so virtual controllers are machine-wide: games on the user's desktop read them too."
                : "Anode has not checked HidHide yet.";
        if (!_hidHide.Usable) return "HidHide cannot keep controllers in the seat: " + (_hidHide.Problem ?? _hidHide.Label) + ".";
        string text = $"Virtual controllers of the seat can be opened only in session {_seat}; programs on the user's desktop are refused.";
        if (_clients.Count > 0) text += $" Other programs use ViGEm outside the seat ({string.Join(", ", _clients)}), so only Anode's own controllers are kept in the seat.";
        if (_hidHide.Everywhere.Count > 0) text += $" HidHide lets these programs open hidden devices anywhere: {string.Join(", ", _hidHide.Everywhere)}.";
        return text;
    }

    private static bool IsHidHideTool(string path) =>
        Path.GetFileName(path) is { } name && (name.Equals("HidHideClient.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("HidHideCLI.exe", StringComparison.OrdinalIgnoreCase));

    private static bool SessionExists(uint session) => ChildSession.Exists(session) != false;
}
