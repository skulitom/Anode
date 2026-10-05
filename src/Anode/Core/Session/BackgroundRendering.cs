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
    /// --state-dir was given, any in a packaged app's Anode folder. A desktop app installed as an MSIX package, such as
    /// the Claude or Codex app, sees AppData through its package, so a daemon it starts keeps its state, this backup
    /// included, under %LOCALAPPDATA%\Packages\&lt;app&gt;\LocalCache\Local\Anode (see <c>Env.ResolveDirectory</c>).
    /// </summary>
    internal static List<string> Backups(string stateDirectory, bool chosen, string localAppData, string folderName)
    {
        string own = Path.Combine(stateDirectory, BackupFile);
        if (File.Exists(own)) return new() { own };
        var found = new List<string>();
        if (chosen) return found;
        try
        {
            foreach (string package in Directory.EnumerateDirectories(Path.Combine(localAppData, "Packages")))
            {
                string candidate = Path.Combine(package, "LocalCache", "Local", folderName, BackupFile);
                if (File.Exists(candidate)) found.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
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
        var backups = Backups(Env.StateDirectory, Env.StateDirectoryChosen, Env.LocalAppData, Env.StateFolderName);
        if (backups.Count == 0)
            return $"No Anode background-rendering backup exists in {Env.StateDirectory}"
                + (Env.StateDirectoryChosen ? "." : ", or in the Anode folder of a packaged app such as the Claude or Codex desktop app.");
        if (backups.Count > 1)
            throw new InvalidOperationException("Daemons started by different packaged apps each saved a background-rendering backup:"
                + string.Concat(backups.Select(path => $"{System.Environment.NewLine}  {path} (saved {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm})"))
                + $"{System.Environment.NewLine}Nothing was changed. The newest holds the value from just before Anode last changed it; "
                + "restore it with anode rendering --restore --state-dir \"<its folder>\".");
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
}
