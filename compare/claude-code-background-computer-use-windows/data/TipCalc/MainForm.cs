using System.Globalization;

namespace TipCalc;

public sealed class MainForm : Form
{
    private readonly TextBox _bill = new() { Name = "billBox", AccessibleName = "Bill amount", Left = 140, Top = 20, Width = 120 };
    private readonly TextBox _tip = new() { Name = "tipBox", AccessibleName = "Tip percent", Left = 140, Top = 60, Width = 120 };
    private readonly Button _calc = new() { Name = "calcButton", Text = "Calculate", Left = 140, Top = 100, Width = 120, Height = 32 };
    private readonly Label _result = new() { Name = "resultLabel", Text = "Total: -", Left = 20, Top = 150, Width = 260, Height = 28 };

    public MainForm()
    {
        Text = "TipCalc";
        ClientSize = new Size(300, 200);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Text = "Bill amount", Left = 20, Top = 23, Width = 110 });
        Controls.Add(_bill);
        Controls.Add(new Label { Text = "Tip percent", Left = 20, Top = 63, Width = 110 });
        Controls.Add(_tip);
        Controls.Add(_calc);
        Controls.Add(_result);
        AcceptButton = _calc;
        _calc.Click += (_, _) => Calculate();
    }

    private void Calculate()
    {
        var inv = CultureInfo.InvariantCulture;
        if (decimal.TryParse(_bill.Text, NumberStyles.Number, inv, out var bill) &&
            decimal.TryParse(_tip.Text, NumberStyles.Number, inv, out var tip))
        {
            _result.Text = "Total: " + TipMath.Total(bill, tip).ToString("0.00", inv);
        }
        else
        {
            _result.Text = "Total: enter two numbers";
        }
    }
}
