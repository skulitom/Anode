namespace TipCalc;

public static class TipMath
{
    /// <summary>Total to pay: the bill plus the tip.</summary>
    public static decimal Total(decimal bill, decimal tipPercent)
    {
        return bill + tipPercent;
    }
}
