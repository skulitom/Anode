using System.Globalization;
using System.Text;
using Anode.Core.Util;

namespace Anode.Core.Steam;

/// <summary>An installed Steam game and the Windows launch Steam's configuration names for it.</summary>
internal sealed record SteamGame(int AppId, string Name, string InstallDirectory, string Executable,
    string Arguments, string WorkingDirectory);

/// <summary>
/// Reads the Steam library on disk: which library holds a game, where it is installed and which
/// executable Steam would start on Windows. It only reads Steam's files; Steam need not be running.
/// </summary>
internal static class SteamLibrary
{
    private const int FullyInstalled = 4;

    /// <summary>
    /// Finds an installed game. <paramref name="executable"/>, absolute or relative to the install
    /// folder, replaces the launch configuration when Steam's is missing or names the wrong program.
    /// </summary>
    public static SteamGame Find(string steamDirectory, int appId, string? executable = null)
    {
        foreach (string library in Libraries(steamDirectory))
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;

            var state = Vdf.Parse(File.ReadAllText(manifest)).Section("AppState")
                ?? throw new InvalidDataException($"{manifest} has no AppState section.");
            string name = state.Text("name") ?? $"app {appId}";
            string folder = state.Text("installdir") is { Length: > 0 } installdir
                ? Path.Combine(library, "steamapps", "common", installdir)
                : throw new InvalidDataException($"{manifest} does not say where {name} is installed.");
            if (!int.TryParse(state.Text("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags)
                || (flags & FullyInstalled) == 0 || !Directory.Exists(folder))
                throw new InvalidOperationException($"Steam has not finished installing {name}. Finish the installation in Steam, then launch again.");

            if (executable is not null) return Override(appId, name, folder, executable);
            string branch = state.Section("MountedConfig")?.Text("BetaKey") ?? state.Section("UserConfig")?.Text("BetaKey") ?? "";
            return Configured(steamDirectory, appId, name, folder, branch)
                ?? throw new InvalidOperationException($"Steam's launch configuration for {name} names no Windows program Anode can start. "
                    + $"Pass the game's executable (CLI: --exe, MCP: exe), relative to {folder}.");
        }
        throw new InvalidOperationException($"Steam app {appId} is not installed in a Steam library on this machine. Install it in Steam first.");
    }

    /// <summary>The Steam folder itself and every library folder Steam lists.</summary>
    internal static IEnumerable<string> Libraries(string steamDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(Path.GetFullPath(steamDirectory))) yield return steamDirectory;

        string list = Path.Combine(steamDirectory, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(list)) yield break;
        var folders = Vdf.Parse(File.ReadAllText(list)).Section("libraryfolders");
        foreach (var (_, value) in folders?.Entries ?? Enumerable.Empty<(string, object)>())
        {
            // Current Steam writes a section per library; older Steam wrote the path directly.
            string? path = value is Vdf library ? library.Text("path") : value as string;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
            if (seen.Add(Path.GetFullPath(path))) yield return path;
        }
    }

    private static SteamGame Override(int appId, string name, string folder, string executable)
    {
        string path = Path.GetFullPath(Path.Combine(folder, executable));
        if (!File.Exists(path)) throw new InvalidOperationException($"{path} does not exist. Name an executable inside {folder}.");
        return new SteamGame(appId, name, folder, path, string.Empty, Path.GetDirectoryName(path)!);
    }

    private static SteamGame? Configured(string steamDirectory, int appId, string name, string folder, string branch)
    {
        Vdf? app;
        try { app = AppInfo.Read(Path.Combine(steamDirectory, "appcache", "appinfo.vdf"), appId); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or IndexOutOfRangeException or ArgumentException)
        {
            Log.Warn($"could not read Steam's launch configuration for app {appId}: {ex.Message}");
            app = null;
        }
        if (ChooseLaunch(app?.Section("config")?.Section("launch"), branch) is not { } launch) return Fallback(appId, name, folder);

        string executable = Path.GetFullPath(Path.Combine(folder, launch.Executable.Replace('/', '\\')));
        if (!File.Exists(executable)) return Fallback(appId, name, folder);
        string directory = launch.WorkingDirectory is { Length: > 0 } relative
            ? Path.GetFullPath(Path.Combine(folder, relative.Replace('/', '\\')))
            : Path.GetDirectoryName(executable)!;
        return new SteamGame(appId, name, folder, executable, launch.Arguments, Directory.Exists(directory) ? directory : folder);
    }

    /// <summary>A game whose install folder holds exactly one program other than crash reporters and installers.</summary>
    private static SteamGame? Fallback(int appId, string name, string folder)
    {
        string[] helpers = { "unitycrashhandler", "crashreport", "crashpad", "setup", "redist", "vcredist", "dxsetup", "dotnet", "prereq", "uninst" };
        var programs = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(path => !helpers.Any(helper => Path.GetFileName(path).Contains(helper, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return programs.Length == 1
            ? new SteamGame(appId, name, folder, programs[0], string.Empty, folder)
            : null;
    }

    /// <summary>
    /// Picks the launch entry Steam would use on 64-bit Windows: entries for Windows only, for the
    /// installed branch or any branch, default choices before optional ones, 64-bit before 32-bit,
    /// then Steam's own order.
    /// </summary>
    internal static (string Executable, string Arguments, string? WorkingDirectory)? ChooseLaunch(Vdf? launch, string? branch)
    {
        if (launch is null) return null;
        bool publicBranch = string.IsNullOrEmpty(branch) || branch.Equals("public", StringComparison.OrdinalIgnoreCase);
        var candidates = new List<(int Rank, int Order, Vdf Entry)>();
        int order = 0;
        foreach (var (_, value) in launch.Entries)
        {
            order++;
            if (value is not Vdf entry || string.IsNullOrWhiteSpace(entry.Text("executable"))) continue;
            var config = entry.Section("config");

            string systems = config?.Text("oslist") ?? string.Empty;
            if (systems.Length > 0 && !systems.Split(',').Any(os => os.Trim().Equals("windows", StringComparison.OrdinalIgnoreCase))) continue;

            string beta = config?.Text("BetaKey") ?? string.Empty;
            int betaRank;
            if (beta.Length == 0) betaRank = 1;
            else if (beta.Equals(branch, StringComparison.OrdinalIgnoreCase)
                || publicBranch && (beta.Equals("default", StringComparison.OrdinalIgnoreCase) || beta.Equals("public", StringComparison.OrdinalIgnoreCase)))
                betaRank = 0;
            else continue;

            string architecture = config?.Text("osarch") ?? string.Empty;
            int architectureRank = architecture is "" or "64" ? 0 : architecture == "32" ? 1 : 2;
            string type = (entry.Text("type") ?? string.Empty).ToLowerInvariant();
            int typeRank = type is "" or "default" or "none" or "option1" ? 0 : type.StartsWith("option", StringComparison.Ordinal) ? 1 : 2;
            candidates.Add((typeRank * 100 + betaRank * 10 + architectureRank, order, entry));
        }
        if (candidates.Count == 0) return null;
        var chosen = candidates.OrderBy(c => c.Rank).ThenBy(c => c.Order).First().Entry;
        return (chosen.Text("executable")!.Trim(), chosen.Text("arguments")?.Trim() ?? string.Empty, chosen.Text("workingdir")?.Trim());
    }
}

/// <summary>
/// Valve's KeyValues: nested, ordered key/value sections, written as text in manifests and in
/// binary in appinfo.vdf. Keys compare case-insensitively, as in Steam.
/// </summary>
internal sealed class Vdf
{
    private readonly List<(string Key, object Value)> _entries = new();

    public IReadOnlyList<(string Key, object Value)> Entries => _entries;

    public void Add(string key, object value) => _entries.Add((key, value));

    public Vdf? Section(string key) => Find(key) as Vdf;

    /// <summary>A value as text; binary numbers are converted, sections are not.</summary>
    public string? Text(string key) => Find(key) switch
    {
        string text => text,
        long number => number.ToString(CultureInfo.InvariantCulture),
        ulong number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    private object? Find(string key)
    {
        foreach (var (name, value) in _entries)
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    /// <summary>Parses text KeyValues, such as libraryfolders.vdf and appmanifest files.</summary>
    public static Vdf Parse(string text)
    {
        int position = 0;
        var root = new Vdf();
        ReadSection(root, text, ref position, nested: false);
        return root;
    }

    private static void ReadSection(Vdf section, string text, ref int position, bool nested)
    {
        while (true)
        {
            string? key = Token(text, ref position, out bool quoted);
            if (key is null)
            {
                if (nested) throw new InvalidDataException("A KeyValues section is not closed.");
                return;
            }
            if (!quoted && key == "}")
            {
                if (!nested) throw new InvalidDataException("A KeyValues file closes a section it never opened.");
                return;
            }
            string value = Token(text, ref position, out bool quotedValue)
                ?? throw new InvalidDataException($"KeyValues key '{key}' has no value.");
            if (!quotedValue && value == "{")
            {
                var child = new Vdf();
                ReadSection(child, text, ref position, nested: true);
                section.Add(key, child);
            }
            else section.Add(key, value);
        }
    }

    private static string? Token(string text, ref int position, out bool quoted)
    {
        quoted = false;
        while (position < text.Length)
        {
            char c = text[position];
            if (char.IsWhiteSpace(c)) position++;
            else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                while (position < text.Length && text[position] != '\n') position++;
            else if (c == '[')
                // A platform condition such as [$WIN32] qualifies the value before it; Steam's own files do not use them.
                while (position < text.Length && text[position++] != ']') { }
            else break;
        }
        if (position >= text.Length) return null;
        if (text[position] is '{' or '}') return text[position++].ToString();
        if (text[position] != '"')
        {
            int start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('"' or '{' or '}')) position++;
            return text[start..position];
        }

        quoted = true;
        var value = new StringBuilder();
        position++;
        while (position < text.Length && text[position] != '"')
        {
            if (text[position] == '\\' && position + 1 < text.Length && text[position + 1] is '\\' or '"' or 'n' or 't')
            {
                value.Append(text[position + 1] switch { 'n' => '\n', 't' => '\t', char other => other });
                position += 2;
            }
            else value.Append(text[position++]);
        }
        if (position >= text.Length) throw new InvalidDataException("A KeyValues string is not closed.");
        position++;
        return value.ToString();
    }
}

/// <summary>
/// Steam's appcache\appinfo.vdf, the product information Steam keeps for every app it knows,
/// with each app's launch configuration. Formats 27 to 29 are read; 29 moved key names into a
/// table at the end of the file.
/// </summary>
internal static class AppInfo
{
    internal const uint Version27 = 0x07564427, Version28 = 0x07564428, Version29 = 0x07564429;

    /// <summary>The app's "appinfo" section, or null when the file does not list the app.</summary>
    public static Vdf? Read(string path, int appId)
    {
        // Steam rewrites this file while it runs; never lock it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream, appId);
    }

    internal static Vdf? Read(Stream stream, int appId)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        uint version = reader.ReadUInt32();
        if (version is not (Version27 or Version28 or Version29))
            throw new InvalidDataException($"appinfo.vdf has format 0x{version:x8}, which this Anode cannot read.");
        reader.ReadUInt32(); // universe

        string[]? keys = null;
        if (version == Version29)
        {
            long table = reader.ReadInt64();
            long entries = stream.Position;
            stream.Position = table;
            uint count = reader.ReadUInt32();
            if (count > stream.Length) throw new InvalidDataException("appinfo.vdf lists more key names than it has bytes.");
            keys = new string[count];
            for (int i = 0; i < keys.Length; i++) keys[i] = ReadString(reader);
            stream.Position = entries;
        }

        while (true)
        {
            uint id = reader.ReadUInt32();
            if (id == 0) return null;
            long next = reader.ReadUInt32() + stream.Position;
            if (id == (uint)appId)
            {
                // Info state, last update, PICS token, text SHA-1, change number; format 28 added a binary SHA-1.
                stream.Position += 4 + 4 + 8 + 20 + 4 + (version == Version27 ? 0 : 20);
                var root = ReadSection(reader, keys);
                return root.Section("appinfo") ?? root;
            }
            stream.Position = next;
        }
    }

    private static Vdf ReadSection(BinaryReader reader, string[]? keys)
    {
        var section = new Vdf();
        while (true)
        {
            byte type = reader.ReadByte();
            if (type is 8 or 11) return section;
            string key = keys is null ? ReadString(reader) : keys[reader.ReadInt32()];
            switch (type)
            {
                case 0: section.Add(key, ReadSection(reader, keys)); break;
                case 1: section.Add(key, ReadString(reader)); break;
                case 2: section.Add(key, (long)reader.ReadInt32()); break;
                case 3: section.Add(key, (double)reader.ReadSingle()); break;
                case 6: reader.ReadInt32(); break; // color
                case 7: section.Add(key, reader.ReadUInt64()); break;
                case 10: section.Add(key, reader.ReadInt64()); break;
                default: throw new InvalidDataException($"appinfo.vdf holds a KeyValues type {type} this Anode cannot read.");
            }
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        for (byte b = reader.ReadByte(); b != 0; b = reader.ReadByte()) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
