using DerivityMeter.Collectors;
using Xunit;

namespace DerivityMeter.Tests;

public class OtelCollectorParseTests
{
    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.ReadAllText(path);
    }

    [Fact]
    public void Log_event_api_request_maps_all_four_token_fields()
    {
        var metrics = OtelCollector.ParseLogPayload(Fixture("claude_code_api_request_log.json"));

        var m = Assert.Single(metrics);
        Assert.Equal(3000, m.InputTokens);
        Assert.Equal(900, m.OutputTokens);
        Assert.Equal(24000, m.CacheReadInputTokens);   // the field that was silently 0 before
        Assert.Equal(420, m.CacheCreationInputTokens);
        Assert.Equal("claude-sonnet-4-6", m.Model);
        Assert.Equal("sess-123", m.SessionId);
    }

    [Fact]
    public void Metric_token_usage_maps_camelCase_type_dimension()
    {
        var metrics = OtelCollector.ParseMetricPayload(Fixture("claude_code_token_usage_metrics.json"));

        var m = Assert.Single(metrics);
        Assert.Equal(3000, m.InputTokens);
        Assert.Equal(900, m.OutputTokens);
        Assert.Equal(24000, m.CacheReadInputTokens);   // type=cacheRead (camelCase) — was 0 before
        Assert.Equal(420, m.CacheCreationInputTokens); // type=cacheCreation (camelCase)
    }

    [Fact]
    public void Log_and_metric_paths_produce_identical_canonical_totals()
    {
        var fromLog = Assert.Single(OtelCollector.ParseLogPayload(Fixture("claude_code_api_request_log.json")));
        var fromMetric = Assert.Single(OtelCollector.ParseMetricPayload(Fixture("claude_code_token_usage_metrics.json")));

        Assert.Equal(fromLog.InputTokens, fromMetric.InputTokens);
        Assert.Equal(fromLog.OutputTokens, fromMetric.OutputTokens);
        Assert.Equal(fromLog.CacheReadInputTokens, fromMetric.CacheReadInputTokens);
        Assert.Equal(fromLog.CacheCreationInputTokens, fromMetric.CacheCreationInputTokens);
        Assert.Equal(fromLog.TotalTokens, fromMetric.TotalTokens);
    }

    [Fact]
    public void Non_usage_event_yields_nothing_and_is_not_a_failure()
    {
        var dbg = new OtelParseDiagnostics();
        var metrics = OtelCollector.ParseLogPayload(Fixture("claude_code_user_prompt_log.json"), dbg);

        Assert.Empty(metrics);
        Assert.Empty(dbg.UnknownTokenLikeKeys); // prompt_length is not flagged as a missed token field
    }

    [Fact]
    public void Unknown_token_like_field_is_surfaced_for_debug()
    {
        // input maps; thinking_tokens does not yet map -> should be flagged, not silently dropped
        const string body = """
        {
          "resourceLogs": [{
            "resource": { "attributes": [] },
            "scopeLogs": [{ "logRecords": [{
              "attributes": [
                { "key": "input_tokens", "value": { "stringValue": "100" } },
                { "key": "thinking_tokens", "value": { "stringValue": "5000" } }
              ]
            }]}]
          }]
        }
        """;

        var dbg = new OtelParseDiagnostics();
        var m = Assert.Single(OtelCollector.ParseLogPayload(body, dbg));

        Assert.Equal(100, m.InputTokens);
        Assert.Contains("thinking_tokens", dbg.UnknownTokenLikeKeys);
    }

    [Fact]
    public void Config_field_max_tokens_is_never_counted()
    {
        const string body = """
        {
          "resourceLogs": [{
            "resource": { "attributes": [] },
            "scopeLogs": [{ "logRecords": [{
              "attributes": [
                { "key": "max_tokens", "value": { "stringValue": "8192" } },
                { "key": "output_tokens", "value": { "stringValue": "250" } }
              ]
            }]}]
          }]
        }
        """;

        var m = Assert.Single(OtelCollector.ParseLogPayload(body));
        Assert.Equal(0, m.InputTokens);
        Assert.Equal(250, m.OutputTokens);
    }
}
