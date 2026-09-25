using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Anode.Core.Session;

internal enum CheckLevel { Pass, Warn, Fail }

internal sealed record Check(string Name, CheckLevel State, string Detail, string? Fix = null)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["state"] = State.ToString().ToLowerInvariant(),
        ["detail"] = Detail,
        ["fix"] = Fix
    };
}

/// <summary>
/// Everything that has to be true before a seat can exist, expressed as checks the
/// user can read. `anode doctor` prints these; `anode up` refuses to start when any
/// of them is a hard failure.
/// </summary>
internal static class Preconditions
{
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string WinStationsKey = TerminalServerKey + @"\WinStations";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static string EditionId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        return key?.GetValue("EditionID") as string ?? "unknown";
    }

    public static string ProductName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        string name = key?.GetValue("ProductName") as string ?? "Windows";

        // Windows 11 still reports "Windows 10" here; the build number is the honest one.
        int build = System.Environment.OSVersion.Version.Build;
        if (build >= 22000) name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);

        string? display = key?.GetValue("DisplayVersion") as string;
        return display is null ? $"{name} (build {build})" : $"{name} {display} (build {build})";
    }

    /// <summary>Home editions have no Remote Desktop host, so they cannot host a child session.</summary>
    public static bool IsHomeEdition()
    {
        string edition = EditionId();
        return edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
            || edition.Equals("Starter", StringComparison.OrdinalIgnoreCase)
            || edition.Contains("SingleLanguage", StringComparison.OrdinalIgnoreCase);
    }

    public static bool RemoteDesktopAllowed()
    {
        using var key = Registry.LocalMachine.OpenSubKey(TerminalServerKey);
        return key?.GetValue("fDenyTSConnections") is int deny && deny == 0;
    }

    public static int? FrameInterval()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WinStationsKey);
        return key?.GetValue("DWMFRAMEINTERVAL") as int?;
    }

    public static bool ViGEmBusPresent()
    {
        // The bus driver publishes a root-enumerated device; the client library
        // fails at construction time when it is missing. Constructing and disposing
        // one is the only reliable probe, and it is cheap.
        try
        {
            using var client = new Nefarius.ViGEm.Client.ViGEmClient();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Whether HidHide can keep virtual controllers inside the seat. Reads its settings, changes nothing.</summary>
    private static Check ControllerIsolation()
    {
        const string Name = "Controller isolation";
        const string Install = "Only needed for gamepad control while you use your own games. Install HidHide from "
            + "https://github.com/nefarius/HidHide/releases and restart when it asks.";
        try
        {
            using var hidHide = Gamepad.HidHideDevice.Open(waitMs: 500);
            if (hidHide is null)
                return new Check(Name, CheckLevel.Warn, "HidHide not found; virtual controllers are machine-wide, so games on your desktop read them too", Install);
            if (hidHide.Inverse())
                return new Check(Name, CheckLevel.Warn, "HidHide's application list is inverted, so it cannot keep controllers inside the seat",
                    "Turn off the inverse application list in HidHide's configuration.");
            return new Check(Name, CheckLevel.Pass, hidHide.Active()
                ? "HidHide keeps the seat's virtual controllers inside the seat"
                : "HidHide installed; a seat switches it on when it has nothing else to hide");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new Check(Name, CheckLevel.Pass, "HidHide installed (" + ex.Message + ")");
        }
    }

    public static IReadOnlyList<Check> Run()
    {
        var checks = new List<Check>();

        checks.Add(System.Environment.OSVersion.Version.Major >= 10
            ? new Check("Windows version", CheckLevel.Pass, ProductName())
            : new Check("Windows version", CheckLevel.Fail, ProductName(),
                "Child sessions need Windows 10 or newer."));

        checks.Add(IsHomeEdition()
            ? new Check("Windows edition", CheckLevel.Fail, $"{EditionId()} (Home)",
                "Child sessions need Pro, Enterprise, Education or Server. Home has no Remote Desktop host.")
            : new Check("Windows edition", CheckLevel.Pass, EditionId()));

        checks.Add(RemoteDesktopAllowed()
            ? new Check("Remote Desktop host", CheckLevel.Pass, "fDenyTSConnections = 0")
            : new Check("Remote Desktop host", CheckLevel.Fail, "fDenyTSConnections = 1 (Remote Desktop is off)",
                "Run `anode setup` (sets fDenyTSConnections to 0; opens no firewall port)."));

        checks.Add(RdpListener.Check());

        bool childEnabled;
        try { childEnabled = ChildSession.IsFeatureEnabled(); }
        catch { childEnabled = false; }
        checks.Add(childEnabled
            ? new Check("Child sessions", CheckLevel.Pass, "enabled")
            : new Check("Child sessions", CheckLevel.Fail, "disabled",
                "Run `anode setup` (calls WTSEnableChildSessions; one administrator prompt)."));

        int? interval = FrameInterval();
        checks.Add(interval == 15
            ? new Check("Seat frame rate", CheckLevel.Pass, "DWMFRAMEINTERVAL = 15 (up to 60 fps)")
            : new Check("Seat frame rate", CheckLevel.Warn,
                interval is null ? "DWMFRAMEINTERVAL not set (capped at 30 fps)" : $"DWMFRAMEINTERVAL = {interval}",
                "Optional. Run `anode setup --fps 60` (takes effect after a reboot)."));

        checks.Add(Setup.GpuPreferred()
            ? new Check("Seat graphics", CheckLevel.Pass, "bEnumerateHWBeforeSW = 1 (seat may use the GPU)")
            : new Check("Seat graphics", CheckLevel.Warn, "the seat will render on the software adapter",
                "Optional, and worth it for games. Run `anode setup --gpu` (takes effect after a reboot)."));

        checks.Add(ViGEmBusPresent()
            ? new Check("Virtual gamepad", CheckLevel.Pass, "ViGEm bus driver responds")
            : new Check("Virtual gamepad", CheckLevel.Warn, "ViGEm bus driver not found",
                "Only needed for gamepad control. Install ViGEmBus from https://github.com/nefarius/ViGEmBus/releases"));
        checks.Add(ControllerIsolation());

        uint? existing = ChildSession.TryGetId();
        bool? sessionExists = existing is { } id ? ChildSession.Exists(id) : false;
        checks.Add(new Check("Existing seat", sessionExists is null ? CheckLevel.Warn : CheckLevel.Pass,
            existing is null ? "none" : sessionExists switch
            {
                false => $"none (Windows retained child ID {existing.Value}, but that session is absent)",
                true => $"child session {existing.Value} exists; use `anode status` to check sign-in and host readiness",
                null => $"Windows reports child ID {existing.Value}; could not verify whether that session exists"
            }));

        return checks;
    }

    public static bool AnyFailed(IReadOnlyList<Check> checks) => checks.Any(c => c.State == CheckLevel.Fail);

    /// <summary>
    /// One paragraph naming every blocking problem and its fix, or null when a seat can
    /// start. Callers use this to fail immediately instead of launching a daemon that is
    /// certain to exit, and then blaming the timeout.
    /// </summary>
    public static string? BlockingSummary()
    {
        var failures = Run().Where(c => c.State == CheckLevel.Fail).ToArray();
        if (failures.Length == 0) return null;

        var lines = failures.Select(c => c.Fix is null
            ? $"{c.Name}: {c.Detail}"
            : $"{c.Name}: {c.Detail}. {c.Fix}");
        return "This machine cannot host a seat yet. " + string.Join(" | ", lines);
    }
}
