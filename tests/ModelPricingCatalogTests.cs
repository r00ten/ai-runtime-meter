using DerivityMeter.Models;
using Xunit;

namespace DerivityMeter.Tests;

public class ModelPricingCatalogTests
{
    [Theory]
    [InlineData("claude-opus-4-7", 5, 0.50, 25)]
    [InlineData("claude-sonnet-4-6", 3, 0.30, 15)]
    [InlineData("claude-haiku-4-5", 1, 0.10, 5)]
    public void Resolve_maps_active_models(string model, double inputRate, double cacheReadRate, double outputRate)
    {
        var row = ModelPricingCatalog.Resolve(model);
        Assert.Equal(inputRate, row.InputPerMTok);
        Assert.Equal(cacheReadRate, row.CacheReadPerMTok);
        Assert.Equal(outputRate, row.OutputPerMTok);
    }

    [Fact]
    public void Opus_4_7_not_matched_to_legacy_opus_4_row()
    {
        var row = ModelPricingCatalog.Resolve("claude-opus-4-7");
        Assert.Equal(0.50, row.CacheReadPerMTok);
        Assert.Equal(5, row.InputPerMTok);
    }

    [Fact]
    public void Estimate_sonnet_matches_published_cache_read_rate()
    {
        var m = new RuntimeUsageMetric
        {
            Model = "claude-sonnet-4-6",
            InputTokens = 3000,
            CacheCreationInputTokens = 420,
            CacheReadInputTokens = 24000,
            OutputTokens = 900
        };
        var est = ModelPricingCatalog.Estimate(m);
        // 3k×3 + 420×3.75 + 24k×0.30 + 900×15  (all per M)
        Assert.InRange(est, 0.03, 0.04);
    }

    [Fact]
    public void ApplyCost_skips_when_reported_present()
    {
        var m = new RuntimeUsageMetric
        {
            Model = "claude-sonnet-4-6",
            InputTokens = 1_000_000,
            EstimatedCostUsd = 12.34,
            CostIsReported = true
        };
        ModelPricingCatalog.ApplyCost(m);
        Assert.Equal(12.34, m.EstimatedCostUsd);
        Assert.True(m.CostIsReported);
    }
}
