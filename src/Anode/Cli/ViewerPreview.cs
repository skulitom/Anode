using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Anode.Core.Native;

namespace Anode.Cli;

/// <summary>
/// Design preview: renders the viewer, title bar included, in sample states to PNG files, never
/// connected. Each view is captured from a cloaked window placed far off-screen, owned by a
/// hidden window so it has no taskbar button, and unable to activate: Windows composes it without
/// ever displaying it. A view is skipped unless Windows confirms the cloak. No tray icon is created.
/// </summary>
internal static class ViewerPreview
{
    private sealed record Scene(string Name, string State, bool Control = false, bool Details = false,
        bool Seat = true, bool Narrow = false, string? Hover = null);

    private static readonly Scene[] Scenes =
    {
        new("ready", "ready"),
        new("hover", "ready", Hover: "ControlSeat"),
        new("control", "ready", Control: true),
        new("connecting", "connecting", Seat: false),
        new("disconnected", "detached", Seat: false),
        new("sign-in-failed", "logon-error", Details: true, Seat: false),
        new("narrow", "ready", Narrow: true)
    };

    private static readonly Dictionary<string, string> Samples = new()
    {
        ["connecting"] = "Creating the seat...",
        ["ready"] = "Seat ready in session 2 at 1280x720. Programs you start here stay out of your way.",
        ["detached"] = "The viewer disconnected but the seat is still running. Press Reconnect in the viewer to watch it again.",
        ["logon-error"] = "Windows could not sign in the seat. Press Sign in… to use the Windows credential dialog."
    };

    public static int Run(string[] args)
    {
        ConsoleBridge.Attach();
        var options = new Args(args);
        string output = Path.GetFullPath(options.Value("out") ?? "viewer-preview");
        string? backdrop = options.Value("backdrop") ?? DefaultBackdrop();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(output);
        using var owner = new Form();
        _ = owner.Handle;
        foreach (var scene in Scenes) Render(scene, output, backdrop, owner);
        RenderTrayMenu(output);
        Console.WriteLine(output);
        return 0;
    }

    private static void Render(Scene scene, string output, string? backdrop, Form owner)
    {
        using var window = new Daemon.SeatWindow(new Daemon.SeatOptions(), trayIcon: false);
        window.AllowClose();
        window.SetSeatInfo(2, scene.State == "ready");
        window.SetStatus(Samples.GetValueOrDefault(scene.State, scene.State));
        window.SetHeadline(scene.State);
        window.ViewOnly = !scene.Control;
        // The ActiveX control shows a blank window of its own, even when hidden, until a
        // connection this preview never makes. It never gets a window here; a picture stands
        // in for the seat.
        window.Viewer.Visible = false;
        if (scene.Seat && backdrop is not null)
        {
            ShowBackdrop(window, backdrop);
            window.SetSeatPictureLive(true);
        }
        window.CreateControl();
        window.Size = scene.Narrow
            ? new Size(window.MinimumSize.Width, window.LogicalToDeviceUnits(420))
            : new Size(window.LogicalToDeviceUnits(1100), window.LogicalToDeviceUnits(680));
        var toolbar = window.Controls.OfType<ToolStrip>().Single(s => s.Name == "SeatToolbar");
        if (scene.Details && toolbar.Items["ShowDetails"] is ToolStripButton details) details.Checked = true;
        window.PerformLayout();
        toolbar.PerformLayout();
        if (scene.Hover is { } hover) toolbar.Items[hover]!.Select();
        Capture(window, owner, Path.Combine(output, scene.Name + ".png"));
    }

    private static void Capture(Form window, Form owner, string path)
    {
        IntPtr handle = window.Handle;
        int style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE, style | Native.WS_EX_NOACTIVATE);
        int cloak = 1;
        if (Native.DwmSetWindowAttribute(handle, Native.DWMWA_CLOAK, ref cloak, sizeof(int)) != 0
            || Native.DwmGetWindowAttribute(handle, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0)
        {
            Console.Error.WriteLine($"Skipped {Path.GetFileName(path)}: Windows did not confirm the preview window is cloaked.");
            return;
        }
        // An owned window never gets a taskbar button.
        window.Owner = owner;
        window.Location = new Point(-20000, -20000);
        window.Show();
        try
        {
            for (int pass = 0; pass < 3; pass++)
            {
                window.Refresh();
                Application.DoEvents();
                Native.DwmFlush();
            }
            if (Native.DwmGetWindowAttribute(handle, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.Rect frame, Marshal.SizeOf<Native.Rect>()) != 0) return;
            var bounds = window.Bounds;
            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(full))
            {
                IntPtr dc = graphics.GetHdc();
                try { Native.PrintWindow(handle, dc, Native.PW_RENDERFULLCONTENT); }
                finally { graphics.ReleaseHdc(dc); }
            }
            var visible = Rectangle.Intersect(new Rectangle(frame.Left - bounds.X, frame.Top - bounds.Y,
                frame.Right - frame.Left, frame.Bottom - frame.Top), new Rectangle(Point.Empty, bounds.Size));
            using var image = full.Clone(visible, full.PixelFormat);
            image.Save(path, ImageFormat.Png);
        }
        finally { window.Hide(); }
    }

    private static void RenderTrayMenu(string output)
    {
        using var window = new Daemon.SeatWindow(new Daemon.SeatOptions(), trayIcon: false);
        var menu = window.TrayMenu;
        menu.CreateControl();
        // A menu sizes itself when shown; this one never is, so fit it around its laid-out items.
        menu.Size = new Size(1000, 1000);
        menu.PerformLayout();
        var items = menu.Items.Cast<ToolStripItem>().Select(i => i.Bounds).Aggregate(Rectangle.Union);
        menu.Size = new Size(items.Right + menu.Padding.Right, items.Bottom + menu.Padding.Bottom);
        menu.PerformLayout();
        menu.Items[0].Select();
        using var image = new Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(image, new Rectangle(Point.Empty, menu.Size));
        image.Save(Path.Combine(output, "tray-menu.png"), ImageFormat.Png);
    }

    private static void ShowBackdrop(Daemon.SeatWindow window, string path)
    {
        var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.StretchImage, Image = Image.FromFile(path) };
        window.Controls.Add(picture);
        // Docking runs from the back of the z-order; the front control fills what is left.
        window.Controls.SetChildIndex(picture, 0);
    }

    private static string? DefaultBackdrop()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper", "Windows");
        return new[] { "img19.jpg", "img0.jpg" }.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }
}
