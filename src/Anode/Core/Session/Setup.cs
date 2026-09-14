using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Anode.Core.Util;

namespace Anode.Core.Session;

/// <summary>
/// The one part of Anode that changes machine state. It runs elevated, touches
/// the following things, and says out loud what it did:
///
///   1. fDenyTSConnections -> 0, so the machine has a Remote Desktop host at all.
///      Child sessions are loopback RDP; without a host there is nothing to connect to.
///      Starts or restarts TermService when needed to create the listener.
///      This does not add a firewall rule; existing rules determine network access.
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
        bool hostChanged = false;

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
                hostChanged = true;
                return "fDenyTSConnections = 0 (no firewall rule was added; existing rules determine network access)";
            });
        }

        TryStep(failed, done, "Make the Remote Desktop listener available", () =>
        {
            using var service = new ServiceController("TermService");
            return EnsureListener(
                () => service.Status == ServiceControllerStatus.Running,
                RdpListener.Check,
                () => RestartService(service),
                () => StartService(service), forceRestart: hostChanged);
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

    internal static string EnsureListener(Func<bool> isRunning, Func<Check> probe, Action restart, Action start,
        TimeSpan? timeout = null, bool forceRestart = false)
    {
        bool running = isRunning();
        if (running && !forceRestart && probe().State == CheckLevel.Pass) return "TermService and its loopback listener were already ready";
        if (running) restart();
        else start();

        var watch = Stopwatch.StartNew();
        Check listener;
        do
        {
            listener = probe();
            if (listener.State == CheckLevel.Pass)
                return $"TermService {(running ? "restarted" : "started")}; {listener.Detail}";
            if (watch.Elapsed >= (timeout ?? TimeSpan.FromSeconds(20))) break;
            Thread.Sleep(250);
        } while (true);
        throw new InvalidOperationException($"TermService is running, but {listener.Detail}. {listener.Fix}");
    }

    private static void StartService(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.StopPending)
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        if (service.Status == ServiceControllerStatus.Paused) service.Continue();
        else if (service.Status == ServiceControllerStatus.Stopped)
        {
            try { service.Start(); }
            // A COM activation can start the service between Refresh and Start.
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1056 }) { }
        }
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    private static void RestartService(ServiceController service)
    {
        // Stop also stops dependent services. Restore those that were running,
        // even when restarting TermService fails partway through.
        var dependents = new Dictionary<string, ServiceController>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        try
        {
            CollectDependents(service, dependents);
            var running = dependents.Values.Where(s => s.Status == ServiceControllerStatus.Running).ToArray();
            try
            {
                uint previousProcess = ReadServiceState(service).ProcessId;
                service.Stop();
                WaitForServiceStop(() => ReadServiceState(service), previousProcess);
            }
            catch (Exception ex) { failures.Add($"stopping TermService: {ex.Message}"); }
            try { StartService(service); }
            catch (Exception ex) { failures.Add($"starting TermService: {ex.Message}"); }
            foreach (var dependent in running)
            {
                try { StartService(dependent); }
                catch (Exception ex) { failures.Add($"restoring {dependent.ServiceName}: {ex.Message}"); }
            }
        }
        finally { foreach (var dependent in dependents.Values) dependent.Dispose(); }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }

    internal static void WaitForServiceStop(Func<(ServiceControllerStatus Status, uint ProcessId)> readState,
        uint previousProcess, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            var state = readState();
            // DCOM may immediately reactivate TermService. A replacement process
            // proves that the old instance stopped even if polling missed Stopped.
            if (state.Status == ServiceControllerStatus.Stopped
                || (previousProcess != 0 && state.ProcessId != 0 && state.ProcessId != previousProcess)) return;
            if (watch.Elapsed >= (timeout ?? TimeSpan.FromSeconds(20)))
                throw new System.TimeoutException("TermService did not stop or restart within the deadline.");
            Thread.Sleep(100);
        } while (true);
    }

    private static (ServiceControllerStatus Status, uint ProcessId) ReadServiceState(ServiceController service)
    {
        if (!QueryServiceStatusEx(service.ServiceHandle, 0, out var status,
            Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return ((ServiceControllerStatus)status.CurrentState, status.ProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeHandle service, int infoLevel,
        out ServiceStatusProcess status, int bufferSize, out uint bytesNeeded);

    private static void CollectDependents(ServiceController service, Dictionary<string, ServiceController> dependents)
    {
        foreach (var dependent in service.DependentServices)
        {
            if (!dependents.TryAdd(dependent.ServiceName, dependent)) dependent.Dispose();
            else CollectDependents(dependent, dependents);
        }
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
