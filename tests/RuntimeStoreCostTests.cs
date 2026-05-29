using DerivityMeter.Models;
using DerivityMeter.Store;
using Xunit;

namespace DerivityMeter.Tests;

public class RuntimeStoreCostTests
{
    [Fact]
    public void Ingest_uses_reported_cost_and_accumulates_session()
    {
        var settings = new MeterSettings();
        var store = new RuntimeStore();
        store.Init(settings);

        var sessionId = "unit-cost-" + Guid.NewGuid().ToString("N");

        store.Ingest(new RuntimeUsageMetric
        {
            Model = "claude-opus-4-7",
            SessionId = sessionId,
            InputTokens = 500_000,
            CacheReadInputTokens = 5_000_000,
            EstimatedCostUsd = 14.50,
            CostIsReported = true
        }, settings);

        store.Ingest(new RuntimeUsageMetric
        {
            Model = "claude-opus-4-7",
            SessionId = sessionId,
            EstimatedCostUsd = 8.25,
            CostIsReported = true,
            InputTokens = 100
        }, settings);

        var s = store.Sessions!.Get(sessionId, null)!;
        Assert.Equal(22.75, s.EstimatedCostUsd, 2);
        Assert.Equal(22.75, s.CostReportedUsd, 2);
        Assert.Equal(0, s.CostEstimatedUsd);
    }

    [Fact]
    public void Ingest_estimates_with_model_row_when_no_reported_cost()
    {
        var settings = new MeterSettings();
        var store = new RuntimeStore();
        store.Init(settings);

        store.Ingest(new RuntimeUsageMetric
        {
            Model = "claude-opus-4-7",
            SessionId = "unit-est-" + Guid.NewGuid().ToString("N"),
            InputTokens = 1_000_000,
            CacheReadInputTokens = 1_000_000,
            OutputTokens = 100_000
        }, settings);

        var m = store.Current!;
        Assert.False(m.CostIsReported);
        // 1M in @ $5 + 1M cache rd @ $0.50 + 100k out @ $25 = 5 + 0.5 + 2.5 = 8
        Assert.InRange(m.EstimatedCostUsd!.Value, 7.5, 8.5);
    }
}
