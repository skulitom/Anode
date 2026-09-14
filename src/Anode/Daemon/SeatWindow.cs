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
    private readonly ToolStripLabel _headline = new();
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _seatLabel = new();
    private readonly NotifyIcon _tray = new();

    private FormWindowState _restoreState = FormWindowState.Normal;
    private FormBorderStyle _restoreBorder = FormBorderStyle.Sizable;
    private Rectangle _restoreBounds;
    private bool _fullScreen;
    private bool _viewOnly = true;
    private bool _noActivate = true;
    private bool _reallyClosing;

    public RdpViewer Viewer { get; }

    public event Action? StopSeatRequested;
    public event Action? QuitRequested;
    public event Action? ReconnectRequested;

    public SeatWindow(SeatOptions options)
    {
        Viewer = new RdpViewer();

        Text = "Anode seat";
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(560, 360);
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.FromArgb(228, 228, 231);
        Icon = SystemIcons.Application;
        KeyPreview = true;
        _noActivate = true;

        BuildToolbar();
        BuildStatusBar();
        BuildTray();

        Controls.Add(Viewer);
        Controls.Add(_toolbar);
        Controls.Add(_statusBar);

        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        int width = Math.Min(work.Width - 80, options.Width + 32);
        int height = Math.Min(work.Height - 80, options.Height + 108);
        Size = new Size(Math.Max(640, width), Math.Max(420, height));
        Location = new Point(work.Right - Size.Width - 24, work.Bottom - Size.Height - 24);

        ViewOnly = options.StartViewOnly;
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// True while the viewer swallows your clicks and keystrokes. The seat still runs;
    /// this only decides whether a stray click of yours reaches it.
    /// </summary>
    public bool ViewOnly
    {
        get => _viewOnly;
        set
        {
            _viewOnly = value;
            Viewer.SetInputEnabled(!value);
            _controlButton.Text = value ? "Take control" : "Release control";
            _controlButton.Checked = !value;
            _controlButton.ToolTipText = value
                ? "Let your mouse and keyboard reach the seat."
                : "Stop your mouse and keyboard from reaching the seat.";
        }
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.RenderMode = ToolStripRenderMode.System;
        _toolbar.BackColor = Color.FromArgb(32, 32, 36);
        _toolbar.ForeColor = Color.FromArgb(228, 228, 231);
        _toolbar.Padding = new Padding(6, 4, 6, 4);
        _toolbar.ImageScalingSize = new Size(16, 16);

        _stopButton.Text = "Stop seat";
        _stopButton.ToolTipText = "Sign the seat out. Every program running in it is closed immediately, wedged or not. (Ctrl+Alt+Shift+K)";
        _stopButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _stopButton.ForeColor = Color.FromArgb(248, 113, 113);
        _stopButton.Font = new Font(_toolbar.Font, FontStyle.Bold);
        _stopButton.Click += (_, _) => StopSeatRequested?.Invoke();

        _controlButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _controlButton.CheckOnClick = false;
        _controlButton.Click += (_, _) => ViewOnly = !ViewOnly;

        _fullScreenButton.Text = "Full screen";
        _fullScreenButton.ToolTipText = "F11";
        _fullScreenButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _fullScreenButton.Click += (_, _) => ToggleFullScreen();

        _reconnectButton.Text = "Reconnect";
        _reconnectButton.ToolTipText = "Reconnect the viewer. The seat and its programs keep running.";
        _reconnectButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _reconnectButton.Click += (_, _) => ReconnectRequested?.Invoke();

        _headline.Alignment = ToolStripItemAlignment.Right;
        _headline.ForeColor = Color.FromArgb(161, 161, 170);
        _headline.Text = "starting";

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _stopButton,
            new ToolStripSeparator(),
            _controlButton,
            _fullScreenButton,
            _reconnectButton,
            _headline
        });
    }

    private void BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.BackColor = Color.FromArgb(32, 32, 36);
        _statusBar.ForeColor = Color.FromArgb(161, 161, 170);
        _statusBar.SizingGrip = true;

        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "Starting Anode...";

        _seatLabel.TextAlign = ContentAlignment.MiddleRight;
        _seatLabel.Text = "no seat";

        _statusBar.Items.AddRange(new ToolStripItem[] { _statusLabel, _seatLabel });
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show the seat", null, (_, _) => ShowViewer());
        menu.Items.Add("Stop the seat", null, (_, _) => StopSeatRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Anode", null, (_, _) => QuitRequested?.Invoke());

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "Anode seat";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowViewer();
    }

    // ------------------------------------------------------------------ status

    public void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
        _statusLabel.Text = text;
    }

    public void SetHeadline(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetHeadline(text))); return; }
        _headline.Text = text;
    }

    public void SetSeatInfo(uint? sessionId, bool hostReady)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetSeatInfo(sessionId, hostReady))); return; }
        _seatLabel.Text = sessionId is null
            ? "no seat"
            : $"session {sessionId}  |  agent {(hostReady ? "ready" : "starting")}";
        string tip = sessionId is null ? "Anode: no seat" : $"Anode seat: session {sessionId}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    // ------------------------------------------------------------- visibility

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
            FormBorderStyle = _restoreBorder;
            WindowState = _restoreState;
            Bounds = _restoreBounds;
            _toolbar.Visible = true;
            _statusBar.Visible = true;
            _fullScreen = false;
            _fullScreenButton.Text = "Full screen";
        }
        else
        {
            _restoreBorder = FormBorderStyle;
            _restoreState = WindowState;
            _restoreBounds = Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = (Screen.FromControl(this) ?? Screen.PrimaryScreen!).Bounds;
            _toolbar.Visible = false;
            _statusBar.Visible = false;
            _fullScreen = true;
            _fullScreenButton.Text = "Exit full screen";
        }
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
        // Ctrl+Alt+Shift+K works from anywhere on the user's desktop. If something
        // else already owns it, the toolbar and tray buttons still do the job.
        if (!Native.RegisterHotKey(Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 'K'))
        {
            Log.Warn("Ctrl+Alt+Shift+K is already taken; use the Stop seat button or `anode kill`.");
        }
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
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
