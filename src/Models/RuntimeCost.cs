namespace DerivityMeter.Models;

public static class RuntimeCost
{
    private static readonly string[] CostKeys =
    [
        "cost_usd", "estimated_cost_usd", "cost", "estimated_cost",
        "total_cost_usd", "request_cost_usd"
    ];

    public static bool TryParseReportedUsd(IReadOnlyDictionary<string, string> attrs, out double cost)
    {
        cost = 0;
        foreach (var key in CostKeys)
        {
            if (!attrs.TryGetValue(key, out var raw)) continue;
            if (TryParseUsd(raw, out cost) && cost > 0) return true;
        }
        return false;
    }

    public static bool TryParseUsd(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().TrimStart('$');
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    public static string FormatUsd(double? amount, bool reported) =>
        amount is null or <= 0 ? "—" : $"${amount.Value:F2} ({(reported ? "reported" : "est.")})";

    public static string FormatUsdShort(double? amount, bool reported) =>
        amount is null or <= 0 ? "—" : reported ? $"${amount.Value:F2}" : $"~${amount.Value:F2}";
}
