using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Windows.Forms;
using Anode.Core.Bridge;
using Anode.Core.Display;
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
    private bool _promptForCredentials;
    private int _connectionAttempt;
    private readonly Func<uint?> _findChild;
    private readonly Func<string?> _blockingSummary;
    private readonly Func<Task> _startHost;
    private readonly Func<bool, uint?> _logoff;
    private readonly Action _disconnectViewer;
    private readonly Func<DisplayMode, bool, CancellationToken, Task> _changeDisplay;

    private SeatWindow? _window;
    private JsonPipeServer? _control;
    private System.Threading.Timer? _deskWatch;
    private int _watchingDesk;
    private JsonPipeClient? _seat;
    private uint? _sessionId;
    private bool _hostReady;
    private string _state = "starting";
    private string _lastError = string.Empty;
    private DateTime _startedUtc = DateTime.UtcNow;
    /// <summary>The display the seat showed when it first became ready, which a reset and a lease's end restore.</summary>
    private DisplayMode? _startDisplay;
    /// <summary>The display an agent chose, which every later viewer connection asks for until the seat stops.</summary>
    private DisplayMode? _display;
    private bool _changingDisplay;
    private TaskCompletionSource? _displayLogin;

    internal AnodeDaemon(SeatOptions options, Func<uint?>? findChild = null, Func<string?>? blockingSummary = null,
        Func<Task>? startHost = null, Func<bool, uint?>? logoff = null, Action? disconnectViewer = null,
        Func<DisplayMode, bool, CancellationToken, Task>? changeDisplay = null)
    {
        _options = options;
        _promptForCredentials = options.PromptForCredentials;
        _findChild = findChild ?? ChildSession.TryGetId;
        _blockingSummary = blockingSummary ?? Preconditions.BlockingSummary;
        _startHost = startHost ?? BringUpSeatAsync;
        _logoff = logoff ?? ChildSession.Logoff;
        _disconnectViewer = disconnectViewer ?? (() => _window?.BeginInvoke(new Action(() => _window.Viewer.Disconnect())));
        _changeDisplay = changeDisplay ?? ChangeViewerDisplayAsync;
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

        using var seat = SeatSlot.Claim(out string? taken);
        if (seat is null)
        {
            Console.Error.WriteLine(taken);
            Log.Warn("not starting: " + taken);
            return 3;
        }
        // A seat that ended without its daemon, for example at a restart, left its entries behind.
        ClearEndedPads(null);

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
        _window.SignInRequested += () => _ = ReconnectAsync(promptForCredentials: true);
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
        _deskWatch = new System.Threading.Timer(_ => _ = WatchDeskAsync(), null, 2000, 2000);

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
            SetState("connecting", _promptForCredentials
                ? "Sign in to the seat using the Windows credential dialog. Your current desktop stays signed in."
                : "Creating the seat...");
            if (_promptForCredentials) _window!.ClearNoActivate();
            DateTime started = DateTime.UtcNow;
            int attempt = Interlocked.Increment(ref _connectionAttempt);
            var options = ConnectOptions(_display) with { PromptForCredentials = _promptForCredentials };
            _window!.SetSeatSize(options.Width, options.Height);
            _window.Viewer.ConnectToChildSession(options);
            // During an explicit prompt Windows may retry after a failed credential
            // attempt. Its intermediate event-log failures are not terminal yet.
            if (!_promptForCredentials)
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
            // A reconnect for a new display keeps the seat ready; its login completes the change.
            if (_reconnecting || _changingDisplay) return;
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
            if (_changingDisplay)
            {
                // The seat host never went away, so there is no host to start.
                _displayLogin?.TrySetResult();
                return;
            }
            if (_stopRequested || _reconnecting || _bringUpCancellation.IsCancellationRequested) return;
        }
        // Connected only confirms the RDP transport. A reserved child-session ID
        // can exist before Windows has logged in and cannot host a process yet.
        _ = _startHost();
    }

    internal void OnViewerLogonError(int code)
    {
        lock (_lifecycleGate)
            if (_stopRequested || _reconnecting || _bringUpCancellation.IsCancellationRequested) return;
        // OnLogonError also reports continuing login and dialogs, not only failures:
        // https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-onlogonerror
        if (code is -2 or -4 or -5 or 3)
        {
            Log.Info($"Windows sign-in notification {code}; waiting for completed login");
            return;
        }
        lock (_lifecycleGate)
        {
            if (_changingDisplay)
            {
                _displayLogin?.TrySetException(new InvalidOperationException($"Windows did not sign the viewer back in (error {code})."));
                return;
            }
        }
        if (_promptForCredentials && code is 0 or 1 or 2 or unchecked((int)0xC000006D) or unchecked((int)0xC0000224))
        {
            Log.Warn($"Windows requested another sign-in attempt (code {code})");
            _window?.SetStatus("Windows needs your attention in the sign-in dialog. The seat is not ready yet.");
            return;
        }
        // The daemon is still running, so `anode start --sign-in` would be refused here.
        FailStartup("logon-error", $"The seat could not sign in (error {code}). With Windows Hello or a PIN, the user can open the viewer (`anode show` or the tray icon), click Sign in in its header and enter the account password. See {Links.Troubleshooting}#it-asks-for-a-password-every-time");
    }

    internal void OnViewerDisconnected(int reason)
    {
        string why = $"{_window?.Viewer.DescribeDisconnect(reason) ?? RdpDisconnect.Explain(reason)} (disconnect reason {reason})";
        lock (_lifecycleGate)
        {
            // Disconnect is also raised for our deliberate Stop/Reconnect actions.
            if (_stopRequested || _reconnecting) return;
            // And for a reconnect at a new display, which reports a failed connection itself. The viewer's own
            // disconnect can be reported after the new connection has begun; only an ended connection fails it.
            if (_changingDisplay)
            {
                if (_window?.Viewer.ConnectionState is null or 0) _displayLogin?.TrySetException(new InvalidOperationException(why));
                return;
            }
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
                SetState("detached", $"The viewer disconnected but the seat is still running. {why} Press Reconnect in the viewer (open it with `anode show` or the tray icon) to watch it again.");
            }
        }
        _window?.SetSeatInfo(_sessionId, _hostReady);
    }

    private void FailStartup(string state, string message)
    {
        lock (_lifecycleGate)
        {
            _displayLogin?.TrySetException(new InvalidOperationException(message));
            if (_stopRequested || _reconnecting) return;
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
                    SetState("error", $"Windows connected the viewer but never reported a child session. Run `anode doctor`. See {Links.Troubleshooting}#the-seat-will-not-come-up");
                    return;
                }
                _sessionId = id;
                _window?.SetSeatInfo(_sessionId, false);
                SetState("starting-agent", $"Seat is session {id}. Starting the seat host...");
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
                    $"--channel {Env.Channel} __seat-host --state-dir " + DaemonLauncher.Quote(Env.StateDirectory),
                    AppContext.BaseDirectory);

                _seat = await ConnectSeatWithRetryAsync(TimeSpan.FromSeconds(90), cancel).ConfigureAwait(false);
            }

            lock (_lifecycleGate)
            {
                cancel.ThrowIfCancellationRequested();
                if (_seat is null)
                {
                    SetState("error", $"The seat came up but its host never answered. Log: {Env.LogPath}. See {Links.Troubleshooting}#the-seat-came-up-but-its-host-never-answered");
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
                    SetState("error", $"The seat host replied with an error: {pong.Str("error")}");
                    return;
                }
                if (pong.Obj("result")?.Int("session") != (int)id.Value)
                {
                    DisposeSeatClient();
                    SetState("error", "The seat host answered from a different Windows session. Refusing to send input.");
                    return;
                }
                _hostReady = true;
                _window?.SetSeatInfo(_sessionId, true);
                var screen = DisplayMode.FromJson(pong.Obj("result")?.Obj("screen"));
                // Measured rather than taken from the options: this is what a reset restores.
                _startDisplay ??= screen;
                SetState("ready", $"Seat ready in session {id} at {screen?.ToString() ?? "an unknown size"}. Programs you start here stay out of your way.");
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

    // Called from the toolbar; keep the UI context for all ActiveX operations.
    internal async Task ReconnectAsync(bool promptForCredentials = false)
    {
        int stopVersion;
        lock (_lifecycleGate)
        {
            if (_reconnecting || _changingDisplay || _state == "stopping") return;
            stopVersion = _stopVersion;
            _reconnecting = true;
            // Let an old host startup release the seat gate before retrying.
            _bringUpCancellation.Cancel();
        }
        _window?.SetReconnectEnabled(false);
        CancellationToken cancel = default;
        try
        {
            await _seatGate.WaitAsync();
            try
            {
                lock (_lifecycleGate)
                {
                    if (stopVersion != Volatile.Read(ref _stopVersion)) return;
                    if (_blockingSummary() is { } blocked)
                    {
                        SetState("error", blocked);
                        return;
                    }
                    PrepareStart();
                    _reconnecting = true;
                    _promptForCredentials = promptForCredentials || _options.PromptForCredentials;
                    cancel = _bringUpCancellation.Token;
                    _hostReady = false;
                    DisposeSeatClient();
                    _window?.SetSeatInfo(_sessionId, false);
                    SetState("connecting", "Reconnecting the viewer...");
                }
            }
            finally { _seatGate.Release(); }

            // Disconnect only the viewer, never log off the existing seat.
            _window?.Viewer.Disconnect();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (_window?.Viewer.ConnectionState is > 0)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("The viewer did not disconnect. Try Sign in or Reconnect again.");
                await Task.Delay(50, cancel);
            }
            lock (_lifecycleGate)
            {
                cancel.ThrowIfCancellationRequested();
                if (stopVersion != Volatile.Read(ref _stopVersion)) return;
                _reconnecting = false;
            }
            if (_window is not null) BeginConnect();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("could not reconnect the viewer", ex);
            lock (_lifecycleGate)
                if (stopVersion == Volatile.Read(ref _stopVersion))
                    SetState("error", $"Could not reconnect the seat: {ex.Message}");
        }
        finally
        {
            lock (_lifecycleGate) _reconnecting = false;
            _window?.SetReconnectEnabled(true);
        }
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
            // A new seat starts at the display Anode was started with.
            _display = null;
            _startDisplay = null;
            _window?.SetSeatInfo(null, false);
            SetState("stopped", was is null
                ? "There was no seat to stop."
                : $"Seat {was} was signed out. Everything that was running in it is closed.");

            Log.Info($"seat stopped ({why}); session {was?.ToString() ?? "none"}");
            if (was is not null) ClearEndedPads(was);
            return JsonLine.Ok(new JsonObject { ["stopped"] = was is not null, ["session"] = was });
        }
        finally
        {
            _seatGate.Release();
        }
    }

    /// <summary>
    /// Removes the HidHide entries that kept an ended seat's virtual controllers inside it, and those of
    /// any other session that has ended. The seat host cannot: it ends with its session.
    /// </summary>
    private static void ClearEndedPads(uint? ended) => _ = Task.Run(() =>
    {
        try
        {
            int removed = Core.Gamepad.PadIsolation.ClearEnded(ended);
            if (removed > 0) Log.Info($"removed {removed} HidHide entries that kept an ended seat's virtual controllers inside it");
        }
        catch (Exception ex) { Log.Warn($"could not remove the HidHide entries of an ended seat: {ex.Message}"); }
    });

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
            lock (_lifecycleGate)
            {
                if (!_reconnecting)
                {
                    if (_state is not ("connecting" or "signing-in" or "starting-agent"))
                        _promptForCredentials = _options.PromptForCredentials;
                    _hostReady = false;
                    PrepareStart();
                    SetState("connecting", "Creating the seat...");
                    _window?.BeginInvoke(new Action(() =>
                    {
                        if (_stopRequested || _reconnecting) return;
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
            }
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
        return JsonLine.Fail(_promptForCredentials && _state is "connecting" or "signing-in"
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
                // An agent asking learns whether the desktop is its own, not only who holds it.
                return JsonLine.Ok(await StatusAsync(request.Str("agentId")).ConfigureAwait(false));

            case "seat.identity":
                // Queried by the seat host before it starts accepting any input.
                return JsonLine.Ok(new JsonObject
                {
                    ["session"] = ChildSession.TryGetId(),
                    ["parentSession"] = ChildSession.CurrentSessionId()
                });

            case "seat.display":
                // Asked by the seat host, which holds the desktop lease for the agent changing the display.
                return await DisplayAsync(request).ConfigureAwait(false);

            case "seat.pad-visibility":
                // Asked by the seat host after it plugs in a controller: can this, the user's session, open it?
                return JsonLine.Ok(Core.Gamepad.PadIsolation.DesktopView(
                    (request["devices"] as JsonArray ?? new JsonArray()).Select(device => device?.ToString() ?? "")));

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
                // A client helper acquiring on an agent's behalf passes startSeat=false: only an
                // explicit acquisition may bring a stopped seat back.
                if (request.Str("action") == "acquire" && !_hostReady && request.Bool("startSeat") != false)
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
        {
            // CLI and MCP clients share this answer; the code lets MCP report a stopped seat.
            bool stopped = _state == "stopped";
            var notReady = JsonLine.Fail(stopped
                ? "The seat is stopped. Start it with `anode start` or `anode lease acquire` (agents: seat_lease action=acquire), then observe again."
                : $"The seat is not ready ({_state}), so '{op}' has nowhere to go. Check `anode status` (agents: seat_status).");
            notReady["errorCode"] = stopped ? "seat_stopped" : "seat_not_ready";
            notReady["state"] = _state;
            return notReady;
        }

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
            SetState("detached", "Lost contact with the seat host.");
        }
        return response;
    }

    // ----------------------------------------------------------------- display

    /// <summary>The display a seat starts with: as measured once it is ready, or as Anode was asked for.</summary>
    private DisplayMode StartupDisplay() => _startDisplay ?? new DisplayMode(
        Math.Clamp(_options.Width, DisplayMode.MinWidth, DisplayMode.MaxSide), Math.Clamp(_options.Height, DisplayMode.MinHeight, DisplayMode.MaxSide),
        _options.ScaleFactor is int scale ? Math.Clamp(scale, 100, 500) : 100);

    private SeatOptions ConnectOptions(DisplayMode? display) => display is { } chosen
        ? _options with { Width = chosen.Width, Height = chosen.Height, ScaleFactor = chosen.Scale }
        : _options;

    /// <summary>
    /// The seat host's display requests: with no size, the startup display; otherwise a change made live
    /// (method live) or by reconnecting the viewer at the new size (method reconnect). The seat host confirms
    /// the result on the seat's own screen.
    /// </summary>
    internal async Task<JsonObject> DisplayAsync(JsonObject request)
    {
        if (!request.ContainsKey("width")) return JsonLine.Ok(new JsonObject { ["startup"] = StartupDisplay().ToJson() });
        int width = request.Int("width") ?? 0, height = request.Int("height") ?? 0, scale = request.Int("scale") ?? 0;
        if (DisplayMode.Problem(width, height, scale) is { } problem) return JsonLine.Fail(problem);
        if (request.Str("method") is not ("live" or "reconnect")) return JsonLine.Fail("method must be live or reconnect.");
        var target = new DisplayMode(width, height, scale);
        bool reconnect = request.Str("method") == "reconnect";

        CancellationToken cancel;
        lock (_lifecycleGate)
        {
            if (_stopRequested || !_hostReady) return JsonLine.Fail($"The seat is not ready ({_state}), so its display cannot change.");
            if (_reconnecting || _changingDisplay) return JsonLine.Fail("The viewer is already reconnecting. Change the display once it is back.");
            // Windows could not sign this seat in by itself, so a new connection would need the password again: from a
            // dialog on the user's desktop, in the middle of an agent's task. Never disconnect a viewer that cannot come back.
            if (reconnect && _promptForCredentials)
                return JsonLine.Fail("Reconnecting the viewer at the new size would need the Windows password, because this seat signed in "
                    + "through the credential dialog, so the viewer was left connected. To test this display, start a seat at it: "
                    + $"anode quit (which closes the seat's apps), then anode start --sign-in --width {width} --height {height} --scale {scale}.");
            _changingDisplay = true;
            cancel = _bringUpCancellation.Token;
        }
        _window?.SetReconnectEnabled(false);
        _window?.SetStatus($"Changing the seat's display to {target}...");
        try
        {
            await _changeDisplay(target, reconnect, cancel).ConfigureAwait(false);
            bool recovered;
            lock (_lifecycleGate)
            {
                // Stop resets the display for the next seat; a change finishing alongside it must not undo that.
                if (!_stopRequested) _display = target;
                recovered = _state == "detached" && reconnect;
            }
            Log.Info($"asked for seat display {target} {(reconnect ? "by reconnecting the viewer" : "live")}");
            string message = $"Seat ready in session {_sessionId}. An agent asked for its display to be {target}. Programs you start here stay out of your way.";
            if (recovered) SetState("ready", message);
            else _window?.SetStatus(message);
            return JsonLine.Ok(new JsonObject { ["method"] = reconnect ? "reconnect" : "live", ["display"] = target.ToJson() });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return JsonLine.Fail("The seat was stopped while its display was changing.");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not change the seat display to {target} ({(reconnect ? "reconnect" : "live")}): {ex.Message}");
            var failure = JsonLine.Fail(ex.Message);
            // The seat host then asks for the viewer back at the display the seat had.
            if (ex is ViewerDetachedException) failure["errorCode"] = "viewer_detached";
            return failure;
        }
        finally
        {
            lock (_lifecycleGate) { _changingDisplay = false; _displayLogin = null; }
            _window?.SetReconnectEnabled(true);
        }
    }

    /// <summary>
    /// Changes the display through the viewer. Live, the Remote Desktop control asks Windows for it as a monitor
    /// change. Otherwise the viewer disconnects and connects again at the new size, which keeps the session, its
    /// apps and the seat host's pipe. When that connection fails the seat host, which measured the display the seat
    /// had, asks for a reconnect at it; until one succeeds the viewer is reported detached.
    /// </summary>
    private async Task ChangeViewerDisplayAsync(DisplayMode target, bool reconnect, CancellationToken cancel)
    {
        var window = _window ?? throw new InvalidOperationException("Anode has no viewer to change the display through.");
        if (!reconnect)
        {
            await OnViewerAsync(() => window.Viewer.UpdateDisplay(target)).ConfigureAwait(false);
            return;
        }
        try
        {
            await ConnectViewerAsync(window, target, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            string message = $"The viewer could not reconnect at {target}. {ex.Message}";
            if (await OnViewerAsync(() => window.Viewer.ConnectionState).ConfigureAwait(false) == 1) throw new InvalidOperationException(message, ex);
            SetState("detached", $"The viewer could not reconnect after a display change ({ex.Message}). The seat is still running; "
                + "press Reconnect in the viewer (open it with `anode show` or the tray icon) to watch it again.");
            throw new ViewerDetachedException(message, ex);
        }
    }

    /// <summary>A reconnect for a display change that failed and left the viewer without a connection.</summary>
    private sealed class ViewerDetachedException(string message, Exception inner) : InvalidOperationException(message, inner);

    private async Task ConnectViewerAsync(SeatWindow window, DisplayMode display, CancellationToken cancel)
    {
        lock (_lifecycleGate) _displayLogin = null;
        await OnViewerAsync(() => window.Viewer.Disconnect()).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await OnViewerAsync(() => window.Viewer.ConnectionState).ConfigureAwait(false) > 0)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The viewer did not disconnect.");
            await Task.Delay(50, cancel).ConfigureAwait(false);
        }
        var login = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate) _displayLogin = login;
        // An agent's display change never opens a credential dialog on the user's desktop.
        var options = ConnectOptions(display) with { PromptForCredentials = false };
        await OnViewerAsync(() =>
        {
            window.SetSeatSize(options.Width, options.Height);
            window.Viewer.ConnectToChildSession(options);
        }).ConfigureAwait(false);
        try { await login.Task.WaitAsync(TimeSpan.FromSeconds(45), cancel).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException("Windows did not sign the viewer back in within 45 seconds."); }
    }

    /// <summary>Runs on the viewer's thread, which owns the Remote Desktop control.</summary>
    private Task OnViewerAsync(Action action) => OnViewerAsync(() => { action(); return true; });

    private Task<T> OnViewerAsync<T>(Func<T> action)
    {
        var window = _window ?? throw new InvalidOperationException("Anode has no viewer.");
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.BeginInvoke(new Action(() =>
        {
            try { done.SetResult(action()); }
            catch (Exception ex) { done.SetException(ex); }
        }));
        return done.Task;
    }

    internal async Task<JsonObject> StatusAsync(string? agentId = null)
    {
        var status = new JsonObject
        {
            ["state"] = _state,
            ["channel"] = Env.Channel,
            ["session"] = _sessionId,
            ["agentReady"] = _hostReady,
            ["viewerConnection"] = _window?.Viewer.ConnectionState ?? 0,
            ["loginComplete"] = _window?.Viewer.LoginCompleted ?? false,
            ["signInPrompt"] = _promptForCredentials && _state is "connecting" or "signing-in",
            ["viewerVisible"] = _window?.Visible ?? false,
            ["viewOnly"] = _window?.ViewOnly ?? true,
            ["pointerGuard"] = PointerGuard.Status(),
            ["backgroundRenderingConfigured"] = BackgroundRendering.Current() == 2,
            ["parentSession"] = ChildSession.CurrentSessionId(),
            ["startupDisplay"] = StartupDisplay().ToJson(),
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

            if (await DeskAsync(probe, agentId).ConfigureAwait(false) is { } desk) status["lease"] = desk;
        }

        return status;
    }

    /// <summary>
    /// Who holds the desktop and who is waiting, as <paramref name="agentId"/> sees it (its own lease
    /// is "yours", its place in line is given) or as any agent would; never a token.
    /// </summary>
    private static async Task<JsonObject?> DeskAsync(JsonPipeClient probe, string? agentId = null)
    {
        var response = await probe.RequestAsync("lease", new JsonObject { ["agentId"] = agentId ?? "anode-status", ["action"] = "status" }, 5000).ConfigureAwait(false);
        if (response.Bool("ok") != true || response.Obj("result")?.DeepClone() is not JsonObject desk) return null;
        desk.Remove("agentId");
        desk.Remove("leaseToken");
        if (agentId is null) desk.Remove("queuePosition");
        return desk;
    }

    /// <summary>
    /// Keeps the viewer's footer saying who holds the desktop, while someone can see it. The lease
    /// lives in the seat host, where it also expires, so this asks rather than listening.
    /// </summary>
    private async Task WatchDeskAsync()
    {
        if (_window is not { Visible: true } window || !_hostReady || _stopRequested || Interlocked.Exchange(ref _watchingDesk, 1) == 1) return;
        try
        {
            using var probe = await JsonPipeClient.TryConnectAsync(Env.SeatPipe, 500).ConfigureAwait(false);
            var desk = probe is null ? null : await DeskAsync(probe).ConfigureAwait(false);
            window.SetDesk(desk?.Str("ownerAgentId") is { } owner ? desk.Str("ownerName") ?? owner : null,
                (desk?["waiting"] as JsonArray)?.Count ?? 0, known: desk is not null);
        }
        catch (Exception ex) { Log.Warn($"could not read the desktop lease for the viewer: {ex.Message}"); }
        finally { Volatile.Write(ref _watchingDesk, 0); }
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
        _deskWatch?.Dispose();
        _bringUpCancellation.Cancel();
        DisposeSeatClient();
        _control?.Dispose();
        _window?.Dispose();
        // In-flight handlers may still be unwinding and releasing the semaphore.
    }
}
