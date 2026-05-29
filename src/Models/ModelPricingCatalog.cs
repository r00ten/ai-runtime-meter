namespace DerivityMeter.Models;

/// <summary>
/// Anthropic API list prices (USD per 1M tokens). Source: docs.anthropic.com pricing table, May 2026.
/// 5-minute cache write rates used for estimates unless 1h TTL is detected later.
/// </summary>
public static class ModelPricingCatalog
{
    public sealed record Row(
        string Family,
        double InputPerMTok,
        double CacheWrite5mPerMTok,
        double CacheWrite1hPerMTok,
        double CacheReadPerMTok,
        double OutputPerMTok);

    private static readonly Row DefaultRow = new(
        "claude-opus-4.7 (default)",
        5, 6.25, 10, 0.50, 25);

    private static readonly (string[] Keys, Row Row)[] Table =
    [
        (["claude-opus-4-7", "claude-opus-4.7", "opus-4-7", "opus-4.7"], new Row("claude-opus-4.7", 5, 6.25, 10, 0.50, 25)),
        (["claude-opus-4-6", "claude-opus-4.6", "opus-4-6"], new Row("claude-opus-4.6", 5, 6.25, 10, 0.50, 25)),
        (["claude-opus-4-5", "claude-opus-4.5", "opus-4-5"], new Row("claude-opus-4.5", 5, 6.25, 10, 0.50, 25)),
        (["claude-opus-4-1", "opus-4-1"], new Row("claude-opus-4.1", 15, 18.75, 30, 1.50, 75)),
        (["claude-opus-4", "opus-4"], new Row("claude-opus-4", 15, 18.75, 30, 1.50, 75)),
        (["claude-sonnet-4-6", "claude-sonnet-4.6", "sonnet-4-6"], new Row("claude-sonnet-4.6", 3, 3.75, 6, 0.30, 15)),
        (["claude-sonnet-4-5", "claude-sonnet-4.5", "sonnet-4-5"], new Row("claude-sonnet-4.5", 3, 3.75, 6, 0.30, 15)),
        (["claude-sonnet-4", "sonnet-4"], new Row("claude-sonnet-4", 3, 3.75, 6, 0.30, 15)),
        (["claude-haiku-4-5", "claude-haiku-4.5", "haiku-4-5"], new Row("claude-haiku-4.5", 1, 1.25, 2, 0.10, 5)),
        (["claude-haiku-3-5", "claude-haiku-3.5", "haiku-3-5"], new Row("claude-haiku-3.5", 0.80, 1, 1.60, 0.08, 4)),
    ];

    public static Row Resolve(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return DefaultRow;
        var m = model.Trim().ToLowerInvariant();

        foreach (var (keys, row) in Table)
        {
            foreach (var key in keys)
            {
                if (m == key) return row;
            }
        }

        foreach (var (keys, row) in Table)
        {
            foreach (var key in keys.OrderByDescending(k => k.Length))
            {
                if (m.Contains(key, StringComparison.Ordinal))
                    return row;
            }
        }

        if (m.Contains("opus", StringComparison.Ordinal))
            return Table[0].Row;
        if (m.Contains("sonnet", StringComparison.Ordinal))
            return Table[6].Row;
        if (m.Contains("haiku", StringComparison.Ordinal))
            return Table[9].Row;

        return DefaultRow;
    }

    /// <summary>Estimate using 5m cache-write tier (Anthropic default).</summary>
    public static double Estimate(RuntimeUsageMetric m) => Estimate(m, use1hCacheWrite: false);

    public static double Estimate(RuntimeUsageMetric m, bool use1hCacheWrite)
    {
        var row = Resolve(m.Model);
        var writeRate = use1hCacheWrite ? row.CacheWrite1hPerMTok : row.CacheWrite5mPerMTok;
        return (m.InputTokens / 1_000_000.0) * row.InputPerMTok
             + (m.CacheCreationInputTokens / 1_000_000.0) * writeRate
             + (m.CacheReadInputTokens / 1_000_000.0) * row.CacheReadPerMTok
             + (m.OutputTokens / 1_000_000.0) * row.OutputPerMTok;
    }

    public static void ApplyCost(RuntimeUsageMetric m)
    {
        if (m.CostIsReported && m.EstimatedCostUsd is > 0) return;
        m.EstimatedCostUsd = Estimate(m);
        m.CostIsReported = false;
    }
}
