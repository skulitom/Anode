using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Session;
using Anode.Core.Steam;

namespace Anode.Cli;

/// <summary>
/// Steam launches without Steam: launch configurations parsed from memory, launches planned against
/// stand-ins for the machine, and namespace links between privately named objects. Nothing here
/// starts a Steam client, a game or a seat, or reads Steam's files.
/// </summary>
internal static class SteamChecks
{
    private const int AppId = 4242;
    private const string SteamExe = @"C:\Steam\steam.exe";

    public static string LaunchConfiguration()
    {
        var manifest = Vdf.Parse("\"AppState\"\n{\n\t// written by Steam\n\t\"name\"\t\t\"The \\\"Test\\\" Game\"\n\t\"StateFlags\"\t\t\"4\"\n"
            + "\t\"path\"\t\t\"C:\\\\Games\"\n\t\"MountedConfig\"\n\t{\n\t\t\"BetaKey\"\t\t\"beta\"\n\t}\n}\n").Section("AppState");
        Require(manifest?.Text("name") == "The \"Test\" Game" && manifest.Text("stateflags") == "4" && manifest.Text("path") == @"C:\Games"
            && manifest.Section("mountedconfig")?.Text("BetaKey") == "beta", "text KeyValues lost escapes, case-insensitive keys or a nested section");
        Require(Throws<InvalidDataException>(() => Vdf.Parse("\"AppState\" { \"name\" \"x\"")), "an unclosed KeyValues section was accepted");

        var launch = Vdf.Parse("\"launch\" {"
            + " \"0\" { \"executable\" \"run.sh\" \"config\" { \"oslist\" \"linux\" } }"
            + " \"1\" { \"executable\" \"bin\\\\beta.exe\" \"config\" { \"oslist\" \"windows\" \"BetaKey\" \"beta\" } }"
            + " \"2\" { \"executable\" \"game32.exe\" \"config\" { \"oslist\" \"windows\" \"osarch\" \"32\" } }"
            + " \"3\" { \"executable\" \"game.exe\" \"arguments\" \"-steam\" \"workingdir\" \"data\" \"type\" \"default\" \"config\" { \"oslist\" \"windows\" } }"
            + " \"4\" { \"executable\" \"vr.exe\" \"type\" \"vr\" }"
            + " }").Section("launch");
        Require(SteamLibrary.ChooseLaunch(launch, null) == ("game.exe", "-steam", "data"),
            "the public branch did not get the default 64-bit Windows entry");
        Require(SteamLibrary.ChooseLaunch(launch, "beta")?.Executable == @"bin\beta.exe", "an installed beta did not get its own entry");
        Require(SteamLibrary.ChooseLaunch(Vdf.Parse("\"l\" { \"0\" { \"executable\" \"run.sh\" \"config\" { \"oslist\" \"linux,macos\" } } }").Section("l"), null) is null,
            "an entry for other systems was chosen");
        Require(SteamLibrary.ChooseLaunch(Vdf.Parse("\"l\" { \"0\" { \"executable\" \"hl.exe\" \"config\" { \"BetaKey\" \"default\" } } }").Section("l"), "")?.Executable == "hl.exe",
            "an entry for the default branch was not used on the public branch");

        var app = new Vdf();
        app.Add("appid", (long)AppId);
        app.Add("config", Vdf.Parse("\"launch\" { \"0\" { \"executable\" \"game.exe\" \"arguments\" \"-x\" } }"));
        var other = new Vdf();
        other.Add("config", Vdf.Parse("\"launch\" { \"0\" { \"executable\" \"other.exe\" } }"));
        foreach (uint version in new[] { AppInfo.Version27, AppInfo.Version28, AppInfo.Version29 })
        {
            using var file = AppInfoFile(version, (7, other), (AppId, app));
            var found = AppInfo.Read(file, AppId);
            Require(found?.Text("appid") == AppId.ToString() && found.Section("config")?.Section("launch")?.Section("0")?.Text("arguments") == "-x",
                $"appinfo.vdf format 0x{version:x8} lost the app's launch configuration");
            file.Position = 0;
            Require(AppInfo.Read(file, 99) is null, $"appinfo.vdf format 0x{version:x8} reported an app it does not list");
        }
        using var unknown = new MemoryStream(new byte[] { 0x30, 0x44, 0x56, 0x07, 1, 0, 0, 0, 0, 0, 0, 0 });
        Require(Throws<InvalidDataException>(() => AppInfo.Read(unknown, AppId)), "an unknown appinfo.vdf format was read");
        return "text and binary KeyValues (appinfo formats 27-29) parse; launch entries follow system, branch, type and architecture";
    }

    public static string Launch()
    {
        var game = new SteamGame(AppId, "Test Game", @"C:\Games\Test", @"C:\Games\Test\game.exe", "-steam", @"C:\Games\Test\data");
        var started = new List<ProcessStartInfo>();
        var desktopStarts = new List<uint>();
        var links = new List<(uint Seat, int Steam)>();
        var pauses = new List<int>();
        SteamClient? client = null;
        long now = 0;
        var machine = new SteamLaunch.Machine(
            () => client, () => SteamExe, (session, _) => desktopStarts.Add(session),
            (_, _, _) => game, (seat, steam) => { links.Add((seat, steam)); return "AnodeCheck1"; },
            _ => SteamReady.Online, info => { started.Add(info); return null; }, ms => { pauses.Add(ms); now += ms; }, () => now);
        JsonObject Run(SteamLaunch.Machine stand, uint? desktop = 1, int? timeoutMs = null, bool force = false)
        {
            started.Clear(); desktopStarts.Clear(); links.Clear(); pauses.Clear();
            var request = new JsonObject { ["appId"] = AppId, ["args"] = new JsonArray("--fast", "two words"), ["force"] = force };
            if (timeoutMs is int ms) request["timeoutMs"] = ms;
            return SteamLaunch.Run(request, 5, desktop, stand);
        }

        // Steam on the desktop: the game itself starts in the seat and is pointed at that client.
        client = new SteamClient(10, 1, true);
        var bridged = Run(machine);
        var info = started.SingleOrDefault();
        Require(bridged.Bool("ok") == true && bridged.Obj("result")?.Bool("bridged") == true && bridged.Obj("result")?.Int("steamSession") == 1
            && info?.FileName == game.Executable && info.WorkingDirectory == game.WorkingDirectory && !info.UseShellExecute
            && info.Arguments == "-steam --fast \"two words\"" && links.SingleOrDefault() == (5u, 1) && desktopStarts.Count == 0,
            "a game did not start in the seat with its configured and extra arguments");
        Require(info!.Environment["SteamAppId"] == "4242" && info.Environment["SteamGameId"] == "4242"
            && info.Environment[SteamBridge.OverrideVariable] == "AnodeCheck1" && pauses.Count == 0,
            "a bridged game was not given its app id and the client's name, or waited for a ready client");

        // Steam not running: it starts on the desktop, and the game waits until a game could use it,
        // whatever Steam's records say, then a little longer for the account's licences.
        client = null;
        int asked = 0;
        var startsSteam = machine with
        {
            StartOnDesktop = (session, _) => { desktopStarts.Add(session); client = new SteamClient(20, (int)session, true); },
            Ready = _ => ++asked < 3 ? SteamReady.Unavailable : SteamReady.Online
        };
        var fresh = Run(startsSteam);
        Require(fresh.Bool("ok") == true && desktopStarts.SequenceEqual(new[] { 1u }) && started.Count == 1
            && started[0].FileName == game.Executable && asked == 3 && pauses.Last() == 5000,
            "Steam was not started on the desktop and asked until ready before the game");

        client = null;
        var neverReady = startsSteam with { Ready = _ => SteamReady.Unavailable };
        var waiting = Run(neverReady, timeoutMs: 20_000);
        Require(waiting.Bool("ok") == false && waiting.Str("error")!.Contains("not ready for games yet") && started.Count == 0
            && pauses.Sum() >= 5000, "a game started before Steam was ready, or Anode stopped waiting early");
        client = new SteamClient(10, 1, true);
        var offline = Run(machine with { Ready = _ => SteamReady.Offline }, timeoutMs: 20_000);
        Require(offline.Bool("ok") == true && started.Count == 1 && offline.Obj("result")!.Str("note")!.Contains("may run offline"),
            "a game did not start against a Steam that is signed in offline, or was not told");
        client = null;
        Require(Run(machine, desktop: null).Bool("ok") == false && started.Count == 0 && desktopStarts.Count == 0,
            "Steam was started with no known desktop session");

        // Steam already in the seat, or force: the game goes through a client in the seat, as before.
        client = new SteamClient(30, 5, true);
        var inSeat = Run(machine);
        Require(inSeat.Bool("ok") == true && inSeat.Obj("result")?.Bool("bridged") == false && started.Count == 1
            && started[0].FileName == SteamExe && started[0].ArgumentList.SequenceEqual(new[] { "-applaunch", "4242", "--fast", "two words" }),
            "a Steam client in the seat was not asked to launch the game");
        client = new SteamClient(10, 1, true);
        var forced = Run(machine, force: true);
        Require(forced.Bool("ok") == true && started.Count == 1 && started[0].FileName == SteamExe
            && forced.Obj("result")!.Str("note")!.Contains("takes Steam over from session 1"), "force did not use and name a client in the seat");
        client = null;
        Run(machine, force: true);
        Require(started.Count == 2 && started[0].ArgumentList.SequenceEqual(new[] { "-silent" }) && pauses.Sum() > 0 && desktopStarts.Count == 0,
            "force with no Steam did not start a client in the seat first");

        // Failures report and start nothing.
        client = new SteamClient(10, 1, true);
        foreach (var (failing, expected) in new (SteamLaunch.Machine, string)[]
                 {
                     (machine with { Ready = _ => SteamReady.Unavailable }, "not ready for games"),
                     (machine with { FindGame = (_, id, _) => throw new InvalidOperationException($"Steam app {id} is not installed") }, "is not installed"),
                     (machine with { SteamExecutable = () => null }, "Steam was not found")
                 })
        {
            var failed = Run(failing, timeoutMs: 12_000);
            Require(failed.Bool("ok") == false && failed.Str("error")!.Contains(expected) && started.Count == 0, $"a launch that should fail with '{expected}' did not");
        }

        Require(Mcp.Tools.ValidateArguments("steam_launch", new JsonObject { ["appId"] = 1, ["exe"] = @"bin\game.exe" }) is null
            && Mcp.Tools.ValidateArguments("steam_launch", new JsonObject { ["appId"] = 1, ["exe"] = "" }) is not null
            && Mcp.Tools.ValidateArguments("steam_launch", new JsonObject { ["appId"] = 1, ["exe"] = "a|b.exe" }) is not null,
            "steam_launch exe validation");
        return "games start in the seat against Steam where it runs, which starts on the desktop if needed; a client in the seat and force keep the old path";
    }

    public static string Links()
    {
        uint session = ChildSession.CurrentSessionId();
        string tag = Guid.NewGuid().ToString("N")[..12];
        string directory = SteamBridge.NamedObjects(session);
        using var target = new EventWaitHandle(false, EventResetMode.ManualReset, "AnodeCheckTarget" + tag, out bool created);
        Require(created, "the private check event already existed");
        using (NamespaceLink.Create(directory + @"\AnodeCheckLink" + tag, directory + @"\AnodeCheckTarget" + tag))
        {
            using var through = EventWaitHandle.OpenExisting("AnodeCheckLink" + tag);
            through.Set();
            Require(target.WaitOne(0), "a linked name did not reach the object it names");
        }
        bool survived = EventWaitHandle.TryOpenExisting("AnodeCheckLink" + tag, out var stale);
        stale?.Dispose();
        Require(!survived, "a link outlived its handle");

        using var mapping = MemoryMappedFile.CreateNew("AnodeCheckMapping" + tag, 64);
        using (NamespaceLink.Create(directory + @"\AnodeCheckOpen" + tag + "_SharedMemFile", directory + @"\AnodeCheckMapping" + tag))
        using (NamespaceLink.Create(directory + @"\AnodeCheckGone" + tag + "_SharedMemFile", directory + @"\AnodeCheckMissing" + tag))
            Require(SteamBridge.Listening("AnodeCheckOpen" + tag) && !SteamBridge.Listening("AnodeCheckGone" + tag),
                "a client was reported listening, or not, regardless of its objects");

        // No Steam runs in this session number; the name is still made, reused and free of backslashes.
        string name = SteamBridge.Connect(session, 65000);
        Require(!name.Contains('\\') && SteamBridge.Connect(session, 65000) == name && !SteamBridge.Listening(name),
            "a Steam bridge name was unusable, recreated or reported a client that is not there");
        return "names in this session's namespace resolve through links and stop with them; listening follows the client's objects";
    }

    /// <summary>An appinfo.vdf in memory, as Steam writes it: format 29 keeps key names in a table at the end.</summary>
    private static MemoryStream AppInfoFile(uint version, params (int Id, Vdf Info)[] apps)
    {
        var keys = new List<string>();
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(version);
        writer.Write(1u);
        long tableOffset = stream.Position;
        if (version == AppInfo.Version29) writer.Write(0L);
        foreach (var (id, info) in apps)
        {
            writer.Write((uint)id);
            long sizeAt = stream.Position;
            writer.Write(0u);
            long start = stream.Position;
            writer.Write(2u); writer.Write(0u); writer.Write(0UL); writer.Write(new byte[20]); writer.Write(0u);
            if (version != AppInfo.Version27) writer.Write(new byte[20]);
            writer.Write((byte)0);
            Key(writer, "appinfo", version, keys);
            Section(writer, info, version, keys);
            writer.Write((byte)8);
            long end = stream.Position;
            stream.Position = sizeAt;
            writer.Write((uint)(end - start));
            stream.Position = end;
        }
        writer.Write(0u);
        if (version == AppInfo.Version29)
        {
            long table = stream.Position;
            writer.Write((uint)keys.Count);
            foreach (string key in keys) Text(writer, key);
            stream.Position = tableOffset;
            writer.Write(table);
        }
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private static void Section(BinaryWriter writer, Vdf section, uint version, List<string> keys)
    {
        foreach (var (key, value) in section.Entries)
        {
            writer.Write((byte)(value switch { Vdf => 0, long => 2, _ => 1 }));
            Key(writer, key, version, keys);
            if (value is Vdf child) Section(writer, child, version, keys);
            else if (value is long number) writer.Write((int)number);
            else Text(writer, (string)value);
        }
        writer.Write((byte)8);
    }

    private static void Key(BinaryWriter writer, string key, uint version, List<string> keys)
    {
        if (version != AppInfo.Version29) { Text(writer, key); return; }
        int index = keys.IndexOf(key);
        if (index < 0) { index = keys.Count; keys.Add(key); }
        writer.Write(index);
    }

    private static void Text(BinaryWriter writer, string text)
    {
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Write((byte)0);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
}
