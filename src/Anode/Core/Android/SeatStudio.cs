using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Util;

namespace Anode.Core.Android;

/// <summary>
/// Android Studio in the seat, on a profile of its own.
///
/// Android Studio hands a second start on the same settings folders to the instance already running, in whatever
/// session that runs. Started in the seat on the user's settings, it would open its project on the user's desktop,
/// and the user's next Studio would open invisibly in the seat. Its own config, system, plugin and log folders make
/// the seat's Studio a separate instance, and its first-run wizard is skipped because the SDK is already installed.
/// </summary>
internal static class SeatStudio
{
    public static string Profile(Func<string, string?> environment) =>
        Path.Combine(environment("LOCALAPPDATA") ?? "", "AnodeAndroidStudio");

    /// <summary>The properties STUDIO_PROPERTIES points Android Studio at; later sources cannot override them.</summary>
    internal static string Properties(string profile)
    {
        string Folder(string name) => Path.Combine(profile, name).Replace('\\', '/');
        return string.Join("\n",
            "# Written by Anode for Android Studio in the seat. Its own folders keep it apart from the Studio on the desktop.",
            $"idea.config.path={Folder("config")}",
            $"idea.system.path={Folder("system")}",
            $"idea.plugins.path={Folder("plugins")}",
            $"idea.log.path={Folder("log")}",
            "disable.android.first.run=true",
            "");
    }

    public static JsonObject Open(JsonObject request, Func<string, string?> environment, Func<ProcessStartInfo, Process?> start)
    {
        var studios = AndroidSdk.Studios(SeatEmulators.StudioFolders(environment));
        if (studios.Count == 0)
            return JsonLine.Fail("Android Studio was not found in Program Files\\Android or %LOCALAPPDATA%\\Programs. Install it, then try again.");
        var studio = studios[0];
        string? project = request.Str("project");
        if (project is not null && !Directory.Exists(project)) return JsonLine.Fail($"The project folder {project} does not exist.");

        string profile = Profile(environment);
        bool first = !Directory.Exists(Path.Combine(profile, "config"));
        string properties = Path.Combine(profile, "studio.properties");
        try
        {
            // An existing config folder also tells Studio there are no settings to import on first start.
            foreach (string folder in new[] { "config", "system", "plugins", "log" }) Directory.CreateDirectory(Path.Combine(profile, folder));
            File.WriteAllText(properties, Properties(profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return JsonLine.Fail($"Anode could not prepare Android Studio's seat profile in {profile}: {ex.Message}");
        }

        var info = new ProcessStartInfo(studio.Launcher) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(studio.Launcher)! };
        info.Environment["STUDIO_PROPERTIES"] = properties;
        if (string.IsNullOrWhiteSpace(environment("ANDROID_HOME")) && AndroidSdk.Find(environment) is { } sdk) info.Environment["ANDROID_HOME"] = sdk.Root;
        if (project is not null) info.ArgumentList.Add(project);
        using var process = start(info);
        Log.Info($"seat started Android Studio {studio.Version} on its own profile" + (project is null ? "" : $" for {project}"));
        return JsonLine.Ok(new JsonObject
        {
            ["path"] = studio.Launcher, ["version"] = studio.Version, ["pid"] = process?.Id, ["profile"] = profile, ["project"] = project,
            ["firstStart"] = first,
            ["summary"] = $"Android Studio {studio.Version} is starting in the seat on its own profile ({profile}), apart from any Studio on the desktop."
                + (project is null ? "" : $" It opens {project}; it may ask whether to trust the project, and Gradle sync takes a while.")
                + (first ? " This profile is new, so Studio starts with default settings." : "")
                + " Find its window with seat_windows. Most of Studio is invisible to UI Automation: use screenshots and the keyboard,"
                + " such as Ctrl+Shift+A (Find Action) followed by an action's name. Emulators it starts run in the seat too."
        });
    }

    /// <summary>
    /// run would start Android Studio on the user's own settings folders, which hands the start to a Studio on the
    /// desktop or captures the user's next one. Null for anything else.
    /// </summary>
    internal static string? DirectLaunchFailure(string path)
    {
        string name = Path.GetFileName(path.Trim().Trim('"'));
        if (!(name.Equals("studio64.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("studio.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("studio.bat", StringComparison.OrdinalIgnoreCase) || name.Equals("studio64", StringComparison.OrdinalIgnoreCase))) return null;
        return "Started this way, Android Studio would use your own settings folders: a Studio already open on your desktop would take the "
            + "request and open it there, and one started here would capture your next Studio. Open Android Studio with "
            + "`anode android studio [project]` (agents: android_studio), which gives the seat's Studio a profile of its own.";
    }
}
