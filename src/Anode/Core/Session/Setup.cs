using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using Anode.Core.Util;

namespace Anode.Core.Session;

/// <summary>
/// The one part of Anode that changes machine state. It runs elevated, touches
/// exactly three things, and says out loud what it did:
///
///   1. fDenyTSConnections -> 0, so the machine has a Remote Desktop host at all.
///      Child sessions are loopback RDP; without a host there is nothing to connect to.
///      This does NOT open a firewall port, so nothing on the network can reach it.
///   2. WTSEnableChildSessions(TRUE), the documented switch for the feature.
///   3. Optionally DWMFRAMEINTERVAL = 15, which raises the remote-session frame cap
///      from 30 fps to 60 fps. Needed for anything that moves; costs a reboot.
///   4. Optionally bEnumerateHWBeforeSW = 1, the documented "use the hardware graphics
///      adapter for Remote Desktop sessions" policy. Without it the seat renders on the
///      software adapter, which is fine for a text editor and hopeless for a game.
/// </summary>
internal static class Setup
{
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string WinStationsKey = TerminalServerKey + @"\WinStations";
    private const string TerminalServicesPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    public sealed record Result(List<string> Done, List<string> Skipped, List<string> Failed, bool RebootSuggested);

    /// <summary>Runs the elevated half. Throws when not elevated.</summary>
    public static Result Apply(bool setFrameRate, bool useGpu = false, bool undo = false)
    {
        if (!Preconditions.IsElevated())
            throw new UnauthorizedAccessException("Setup must run elevated.");

        var done = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        bool reboot = false;

        if (undo)
        {
            TryStep(failed, done, "Disable child sessions", () =>
            {
                ChildSession.Logoff();
                ChildSession.DisableFeature();
                return "WTSEnableChildSessions(FALSE)";
            });
            // fDenyTSConnections is deliberately left alone on undo: other software may
            // rely on Remote Desktop, and turning someone's RDP off behind their back is
            // worse than leaving a setting they can flip in Settings.
            skipped.Add("Remote Desktop was left enabled. Turn it off in Settings > System > Remote Desktop if you want it off.");
            return new Result(done, skipped, failed, false);
        }

        if (Preconditions.RemoteDesktopAllowed())
        {
            skipped.Add("Remote Desktop host was already enabled.");
        }
        else
        {
            TryStep(failed, done, "Enable the Remote Desktop host", () =>
            {
                using var key = Registry.LocalMachine.OpenSubKey(TerminalServerKey, writable: true)
                    ?? throw new InvalidOperationException($@"HKLM\{TerminalServerKey} is missing.");
                key.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
                return "fDenyTSConnections = 0 (loopback only; no firewall rule was added)";
            });
            reboot = true;
        }

        TryStep(failed, done, "Start the Remote Desktop Services service", () =>
        {
            using var service = new ServiceController("TermService");
            if (service.Status == ServiceControllerStatus.Running) return "TermService was already running";
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            return "TermService started";
        });

        if (ChildSession.IsFeatureEnabledSafe())
        {
            skipped.Add("Child sessions were already enabled.");
        }
        else
        {
            TryStep(failed, done, "Enable child sessions", () =>
            {
                ChildSession.EnableFeature();
                return "WTSEnableChildSessions(TRUE)";
            });
        }

        if (setFrameRate)
        {
            if (Preconditions.FrameInterval() == 15)
            {
                skipped.Add("DWMFRAMEINTERVAL was already 15.");
            }
            else
            {
                TryStep(failed, done, "Raise the seat frame-rate cap", () =>
                {
                    using var key = Registry.LocalMachine.CreateSubKey(WinStationsKey, writable: true)
                        ?? throw new InvalidOperationException($@"Could not open HKLM\{WinStationsKey}.");
                    key.SetValue("DWMFRAMEINTERVAL", 15, RegistryValueKind.DWord);
                    return "DWMFRAMEINTERVAL = 15 (60 fps cap; takes effect after a reboot)";
                });
                reboot = true;
            }
        }

        if (useGpu)
        {
            if (GpuPreferred())
            {
                skipped.Add("The seat was already set to use the hardware graphics adapter.");
            }
            else
            {
                TryStep(failed, done, "Let the seat use the GPU", () =>
                {
                    using var key = Registry.LocalMachine.CreateSubKey(TerminalServicesPolicyKey, writable: true)
                        ?? throw new InvalidOperationException($@"Could not open HKLM\{TerminalServicesPolicyKey}.");
                    key.SetValue("bEnumerateHWBeforeSW", 1, RegistryValueKind.DWord);
                    return "bEnumerateHWBeforeSW = 1 (hardware graphics adapter for Remote Desktop sessions; takes effect after a reboot)";
                });
                reboot = true;
            }
        }

        return new Result(done, skipped, failed, reboot);
    }

    /// <summary>True when Remote Desktop sessions are already set to prefer a real GPU.</summary>
    public static bool GpuPreferred()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TerminalServicesPolicyKey);
            return key?.GetValue("bEnumerateHWBeforeSW") is int value && value == 1;
        }
        catch { return false; }
    }

    private static void TryStep(List<string> failed, List<string> done, string label, Func<string> step)
    {
        try
        {
            string detail = step();
            done.Add($"{label}: {detail}");
            Log.Info($"setup: {label}: {detail}");
        }
        catch (Exception ex)
        {
            failed.Add($"{label}: {ex.Message}");
            Log.Error($"setup step '{label}' failed", ex);
        }
    }

    /// <summary>
    /// Relaunches this executable elevated to run <c>__apply-setup</c>. The user sees a
    /// UAC prompt and decides; nothing changes if they decline.
    /// </summary>
    public static int RelaunchElevated(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Env.ExecutablePath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return 1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.Error.WriteLine("Administrator approval was declined, so nothing was changed.");
            return 1223;
        }
    }
}
