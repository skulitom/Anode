using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Core.Session;
using Anode.Core.Util;
using Anode.Daemon;

namespace Anode.Cli;

/// <summary>Private endpoints and disposable files; no seat or machine settings are changed.</summary>
internal static class DiagnosticsChecks
{
    public static string SteamLaunchGuard()
    {
        int launches = 0;
        Process? Launch(ProcessStartInfo _) { launches++; return null; }
        foreach (string path in new[] { @"D:\STEAM\steam.exe", @"D:\STEAM\steam", "steam.exe", "steam", "STEAM://run/410340", "file:///D:/Steam/steam.exe" })
        {
            var rejected = Seat.SeatHost.RunProgram(new JsonObject { ["path"] = path }, 5, Launch, () => new[] { 3, 5 });
            Require(rejected.Bool("ok") == false && rejected.Str("error")!.Contains("session 3") && launches == 0,
                $"run started a conflicting Steam client or URL: {path}");
        }
        foreach (int[] sessions in new[] { Array.Empty<int>(), new[] { 5 } })
            Require(Seat.SeatHost.RunProgram(new JsonObject { ["path"] = "steam.exe" }, 5, Launch, () => sessions).Bool("ok") == true,
                "Steam was refused when no client was running outside the seat");
        Require(launches == 2, "allowed Steam launches did not reach the process launcher");

        var ordinary = new JsonObject { ["path"] = "notepad.exe", ["args"] = new JsonArray("file with spaces.txt") };
        var reply = Seat.SeatHost.RunProgram(ordinary, 5, info =>
        {
            Require(info.ArgumentList.Single() == "file with spaces.txt", "run changed a program argument");
            launches++;
            return null;
        }, () => throw new InvalidOperationException("ordinary program queried Steam"));
        Require(reply.Bool("ok") == true && reply.Obj("result")?.Int("session") == 5 && launches == 3,
            "ordinary program launch was affected by the Steam guard");
        return "direct Steam clients and URLs are refused before process launch when Steam runs outside the seat; no Steam process started";
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    public static async Task<string> Listener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Require((await RdpListener.CheckAsync(port)).State == CheckLevel.Pass, "a live listener was reported missing");
        listener.Stop();
        var absent = await RdpListener.CheckAsync(port);
        Require(absent.State == CheckLevel.Fail && absent.Detail.Contains(port.ToString())
            && absent.Fix!.Contains("0x80070005"), "a closed configured port was reported ready or lacked diagnosis");
        return "live and closed loopback ports are distinguished; missing listeners explain the next checks";
    }

    public static string RdpSettings()
    {
        var type = Type.GetTypeFromCLSID(new Guid("A0C63C30-F08D-4AB4-907C-34905D770C7D"), throwOnError: true)!;
        object control = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create the Windows Remote Desktop control.");
        try
        {
            Dispatch.Set(control, "Server", "localhost");
            var settings = (IMsRdpExtendedSettings)control;
            foreach (bool enabled in new[] { true, false })
            {
                object value = enabled;
                settings.SetProperty("ConnectToChildSession", ref value);
                Require(settings.GetProperty("ConnectToChildSession") is bool actual && actual == enabled,
                    "the child-session setting did not round-trip through the Windows COM interface");
                Require(Dispatch.Get(control, "Server") as string == "localhost",
                    "writing an extended setting changed the server instead");
            }
            var prompt = (IMsRdpCredentialPrompt)control;
            foreach (bool enabled in new[] { true, false })
            {
                RdpViewer.ConfigureCredentialPrompt(prompt, enabled);
                Require(prompt.GetPromptForCredentials() == enabled && !prompt.GetAllowCredentialSaving()
                    && prompt.GetPromptForCredsOnClient() == enabled && prompt.GetAllowPromptingForCredentials() == enabled,
                    "credential prompting was not restricted to explicit sign-in or credential saving was enabled");
            }
            return "real COM child-session and credential-prompt settings round-trip; no connection or credential prompt opened";
        }
        finally { Marshal.FinalReleaseComObject(control); }
    }

    public static async Task<string> ViewerSignIn()
    {
        int hostStarts = 0, logoffs = 0, disconnects = 0;
        using var daemon = new AnodeDaemon(new SeatOptions(), () => 42, () => null,
            () => { hostStarts++; return Task.CompletedTask; },
            _ => { logoffs++; return 42; }, () => disconnects++);
        daemon.OnViewerLogonError(0);
        Require((await daemon.StatusAsync()).Str("state") == "logon-error", "automatic login did not fail");

        await daemon.ReconnectAsync(promptForCredentials: true);
        var status = await daemon.StatusAsync();
        Require(status.Bool("signInPrompt") == true && status.Str("state") == "connecting"
            && status.Str("lastError") is null && logoffs == 0, "Sign in did not recover in place from failed automatic login");
        daemon.OnViewerConnected();
        daemon.OnViewerLogonError(0);
        Require((await daemon.StatusAsync()).Str("state") == "signing-in" && hostStarts == 0,
            "interactive password retry failed or started a host before login");
        daemon.OnViewerLoginComplete();
        Require(hostStarts == 1, "interactive login did not start the host");

        await daemon.ReconnectAsync();
        Require((await daemon.StatusAsync()).Bool("signInPrompt") == false && logoffs == 0,
            "ordinary Reconnect retained the manual prompt or signed the seat out");
        daemon.OnViewerLogonError(0);
        Require((await daemon.StatusAsync()).Str("state") == "logon-error", "ordinary reconnect allowed interactive retries");

        await daemon.ReconnectAsync(promptForCredentials: true);
        await daemon.StopSeatAsync("injected sign-in test; no live seat");
        daemon.OnViewerLoginComplete();
        Require((await daemon.StatusAsync()).Str("state") == "stopped" && hostStarts == 1
            && logoffs == 1 && disconnects == 1, "Stop did not cancel manual sign-in");
        Task<JsonObject> starting = daemon.StartSeatAsync();
        Require((await daemon.StatusAsync()).Bool("signInPrompt") == false,
            "a later unattended start inherited the toolbar's credential prompt");
        daemon.OnViewerLogonError(0);
        Require((await starting.WaitAsync(TimeSpan.FromSeconds(2))).Bool("ok") == false, "unattended startup hid login failure");

        using var blocked = new AnodeDaemon(new SeatOptions(), () => null, () => "listener missing");
        await blocked.ReconnectAsync(promptForCredentials: true);
        Require((await blocked.StatusAsync()).Str("lastError") == "listener missing", "manual sign-in bypassed prerequisites");
        return "manual sign-in retries, login gating, Stop and later unattended startup verified without connecting a viewer";
    }

    public static string RdpHandshakeFailure()
    {
        DateTime started = DateTime.UtcNow;
        object?[] data = { "RDPClient_SSL", 2, "TsSslStateHandshakeStart", 10,
            "TsSslStateDisconnecting", 8, "TsSslEventStartHandshakeFailed", 0x80004005u };
        Require(RdpStartupDiagnostics.DescribeFailure(Environment.ProcessId, started, started, data)?.Contains("0x80004005") == true,
            "a terminal authentication failure lost its Windows error code");
        Require(RdpStartupDiagnostics.DescribeFailure(-1, started, started, data) is null,
            "another RDP client's failure was attributed to Anode");
        Require(RdpStartupDiagnostics.DescribeFailure(Environment.ProcessId, started.AddSeconds(-1), started, data) is null,
            "a previous connection's failure was reused");
        data[6] = "TsSslEventStartHandshake";
        Require(RdpStartupDiagnostics.DescribeFailure(Environment.ProcessId, started, started, data) is null,
            "a nonterminal transition was treated as a failed handshake");
        return "only the current process and attempt's terminal handshake failure is surfaced";
    }

    public static string RdpCallbacks()
    {
        var previous = SynchronizationContext.Current;
        try { return CheckRdpCallbacks(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static string CheckRdpCallbacks()
    {
        using var viewer = new RdpViewer();
        int connections = 0;
        int hostStarts = 0;
        using var daemon = new AnodeDaemon(new SeatOptions { PromptForCredentials = true }, () => 42, () => null,
            () => { hostStarts++; return Task.CompletedTask; });
        viewer.Connecting += () => connections++;
        viewer.Connected += daemon.OnViewerConnected;
        viewer.LoginComplete += daemon.OnViewerLoginComplete;
        viewer.Disconnected += daemon.OnViewerDisconnected;
        viewer.LogonError += daemon.OnViewerLogonError;
        var sink = new RdpViewer.EventSink(viewer);
        IntPtr dispatch = Marshal.GetComInterfaceForObject(sink, typeof(IMsTscAxEvents));
        try
        {
            var invoke = Marshal.GetDelegateForFunctionPointer<EventInvoke>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(dispatch), 6 * IntPtr.Size));
            object? Call(int id, params object[] args)
            {
                const int variantSize = 24; // Anode is x64.
                IntPtr arguments = Marshal.AllocCoTaskMem(variantSize * Math.Max(1, args.Length));
                IntPtr result = Marshal.AllocCoTaskMem(variantSize);
                Marshal.Copy(new byte[variantSize], 0, result, variantSize);
                int initialized = 0;
                try
                {
                    for (; initialized < args.Length; initialized++)
                        Marshal.GetNativeVariantForObject(args[args.Length - 1 - initialized], arguments + initialized * variantSize);
                    var parameters = new System.Runtime.InteropServices.ComTypes.DISPPARAMS
                    { rgvarg = arguments, cArgs = args.Length };
                    Guid iid = Guid.Empty;
                    int hr = invoke(dispatch, id, ref iid, 0, 1, ref parameters, result, IntPtr.Zero, IntPtr.Zero);
                    Marshal.ThrowExceptionForHR(hr);
                    return Marshal.GetObjectForNativeVariant(result);
                }
                finally
                {
                    for (int i = 0; i < initialized; i++) VariantClear(arguments + i * variantSize);
                    VariantClear(result);
                    Marshal.FreeCoTaskMem(result);
                    Marshal.FreeCoTaskMem(arguments);
                }
            }
            Call(1);
            Require(connections == 1, "native IDispatch did not deliver the RDP event");
            Require(Call(15) is true, "OnConfirmClose did not return VARIANT_TRUE");
            Require(Call(16, "test public key") is true, "OnReceivedTSPublicKey did not return VARIANT_TRUE");
            Require(Call(17, 1, 1) is 0, "OnAutoReconnecting did not return its native action");
            Call(2);
            Call(33);
            Require(hostStarts == 0 && !viewer.LoginCompleted,
                "transport connection or reconnect started the host before Windows login");
            Require(daemon.StatusAsync().GetAwaiter().GetResult().Str("state") == "signing-in",
                "transport connection was not reported as waiting for Windows login");
            Call(22, -2); // Winlogon is continuing, despite the event's name.
            Call(22, 0); // Interactive bad-password prompt permits another attempt.
            Call(3);
            Require(hostStarts == 1 && viewer.LoginCompleted, "completed login did not start the host");
            Call(33);
            Require(hostStarts == 2, "automatic recovery of a logged-in viewer did not reconnect the host");
            Call(4, 2055);
            Require(!viewer.LoginCompleted, "disconnect retained the viewer's logged-in state");
            Call(3);
            Require(hostStarts == 2, "a late login event restarted a cancelled startup");
            Call(1);
            Require(!viewer.LoginCompleted, "a new connection reused the previous login state");
            return "native callbacks verified; host waits for login, reconnects after login and ignores cancelled startup";
        }
        finally { Marshal.Release(dispatch); }
    }

    /// <summary>
    /// Creates the hidden view-only viewer the daemon creates, then calls SetCursorPos the
    /// way mstscax.dll does: through its own import table. Never connects to anything.
    /// </summary>
    public static string ViewerPointerGuard()
    {
        int connections = 0;
        try
        {
            RdpViewer.ConnectGuarded(() => false, () => connections++);
            throw new InvalidOperationException("the viewer accepted a missing pointer guard");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("could not protect your desktop pointer")) { }
        Require(connections == 0, "the viewer connected before rejecting the missing guard");
        try { RdpViewer.ConnectGuarded(() => throw new IOException("guard failure"), () => connections++); }
        catch (IOException) { }
        Require(connections == 0, "an installation exception allowed a connection");
        RdpViewer.ConnectGuarded(() => true, () => connections++);
        Require(connections == 1, "a protected viewer could not connect");
        foreach (bool input in new[] { false, true })
            foreach (bool visible in new[] { false, true })
                foreach (bool foreground in new[] { false, true })
                    Require(PointerGuard.Allows(input, visible, foreground) == (input && visible && foreground),
                        "the local pointer may move while the user is not driving a visible, focused viewer");

        var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { finished.TrySetResult(CheckViewerPointerGuard()); }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private static unsafe string CheckViewerPointerGuard()
    {
        using var window = new SeatWindow(new SeatOptions());
        window.CreateHiddenViewer();
        Require(PointerGuard.Installed,
            "mstscax.dll's SetCursorPos import was not found, so a program in the seat can move the real pointer");
        IntPtr[] targets = PointerGuard.ImportTargets();
        Require(targets.Length > 0 && targets.All(PointerGuard.IsGate), "an import of SetCursorPos still reaches user32 directly");
        Require(PointerGuard.Install() && PointerGuard.ImportTargets().SequenceEqual(targets), "reinstalling changed an intact patch");

        long suppressed = PointerGuard.Suppressed, forwarded = PointerGuard.Forwarded;
        bool Moves(IntPtr target)
        {
            // One pixel, so even a broken gate cannot throw the pointer across the screen.
            // Retried because the user may nudge the mouse onto that pixel themselves.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var before = Core.Input.InputInjector.CursorPosition();
                var aim = new System.Drawing.Point(before.X > 0 ? before.X - 1 : before.X + 1, before.Y);
                Require(((delegate* unmanaged<int, int, int>)target)(aim.X, aim.Y) == 1, "a suppressed pointer move reported failure to the control");
                if (Core.Input.InputInjector.CursorPosition() != aim) return false;
            }
            return true;
        }
        foreach (bool viewOnly in new[] { true, false })
        {
            window.ViewOnly = viewOnly;
            foreach (IntPtr target in targets)
                Require(!Moves(target), $"a hidden viewer moved the real pointer (view only: {viewOnly})");
        }
        Require(PointerGuard.Suppressed >= suppressed + 2 * targets.Length && PointerGuard.Forwarded == forwarded,
            "the gate forwarded a pointer move from a hidden viewer");

        // mstscax.dll calls from its own threads, which the runtime has never seen. The gate is
        // shut here (proved above), so the argument a raw thread start cannot supply is unused.
        window.ViewOnly = true;
        suppressed = PointerGuard.Suppressed;
        IntPtr thread = CreateThread(IntPtr.Zero, UIntPtr.Zero, targets[0], IntPtr.Zero, 0, out _);
        Require(thread != IntPtr.Zero, "could not start a native thread");
        try
        {
            Require(WaitForSingleObject(thread, 5000) == 0 && GetExitCodeThread(thread, out uint result) && result == 1,
                "the gate did not answer a call from a native thread");
        }
        finally { CloseHandle(thread); }
        Require(PointerGuard.Suppressed == suppressed + 1 && PointerGuard.Forwarded == forwarded,
            "a native-thread call was not suppressed");
        return $"{targets.Length} mstscax.dll import(s) gated; failed guards prevent connection; hidden managed/native calls cannot move the real pointer";
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EventInvoke(IntPtr self, int id, ref Guid iid, uint locale, ushort flags,
        ref System.Runtime.InteropServices.ComTypes.DISPPARAMS args, IntPtr result, IntPtr exception, IntPtr argumentError);

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(IntPtr variant);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateThread(IntPtr attributes, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static string SetupListener()
    {
        var missing = new Check("listener", CheckLevel.Fail, "listener missing", "inspect event 17");
        var ready = new Check("listener", CheckLevel.Pass, "listener ready");
        bool listening = false;
        int starts = 0, restarts = 0;
        Setup.EnsureListener(() => true, () => listening ? ready : missing,
            () => { restarts++; listening = true; }, () => starts++, TimeSpan.Zero);
        Require(restarts == 1 && starts == 0, "a running service with no listener was not restarted");
        Setup.EnsureListener(() => true, () => ready, () => restarts++, () => starts++, TimeSpan.Zero);
        Require(restarts == 1 && starts == 0, "healthy Remote Desktop was unnecessarily restarted");
        Setup.EnsureListener(() => true, () => ready, () => restarts++, () => starts++, TimeSpan.Zero, forceRestart: true);
        Require(restarts == 2, "changed host settings were not applied to an already running service");
        listening = false;
        Setup.EnsureListener(() => false, () => listening ? ready : missing,
            () => restarts++, () => { starts++; listening = true; }, TimeSpan.Zero);
        Require(starts == 1, "a stopped service was not started");
        try
        {
            Setup.EnsureListener(() => true, () => missing, () => restarts++, () => starts++, TimeSpan.Zero);
            throw new InvalidOperationException("setup succeeded even though the listener never appeared");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("TermService is running, but listener missing")) { }

        Setup.WaitForServiceStop(() => (ServiceControllerStatus.Stopped, 0u), 42, TimeSpan.Zero);
        Setup.WaitForServiceStop(() => (ServiceControllerStatus.Running, 43u), 42, TimeSpan.Zero);
        foreach (uint processId in new uint[] { 42, 0 })
        {
            try
            {
                Setup.WaitForServiceStop(() => (ServiceControllerStatus.Running, processId), 42, TimeSpan.Zero);
                throw new InvalidOperationException("setup accepted a restart without evidence that the original service stopped");
            }
            catch (System.TimeoutException) { }
        }
        return "start, repair, healthy no-op, listener failure, and immediate service reactivation verified without changing services";
    }

    public static async Task<string> StartupFailure()
    {
        foreach (uint? child in new uint?[] { null, 42 })
        {
            using var daemon = new AnodeDaemon(new SeatOptions(), () => child, () => null);
            Task<JsonObject> starting = daemon.StartSeatAsync();
            daemon.OnViewerDisconnected(1800);
            var reply = await starting.WaitAsync(TimeSpan.FromSeconds(2));
            var status = await daemon.StatusAsync();
            Require(reply.Bool("ok") == false && reply.Str("error")!.Contains("1800"), "startup hid the disconnect reason");
            Require(status.Str("state") == "error" && status.Str("lastError") == reply.Str("error"),
                "disconnect did not persist as a startup error");
            daemon.OnViewerDisconnected(1);
            Require((await daemon.StatusAsync()).Str("lastError") == reply.Str("error"), "a later disconnect erased the cause");
        }
        using var blocked = new AnodeDaemon(new SeatOptions(), () => null, () => "listener missing");
        Require((await blocked.StartSeatAsync()).Str("error") == "listener missing", "an existing daemon bypassed readiness checks");
        using var unattended = new AnodeDaemon(new SeatOptions(), () => null, () => null);
        unattended.OnViewerLogonError(0);
        Require((await unattended.StatusAsync()).Str("state") == "logon-error",
            "an unattended bad-password failure was treated as interactive retry");
        return "refused connections fail promptly with the reason, including when a child session already exists";
    }

    public static async Task<string> SeatCleanup()
    {
        Require(ChildSession.Exists(ChildSession.CurrentSessionId()) is true, "session enumeration missed the current session");
        Require(ChildSession.Exists(uint.MaxValue) is false, "session enumeration accepted the no-session sentinel");
        int kills = 0;
        Require(ChildSession.HandleLogoffFailure(42, new Win32Exception(2), () => false, () => ++kills) is null
            && kills == 0, "a reserved but absent session triggered process termination");
        foreach (bool? exists in new bool?[] { true, null })
        {
            try
            {
                ChildSession.HandleLogoffFailure(42, new Win32Exception(2), () => exists, () => 0);
                throw new InvalidOperationException("cleanup hid an unconfirmed logoff failure");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2) { }
        }
        int disconnects = 0;
        using var daemon = new AnodeDaemon(new SeatOptions(), () => 42, () => null,
            logoff: _ => throw new Win32Exception(5), disconnectViewer: () => disconnects++);
        Task<JsonObject> starting = daemon.StartSeatAsync();
        var stopped = await daemon.StopSeatAsync("test");
        Require(disconnects == 1 && stopped.Bool("ok") == false,
            "failed logoff left the viewer connected or lost its error");
        Require((await starting.WaitAsync(TimeSpan.FromSeconds(2))).Bool("ok") == false,
            "stop did not cancel startup after failed logoff");
        return "absent sessions are harmless; uncertain failures remain visible; Stop disconnects despite logoff failure";
    }

    public static async Task<string> StartupPolling()
    {
        foreach (string state in new[] { "error", "logon-error", "stopped", "detached", "transport-failure" })
        {
            string name = $"anode-selftest-{Guid.NewGuid():N}";
            using var server = new JsonPipeServer(name, _ => Task.FromResult(state == "transport-failure"
                ? JsonLine.Fail("connection lost")
                : JsonLine.Ok(new JsonObject { ["state"] = state, ["lastError"] = "refused: 1800" })));
            server.Start();
            using var client = await JsonPipeClient.TryConnectAsync(name, 3000)
                ?? throw new InvalidOperationException("no test pipe");
            try
            {
                await SeatStartup.WaitAsync(client).WaitAsync(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("startup accepted a failure");
            }
            catch (InvalidOperationException ex) when (ex.Message == "refused: 1800" || ex.Message == "connection lost") { }
        }
        return "CLI/MCP startup polling immediately returns terminal states and pipe failures";
    }

    internal static int LogProbe(string[] args)
    {
        var options = new Args(args);
        string marker = options.Value("marker") ?? throw new ArgumentException("log probe needs --marker");
        // Marker is a name, never a path outside the disposable state directory.
        if (marker != Path.GetFileName(marker)) throw new ArgumentException("Invalid log probe marker.");
        Log.SetRole("log-probe");
        Log.Info(marker);
        Env.EnsureStateDirectory();
        string report = Path.Combine(Env.StateDirectory, marker + ".json");
        File.WriteAllText(report + ".tmp", new JsonObject
        {
            ["logPath"] = Env.LogPath, ["logError"] = Log.LastWriteError,
            ["session"] = ChildSession.CurrentSessionId()
        }.ToJsonString());
        File.Move(report + ".tmp", report, overwrite: true);
        return Log.LastWriteError is null ? 0 : 1;
    }

    private static string ProbeArguments(string directory, string marker) =>
        $"__log-probe --state-dir {DaemonLauncher.Quote(directory)} --marker {marker}";

    public static async Task<string> Logging()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"anode-selftest log {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, "anode.log");
        try
        {
            using (var held = new FileStream(log, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var failure = await RunProbe(directory, "locked");
                var result = JsonLine.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "locked.json")))!;
                Require(failure.Code == 1 && failure.Error.Contains("Cannot write Anode log")
                    && result.Str("logError")!.Contains(log), "log write failure was silent or lost the path");
            }
            Require((await RunProbe(directory, "recovered")).Code == 0, "logging did not recover after the file was unlocked");
            Require((await File.ReadAllTextAsync(log)).Contains("[log-probe]") && (await File.ReadAllTextAsync(log)).Contains("recovered"),
                "subprocess did not use its explicit state directory");
            var writers = Enumerable.Range(0, 8).Select(i => RunProbe(directory, $"writer-{i}")).ToArray();
            var results = await Task.WhenAll(writers);
            Require(results.All(r => r.Code == 0), "concurrent log writers failed");
            var lines = await File.ReadAllLinesAsync(log);
            foreach (int i in Enumerable.Range(0, 8))
                Require(lines.Count(line => line.EndsWith($"writer-{i}")) == 1, "a concurrent log line was lost or duplicated");
            return "explicit paths, concurrent subprocess writers, and visible write failures verified";
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(int Code, string Error)> RunProbe(string directory, string marker)
    {
        using var process = Process.Start(new ProcessStartInfo(Env.ExecutablePath, ProbeArguments(directory, marker))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("could not launch the log probe");
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        Require((await output).Length == 0, "log diagnostics polluted stdout");
        return (process.ExitCode, await error);
    }

    public static string ScheduledLogging()
    {
        string directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Anode", $"selftest scheduled log {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string physicalDirectory = Env.ResolveDirectory(directory);
            SeatLauncher.LaunchInSession(ChildSession.CurrentSessionId(), Env.ExecutablePath,
                ProbeArguments(physicalDirectory, "scheduled"), AppContext.BaseDirectory);
            string marker = Path.Combine(directory, "scheduled.json");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(marker) && watch.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(100);
            Require(File.Exists(marker), "the scheduled logging probe never completed");
            var result = JsonLine.Parse(File.ReadAllText(marker))!;
            Require(result.Str("logError") is null, result.Str("logError") ?? "scheduled log failed");
            Require(result.Str("logPath") == Path.Combine(physicalDirectory, "anode.log"), "scheduler lost the log path");
            Require(File.ReadAllText(Path.Combine(directory, "anode.log")).Contains("scheduled"), "scheduled log line is missing");
            Require(result.Int("session") == ChildSession.CurrentSessionId(), "scheduler launched in a different session");
            return $"scheduled Anode in session {result.Int("session")} wrote to the requested log";
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
