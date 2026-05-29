using System.Text.Json.Serialization;

namespace DerivityMeter.Models;

public enum RuntimeProvider { Anthropic, OpenAI, Local, Unknown }
public enum RuntimeSource { ClaudeCodeOtel, AnthropicUsageApi, OpenAIUsageApi, CodexCli, LocalAgent, ManualJson }
public enum PressureLevel { Normal, Watch, Warning, Critical }

public class RuntimeUsageMetric
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RuntimeProvider Provider { get; set; } = RuntimeProvider.Unknown;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RuntimeSource Source { get; set; } = RuntimeSource.ManualJson;

    public string? Model { get; set; }
    public string? SessionId { get; set; }
    public string? RequestId { get; set; }
    public string? ProjectPath { get; set; }
    public string? Cwd { get; set; }

    public long InputTokens { get; set; }
    public long CacheCreationInputTokens { get; set; }
    public long CacheReadInputTokens { get; set; }
    public long OutputTokens { get; set; }

    public long TotalInputSideTokens => InputTokens + CacheCreationInputTokens + CacheReadInputTokens;
    public long TotalTokens => TotalInputSideTokens + OutputTokens;

    public double? EstimatedCostUsd { get; set; }

    public int? ToolCallCount { get; set; }
    public List<string>? ToolNames { get; set; }

    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
}

public class RuntimeDerivedMetric
{
    public double CacheReadRatio { get; set; }
    public double CacheWriteRatio { get; set; }
    public double OutputRatio { get; set; }
    public double? CostPerRequest { get; set; }
    public double CacheReadPerTurn { get; set; }
    public PressureLevel PressureLevel { get; set; }

    public static RuntimeDerivedMetric From(RuntimeUsageMetric m, MeterSettings settings)
    {
        long total = m.TotalInputSideTokens;
        double cacheRead = total > 0 ? (double)m.CacheReadInputTokens / total : 0;
        double cacheWrite = total > 0 ? (double)m.CacheCreationInputTokens / total : 0;
        double output = m.TotalTokens > 0 ? (double)m.OutputTokens / m.TotalTokens : 0;

        return new RuntimeDerivedMetric
        {
            CacheReadRatio = cacheRead,
            CacheWriteRatio = cacheWrite,
            OutputRatio = output,
            CostPerRequest = m.EstimatedCostUsd,
            CacheReadPerTurn = m.CacheReadInputTokens,
            PressureLevel = ComputePressure(m, settings)
        };
    }

    public static PressureLevel ComputePressure(RuntimeUsageMetric m, MeterSettings settings)
    {
        var t = settings.Thresholds;
        if (m.CacheReadInputTokens > t.CacheReadCritical)     return PressureLevel.Critical;
        if (m.CacheReadInputTokens > t.CacheReadHighWarning)  return PressureLevel.Critical;
        if (m.CacheReadInputTokens > t.CacheReadWarning)      return PressureLevel.Warning;
        if (m.CacheReadInputTokens > t.CacheReadWatch)        return PressureLevel.Watch;
        if (m.EstimatedCostUsd > t.CostRequestCritical)       return PressureLevel.Critical;
        if (m.EstimatedCostUsd > t.CostRequestWarning)        return PressureLevel.Warning;
        return PressureLevel.Normal;
    }
}

public class ManualJsonSource
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    [JsonPropertyName("input_tokens")] public long InputTokens { get; set; }
    [JsonPropertyName("cache_creation_input_tokens")] public long CacheCreationInputTokens { get; set; }
    [JsonPropertyName("cache_read_input_tokens")] public long CacheReadInputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("estimated_cost")] public double? EstimatedCost { get; set; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }

    public RuntimeUsageMetric ToMetric() => new()
    {
        Provider = Provider?.ToLower() switch
        {
            "anthropic" => RuntimeProvider.Anthropic,
            "openai" => RuntimeProvider.OpenAI,
            "local" => RuntimeProvider.Local,
            _ => RuntimeProvider.Unknown
        },
        Source = RuntimeSource.ManualJson,
        Model = Model,
        InputTokens = InputTokens,
        CacheCreationInputTokens = CacheCreationInputTokens,
        CacheReadInputTokens = CacheReadInputTokens,
        OutputTokens = OutputTokens,
        EstimatedCostUsd = EstimatedCost,
        Timestamp = UpdatedAt ?? DateTime.UtcNow.ToString("o")
    };
}
