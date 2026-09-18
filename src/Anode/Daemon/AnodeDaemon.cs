using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Windows.Forms;
using Anode.Core.Bridge;
using Anode.Core.Launch;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Daemon;

/// <summary>
/// Owns the seat for as long as it exists.
///
/// The daemon runs in the user's own session. It brings a child session up through the
/// viewer, drops the seat host into it, and then does very little: it forwards work to
/// the seat host and it can kill the seat. Keeping the lifecycle in one place is what
/// makes the stop button trustworthy, because there is exactly one thing to stop.
/// </summary>
internal sealed class AnodeDaemon : IDisposable
{
    private readonly SeatOptions _options;
    private readonly SemaphoreSlim _seatGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource _bringUpCancellation = new();
    private volatile bool _stopRequested;
    private int _stopVersion;
    private bool _reconnecting;
    private int _connectionAttempt;
    private readonly Func<uint?> _findChild;
    private readonly Func<string?> _blockingSummary;
    private readonly Func<Task> _startHost;
    private readonly Func<bool, uint?> _logoff;
    private readonly Action _disconnectViewer;

    private SeatWindow? _window;
    private JsonPipeServer? _control;
    private JsonPipeClient? _seat;
    private uint? _sessionId;
    private bool _hostReady;
    private string _state = "starting";
    private string _lastError = string.Empty;
    private DateTime _startedUtc = DateTime.UtcNow;

    internal AnodeDaemon(SeatOptions options, Func<uint?>? findChild = null, Func<string?>? blockingSummary = null,
        Func<Task>? startHost = null, Func<bool, uint?>? logoff = null, Action? disconnectViewer = null)
    {
        _options = options;
        _findChild = findChild ?? ChildSession.TryGetId;
        _blockingSummary = blockingSummary ?? Preconditions.BlockingSummary;
        _startHost = startHost ?? BringUpSeatAsync;
        _logoff = logoff ?? ChildSession.Logoff;
        _disconnectViewer = disconnectViewer ?? (() => _window?.BeginInvoke(new Action(() => _window.Viewer.Disconnect())));
    }

    public static int Run(SeatOptions options)
    {
        Log.SetRole("daemon");
        Log.Info($"starting in session {ChildSession.CurrentSessionId()}; log path: {Env.LogPath}");

        using var single = new Mutex(true, Env.DaemonMutex, out bool acquired);
        if (!acquired)
        {
            Console.Error.WriteLine("Anode is already running. Use `anode status`, or `anode kill` to stop the seat.");
            Log.Warn("another daemon already owns the seat");
            return 2;
        }

        var checks = Preconditions.Run();
        if (Preconditions.AnyFailed(checks))
        {
            Console.Error.WriteLine("Anode cannot bring a seat up yet:");
            foreach (var check in checks.Where(c => c.State == CheckLevel.Fail))
            {
                Console.Error.WriteLine($"  x {check.Name}: {check.Detail}");
                Log.Error($"startup blocked: {check.Name}: {check.Detail}. {check.Fix}");
                if (check.Fix is not null) Console.Error.WriteLine($"    {check.Fix}");
            }
            return 3;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var daemon = new AnodeDaemon(options);
        return daemon.Start();
    }

    private int Start()
    {
        // Must precede ActiveX creation. This affects the current user's RDP clients,
        // not machine security or the child-session authentication settings.
        try { if (_options.ConfigureBackgroundRendering) BackgroundRendering.Configure(); }
        catch (Exception ex) { Log.Warn("Background rendering could not be configured: " + ex.Message); }
        _window = new SeatWindow(_options);
        _window.StopSeatRequested += () => _ = StopSeatAsync("stopped from the viewer");
        _window.ReconnectRequested += () => _ = ReconnectAsync();
        _window.QuitRequested += Quit;
        _window.Viewer.Connected += OnViewerConnected;
        _window.Viewer.LoginComplete += OnViewerLoginComplete;
        _window.Viewer.Disconnected += OnViewerDisconnected;
        _window.Viewer.LogonError += OnViewerLogonError;
        _window.Viewer.FatalError += code => FailStartup("error", $"The Remote Desktop control failed (error {code}).");
        _window.Viewer.AuthenticationPrompt += () =>
            _window?.SetStatus("Windows needs your attention in its sign-in dialog. The seat is not ready until sign-in succeeds.");

        _control = new JsonPipeServer(Env.ControlPipe, HandleControlAsync);
        _control.Start();
        Log.Info($"control pipe listening on \\\\.\\pipe\\{Env.ControlPipe}");

        if (_options.ShowWindow)
        {
            _window.Shown += (_, _) => BeginConnect();
            _window.Show();
        }
        else
        {
            _window.CreateHiddenViewer();
            _window.BeginInvoke(new Action(BeginConnect));
        }

        Application.Run();
        return 0;
    }

    private void BeginConnect()
    {
        if (_stopRequested) return;
        try
        {
            SetState("connecting", _options.PromptForCredentials
                ? "Sign in to the seat using the Windows credential dialog. Your current desktop stays signed in."
                : "Creating the seat...");
            if (_options.PromptForCredentials) _window!.ClearNoActivate();
            DateTime started = DateTime.UtcNow;
            int attempt = Interlocked.Increment(ref _connectionAttempt);
            _window!.Viewer.ConnectToChildSession(_options);
            // During an explicit prompt Windows may retry after a failed credential
            // attempt. Its intermediate event-log failures are not terminal yet.
            if (!_options.PromptForCredentials)
                _ = MonitorConnectionAsync(started, attempt, _bringUpCancellation.Token);
        }
        catch (Exception ex)
        {
            Log.Error("could not start the viewer connection", ex);
            SetState("error", $"Could not start the seat: {ex.Message}");
        }
    }

    private async Task MonitorConnectionAsync(DateTime started, int attempt, CancellationToken cancel)
    {
        try
        {
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
            {
                await Task.Delay(500, cancel).ConfigureAwait(false);
                lock (_lifecycleGate)
                    if (attempt != _connectionAttempt || _state != "connecting" || cancel.IsCancellationRequested) return;
                string? failure = RdpStartupDiagnostics.FindFailure(started);
                if (failure is null) continue;
                lock (_lifecycleGate)
                {
                    if (attempt == _connectionAttempt && _state == "connecting" && !cancel.IsCancellationRequested)
                        FailStartup("error", failure);
                }
                return;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warn($"could not inspect the RDP startup event log: {ex.Message}"); }
    }

    // ------------------------------------------------------------ viewer events

    internal void OnViewerConnected()
    {
        lock (_lifecycleGate)
        {
            if (_stopRequested || _bringUpCancellation.IsCancellationRequested)
            {
                _window?.Viewer.Disconnect();
                return;
            }
            _window?.ClearNoActivate();
            SetState("signing-in", "The seat is connected. Waiting for Windows to finish signing it in...");
        }
    }

    internal void OnViewerLoginComplete()
    {
        lock (_lifecycleGate)
        {
            if (_stopRequested || _bringUpCancellation.IsCancellationRequested) return;
        }
        // Connected only confirms the RDP transport. A reserved child-session ID
        // can exist before Windows has logged in and cannot host a process yet.
        _ = _startHost();
    }

    internal void OnViewerLogonError(int code)
    {
        // OnLogonError also reports continuing login and dialogs, not only failures:
        // https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-onlogonerror
        if (code is -2 or -4 or -5 or 3)
        {
            Log.Info($"Windows sign-in notification {code}; waiting for completed login");
            return;
        }
        if (_options.PromptForCredentials && code is 0 or 1 or 2 or unchecked((int)0xC000006D) or unchecked((int)0xC0000224))
        {
            Log.Warn($"Windows requested another sign-in attempt (code {code})");
            _window?.SetStatus("Windows needs your attention in the sign-in dialog. The seat is not ready yet.");
            return;
        }
        FailStartup("logon-error", $"The seat could not sign in (error {code}).");
    }

    internal void OnViewerDisconnected(int reason)
    {
        string why = $"{_window?.Viewer.DescribeDisconnect(reason) ?? RdpDisconnect.Explain(reason)} (disconnect reason {reason})";
        lock (_lifecycleGate)
        {
            // Disconnect is also raised for our deliberate Stop/Reconnect actions.
            if (_stopRequested || _reconnecting) return;
            uint? stillThere = _findChild();
            bool starting = _state is "starting" or "connecting" or "signing-in" or "starting-agent";
            bool failed = _state is "error" or "logon-error";
            if (starting || failed || stillThere is null)
            {
                _bringUpCancellation.Cancel();
                _hostReady = false;
                _sessionId = stillThere;
                DisposeSeatClient();
                if (failed) Log.Info($"disconnect after startup failure: {why}");
                else if (starting) SetState("error", $"The seat could not start. {why}");
                else
                {
                    _lastError = $"The seat is gone. {why}";
                    SetState("stopped", _lastError);
                }
            }
            else
            {
                SetState("detached", $"The viewer disconnected but the seat is still running. {why} Press Reconnect to watch it again.");
            }
        }
        _window?.SetSeatInfo(_sessionId, _hostReady);
    }

    private void FailStartup(string state, string message)
    {
        lock (_lifecycleGate)
        {
            if (_stopRequested) return;
            _bringUpCancellation.Cancel();
            _hostReady = false;
            SetState(state, message);
        }
    }

    // -------------------------------------------------------------- seat bring-up

    private async Task BringUpSeatAsync()
    {
        CancellationToken cancel;
        lock (_lifecycleGate) cancel = _bringUpCancellation.Token;
        bool entered = false;
        try
        {
            await _seatGate.WaitAsync(cancel).ConfigureAwait(false);
            entered = true;
            if (_hostReady && _seat is { IsConnected: true }) return;
            uint? id = await ChildSession.WaitForIdAsync(TimeSpan.FromSeconds(60), cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            lock (_lifecycleGate)
            {
                cancel.ThrowIfCancellationRequested();
                if (id is null)
                {
                    SetState("error", "Windows connected the viewer but never reported a child session. Run `anode doctor`.");
                    return;
                }
                _sessionId = id;
                _window?.SetSeatInfo(_sessionId, false);
                SetState("starting-agent", $"Seat is session {id}. Starting the in-seat agent...");
                DisposeSeatClient();
            }

            // Reuse a host that is already listening when its viewer reconnects.
            _seat = await JsonPipeClient.TryConnectAsync(Env.SeatPipe, 500, cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();

            if (_seat is null)
            {
                SeatLauncher.LaunchInSession(
                    id.Value,
                    Env.ExecutablePath,
                    "__seat-host --state-dir " + DaemonLauncher.Quote(Env.StateDirectory),
                    AppContext.BaseDirectory);

                _seat = await ConnectSeatWithRetryAsync(TimeSpan.FromSeconds(90), cancel).ConfigureAwait(false);
            }

            lock (_lifecycleGate)
            {
                cancel.ThrowIfCancellationRequested();
                if (_seat is null)
                {
                    SetState("error", $"The seat came up but its agent never answered. Look in {Env.LogPath}.");
                    return;
                }
            }

            var pong = await _seat.RequestAsync("ping", timeoutMs: 10_000).WaitAsync(cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            lock (_lifecycleGate)
            {
                cancel.ThrowIfCancellationRequested();
                if (pong.Bool("ok") != true)
                {
                    SetState("error", $"The in-seat agent replied with an error: {pong.Str("error")}");
                    return;
                }
                if (pong.Obj("result")?.Int("session") != (int)id.Value)
                {
                    DisposeSeatClient();
                    SetState("error", "The in-seat agent answered from a different Windows session. Refusing to send input.");
                    return;
                }
                _hostReady = true;
                _window?.SetSeatInfo(_sessionId, true);
                var result = pong.Obj("result");
                var screen = result?.Obj("screen");
                SetState("ready",
                    $"Seat ready in session {id} at {screen?.Int("width")}x{screen?.Int("height")}. Programs you start here stay out of your way.");
            }
            Log.Info($"seat ready: session {id}");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (entered) DisposeSeatClient();
            Log.Info("seat bring-up cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("bring-up failed", ex);
            lock (_lifecycleGate)
                if (!cancel.IsCancellationRequested) SetState("error", $"Bring-up failed: {ex.Message}");
        }
        finally
        {
            if (entered) _seatGate.Release();
        }
    }

    private static async Task<JsonPipeClient?> ConnectSeatWithRetryAsync(TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var client = await JsonPipeClient.TryConnectAsync(Env.SeatPipe, 1000, cancel).ConfigureAwait(false);
            if (client is not null) return client;
            await Task.Delay(500, cancel).ConfigureAwait(false);
        }
        return null;
    }

    private async Task ReconnectAsync()
    {
        await _seatGate.WaitAsync().ConfigureAwait(false);
        try { PrepareStart(); }
        finally { _seatGate.Release(); }
        SetState("connecting", "Reconnecting the viewer...");
        lock (_lifecycleGate) _reconnecting = true;
        _window?.BeginInvoke(new Action(() => _window.Viewer.Disconnect()));
        await Task.Delay(600).ConfigureAwait(false);
        _window?.BeginInvoke(new Action(() =>
        {
            lock (_lifecycleGate) _reconnecting = false;
            BeginConnect();
        }));
    }

    // ------------------------------------------------------------------ stopping

    internal async Task<JsonObject> StopSeatAsync(string why)
    {
        Interlocked.Increment(ref _stopVersion);
        CancelBringUp();
        // Close pending Windows prompts even if logoff fails or the session ID was
        // only reserved. Posting this first also prevents a late login completing.
        try { _disconnectViewer(); }
        catch (Exception ex) { Log.Warn($"could not request viewer disconnect: {ex.Message}"); }
        await _seatGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelBringUp();
            SetState("stopping", $"Stopping the seat ({why})...");

            if (_seat is not null)
            {
                // Best effort and short: the seat may be exactly the kind of wedged
                // that made someone press stop, and waiting on it would defeat the point.
                try { await _seat.RequestAsync("shutdown", timeoutMs: 1500).ConfigureAwait(false); } catch { }
                DisposeSeatClient();
            }

            uint? logged = null;
            try { logged = _logoff(true); }
            catch (Exception ex)
            {
                Log.Error("logoff failed", ex);
                _hostReady = false;
                SetState("error", $"Could not stop the seat: {ex.Message}");
                return JsonLine.Fail(_lastError);
            }

            _hostReady = false;
            uint? was = _sessionId ?? logged;
            _sessionId = null;
            _window?.SetSeatInfo(null, false);
            SetState("stopped", was is null
                ? "There was no seat to stop."
                : $"Seat {was} was signed out. Everything that was running in it is closed.");

            Log.Info($"seat stopped ({why}); session {was?.ToString() ?? "none"}");
            return JsonLine.Ok(new JsonObject { ["stopped"] = was is not null, ["session"] = was });
        }
        finally
        {
            _seatGate.Release();
        }
    }

    internal async Task<JsonObject> StartSeatAsync(int? expectedStopVersion = null)
    {
        await _seatGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (expectedStopVersion is int version && version != Volatile.Read(ref _stopVersion))
                return JsonLine.Fail("The seat was stopped while this acquisition was pending. Request a new lease when desktop work should resume.");
            if (_hostReady && _seat is { IsConnected: true })
                return JsonLine.Ok(new JsonObject { ["session"] = _sessionId, ["note"] = "already running" });
            if (_blockingSummary() is { } blocked)
            {
                SetState("error", blocked);
                return JsonLine.Fail(blocked);
            }
            _hostReady = false;
            PrepareStart();
            SetState("connecting", "Creating the seat...");
            _window?.BeginInvoke(new Action(() =>
            {
                if (_stopRequested) return;
                // A pipe timeout does not disconnect the viewer. Reconnect the
                // host directly instead of waiting for an RDP event that won't fire.
                if (_window.Viewer.ConnectionState == 1)
                {
                    if (_window.Viewer.LoginCompleted) _ = _startHost();
                    else OnViewerConnected();
                }
                else BeginConnect();
            }));
        }
        finally { _seatGate.Release(); }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (_stopRequested) return JsonLine.Fail("The seat was stopped before startup completed.");
            if (_hostReady) return JsonLine.Ok(new JsonObject { ["session"] = _sessionId });
            if (_state is "error" or "logon-error" or "stopped" or "detached")
                return JsonLine.Fail(string.IsNullOrEmpty(_lastError) ? $"Seat startup ended in state '{_state}'." : _lastError);
            await Task.Delay(300).ConfigureAwait(false);
        }
        return JsonLine.Fail(_options.PromptForCredentials && _state is "connecting" or "signing-in"
            ? "Windows has not finished signing in. Complete the credential dialog in Anode, then check `anode status`."
            : "The seat did not become ready in time.");
    }

    private void PrepareStart()
    {
        lock (_lifecycleGate)
        {
            if (_bringUpCancellation.IsCancellationRequested)
            {
                _bringUpCancellation.Dispose();
                _bringUpCancellation = new CancellationTokenSource();
            }
            _stopRequested = false;
            _reconnecting = false;
            _lastError = string.Empty;
        }
    }

    private void CancelBringUp()
    {
        lock (_lifecycleGate)
        {
            _stopRequested = true;
            _bringUpCancellation.Cancel();
        }
    }

    private void Quit()
    {
        _ = Task.Run(async () =>
        {
            if (!_options.KeepSeatOnExit) await StopSeatAsync("Anode is quitting").ConfigureAwait(false);
            try
            {
                _window?.BeginInvoke(new Action(() =>
                {
                    _window!.AllowClose();
                    Application.Exit();
                }));
            }
            catch { Application.Exit(); }
        });
    }

    // ------------------------------------------------------------- control pipe

    internal async Task<JsonObject> HandleControlAsync(JsonObject request)
    {
        string op = request.Str("op") ?? string.Empty;
        int stopVersion = Volatile.Read(ref _stopVersion);
        if (Core.Agents.AgentAccess.Validate(request) is { } invalid) return JsonLine.Fail(invalid);
        if (Mcp.Tools.ValidateOperation(op, Core.Agents.AgentAccess.Arguments(request)) is { } badArgs) return JsonLine.Fail(badArgs);

        switch (op)
        {
            case "ping":
                return JsonLine.Ok(new JsonObject { ["daemon"] = true, ["state"] = _state });

            case "status":
                return JsonLine.Ok(await StatusAsync().ConfigureAwait(false));

            case "seat.identity":
                // Queried by the seat host before it starts accepting any input.
                return JsonLine.Ok(new JsonObject
                {
                    ["session"] = ChildSession.TryGetId(),
                    ["parentSession"] = ChildSession.CurrentSessionId()
                });

            case "doctor":
            {
                var array = new JsonArray();
                foreach (var check in Preconditions.Run()) array.Add(check.ToJson());
                return JsonLine.Ok(new JsonObject { ["checks"] = array });
            }

            case "seat.stop":
            case "kill":
                return await StopSeatAsync(request.Str("reason") ?? "requested").ConfigureAwait(false);

            case "seat.start":
                return await StartSeatAsync().ConfigureAwait(false);

            case "lease":
                if (_stopRequested && _state != "stopped")
                    return JsonLine.Fail("The seat is stopping or has failed to stop. Wait for Stop to finish before acquiring a desktop lease.");
                if (request.Str("action") == "acquire" && !_hostReady)
                {
                    var started = await StartSeatAsync(stopVersion).ConfigureAwait(false);
                    if (started.Bool("ok") != true) return started;
                }
                return await ForwardToSeatAsync(op, request).ConfigureAwait(false);

            case "seat.show":
                _window?.ShowViewer();
                return JsonLine.Ok();

            case "seat.hide":
                _window?.HideViewer();
                return JsonLine.Ok();

            case "seat.control":
            {
                bool viewOnly = request.Bool("viewOnly") ?? true;
                _window?.BeginInvoke(new Action(() => _window!.ViewOnly = viewOnly));
                return JsonLine.Ok(new JsonObject { ["viewOnly"] = viewOnly });
            }

            case "quit":
                Quit();
                return JsonLine.Ok();

            default:
                return await ForwardToSeatAsync(op, request).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Anything the daemon does not own is the seat's business. Forwarding by default
    /// means the seat can grow new operations without the daemon learning about them.
    /// </summary>
    private async Task<JsonObject> ForwardToSeatAsync(string op, JsonObject request)
    {
        var seat = _seat;
        if (seat is null || !_hostReady || _stopRequested)
            return JsonLine.Fail($"The seat is not ready ({_state}), so '{op}' has nowhere to go. Try `anode status`.");

        var forwarded = (JsonObject)request.DeepClone();
        forwarded.Remove("id");
        forwarded.Remove("op");

        int timeout = request.Int("timeoutMs") ?? 60_000;
        // Ownership renewal/status and owned job reads must not queue behind a desktop action.
        // The verified host enforces the same lease rules on every connection.
        if (op is "lease" or "exec.read")
        {
            var clock = Stopwatch.StartNew();
            using var independent = await JsonPipeClient.TryConnectAsync(Env.SeatPipe, Math.Min(1000, timeout)).ConfigureAwait(false);
            int remaining = timeout - (int)clock.ElapsedMilliseconds;
            if (independent is null || remaining <= 0) return JsonLine.Fail("Could not reach the seat host within the request deadline.");
            return await independent.RequestAsync(op, forwarded, remaining).ConfigureAwait(false);
        }
        var response = await seat.RequestAsync(op, forwarded, timeout).ConfigureAwait(false);

        if (response.Bool("ok") != true && !seat.IsConnected && ReferenceEquals(_seat, seat) && !_stopRequested)
        {
            _hostReady = false;
            _window?.SetSeatInfo(_sessionId, false);
            SetState("detached", "Lost contact with the in-seat agent.");
        }
        return response;
    }

    internal async Task<JsonObject> StatusAsync()
    {
        var status = new JsonObject
        {
            ["state"] = _state,
            ["session"] = _sessionId,
            ["agentReady"] = _hostReady,
            ["viewerConnection"] = _window?.Viewer.ConnectionState ?? 0,
            ["loginComplete"] = _window?.Viewer.LoginCompleted ?? false,
            ["signInPrompt"] = _options.PromptForCredentials && _state is "connecting" or "signing-in",
            ["viewerVisible"] = _window?.Visible ?? false,
            ["viewOnly"] = _window?.ViewOnly ?? true,
            ["pointerGuard"] = PointerGuard.Status(),
            ["backgroundRenderingConfigured"] = BackgroundRendering.Current() == 2,
            ["parentSession"] = ChildSession.CurrentSessionId(),
            ["uptimeSeconds"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1),
            ["lastError"] = string.IsNullOrEmpty(_lastError) ? null : _lastError,
            ["logPath"] = Env.LogPath,
            ["logError"] = Log.LastWriteError
        };

        if (_hostReady && !_stopRequested && _seat is not null)
        {
            // Diagnostics must not queue behind a long input operation or close its
            // connection if the diagnostic times out. Use a disposable probe pipe.
            using var probe = await JsonPipeClient.TryConnectAsync(Env.SeatPipe, 500).ConfigureAwait(false);
            if (probe is null) return status;
            var pong = await probe.RequestAsync("ping", timeoutMs: 5000).ConfigureAwait(false);
            if (pong.Obj("result")?.Int("session") != (int?)_sessionId) return status;
            if (pong.Bool("ok") == true) status["seat"] = pong.Obj("result")?.DeepClone();

            var steam = await probe.RequestAsync("steam.status", timeoutMs: 5000).ConfigureAwait(false);
            if (steam.Bool("ok") == true) status["steam"] = steam.Obj("result")?.DeepClone();
        }

        return status;
    }

    private void SetState(string state, string message)
    {
        _state = state;
        if (state is "error" or "logon-error") _lastError = message;
        else if (state == "ready") _lastError = string.Empty;
        _window?.SetStatus(message);
        _window?.SetHeadline(state);
        Log.Info($"[{state}] {message}");
    }

    private void DisposeSeatClient()
    {
        try { _seat?.Dispose(); } catch { }
        _seat = null;
    }

    public void Dispose()
    {
        _bringUpCancellation.Cancel();
        DisposeSeatClient();
        _control?.Dispose();
        _window?.Dispose();
        // In-flight handlers may still be unwinding and releasing the semaphore.
    }
}
