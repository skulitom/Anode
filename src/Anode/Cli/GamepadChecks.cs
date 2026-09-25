using System.Text.Json.Nodes;
using Anode.Core.Gamepad;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Anode.Cli;

/// <summary>
/// Keeping virtual controllers inside the seat, against a made-up Plug and Play tree, an in-memory
/// HidHide and stand-in controllers. Nothing here plugs in a controller, opens HidHide or reads
/// another process.
/// </summary>
internal static class GamepadChecks
{
    private const uint Seat = 5, OtherSeat = 7, Ended = 3;
    private const string Pad1 = @"USB\VID_045E&PID_028E\01", Pad2 = @"USB\VID_045E&PID_028E\02";
    private const string Usb1 = @"USB\VID_045E&PID_028E&IG_07\2&dee0f28&0&07", Hid1 = @"HID\VID_045E&PID_028E&IG_07\3&76c8945&0&0000";
    private const string Usb2 = @"USB\VID_045E&PID_028E&IG_08\2&4cba17d&0&08", Hid2 = @"HID\VID_045E&PID_028E&IG_08\3&1b2c3d4&0&0000";
    private const string Old1 = @"HID\VID_045E&PID_028E&IG_00\3&8968588&0&0000";
    /// <summary>A controller of the user's own that they hide with HidHide, as DS4Windows users do.</summary>
    private const string Theirs = @"HID\VID_054C&PID_09CC&MI_03\7&1a2b3c4d&0&0000";

    public static string Lists()
    {
        var round = HidHideDevice.FromMultiString(HidHideDevice.ToMultiString(new[] { Pad1 + "!5", Theirs }));
        Require(round.SequenceEqual(new[] { Pad1 + "!5", Theirs }) && HidHideDevice.FromMultiString(new byte[] { 0, 0 }).Count == 0,
            "HidHide lists did not survive the multi-string round trip");
        Require(PadIsolation.Jail(Pad1 + "!12") == (Pad1, 12u) && PadIsolation.Jail(Pad1) is null && PadIsolation.Jail(Pad1 + "!0") is null
            && PadIsolation.Jail(Pad1 + "!-2") is null, "jail entries were misread");
        Require(PadDevices.IsPadDevice(Pad1) && PadDevices.IsPadDevice(Hid1) && PadDevices.IsPadDevice(@"USB\VID_054C&PID_05C4\02")
            && !PadDevices.IsPadDevice(Theirs) && PadDevices.SerialOf(Pad1) == 1 && PadDevices.SerialOf(Usb1) is null
            && PadDevices.PathFor(PadDevices.Xbox360, 3) == @"USB\VID_045E&PID_028E\03", "virtual pad names were misread");

        // A fresh HidHide: the next free names and every device Windows recorded under them are jailed to the seat.
        var tree = new FakeTree();
        tree.Known[Pad1] = new[] { Usb1, Hid1, Old1 };
        var hidHide = new FakeHidHide();
        using (var isolation = Isolation(tree, hidHide))
        {
            isolation.Reconcile();
            var expected = new[] { Pad1, Usb1, Hid1, Old1, Pad2, @"USB\VID_045E&PID_028E\03", @"USB\VID_045E&PID_028E\04",
                @"USB\VID_054C&PID_05C4\01", @"USB\VID_054C&PID_05C4\02", @"USB\VID_054C&PID_05C4\03", @"USB\VID_054C&PID_05C4\04" };
            Require(hidHide.List.ToHashSet().SetEquals(expected.Select(device => device + "!5")), "the next pad names were not jailed to the seat: " + string.Join(", ", hidHide.List));
            Require(hidHide.On, "a fresh HidHide holding only the seat's entries was not switched on");
            int writes = hidHide.Writes;
            isolation.Reconcile();
            Require(hidHide.Writes == writes, "an unchanged list was written again");
        }

        // The user's own entries stay; entries of an ended seat go; another live session's jail stays.
        hidHide = new FakeHidHide { On = true, List = { Theirs, Pad2 + "!" + Ended, Usb2 + "!" + OtherSeat } };
        using (var isolation = Isolation(new FakeTree(), hidHide))
        {
            isolation.Reconcile();
            Require(hidHide.List.Contains(Theirs) && hidHide.List.Contains(Usb2 + "!" + OtherSeat) && !hidHide.List.Contains(Pad2 + "!" + Ended)
                && hidHide.List.Contains(Pad2 + "!5"), "someone else's entries were changed, or an ended seat's were kept: " + string.Join(", ", hidHide.List));
            int seatEntries = hidHide.List.Count(entry => entry.EndsWith("!5", StringComparison.Ordinal));
            Require(hidHide.List.Take(seatEntries).All(entry => entry.EndsWith("!5", StringComparison.Ordinal)),
                "the seat's entries do not come first, so an earlier entry for the same device would decide");
        }

        // HidHide switched off while it hides the user's devices, or with an inverted list: nothing is plugged in.
        foreach (var (setup, words) in new (FakeHidHide, string)[]
        {
            (new FakeHidHide { List = { Theirs } }, "switched off"),
            (new FakeHidHide { On = true, Inverted = true }, "inverted")
        })
        {
            bool on = setup.On;
            using var isolation = Isolation(new FakeTree(), setup);
            Require(Throws<PadIsolationException>(() => isolation.BeforePlugIn(), words) && setup.On == on,
                $"a controller was allowed while HidHide was {words}, or Anode changed the user's HidHide setting");
        }

        // A program outside the seat uses ViGEm: its new pad is left alone, and only Anode's own next name is kept.
        tree = new FakeTree();
        hidHide = new FakeHidHide();
        using (var isolation = Isolation(tree, hidHide, clients: new[] { "DS4Windows.exe (PID 10, session 1)" }))
        {
            tree.Pads.Add(new VirtualPad(Pad1, 1, new[] { Usb1, Hid1 }));
            isolation.Reconcile();
            Require(hidHide.List.Count == 0, "another program's pad was jailed to the seat: " + string.Join(", ", hidHide.List));
            var before = isolation.BeforePlugIn();
            Require(before.SequenceEqual(new[] { Pad1 }) && hidHide.List.Contains(Pad2 + "!5") && !hidHide.List.Contains(Pad1 + "!5"),
                "Anode's next pad name was not jailed before it plugged in");
            tree.Pads.Add(new VirtualPad(Pad2, 2, new[] { Usb2, Hid2 }));
            var adoption = isolation.Adopt(before, TimeSpan.FromSeconds(1));
            Require(adoption is { PadId: Pad2, Isolated: true, Verified: true, Required: true }
                && new[] { Pad2, Usb2, Hid2 }.All(device => hidHide.List.Contains(device + "!5")) && !hidHide.List.Contains(Pad1 + "!5"),
                $"Anode's pad was not claimed and jailed ({adoption})");
            Require(!hidHide.List.Contains(@"USB\VID_045E&PID_028E\03!5"), "the name reserved for Anode's plug-in stayed reserved");
        }

        // A seat program's own pad is jailed from the moment it appears, then checked from the desktop once.
        tree = new FakeTree();
        hidHide = new FakeHidHide();
        int asked = 0;
        using (var isolation = Isolation(tree, hidHide, probe: devices => { asked++; return Probe(devices, false); }))
        {
            isolation.Refresh();
            Require(hidHide.List.Contains(Pad1 + "!5"), "the next pad name was not jailed before a seat program plugged in");
            tree.Pads.Add(new VirtualPad(Pad1, 1, new[] { Usb1, Hid1 }));
            isolation.Refresh();
            isolation.Refresh();
            var pad = (isolation.Describe()["pads"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault();
            Require(asked == 1 && pad?["owner"]?.GetValue<string>() == "seat" && pad["seatOnly"]?.GetValue<bool>() == true
                && pad["verifiedFromDesktop"]?.GetValue<bool>() == true && hidHide.List.Contains(Hid1 + "!5"),
                $"a seat program's pad was not kept in the seat and checked once ({asked} checks): {pad?.ToJsonString()}");
        }

        // Pads plugged in before the seat host started are someone else's.
        tree = new FakeTree { Pads = { new VirtualPad(Pad1, 1, new[] { Usb1, Hid1 }) } };
        hidHide = new FakeHidHide();
        using (var isolation = Isolation(tree, hidHide))
        {
            isolation.Reconcile();
            Require(!hidHide.List.Contains(Pad1 + "!5") && hidHide.List.Contains(Pad2 + "!5"), "a pad from before the seat was jailed to it");
        }

        // The daemon clears the entries of an ended seat and nothing else.
        hidHide = new FakeHidHide { On = true, List = { Theirs, Pad1 + "!5", Hid1 + "!5", Pad2 + "!" + Ended, Usb2 + "!" + OtherSeat } };
        int removed = PadIsolation.ClearEnded(Seat, () => hidHide, session => session == OtherSeat);
        Require(removed == 3 && hidHide.List.SequenceEqual(new[] { Theirs, Usb2 + "!" + OtherSeat }),
            "clearing an ended seat removed the wrong entries: " + string.Join(", ", hidHide.List));
        Require(PadIsolation.ClearEnded(Seat, () => null) == 0, "clearing without HidHide failed");

        // Programs outside the seat that use ViGEm: by name, or once their modules show ViGEm's client.
        long now = 0;
        bool loads = false;
        var clients = new ViGEmClients(Seat, () => now, pid => pid == 42 && loads);
        var processes = new (int, string, int)[] { (42, "python", 1), (43, "DS4Windows", 1), (44, "anode", 1), (45, "Liftoff", 5), (46, "sunshine", 0), (47, "svchost", 0) };
        Require(clients.Scan(processes).SequenceEqual(new[] { "DS4Windows.exe (PID 43, session 1)", "sunshine.exe (PID 46, session 0)" }),
            "known ViGEm programs outside the seat were missed, or Anode or seat programs counted");
        loads = true;
        now = 1_000;
        Require(clients.Scan(processes).Count == 2, "a process was read again before its next check");
        now = 2_000;
        Require(clients.Scan(processes).Contains("python.exe (PID 42, session 1)"), "a program that loaded ViGEm's client later was missed");
        Require(ViGEmClients.IsClientModule("ViGEmClient.dll") && !ViGEmClients.IsClientModule("xinput1_4.dll"), "ViGEm's client module was misread");

        return "entries jail the next pad names and their recorded devices to the seat, leave the user's HidHide entries and settings alone, "
            + "clear ended seats, and step aside for ViGEm programs outside the seat";
    }

    public static string Attach()
    {
        // Plugging in adds the pad and its devices to the tree, as ViGEm and Windows would.
        var tree = new FakeTree();
        var hidHide = new FakeHidHide();
        bool desktopOpens = false;
        using var isolation = Isolation(tree, hidHide, probe: devices => Probe(devices, desktopOpens));
        var pads = new GamepadManager(() => new FakePad(tree, Pad1, Usb1, Hid1));
        pads.Isolate(isolation);
        var attached = pads.Attach(0);
        Require(attached["seatOnly"]?.GetValue<bool>() == true && attached["device"]?.GetValue<string>() == Pad1
            && attached["verifiedFromDesktop"]?.GetValue<bool>() == true && new[] { Pad1, Usb1, Hid1 }.All(device => hidHide.List.Contains(device + "!5")),
            "an attached controller was not kept in the seat: " + attached.ToJsonString());
        pads.Detach(0);
        Require(tree.Pads.Count == 0 && pads.Slots().Count == 0, "detaching left the controller plugged in");

        // The desktop can still open it: unplugged again, and the agent is told why.
        desktopOpens = true;
        FakePad? exposed = null;
        pads = new GamepadManager(() => exposed = new FakePad(tree, Pad1, Usb1, Hid1));
        pads.Isolate(isolation);
        Require(Throws<PadIsolationException>(() => pads.Attach(0), "user's desktop") && exposed is { Disconnected: true } && pads.Slots().Count == 0,
            "a controller the desktop could open stayed plugged in");

        // Without HidHide the controller is machine-wide, as before, and says so.
        using var missing = Isolation(new FakeTree(), null);
        pads = new GamepadManager(() => new FakePad(null, Pad1));
        pads.Isolate(missing);
        attached = pads.Attach(1);
        Require(attached["seatOnly"]?.GetValue<bool>() == false && attached["summary"]?.GetValue<string>()?.Contains("machine-wide") == true,
            "a controller without HidHide did not say it is machine-wide: " + attached.ToJsonString());
        pads.DetachAll();
        return "attach jails the controller before it exists, checks from the desktop, unplugs one the desktop can open, and reports a machine-wide one";
    }

    /// <summary>
    /// The full self-test's live check, on the desktop: plugs in a controller jailed to an unused session and
    /// checks that XInput and HID opens from this session are refused. It sends no input and removes its
    /// HidHide entries afterward. Skipped without HidHide.
    /// </summary>
    public static string Live()
    {
        using (var hidHide = HidHideDevice.Open())
            if (hidHide is null) return "skipped: HidHide is not installed, so virtual controllers are machine-wide";
        // Not a running seat's session: this check would then manage, and could drop, that seat's own entries.
        // A seat host may move the pad into its seat meanwhile; either way this session stays refused.
        uint jail = UnusedSession();
        var connectedBefore = XInputConnected();
        var isolation = new PadIsolation(jail, desktopProbe: devices => PadIsolation.DesktopView(devices));
        try
        {
            using var pads = new GamepadManager();
            pads.Isolate(isolation);
            var attached = pads.Attach(0);
            try
            {
                Require(attached["seatOnly"]?.GetValue<bool>() == true && attached["verifiedFromDesktop"]?.GetValue<bool>() == true,
                    "the controller was not verified as hidden from this session: " + attached.ToJsonString());
                var connected = XInputConnected();
                Require(connected.IsSubsetOf(connectedBefore), $"XInput in this session sees the jailed controller (slots {string.Join(", ", connected.Except(connectedBefore))})");
            }
            finally { pads.Detach(0); }
            return $"{attached["device"]} jailed to session {jail}: this session's XInput and HID opens were refused";
        }
        finally
        {
            isolation.Dispose();
            PadIsolation.ClearEnded(jail);
        }
    }

    private static uint UnusedSession()
    {
        uint session = 64_000;
        while (Anode.Core.Session.ChildSession.Exists(session) != false) session++;
        return session;
    }

    private static HashSet<int> XInputConnected()
    {
        var slots = new HashSet<int>();
        for (int slot = 0; slot < 4; slot++)
            if (XInputGetState(slot, out _) == 0) slots.Add(slot);
        return slots;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [System.Runtime.InteropServices.DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int userIndex, out XInputState state);

    private static PadIsolation Isolation(FakeTree tree, FakeHidHide? hidHide, IReadOnlyList<string>? clients = null,
        Func<IReadOnlyList<string>, JsonObject?>? probe = null) =>
        new(Seat, tree, () => hidHide, () => clients ?? Array.Empty<string>(), probe ?? (devices => Probe(devices, false)),
            session => session is Seat or OtherSeat);

    /// <summary>The daemon's answer: every XInput or HID interface of each device, opened or refused.</summary>
    private static JsonObject Probe(IReadOnlyList<string> devices, bool opens) => new()
    {
        ["devices"] = new JsonArray(devices.Select(device => (JsonNode)new JsonObject
        {
            ["device"] = device,
            ["interfaces"] = PadDevices.SerialOf(device) is not null || device.StartsWith(@"HID\", StringComparison.Ordinal)
                ? new JsonArray(new JsonObject { ["path"] = @"\\?\" + device, ["opens"] = opens }) : new JsonArray()
        }).ToArray())
    };

    private sealed class FakeTree : IPadTree
    {
        public List<VirtualPad> Pads { get; init; } = new();
        public Dictionary<string, IReadOnlyCollection<string>> Known { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<VirtualPad> Present() => Pads.ToList();
        public IReadOnlyDictionary<string, IReadOnlyCollection<string>> History() => Known;
    }

    private sealed class FakeHidHide : IHidHide
    {
        public List<string> List { get; set; } = new();
        public bool On { get; set; }
        public bool Inverted { get; init; }
        public int Writes { get; private set; }
        public IReadOnlyList<string> Blacklist() => List.ToList();
        public void SetBlacklist(IReadOnlyCollection<string> entries) { List = entries.ToList(); Writes++; }
        public IReadOnlyList<string> Whitelist() => new[] { @"\Device\HarddiskVolume3\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideClient.exe" };
        public bool Active() => On;
        public void SetActive(bool active) => On = active;
        public bool Inverse() => Inverted;
        public void Dispose() { }
    }

    /// <summary>A controller that appears in the fake tree, with its devices, when it connects.</summary>
    private sealed class FakePad(FakeTree? tree, string id, params string[] devices) : IXbox360Controller
    {
        private byte _leftTrigger, _rightTrigger;
        private short _leftX, _leftY, _rightX, _rightY;
        private ushort _buttons;

        public bool Disconnected { get; private set; }
        public int UserIndex => 0;
        public ref byte LeftTrigger => ref _leftTrigger;
        public ref byte RightTrigger => ref _rightTrigger;
        public ref short LeftThumbX => ref _leftX;
        public ref short LeftThumbY => ref _leftY;
        public ref short RightThumbX => ref _rightX;
        public ref short RightThumbY => ref _rightY;
        public ref ushort ButtonState => ref _buttons;
        public int ButtonCount => 15;
        public int AxisCount => 4;
        public int SliderCount => 2;
        public bool AutoSubmitReport { get; set; }
        public event Xbox360FeedbackReceivedEventHandler FeedbackReceived { add { } remove { } }

        public void Connect() => tree?.Pads.Add(new VirtualPad(id, PadDevices.SerialOf(id), devices));
        public void Disconnect()
        {
            Disconnected = true;
            tree?.Pads.RemoveAll(pad => pad.Id == id);
        }
        public void SetButtonState(Xbox360Button button, bool pressed) { }
        public void SetAxisValue(Xbox360Axis axis, short value) { }
        public void SetSliderValue(Xbox360Slider slider, byte value) { }
        public void SetButtonState(int index, bool pressed) { }
        public void SetAxisValue(int index, short value) { }
        public void SetSliderValue(int index, byte value) { }
        public void SetButtonsFull(ushort buttons) => _buttons = buttons;
        public void ResetReport() { }
        public void SubmitReport() { }
    }

    private static bool Throws<T>(Action action, string words) where T : Exception
    {
        try { action(); return false; }
        catch (T ex) { return ex.Message.Contains(words, StringComparison.OrdinalIgnoreCase); }
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
}
