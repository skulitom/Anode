using System.Drawing;
using System.Windows.Forms;

namespace Anode.Cli;

/// <summary>Opt-in live fixture: it refuses to create a window on the parent desktop.</summary>
internal static class DesktopFixture
{
    public static int Run()
    {
        try { Seat.SeatHost.VerifyCurrentSession(); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
        Application.EnableVisualStyles();
        using var form = new Form
        {
            Text = "Anode accessibility test", Size = new Size(620, 460),
            StartPosition = FormStartPosition.CenterScreen
        };
        var text = new TextBox { Name = "DraftNote", AccessibleName = "Draft note", Text = "Initial draft", Location = new Point(24, 24), Width = 540 };
        var apply = new Button { Name = "ApplyNote", AccessibleName = "Apply note", Text = "Apply note", Location = new Point(24, 68), Width = 140 };
        var status = new Label { Name = "Result", Text = "No note applied", Location = new Point(24, 116), Size = new Size(540, 35) };
        apply.Click += (_, _) => status.Text = "Applied: " + text.Text;
        var prepare = new Button { Name = "Prepare", AccessibleName = "Prepare result", Text = "Prepare result", Location = new Point(185, 68), Width = 140 };
        using var preparation = new System.Windows.Forms.Timer { Interval = 1500 };
        prepare.Click += (_, _) => { status.Text = "Preparing"; apply.Enabled = false; preparation.Start(); };
        preparation.Tick += (_, _) => { preparation.Stop(); status.Text = "Preparation complete"; apply.Enabled = true; };
        var check = new CheckBox { Name = "Preview", AccessibleName = "Enable preview", Text = "Enable preview", Location = new Point(24, 158), Width = 220 };
        var secret = new TextBox { Name = "PasswordFixture", AccessibleName = "Secret test field", Text = "fixture-secret-must-not-be-exported", UseSystemPasswordChar = true, Location = new Point(24, 204), Width = 540 };
        var list = new ListBox { Name = "Choice", AccessibleName = "Example choices", Location = new Point(24, 248), Size = new Size(260, 100) };
        list.Items.AddRange(new object[] { "Alpha", "Beta", "Gamma" });
        var readOnly = new TextBox { Name = "ReadOnlyNote", AccessibleName = "Read-only note", Text = "Fixed value", ReadOnly = true, Location = new Point(306, 248), Width = 258 };
        var range = new TrackBar { Name = "Level", AccessibleName = "Preview level", Minimum = 0, Maximum = 100, Value = 25, Location = new Point(306, 292), Width = 258 };
        var numericRange = new System.Windows.Controls.Slider { Minimum = 0, Maximum = 100, Value = 25 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(numericRange, "RangeLevel");
        System.Windows.Automation.AutomationProperties.SetName(numericRange, "Numeric preview level");
        var rangeHost = new System.Windows.Forms.Integration.ElementHost { Location = new Point(24, 356), Size = new Size(540, 35), Child = numericRange };
        form.Controls.AddRange(new Control[] { text, apply, prepare, status, check, secret, list, readOnly, range, rangeHost });
        using var timer = new System.Windows.Forms.Timer { Interval = 600_000 };
        timer.Tick += (_, _) => form.Close();
        timer.Start();
        Application.Run(form);
        return 0;
    }
}
