using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Anode.Core.Native;
using Anode.Core.Util;

namespace Anode.Daemon;

/// <summary>
/// The window you watch the seat through, and the place the stop button lives.
///
/// It deliberately does not own the seat. Closing it hides it and leaves the seat
/// running; the seat only ends when someone says so, through the Stop seat button,
/// the tray menu, the global hotkey or `anode kill`. That separation is the point:
/// an agent working in the seat should not care whether anyone is looking.
/// </summary>
internal sealed class SeatWindow : Form
{
    private const int HotkeyId = 0xA0DE;

    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _stopButton = new();
    private readonly ToolStripButton _controlButton = new();
    private readonly ToolStripButton _fullScreenButton = new();
    private readonly ToolStripButton _reconnectButton = new();
    private readonly ToolStripButton _signInButton = new();
    private readonly ToolStripButton _detailsButton = new();
    private readonly ToolStripLabel _headline = new();
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _seatLabel = new();
    private readonly ToolStripStatusLabel _deskLabel = new();
    private readonly ToolStripStatusLabel _modeLabel = new();
    private readonly TextBox _details = new();
    private readonly Panel _detailsPanel = new();
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _traySignIn = new("Sign in…");
    private readonly Icon? _icon = LoadIcon();
    private readonly SeatPlaceholder _placeholder;
    /// <summary>Where the seat shows: the viewer's dark background around a control of the seat's shape.</summary>
    private readonly Panel _seatArea = new();
    private readonly bool _fitSeat;
    private Size _seatSize;
    private Control? _standIn;
    private bool _fitted;
    private Icon? _trayIcon;
    private bool _seatPictureLive;

    private FormWindowState _restoreState = FormWindowState.Normal;
    private FormBorderStyle _restoreBorder = FormBorderStyle.Sizable;
    private Rectangle _restoreBounds;
    private bool _fullScreen;
    private bool _viewOnly = true;
    private bool _noActivate = true;
    private bool _reallyClosing;
    private bool _hotkeyAvailable;
    private string _state = "starting";
    private uint? _sessionId;

    public RdpViewer Viewer { get; }

    public event Action? StopSeatRequested;
    public event Action? QuitRequested;
    public event Action? ReconnectRequested;
    public event Action? SignInRequested;

    /// <param name="trayIcon">False only for design previews, which must not add a tray icon.</param>
    public SeatWindow(SeatOptions options, bool trayIcon = true)
    {
        Viewer = new RdpViewer();
        Viewer.TabIndex = 1;
        _placeholder = new SeatPlaceholder(_icon) { Dock = DockStyle.Fill };
        Viewer.Connected += () => SetSeatPictureLive(true);
        Viewer.Disconnected += _ => SetSeatPictureLive(false);
        _seatSize = new Size(Math.Clamp(options.Width, 640, 8192), Math.Clamp(options.Height, 480, 8192));
        // Scaled to fit, the seat keeps its shape inside the control, and the control pads any other
        // shape with white bars. Keep the control at the seat's shape; the dark area fills the rest.
        _fitSeat = options.SmartSizing;
        _seatArea.Name = "SeatArea";
        _seatArea.Dock = DockStyle.Fill;
        if (_fitSeat) Viewer.Dock = DockStyle.None;
        _seatArea.Controls.Add(Viewer);
        _seatArea.Resize += (_, _) => PlaceSeat();
        Viewer.RemoteSizeChanged += (width, height) =>
        {
            if (width <= 0 || height <= 0) return;
            _seatSize = new Size(width, height);
            PlaceSeat();
        };

        Text = "Anode seat" + Env.ChannelSuffix;
        AccessibleName = "Anode seat viewer" + Env.ChannelSuffix;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(560, 360);
        BackColor = ViewerTheme.Background;
        ForeColor = ViewerTheme.Text;
        Icon = _icon ?? SystemIcons.Application;
        KeyPreview = true;
        _noActivate = true;

        BuildToolbar();
        BuildStatusBar();
        BuildTray(trayIcon);
        _details.Name = "SeatDetails";
        _details.AccessibleName = "Seat status details";
        _details.AccessibleDescription = "Full seat status. Select text and press Ctrl+C to copy.";
        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.None;
        _details.BorderStyle = BorderStyle.None;
        _details.Dock = DockStyle.Fill;
        _details.Font = ViewerTheme.UiFont();
        _details.Resize += (_, _) => FitDetailsScrollBar();
        _detailsPanel.Name = "SeatDetailsPanel";
        _detailsPanel.TabIndex = 2;
        _detailsPanel.Dock = DockStyle.Bottom;
        _detailsPanel.Height = 156;
        _detailsPanel.Padding = new Padding(12, 10, 12, 10);
        _detailsPanel.Visible = false;
        _detailsPanel.Controls.Add(_details);
        ApplyTheme();

        // In front of the viewer and filling the same space until the seat picture is live.
        Controls.Add(_placeholder);
        Controls.Add(_seatArea);
        Controls.Add(_detailsPanel);
        Controls.Add(_toolbar);
        Controls.Add(_statusBar);
        FitToSeat();

        ViewOnly = options.StartViewOnly;
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// Sizes the window so its seat area has the seat's shape: the seat at its own size (1:1 at 100% scaling)
    /// when that fits the screen, otherwise the largest size of that shape that does. The frame, toolbar and
    /// status bar are measured rather than assumed, so the seat fills the area with nothing beside it.
    /// </summary>
    private void FitToSeat()
    {
        var work = (IsHandleCreated ? Screen.FromHandle(Handle) : Screen.PrimaryScreen)?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        PerformLayout();
        var chrome = Size - _seatArea.ClientSize;
        float dpi = DeviceDpi / 96f;
        float room = Math.Min((work.Width - 80 - chrome.Width) / (_seatSize.Width * dpi), (work.Height - 80 - chrome.Height) / (_seatSize.Height * dpi));
        float scale = dpi * Math.Clamp(room, 0.1f, 1f);
        Size = new Size(Math.Max(MinimumSize.Width, (int)Math.Round(_seatSize.Width * scale) + chrome.Width),
            Math.Max(MinimumSize.Height, (int)Math.Round(_seatSize.Height * scale) + chrome.Height));
        Location = new Point(work.Right - Width - 24, work.Bottom - Height - 24);
    }

    /// <summary>
    /// Centres the control, at the seat's shape, in the seat area. Smart sizing scales the seat to the control,
    /// so the seat is never padded with the control's own white bars; any other space shows the dark area.
    /// </summary>
    private void PlaceSeat()
    {
        var area = _seatArea.ClientSize;
        if (!_fitSeat || area.Width <= 0 || area.Height <= 0) return;
        double scale = Math.Min((double)area.Width / _seatSize.Width, (double)area.Height / _seatSize.Height);
        int width = Math.Clamp((int)Math.Round(_seatSize.Width * scale), 1, area.Width);
        int height = Math.Clamp((int)Math.Round(_seatSize.Height * scale), 1, area.Height);
        var bounds = new Rectangle((area.Width - width) / 2, (area.Height - height) / 2, width, height);
        Viewer.Bounds = bounds;
        if (_standIn is not null) _standIn.Bounds = bounds;
    }

    /// <summary>For design previews: a picture that stands in for the seat, placed where the seat shows.</summary>
    internal void ShowStandIn(Control picture)
    {
        _standIn = picture;
        _seatArea.Controls.Add(picture);
        picture.BringToFront();
        PlaceSeat();
    }

    /// <summary>
    /// True while the viewer swallows your clicks and keystrokes. The seat still runs;
    /// this only decides whether a stray click of yours reaches it.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ViewOnly
    {
        get => _viewOnly;
        set
        {
            _viewOnly = value;
            Viewer.SetInputEnabled(!value);
            _controlButton.Text = value ? "Take control" : "Release control";
            _controlButton.Checked = !value;
            _controlButton.AccessibleName = _controlButton.Text;
            _modeLabel.Text = value ? "View only" : "You have control";
            _modeLabel.ForeColor = value ? ViewerTheme.Muted : ViewerTheme.Accent;
            _controlButton.ToolTipText = value
                ? "Let your mouse and keyboard reach the seat."
                : "Stop your mouse and keyboard from reaching the seat.";
            _modeLabel.ToolTipText = value ? "Your clicks and keystrokes do not reach the seat." : _controlButton.ToolTipText;
            RefreshDetails();
        }
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.Name = "SeatToolbar";
        _toolbar.AccessibleName = "Seat controls";
        _toolbar.TabStop = true;
        _toolbar.TabIndex = 0;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(10, 6, 10, 6);
        // Icons are 16 px squares; the extra width is the gap before each label.
        _toolbar.ImageScalingSize = new Size(21, 16);
        _toolbar.Font = ViewerTheme.UiFont();
        _toolbar.OverflowButton.DropDown.HandleCreated += (_, _) => ViewerTheme.ApplyMenuFrame(_toolbar.OverflowButton.DropDown.Handle);

        _stopButton.Text = "Stop seat";
        _stopButton.Name = "StopSeat";
        _stopButton.ToolTipText = "Close every program in the seat immediately, including unsaved work. Ctrl+Alt+Shift+K also stops the seat when available.";
        _stopButton.AccessibleDescription = _stopButton.ToolTipText;
        _stopButton.ForeColor = ViewerTheme.Danger;
        _stopButton.Font = ViewerTheme.StrongFont();
        // Always tinted: the emergency action reads as a button before anyone hovers over it.
        ViewerTheme.Decorate(_stopButton, new(ViewerTheme.Tone.Danger, ViewerTheme.Glyphs.Stop));
        _stopButton.Click += (_, _) => StopSeatRequested?.Invoke();

        _controlButton.Name = "ControlSeat";
        _controlButton.CheckOnClick = false;
        ViewerTheme.Decorate(_controlButton, new(Glyph: ViewerTheme.Glyphs.Pointer));
        _controlButton.Click += (_, _) => ViewOnly = !ViewOnly;

        _fullScreenButton.Text = "Full screen";
        _fullScreenButton.Name = "FullScreen";
        _fullScreenButton.ToolTipText = "Toggle full screen (F11). Seat controls stay available.";
        ViewerTheme.Decorate(_fullScreenButton, new(Glyph: ViewerTheme.Glyphs.FullScreen));
        _fullScreenButton.Click += (_, _) => ToggleFullScreen();

        _reconnectButton.Text = "Reconnect";
        _reconnectButton.ToolTipText = "Reconnect the viewer. The seat and its programs keep running.";
        ViewerTheme.Decorate(_reconnectButton, new(Glyph: ViewerTheme.Glyphs.Refresh));
        _reconnectButton.Click += (_, _) => ReconnectRequested?.Invoke();

        _signInButton.Text = "Sign in…";
        _signInButton.ToolTipText = "Reconnect with the Windows credential dialog. The seat and its programs keep running.";
        ViewerTheme.Decorate(_signInButton, new(Glyph: ViewerTheme.Glyphs.Person));
        _signInButton.Click += (_, _) => SignInRequested?.Invoke();

        _detailsButton.Name = "ShowDetails";
        _detailsButton.Text = "Details";
        _detailsButton.ToolTipText = "Read and copy the full seat status and troubleshooting details.";
        ViewerTheme.Decorate(_detailsButton, new(Glyph: ViewerTheme.Glyphs.Info));
        _detailsButton.CheckOnClick = true;
        _detailsButton.CheckedChanged += (_, _) =>
        {
            _detailsPanel.Visible = _detailsButton.Checked;
            FitDetailsScrollBar();
            if (_detailsPanel.Visible) _details.Focus();
        };

        _headline.Alignment = ToolStripItemAlignment.Right;
        _headline.ForeColor = ViewerTheme.Muted;
        _headline.Text = "Starting";
        _headline.Name = "SeatState";
        _headline.Margin = new Padding(8, 1, 6, 2);
        ViewerTheme.Decorate(_headline, new(ViewerTheme.Tone.Busy));

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _stopButton,
            // Spacing, not a line: the renderer draws no separators in the header.
            new ToolStripSeparator { Margin = new Padding(4, 0, 4, 0) },
            _controlButton,
            _fullScreenButton,
            _reconnectButton,
            _signInButton,
            _detailsButton,
            _headline
        });
        foreach (var button in _toolbar.Items.OfType<ToolStripButton>())
        {
            button.Padding = new Padding(7, 5, 10, 5);
            button.Margin = new Padding(2, 0, 2, 0);
            button.AccessibleName = button.Text;
        }
        // The emergency action and the way out of full screen must never disappear into overflow,
        // and neither should the seat's state; secondary actions overflow first.
        _stopButton.Overflow = _controlButton.Overflow = _fullScreenButton.Overflow = ToolStripItemOverflow.Never;
        _headline.Overflow = ToolStripItemOverflow.Never;
    }

    private void BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.Name = "SeatStatus";
        _statusBar.AccessibleName = "Seat status";
        _statusBar.ShowItemToolTips = true;
        // Windows resizes from any edge; the grip was only texture.
        _statusBar.SizingGrip = false;
        _statusBar.Padding = new Padding(6, 2, 6, 4);
        _statusBar.Font = ViewerTheme.UiFont();

        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "Starting Anode...";
        _statusLabel.ToolTipText = _statusLabel.Text;

        _seatLabel.TextAlign = ContentAlignment.MiddleRight;
        _seatLabel.Text = "No seat";
        _seatLabel.Margin = new Padding(10, 3, 0, 2);
        _modeLabel.Name = "InputMode";
        _modeLabel.Margin = new Padding(6, 3, 12, 2);

        // Hidden until the daemon reports who holds the desktop lease.
        _deskLabel.Name = "DeskOwner";
        _deskLabel.Margin = new Padding(10, 3, 0, 2);
        _deskLabel.Visible = false;

        _statusBar.Items.AddRange(new ToolStripItem[] { _modeLabel, _statusLabel, _deskLabel, _seatLabel });
    }

    /// <summary>The tray icon's menu, for design previews.</summary>
    internal ContextMenuStrip TrayMenu => _tray.ContextMenuStrip!;

    private void BuildTray(bool visible)
    {
        // The same request as the header's Sign in button. The credential dialog belongs
        // to the viewer, so show the viewer first.
        _traySignIn.Click += (_, _) =>
        {
            ShowViewer();
            SignInRequested?.Invoke();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show the seat", null, (_, _) => ShowViewer());
        menu.Items.Add(_traySignIn);
        menu.Items.Add("Stop the seat", null, (_, _) => StopSeatRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Help", null, (_, _) => OpenHelp());
        menu.Items.Add("Quit Anode", null, (_, _) => QuitRequested?.Invoke());
        menu.Font = ViewerTheme.UiFont();
        ViewerTheme.ApplyMenu(menu);

        // Select the matching small frame; scaling the 32 px icon blurs it in the tray.
        _trayIcon = _icon is null ? null : new Icon(_icon, SystemInformation.SmallIconSize);
        _tray.Icon = _trayIcon ?? SystemIcons.Application;
        _tray.Text = TrayText(null);
        _tray.Visible = visible;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowViewer();
    }

    private static string TrayText(uint? sessionId)
    {
        string text = sessionId is null ? $"Anode{Env.ChannelSuffix}: no seat" : $"Anode{Env.ChannelSuffix} background desktop: session {sessionId}";
        return text.Length > 63 ? text[..63] : text;
    }

    /// <summary>The Anode icon embedded from assets\anode.ico, with all of its sizes.</summary>
    private static Icon? LoadIcon()
    {
        try
        {
            using var stream = typeof(SeatWindow).Assembly.GetManifestResourceStream("Anode.Icon");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not load the Anode icon: {ex.Message}");
            return null;
        }
    }

    private static void OpenHelp()
    {
        try { Process.Start(new ProcessStartInfo(Links.Troubleshooting) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"could not open {Links.Troubleshooting}: {ex.Message}"); }
    }

    private void ApplyTheme()
    {
        BackColor = _seatArea.BackColor = ViewerTheme.Background;
        ForeColor = ViewerTheme.Text;
        ViewerTheme.Apply(_toolbar);
        ViewerTheme.Apply(_statusBar);
        if (_tray.ContextMenuStrip is { } menu)
        {
            ViewerTheme.Apply(menu);
            foreach (ToolStripItem item in menu.Items) item.ForeColor = ViewerTheme.Text;
        }
        _stopButton.ForeColor = ViewerTheme.Danger;
        _headline.ForeColor = _state is "error" or "logon-error" ? ViewerTheme.Danger : ViewerTheme.Muted;
        _statusLabel.ForeColor = _seatLabel.ForeColor = ViewerTheme.Muted;
        _modeLabel.ForeColor = ViewOnly ? ViewerTheme.Muted : ViewerTheme.Accent;
        // Details continue the header's surface instead of opening a differently colored box.
        _detailsPanel.BackColor = _details.BackColor = SystemInformation.HighContrast ? SystemColors.Window : ViewerTheme.Chrome;
        _details.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : ViewerTheme.Text;
        if (IsHandleCreated) ViewerTheme.ApplyFrame(Handle);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyTheme();
    }

    /// <summary>A scroll bar only when the details overflow, instead of an always-visible light one.</summary>
    private void FitDetailsScrollBar()
    {
        // Measure narrower than the box: its internal margins wrap text slightly earlier.
        int width = _details.Width - LogicalToDeviceUnits(8);
        if (!_detailsPanel.Visible || width <= 0) return;
        int height = TextRenderer.MeasureText(_details.Text, _details.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        var wanted = height > _details.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
        if (_details.ScrollBars != wanted) _details.ScrollBars = wanted;
    }

    private void RefreshDetails()
    {
        _details.Text = $"{_headline.Text}\r\n{_statusLabel.Text}\r\n\r\n{_seatLabel.Text} · {_modeLabel.Text}\r\n"
            + (_hotkeyAvailable ? "Emergency stop: Ctrl+Alt+Shift+K."
                : "Emergency shortcut unavailable. Use Stop seat, the tray menu or anode kill.")
            + "\r\nStop closes all seat programs, including unsaved work. Closing the viewer keeps the seat running.";
        FitDetailsScrollBar();
        _placeholder.Describe(_headline.Text ?? string.Empty, _statusLabel.Text ?? string.Empty,
            busy: _state is "starting" or "connecting" or "signing-in" or "starting-agent" or "stopping",
            problem: _state is "error" or "logon-error");
        string seat = _sessionId is { } id ? $" · session {id}" : "";
        string tooltip = $"Anode{Env.ChannelSuffix}: {_headline.Text}{seat}";
        _tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    // ------------------------------------------------------------------ status

    public void SetReconnectEnabled(bool enabled)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetReconnectEnabled(enabled))); return; }
        _reconnectButton.Enabled = enabled;
        _signInButton.Enabled = enabled;
        _traySignIn.Enabled = enabled;
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
        _statusLabel.Text = text;
        _statusLabel.ToolTipText = text;
        RefreshDetails();
    }

    public void SetHeadline(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetHeadline(text))); return; }
        _state = text;
        _headline.Text = text switch
        {
            "starting" => "Starting", "connecting" => "Connecting", "signing-in" => "Signing in",
            "starting-agent" => "Preparing desktop", "ready" => "Ready", "stopping" => "Stopping",
            "stopped" => "Stopped", "detached" => "Disconnected", "logon-error" => "Sign-in failed",
            "error" => "Needs attention", _ => text
        };
        _headline.ForeColor = text is "error" or "logon-error" ? ViewerTheme.Danger : ViewerTheme.Muted;
        ViewerTheme.Decorate(_headline, new(text switch
        {
            "ready" => ViewerTheme.Tone.Good,
            "error" or "logon-error" => ViewerTheme.Tone.Problem,
            "stopped" or "detached" => ViewerTheme.Tone.Neutral,
            _ => ViewerTheme.Tone.Busy
        }));
        RefreshDetails();
    }

    /// <summary>
    /// Who holds the desktop lease, for someone watching several agents take turns on one seat.
    /// Unknown hides the label rather than guessing.
    /// </summary>
    public void SetDesk(string? owner, int waiting, bool known)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetDesk(owner, waiting, known))); return; }
        if (owner is { Length: > 32 }) owner = owner[..32];
        _deskLabel.Visible = known;
        _deskLabel.Text = owner is null ? "Desktop free" : $"In use by {owner}" + (waiting > 0 ? $" · {waiting} waiting" : "");
        _deskLabel.ToolTipText = owner is null ? "No agent holds the desktop lease."
            : $"{owner} holds the desktop lease" + (waiting > 0 ? $"; {waiting} more agent(s) waiting in line." : ".");
        _deskLabel.AccessibleName = _deskLabel.Text;
        _deskLabel.ForeColor = ViewerTheme.Muted;
    }

    public void SetSeatInfo(uint? sessionId, bool hostReady)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetSeatInfo(sessionId, hostReady))); return; }
        _sessionId = sessionId;
        _seatLabel.Text = sessionId is null
            ? "No seat"
            : $"Session {sessionId} · {(hostReady ? "Host ready" : "Host starting")}";
        RefreshDetails();
    }

    // ------------------------------------------------------------- visibility

    /// <summary>True once the Remote Desktop control has connected and shows the seat itself.</summary>
    internal bool SeatPictureLive => _seatPictureLive;

    /// <summary>
    /// Reveals the Remote Desktop control as soon as it connects, whatever the seat then shows,
    /// and covers it with the placeholder again when it disconnects.
    /// </summary>
    internal void SetSeatPictureLive(bool live)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetSeatPictureLive(live))); return; }
        _seatPictureLive = live;
        _placeholder.Visible = !live;
    }

    internal void CreateHiddenViewer()
    {
        // Allocate native handles without showing even a single frame on the parent desktop.
        _ = Handle;
        Viewer.CreateHiddenHandle();
    }

    public void ShowViewer()
    {
        if (InvokeRequired) { BeginInvoke(new Action(ShowViewer)); return; }
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = _restoreState;
        ClearNoActivate();
        Activate();
    }

    public void HideViewer()
    {
        if (InvokeRequired) { BeginInvoke(new Action(HideViewer)); return; }
        Hide();
    }

    /// <summary>
    /// The window is created without taking focus so bringing a seat up mid-sentence
    /// does not steal your keystrokes. Once the seat is connected the flag is dropped,
    /// so clicking the window afterwards behaves normally.
    /// </summary>
    public void ClearNoActivate()
    {
        if (!_noActivate || !IsHandleCreated) return;
        _noActivate = false;
        int style = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, style & ~Native.WS_EX_NOACTIVATE);
    }

    protected override bool ShowWithoutActivation => _noActivate;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (_noActivate) parameters.ExStyle |= Native.WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    public void ToggleFullScreen()
    {
        if (_fullScreen)
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = _restoreBorder;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            _toolbar.Visible = true;
            _statusBar.Visible = true;
            _fullScreen = false;
            _fullScreenButton.Text = "Full screen";
            ViewerTheme.Decorate(_fullScreenButton, new(Glyph: ViewerTheme.Glyphs.FullScreen));
        }
        else
        {
            _restoreBorder = FormBorderStyle;
            _restoreState = WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = (Screen.FromControl(this) ?? Screen.PrimaryScreen!).Bounds;
            // Keep emergency stop, control release and the exit button visible even if
            // the remote app captures F11/Escape or the global hotkey is unavailable.
            _toolbar.Visible = true;
            _statusBar.Visible = true;
            _fullScreen = true;
            _fullScreenButton.Text = "Exit full screen";
            ViewerTheme.Decorate(_fullScreenButton, new(Glyph: ViewerTheme.Glyphs.ExitFullScreen));
        }
        _fullScreenButton.AccessibleName = _fullScreenButton.Text;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == System.Windows.Forms.Keys.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.KeyCode == System.Windows.Forms.Keys.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
    }

    // -------------------------------------------------------------- lifecycle

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The view-only state is set in the constructor, before the control has a
        // window to disable. Re-apply it now that it does, or the first clicks would
        // reach a seat the user believes they are only watching.
        Viewer.SetInputEnabled(!_viewOnly);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ViewerTheme.ApplyFrame(Handle);
        // Measured again on the monitor the window opens on, whose DPI sizes its frame and bars; only once,
        // so later handles (full screen changes the border) keep the size the user chose.
        if (!_fitted)
        {
            _fitted = true;
            FitToSeat();
        }
        // Ctrl+Alt+Shift+K works from anywhere on the user's desktop. If something
        // else already owns it, the toolbar and tray buttons still do the job.
        _hotkeyAvailable = Native.RegisterHotKey(Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 'K');
        if (!_hotkeyAvailable)
        {
            Log.Warn("Ctrl+Alt+Shift+K is already taken; use the Stop seat button or `anode kill`.");
        }
        RefreshDetails();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try { Native.UnregisterHotKey(Handle, HotkeyId); } catch { }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        // Hiding keeps the ActiveX client out of its minimized-window path.
        // The per-user RDP rendering preference also prevents display suppression.
        if (m.Msg == 0x0112 && (m.WParam.ToInt64() & 0xFFF0) == 0xF020)
        {
            HideViewer();
            return;
        }
        if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            StopSeatRequested?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Marks the window as free to close for real, rather than hiding to the tray.</summary>
    public void AllowClose() => _reallyClosing = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (_fullScreen) ToggleFullScreen();
            Hide();
            _tray.BalloonTipTitle = "Anode is still running";
            _tray.BalloonTipText = "The seat is untouched. Use the tray icon to watch it again or to stop it.";
            _tray.ShowBalloonTip(3000);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            var menu = _tray.ContextMenuStrip;
            _tray.Dispose();
            menu?.Dispose();
            _trayIcon?.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) _icon?.Dispose();
    }
}
