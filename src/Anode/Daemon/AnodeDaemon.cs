using System.Text.Json.Nodes;
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

    private SeatWindow? _window;
    private JsonPipeServer? _control;
    private JsonPipeClient? _seat;
    private uint? _sessionId;
    private bool _hostReady;
    private string _state = "starting";
    private string _lastError = string.Empty;
    private DateTime _startedUtc = DateTime.UtcNow;

    private AnodeDaemon(SeatOptions options) => _options = options;

    public static int Run(SeatOptions options)
    {
        Log.SetRole("daemon");

        using var single = new Mutex(true, Env.DaemonMutex, out bool acquired);
        if (!acquired)
        {
            Console.Error.WriteLine("Anode is already running. Use `anode status`, or `anode kill` to stop the seat.");
            return 2;
        }

        var checks = Preconditions.Run();
        if (Preconditions.AnyFailed(checks))
        {
            Console.Error.WriteLine("Anode cannot bring a seat up yet:");
            foreach (var check in checks.Where(c => c.State == CheckLevel.Fail))
            {
                Console.Error.WriteLine($"  x {check.Name}: {check.Detail}");
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
        _window = new SeatWindow(_options);
        _window.StopSeatRequested += () => _ = StopSeatAsync("stopped from the viewer");
        _window.ReconnectRequested += () => _ = ReconnectAsync();
        _window.QuitRequested += Quit;
        _window.Viewer.Connected += OnViewerConnected;
        _window.Viewer.Disconnected += OnViewerDisconnected;
        _window.Viewer.LogonError += code => SetState("logon-error", $"The seat could not sign in (error {code}).");
        _window.Viewer.FatalError += code => SetState("error", $"The Remote Desktop control failed (error {code}).");
        _window.Viewer.AuthenticationPrompt += () =>
            _window?.SetStatus("Windows is asking to confirm the connection. Approve it in the viewer once; later seats will not ask.");

        _control = new JsonPipeServer(Env.ControlPipe, HandleControlAsync);
        _control.Start();
        Log.Info($"control pipe listening on \\\\.\\pipe\\{Env.ControlPipe}");

        _window.Shown += (_, _) => BeginConnect();
        _window.Show();
        if (!_options.ShowWindow)
        {
            // Still shown once so the ActiveX control gets a real window to live in,
            // then tucked away. The seat is unaffected either way.
            _window.BeginInvoke(new Action(() => _window!.HideViewer()));
        }

        Application.Run();
        return 0;
    }

    private void BeginConnect()
    {
        try
        {
            SetState("connecting", "Creating the seat...");
            _window!.Viewer.ConnectToChildSession(_options);
        }
        catch (Exception ex)
        {
            Log.Error("could not start the viewer connection", ex);
            SetState("error", $"Could not start the seat: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ viewer events

    private void OnViewerConnected()
    {
        _window?.ClearNoActivate();
        SetState("signing-in", "The seat is connected. Waiting for Windows to finish signing it in...");
        _ = BringUpSeatAsync();
    }

    private void OnViewerDisconnected(int reason)
    {
        string why = RdpDisconnect.Explain(reason);
        uint? stillThere = ChildSession.TryGetId();
        if (stillThere is null)
        {
            _hostReady = false;
            _sessionId = null;
            DisposeSeatClient();
            SetState("stopped", $"The seat is gone. {why}");
        }
        else
        {
            SetState("detached", $"The viewer disconnected but the seat is still running. {why} Press Reconnect to watch it again.");
        }
        _window?.SetSeatInfo(_sessionId, _hostReady);
    }

    // -------------------------------------------------------------- seat bring-up

    private async Task BringUpSeatAsync()
    {
        await _seatGate.WaitAsync().ConfigureAwait(false);
        try
        {
            uint? id = await ChildSession.WaitForIdAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (id is null)
            {
                SetState("error", "Windows connected the viewer but never reported a child session. Run `anode doctor`.");
                return;
            }

            _sessionId = id;
            _window?.SetSeatInfo(_sessionId, false);
            SetState("starting-agent", $"Seat is session {id}. Starting the in-seat agent...");

            // Reuse a seat host that is already listening, in case the viewer merely
            // reconnected to a seat that never went away.
            _seat = await JsonPipeClient.TryConnectAsync(Env.SeatPipe, 500).ConfigureAwait(false);

            if (_seat is null)
            {
                SeatLauncher.LaunchInSession(
                    id.Value,
                    Env.ExecutablePath,
                    "__seat-host",
                    AppContext.BaseDirectory);

                _seat = await ConnectSeatWithRetryAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            }

            if (_seat is null)
            {
                SetState("error", "The seat came up but its agent never answered. Look in %LOCALAPPDATA%\\Anode\\anode.log.");
                return;
            }

            var pong = await _seat.RequestAsync("ping", timeoutMs: 10_000).ConfigureAwait(false);
            if (pong.Bool("ok") != true)
            {
                SetState("error", $"The in-seat agent replied with an error: {pong.Str("error")}");
                return;
            }

            _hostReady = true;
            _window?.SetSeatInfo(_sessionId, true);
            var result = pong.Obj("result");
            var screen = result?.Obj("screen");
            SetState("ready",
                $"Seat ready in session {id} at {screen?.Int("width")}x{screen?.Int("height")}. Programs you start here stay out of your way.");
            Log.Info($"seat ready: session {id}");
        }
        catch (Exception ex)
        {
            Log.Error("bring-up failed", ex);
            SetState("error", $"Bring-up failed: {ex.Message}");
        }
        finally
        {
            _seatGate.Release();
        }
    }

    private static async Task<JsonPipeClient?> ConnectSeatWithRetryAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var client = await JsonPipeClient.TryConnectAsync(Env.SeatPipe, 1000).ConfigureAwait(false);
            if (client is not null) return client;
            await Task.Delay(500).ConfigureAwait(false);
        }
        return null;
    }

    private async Task ReconnectAsync()
    {
        SetState("connecting", "Reconnecting the viewer...");
        _window?.Viewer.Disconnect();
        await Task.Delay(600).ConfigureAwait(false);
        _window?.BeginInvoke(new Action(BeginConnect));
    }

    // ------------------------------------------------------------------ stopping

    private async Task<JsonObject> StopSeatAsync(string why)
    {
        await _seatGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetState("stopping", $"Stopping the seat ({why})...");

            if (_seat is not null)
            {
                // Best effort and short: the seat may be exactly the kind of wedged
                // that made someone press stop, and waiting on it would defeat the point.
                try { await _seat.RequestAsync("shutdown", timeoutMs: 1500).ConfigureAwait(false); } catch { }
                DisposeSeatClient();
            }

            uint? logged = null;
            try { logged = ChildSession.Logoff(wait: true); }
            catch (Exception ex) { Log.Error("logoff failed", ex); _lastError = ex.Message; }

            try { _window?.BeginInvoke(new Action(() => _window!.Viewer.Disconnect())); } catch { }

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

    private async Task<JsonObject> StartSeatAsync()
    {
        if (_hostReady) return JsonLine.Ok(new JsonObject { ["session"] = _sessionId, ["note"] = "already running" });
        _window?.BeginInvoke(new Action(BeginConnect));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (_hostReady) return JsonLine.Ok(new JsonObject { ["session"] = _sessionId });
            if (_state is "error" or "logon-error") return JsonLine.Fail(_lastError);
            await Task.Delay(300).ConfigureAwait(false);
        }
        return JsonLine.Fail("The seat did not become ready in time.");
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

    private async Task<JsonObject> HandleControlAsync(JsonObject request)
    {
        string op = request.Str("op") ?? string.Empty;

        switch (op)
        {
            case "ping":
                return JsonLine.Ok(new JsonObject { ["daemon"] = true, ["state"] = _state });

            case "status":
                return JsonLine.Ok(await StatusAsync().ConfigureAwait(false));

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
        if (seat is null || !_hostReady)
            return JsonLine.Fail($"The seat is not ready ({_state}), so '{op}' has nowhere to go. Try `anode status`.");

        var forwarded = (JsonObject)request.DeepClone();
        forwarded.Remove("id");
        forwarded.Remove("op");

        int timeout = request.Int("timeoutMs") ?? 60_000;
        var response = await seat.RequestAsync(op, forwarded, timeout).ConfigureAwait(false);

        if (response.Bool("ok") != true && !seat.IsConnected)
        {
            _hostReady = false;
            _window?.SetSeatInfo(_sessionId, false);
            SetState("detached", "Lost contact with the in-seat agent.");
        }
        return response;
    }

    private async Task<JsonObject> StatusAsync()
    {
        var status = new JsonObject
        {
            ["state"] = _state,
            ["session"] = _sessionId,
            ["agentReady"] = _hostReady,
            ["viewerConnection"] = _window?.Viewer.ConnectionState ?? 0,
            ["viewerVisible"] = _window?.Visible ?? false,
            ["viewOnly"] = _window?.ViewOnly ?? true,
            ["parentSession"] = ChildSession.CurrentSessionId(),
            ["uptimeSeconds"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1),
            ["lastError"] = string.IsNullOrEmpty(_lastError) ? null : _lastError,
            ["logPath"] = Env.LogPath
        };

        if (_hostReady && _seat is not null)
        {
            var pong = await _seat.RequestAsync("ping", timeoutMs: 5000).ConfigureAwait(false);
            if (pong.Bool("ok") == true) status["seat"] = pong.Obj("result")?.DeepClone();

            var steam = await _seat.RequestAsync("steam.status", timeoutMs: 5000).ConfigureAwait(false);
            if (steam.Bool("ok") == true) status["steam"] = steam.Obj("result")?.DeepClone();
        }

        return status;
    }

    private void SetState(string state, string message)
    {
        _state = state;
        if (state is "error" or "logon-error") _lastError = message;
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
        DisposeSeatClient();
        _control?.Dispose();
        _window?.Dispose();
        _seatGate.Dispose();
    }
}
