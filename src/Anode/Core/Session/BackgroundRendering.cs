using System.Text.Json;
using Microsoft.Win32;
using Anode.Core.Util;

namespace Anode.Core.Session;

/// <summary>The RDP client reads this per-user preference when its control is created.</summary>
internal static class BackgroundRendering
{
    private const string KeyPath = @"Software\Microsoft\Terminal Server Client";
    private const string ValueName = "RemoteDesktop_SuppressWhenMinimized";
    private const string BackupFile = "rdp-rendering-backup.json";
    private static string BackupPath => Path.Combine(Env.StateDirectory, BackupFile);
    private sealed record Backup(int? PreviousValue);

    /// <summary>
    /// The backups <c>rendering --restore</c> can use: the state directory's own, or, when it has none and no
    /// --state-dir was given, any in a packaged app's Anode folder. Some desktop apps installed as MSIX packages, such
    /// as the Claude desktop app, give the programs they start a private view of AppData, so a daemon such an app starts
    /// keeps its state, this backup included, under %LOCALAPPDATA%\Packages\&lt;app&gt;\LocalCache\Local\Anode (see
    /// <c>Env.ResolveDirectory</c>).
    /// </summary>
    internal static List<string> Backups(string stateDirectory, bool chosen, string localAppData, string folderName)
    {
        string own = Path.Combine(stateDirectory, BackupFile);
        if (File.Exists(own)) return new() { own };
        var found = new List<string>();
        string packages = Path.Combine(localAppData, "Packages");
        if (chosen || !Directory.Exists(packages)) return found;
        try
        {
            foreach (string package in Directory.EnumerateDirectories(packages))
            {
                string candidate = Path.Combine(package, "LocalCache", "Local", folderName, BackupFile);
                if (File.Exists(candidate)) found.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial list could restore the wrong backup, so say so instead.
            throw new InvalidOperationException($"Could not search packaged apps' Anode folders in {packages} ({ex.Message}). Nothing was "
                + "changed; run anode rendering --restore --state-dir \"<folder>\" for a folder you know.");
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>
    /// Why <c>rendering --restore</c> must not run here, or null. Inside a packaged app the state directory resolves into
    /// the app's own folder, so this process can't see %LOCALAPPDATA%\Anode as a terminal does, and its registry changes
    /// may stay private to the app too.
    /// </summary>
    internal static string? PackagedRefusal(string stateDirectory, bool chosen, string localAppData)
    {
        string packages = Path.GetFullPath(Path.Combine(localAppData, "Packages")) + Path.DirectorySeparatorChar;
        if (chosen || !Path.GetFullPath(stateDirectory).StartsWith(packages, StringComparison.OrdinalIgnoreCase)) return null;
        return $"This terminal runs inside a packaged app, which keeps Anode's files in {stateDirectory} and may keep registry changes "
            + "to itself. Run anode rendering --restore from your own terminal, such as Windows Terminal opened from the Start menu.";
    }

    public static int? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as int?;
    }

    public static void Configure()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        object? previous = key.GetValue(ValueName);
        if (previous is int current && current == 2) return;
        if (previous is not null && key.GetValueKind(ValueName) != RegistryValueKind.DWord)
            throw new InvalidOperationException("The RDP background-rendering preference has an unexpected registry type; it was left unchanged.");
        Env.EnsureStateDirectory();
        if (!File.Exists(BackupPath))
        {
            using var backup = new FileStream(BackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(backup, new Backup(previous as int?));
        }
        key.SetValue(ValueName, 2, RegistryValueKind.DWord);
        Log.Info("Enabled per-user RDP rendering while minimized. Previous preference saved in " + BackupPath);
    }

    public static string Restore()
    {
        if (PackagedRefusal(Env.StateDirectory, Env.StateDirectoryChosen, Env.LocalAppData) is { } refusal)
            throw new InvalidOperationException(refusal);
        var backups = Backups(Env.StateDirectory, Env.StateDirectoryChosen, Env.LocalAppData, Env.StateFolderName);
        if (backups.Count == 0)
            return $"No Anode background-rendering backup exists in {Env.StateDirectory}"
                + (Env.StateDirectoryChosen ? "." : ", or in the Anode folder of a packaged app such as the Claude desktop app.");
        if (backups.Count > 1)
            throw new InvalidOperationException("Daemons started by different packaged apps each saved a background-rendering backup:"
                + string.Concat(backups.Select(path => $"{System.Environment.NewLine}  {path} (saved {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}, "
                    + $"previous value {SavedValue(path)})"))
                + $"{System.Environment.NewLine}Nothing was changed. Restore the one with the value you want with anode rendering --restore "
                + "--state-dir \"<its folder>\", then delete the other backups, which are out of date once the value is back.");
        string path = backups[0];
        var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Invalid background-rendering backup.");
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key.GetValue(ValueName) is not int current || current != 2)
            throw new InvalidOperationException("The RDP preference changed after Anode set it; it was left unchanged.");
        if (backup.PreviousValue is int value) key.SetValue(ValueName, value, RegistryValueKind.DWord);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        File.Delete(path);
        return "Previous RDP rendering preference restored. Restart RDP clients for it to take effect."
            + (path == BackupPath ? "" : $" Its backup was in {Path.GetDirectoryName(path)}, saved by a daemon a packaged app started.");
    }

    private static string SavedValue(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Backup>(File.ReadAllText(path)) is { } backup
                ? backup.PreviousValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none (the value was absent)"
                : "unreadable";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return "unreadable"; }
    }
}
