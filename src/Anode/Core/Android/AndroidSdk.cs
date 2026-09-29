using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Anode.Core.Android;

/// <summary>A virtual device as its .ini files describe it.</summary>
internal sealed record Avd(string Name, string DisplayName, int? Api, string Image, string Abi, string? Screen, bool PlayStore)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name, ["displayName"] = DisplayName, ["api"] = Api, ["image"] = Image, ["abi"] = Abi,
        ["screen"] = Screen, ["playStore"] = PlayStore
    };
}

/// <summary>An Android Studio installation.</summary>
internal sealed record StudioInstall(string Launcher, string? Version)
{
    public JsonObject ToJson() => new() { ["path"] = Launcher, ["version"] = Version };
}

/// <summary>
/// The Android SDK, virtual devices and Android Studio this user has, found where Android Studio and the
/// command-line tools look for them. It only reads files; nothing here starts a process.
/// </summary>
internal sealed class AndroidSdk
{
    private AndroidSdk(string root, Version? emulatorVersion)
    {
        Root = root;
        EmulatorVersion = emulatorVersion;
    }

    public string Root { get; }
    public string Emulator => Path.Combine(Root, "emulator", "emulator.exe");
    public string Adb => Path.Combine(Root, "platform-tools", "adb.exe");
    public Version? EmulatorVersion { get; }

    /// <summary>
    /// The SDK in ANDROID_HOME, then ANDROID_SDK_ROOT, then Android Studio's default under LOCALAPPDATA:
    /// the first folder that holds the emulator or adb.
    /// </summary>
    public static AndroidSdk? Find(Func<string, string?> environment)
    {
        string? local = environment("LOCALAPPDATA");
        foreach (string? candidate in new[] { environment("ANDROID_HOME"), environment("ANDROID_SDK_ROOT"),
                     string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "Android", "Sdk") })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
            string root = Path.GetFullPath(candidate);
            if (!File.Exists(Path.Combine(root, "emulator", "emulator.exe")) && !File.Exists(Path.Combine(root, "platform-tools", "adb.exe"))) continue;
            return new AndroidSdk(root, Revision(Path.Combine(root, "emulator", "source.properties")));
        }
        return null;
    }

    /// <summary>The emulator package revision, such as 36.2.12, from its source.properties.</summary>
    internal static Version? Revision(string sourceProperties)
    {
        try
        {
            string? value = Values(sourceProperties).GetValueOrDefault("Pkg.Revision");
            return value is not null && Version.TryParse(value.Split(' ', '-')[0], out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The -gpu value for auto, host or software, as this emulator names it. Emulator 36.4.9 introduced
    /// software and deprecated swiftshader_indirect, which earlier versions need instead. Null leaves the
    /// AVD's own setting.
    /// </summary>
    public static string? GpuFlag(string mode, Version? emulator) => mode switch
    {
        "host" => "host",
        "software" => emulator is not null && emulator >= new Version(36, 4, 9) ? "software" : "swiftshader_indirect",
        _ => null
    };

    /// <summary>Where the emulator keeps virtual devices: ANDROID_AVD_HOME, ANDROID_USER_HOME\avd, or .android\avd.</summary>
    public static string AvdHome(Func<string, string?> environment)
    {
        if (environment("ANDROID_AVD_HOME") is { Length: > 0 } avdHome) return avdHome;
        if (environment("ANDROID_USER_HOME") is { Length: > 0 } userHome) return Path.Combine(userHome, "avd");
        if (environment("ANDROID_SDK_HOME") is { Length: > 0 } legacy) return Path.Combine(legacy, ".android", "avd");
        return Path.Combine(environment("USERPROFILE") ?? "", ".android", "avd");
    }

    /// <summary>The virtual devices defined in an AVD home, by name.</summary>
    public static IReadOnlyList<Avd> Avds(string avdHome)
    {
        var found = new List<Avd>();
        if (!Directory.Exists(avdHome)) return found;
        foreach (string pointer in Directory.EnumerateFiles(avdHome, "*.ini").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(pointer);
                var link = Values(pointer);
                string? directory = link.GetValueOrDefault("path") is { Length: > 0 } absolute && Directory.Exists(absolute) ? absolute
                    : link.GetValueOrDefault("path.rel") is { Length: > 0 } relative && Directory.Exists(Path.Combine(Path.GetDirectoryName(avdHome) ?? avdHome, relative))
                        ? Path.Combine(Path.GetDirectoryName(avdHome) ?? avdHome, relative)
                        : Path.Combine(avdHome, name + ".avd");
                string config = Path.Combine(directory, "config.ini");
                if (!File.Exists(config)) continue;
                var settings = Values(config);
                string target = settings.GetValueOrDefault("target") ?? link.GetValueOrDefault("target") ?? "";
                string sysdir = settings.GetValueOrDefault("image.sysdir.1") ?? "";
                var api = Regex.Match(target + " " + sysdir, @"android-(\d+)");
                string? width = settings.GetValueOrDefault("hw.lcd.width"), height = settings.GetValueOrDefault("hw.lcd.height");
                string? density = settings.GetValueOrDefault("hw.lcd.density");
                found.Add(new Avd(name,
                    settings.GetValueOrDefault("avd.ini.displayname") ?? name.Replace('_', ' '),
                    api.Success ? int.Parse(api.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                    settings.GetValueOrDefault("tag.display") ?? settings.GetValueOrDefault("tag.id") ?? "",
                    settings.GetValueOrDefault("abi.type") ?? "",
                    width is null || height is null ? null : $"{width}x{height}" + (density is null ? "" : $" at {density} dpi"),
                    settings.GetValueOrDefault("PlayStore.enabled")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>
    /// Android Studio installations under the Program Files Android folders, newest first. A folder without
    /// product-info.json is the remains of an uninstall or an interrupted update, and does not start.
    /// </summary>
    public static IReadOnlyList<StudioInstall> Studios(IEnumerable<string> androidFolders)
    {
        var found = new List<StudioInstall>();
        foreach (string folder in androidFolders.Where(Directory.Exists))
        {
            IEnumerable<string> installs;
            try { installs = Directory.EnumerateDirectories(folder, "Android Studio*").ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string install in installs)
            {
                string launcher = Path.Combine(install, "bin", "studio64.exe");
                if (!File.Exists(launcher) || !File.Exists(Path.Combine(install, "product-info.json"))) continue;
                found.Add(new StudioInstall(launcher, StudioVersion(install)));
            }
        }
        return found.OrderByDescending(s => s.Version is { } v && Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
            .ThenBy(s => s.Launcher, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The release year and number Android Studio names its settings folder after, such as 2025.2.1.</summary>
    private static string? StudioVersion(string install)
    {
        try
        {
            string info = Path.Combine(install, "product-info.json");
            if (!File.Exists(info)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(info));
            return document.RootElement.TryGetProperty("dataDirectoryName", out var data) && data.GetString() is { } name
                ? Regex.Match(name, @"\d+(\.\d+)+").Value is { Length: > 0 } version ? version : null
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>key=value lines, as the SDK's .ini and .properties files hold them.</summary>
    internal static Dictionary<string, string> Values(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0 || line.TrimStart().StartsWith('#')) continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return values;
    }
}
