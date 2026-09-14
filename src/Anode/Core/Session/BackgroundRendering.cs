using System.Text.Json;
using Microsoft.Win32;
using Anode.Core.Util;

namespace Anode.Core.Session;

/// <summary>The RDP client reads this per-user preference when its control is created.</summary>
internal static class BackgroundRendering
{
    private const string KeyPath = @"Software\Microsoft\Terminal Server Client";
    private const string ValueName = "RemoteDesktop_SuppressWhenMinimized";
    private static string BackupPath => Path.Combine(Env.StateDirectory, "rdp-rendering-backup.json");
    private sealed record Backup(int? PreviousValue);

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
        if (!File.Exists(BackupPath)) return "No Anode background-rendering backup exists in this state directory.";
        var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath))
            ?? throw new InvalidOperationException("Invalid background-rendering backup.");
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key.GetValue(ValueName) is not int current || current != 2)
            throw new InvalidOperationException("The RDP preference changed after Anode set it; it was left unchanged.");
        if (backup.PreviousValue is int value) key.SetValue(ValueName, value, RegistryValueKind.DWord);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        File.Delete(BackupPath);
        return "Previous RDP rendering preference restored. Restart RDP clients for it to take effect.";
    }
}
