using System.Runtime.InteropServices;
using System.Windows.Forms;
using Anode.Core.Util;

namespace Anode.Daemon;

/// <summary>
/// Hosts the Remote Desktop ActiveX control and points it at a child session.
///
/// This control is the only thing in Anode that can create a seat: Windows exposes no
/// API to spawn a child session directly. You host the RDP client, set
/// "ConnectToChildSession", connect to localhost, and Windows signs the seat in with
/// your existing credentials when Windows can delegate them. An explicit sign-in
/// option lets Windows request credentials without signing out the parent desktop.
/// </summary>
internal sealed class RdpViewer : AxHost
{
    /// <summary>MsRdpClient10NotSafeForScripting: the non-scriptable control shipped with Windows 10 and 11.</summary>
    private const string ControlClsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";

    private ConnectionPointCookie? _cookie;
    private EventSink? _sink;
    private bool _inputEnabled;

    public RdpViewer() : base(ControlClsid)
    {
        Dock = DockStyle.Fill;
    }

    public event Action? Connecting;
    public event Action? Connected;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action<int>? LogonError;
    public event Action<int>? FatalError;
    public event Action<int, int>? RemoteSizeChanged;
    public event Action? AuthenticationPrompt;
    internal bool LoginCompleted { get; private set; }

    internal void CreateHiddenHandle() => CreateHandle();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableWindow(Handle, _inputEnabled);
        // The control exists by now, so mstscax.dll is loaded and can be patched
        // before any connection delivers a pointer update.
        PointerGuard.Track(Handle, _inputEnabled);
        PointerGuard.Install();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (IsHandleCreated) PointerGuard.Untrack(Handle);
        base.OnHandleDestroyed(e);
    }

    /// <summary>The control's automation object, which exists once its handle does.</summary>
    private object Ocx => GetOcx() ?? throw new InvalidOperationException("The Remote Desktop control has not been created.");

    /// <summary>0 = disconnected, 1 = connected, 2 = connecting.</summary>
    public int ConnectionState
    {
        get
        {
            if (!IsHandleCreated) return 0;
            try { return Convert.ToInt32(Dispatch.Get(Ocx, "Connected") ?? 0); }
            catch { return 0; }
        }
    }

    protected override void CreateSink()
    {
        base.CreateSink();
        try
        {
            _sink = new EventSink(this);
            _cookie = new ConnectionPointCookie(Ocx, _sink, typeof(IMsTscAxEvents));
        }
        catch (Exception ex)
        {
            Log.Error("could not subscribe to Remote Desktop control events", ex);
        }
    }

    protected override void DetachSink()
    {
        try { _cookie?.Disconnect(); }
        catch { }
        finally
        {
            _cookie = null;
            _sink = null;
            base.DetachSink();
        }
    }

    /// <summary>Applies every setting the seat needs and starts connecting.</summary>
    public void ConnectToChildSession(SeatOptions options)
    {
        if (ConnectionState != 0) return;

        object control = Ocx;

        Dispatch.Set(control, "Server", "localhost");
        Dispatch.Set(control, "DesktopWidth", Math.Clamp(options.Width, 640, 8192));
        Dispatch.Set(control, "DesktopHeight", Math.Clamp(options.Height, 480, 8192));
        Dispatch.TrySet(control, "ColorDepth", 32);
        Dispatch.TrySet(control, "ConnectingText", "Bringing the Anode seat up...");
        Dispatch.TrySet(control, "DisconnectedText", "The Anode seat is not connected.");

        object advanced = Dispatch.Get(control, "AdvancedSettings9")
            ?? throw new InvalidOperationException("The Remote Desktop control did not return AdvancedSettings9.");

        // Child-session mode supplies the existing interactive identity itself;
        // the control requires CredSSP support for that connection mode.
        Dispatch.Set(advanced, "EnableCredSspSupport", true);
        Dispatch.TrySet(advanced, "AuthenticationLevel", 0);
        Dispatch.TrySet(advanced, "RDPPort", Anode.Core.Session.RdpListener.Port);
        Dispatch.TrySet(advanced, "SmartSizing", options.SmartSizing);
        Dispatch.TrySet(advanced, "DisplayConnectionBar", false);
        Dispatch.TrySet(advanced, "PinConnectionBar", false);
        Dispatch.TrySet(advanced, "ContainerHandledFullScreen", 1);
        Dispatch.TrySet(advanced, "EnableAutoReconnect", true);
        Dispatch.TrySet(advanced, "MaxReconnectAttempts", 8);
        Dispatch.TrySet(advanced, "Compress", 0);
        Dispatch.TrySet(advanced, "EnableWindowsKey", 1);
        Dispatch.TrySet(advanced, "GrabFocusOnConnect", false);

        // Isolation defaults: the seat gets no view of the user's clipboard, drives,
        // printers, ports or smart cards unless it is asked for explicitly.
        Dispatch.TrySet(advanced, "RedirectClipboard", options.ShareClipboard);
        Dispatch.TrySet(advanced, "RedirectDrives", false);
        Dispatch.TrySet(advanced, "RedirectPrinters", false);
        Dispatch.TrySet(advanced, "RedirectPorts", false);
        Dispatch.TrySet(advanced, "RedirectSmartCards", false);

        object secured = Dispatch.Get(control, "SecuredSettings2")
            ?? throw new InvalidOperationException("The Remote Desktop control did not return SecuredSettings2.");

        // 2 = Windows-key shortcuts reach the seat only while the viewer is full screen,
        // so Alt+Tab keeps working normally on the user's own desktop.
        Dispatch.TrySet(secured, "KeyboardHookMode", options.CaptureWindowsKeys ? 1 : 2);
        // 0 = play the seat's audio here, 2 = the seat stays silent.
        Dispatch.TrySet(secured, "AudioRedirectionMode", options.Audio ? 0 : 2);

        var extended = (IMsRdpExtendedSettings)control;
        object childMode = true;
        extended.SetProperty("ConnectToChildSession", ref childMode);
        if (extended.GetProperty("ConnectToChildSession") is not true)
            throw new InvalidOperationException("The Remote Desktop control did not enable child-session mode.");
        TryExtended(extended, "EnableHardwareMode", true);

        ConfigureCredentialPrompt((IMsRdpCredentialPrompt)control, options.PromptForCredentials);

        if (options.ScaleFactor is int scale && scale > 100)
        {
            TryExtended(extended, "DesktopScaleFactor", (uint)Math.Min(500, scale));
            TryExtended(extended, "DeviceScaleFactor", 100u);
        }

        // A hidden/disabled RDP control can still move the real pointer. Never connect
        // (including reconnects) unless the isolation gate has been installed successfully.
        ConnectGuarded(PointerGuard.Install, () =>
        {
            Log.Info($"connecting the viewer to a child session at {options.Width}x{options.Height}");
            Dispatch.Call(control, "Connect");
        });
    }

    internal static void ConnectGuarded(Func<bool> installGuard, Action connect)
    {
        if (!installGuard())
            throw new InvalidOperationException("Anode could not protect your desktop pointer. The viewer was not connected. "
                + "Update Anode and retry; if this continues, see " + Links.Troubleshooting + "#my-real-pointer-jumps-while-something-runs-in-the-seat");
        connect();
    }

    internal string? DescribeDisconnect(int reason)
    {
        try
        {
            object control = Ocx;
            int extended = Convert.ToInt32(Dispatch.Get(control, "ExtendedDisconnectReason") ?? 0);
            string? description = Dispatch.Call(control, "GetErrorDescription", reason, extended) as string;
            if (string.IsNullOrWhiteSpace(description)) return null;
            return $"{description.Replace('\r', ' ').Replace('\n', ' ').Trim()} (extended reason {extended})";
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the Remote Desktop disconnect description: {ex.Message}");
            return null;
        }
    }

    internal static void ConfigureCredentialPrompt(IMsRdpCredentialPrompt prompt, bool enabled)
    {
        // Unattended starts must not open authentication dialogs on the parent
        // desktop. Only --sign-in or the viewer's Sign in action enables the prompt.
        // Never retrieve the password or pass it through CLI, pipes, or logs.
        prompt.SetAllowCredentialSaving(false);
        prompt.SetAllowPromptingForCredentials(enabled);
        prompt.SetPromptForCredsOnClient(enabled);
        prompt.SetPromptForCredentials(enabled);
    }

    public void Disconnect()
    {
        if (!IsHandleCreated || ConnectionState == 0) return;
        try { Dispatch.Call(Ocx, "Disconnect"); }
        catch (Exception ex) { Log.Warn($"viewer disconnect failed: {ex.Message}"); }
    }

    private static void TryExtended(IMsRdpExtendedSettings settings, string name, object value)
    {
        try { settings.SetProperty(name, ref value); }
        catch { /* not every build supports every extended property */ }
    }

    /// <summary>
    /// Blocks or restores mouse and keyboard input to the control without hiding the picture.
    /// The same switch tells <see cref="PointerGuard"/> whether the seat may move the local pointer.
    /// </summary>
    public void SetInputEnabled(bool enabled)
    {
        _inputEnabled = enabled;
        if (!IsHandleCreated) return;
        EnableWindow(Handle, enabled);
        PointerGuard.Track(Handle, enabled);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    /// <summary>Receives the control's events and re-raises them as ordinary .NET events.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class EventSink : IMsTscAxEvents
    {
        private readonly RdpViewer _owner;
        public EventSink(RdpViewer owner) => _owner = owner;

        public void OnConnecting()
        {
            _owner.LoginCompleted = false;
            _owner.Connecting?.Invoke();
        }
        public void OnConnected() => _owner.Connected?.Invoke();
        public void OnLoginComplete()
        {
            _owner.LoginCompleted = true;
            _owner.LoginComplete?.Invoke();
        }
        public void OnDisconnected(int discReason)
        {
            _owner.LoginCompleted = false;
            _owner.Disconnected?.Invoke(discReason);
        }
        public void OnEnterFullScreenMode() { }
        public void OnLeaveFullScreenMode() { }
        public void OnChannelReceivedData(string channelName, string data) { }
        public void OnRequestGoFullScreen() { }
        public void OnRequestLeaveFullScreen() { }
        public void OnFatalError(int errorCode) => _owner.FatalError?.Invoke(errorCode);
        public void OnWarning(int warningCode) => Log.Warn($"Remote Desktop warning {warningCode}");
        public void OnRemoteDesktopSizeChange(int width, int height) => _owner.RemoteSizeChanged?.Invoke(width, height);
        public void OnIdleTimeoutNotification() { }
        public void OnRequestContainerMinimize() { }
        public bool OnConfirmClose() => true;
        public bool OnReceivedTSPublicKey(string publicKey) => true;
        public int OnAutoReconnecting(int disconnectReason, int attemptCount) => 0;
        public void OnAuthenticationWarningDisplayed() => _owner.AuthenticationPrompt?.Invoke();
        public void OnAuthenticationWarningDismissed() { }
        public void OnRemoteProgramResult(string remoteProgramName, int result, bool displayErrorDialog) { }
        public void OnRemoteProgramDisplayed(bool displayed, uint exeStyle) { }
        public void OnLogonError(int errorCode) => _owner.LogonError?.Invoke(errorCode);
        public void OnFocusReleased(int direction) { }
        public void OnUserNameAcquired(string userName) { }
        public void OnMouseInputModeChanged(bool absoluteMouseMode) { }
        public void OnServiceMessageReceived(string serviceMessage) { }
        public void OnRemoteWindowDisplayed(bool displayed, IntPtr hwnd, int windowState) { }
        public void OnConnectionBarPullDown() { }
        public void OnNetworkStatusChanged(uint quality, int bandwidth, int rtt) { }
        public void OnAutoReconnected()
        {
            _owner.Connected?.Invoke();
            // Automatic transport recovery can retain the logged-in session and
            // does not necessarily emit a second OnLoginComplete event.
            if (_owner.LoginCompleted) _owner.LoginComplete?.Invoke();
        }
        public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttempts) { }
        public void OnDevicesButtonPressed() { }
    }
}

/// <summary>How a seat should be created. Set once at `anode up` and kept for reconnects.</summary>
internal sealed record SeatOptions
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool SmartSizing { get; init; } = true;
    public bool Audio { get; init; }
    public bool ShareClipboard { get; init; }
    public bool CaptureWindowsKeys { get; init; }
    public int? ScaleFactor { get; init; }
    public bool StartViewOnly { get; init; } = true;
    public bool ShowWindow { get; init; } = true;
    public bool KeepSeatOnExit { get; init; }
    public bool PromptForCredentials { get; init; }
    public bool ConfigureBackgroundRendering { get; init; } = true;
}
