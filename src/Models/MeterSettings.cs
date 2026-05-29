using System.Text.Json;

namespace DerivityMeter.Models;

public class WarningThresholds
{
    public long CacheReadWatch { get; set; } = 50_000;
    public long CacheReadWarning { get; set; } = 100_000;
    public long CacheReadHighWarning { get; set; } = 500_000;
    public long CacheReadCritical { get; set; } = 1_000_000;
    public double CostRequestWarning { get; set; } = 0.25;
    public double CostRequestCritical { get; set; } = 1.00;
    public double CostSessionWarning { get; set; } = 5.00;
    public double CostSessionCritical { get; set; } = 15.00;
}

public class PricingModel
{
    public double InputPerMTok { get; set; } = 3.00;
    public double CacheWritePerMTok { get; set; } = 3.75;
    public double CacheReadPerMTok { get; set; } = 0.30;
    public double OutputPerMTok { get; set; } = 15.00;

    public double Estimate(RuntimeUsageMetric m) =>
        (m.InputTokens / 1_000_000.0) * InputPerMTok
        + (m.CacheCreationInputTokens / 1_000_000.0) * CacheWritePerMTok
        + (m.CacheReadInputTokens / 1_000_000.0) * CacheReadPerMTok
        + (m.OutputTokens / 1_000_000.0) * OutputPerMTok;
}

public class OtelSettings
{
    /// <summary>
    /// User-configured preferred OTLP port. 0 = use auto-resolution.
    /// Preferred order: OtelHttpPort (if > 0) → 14318 → 4318 → 14319–14340.
    /// </summary>
    public int OtelHttpPort { get; set; } = 0;

    /// <summary>
    /// When true, every received OTLP payload's attribute keys and parse outcome are
    /// written to ~/.derivity/runtime-meter/otel-debug.jsonl. Use to diagnose missing
    /// token/cache capture. Off by default; no payload content is ever written.
    /// </summary>
    public bool Debug { get; set; } = false;
}

public class MeterSettings
{
    public WarningThresholds Thresholds { get; set; } = new();
    public PricingModel Pricing { get; set; } = new();
    public OtelSettings Otel { get; set; } = new();
    public int JsonPollIntervalMs { get; set; } = 1000;
    public string JsonSourcePath { get; set; } = "meter.json";

    /// <summary>
    /// User-configured preferred MCP port. 0 = use auto-resolution (7891 → 7892–7910).
    /// </summary>
    public int McpPort { get; set; } = 0;

    /// <summary>
    /// Last workspace directory chosen in the Launchers tab. Persisted across restarts.
    /// </summary>
    public string? LastWorkspacePath { get; set; }

    /// <summary>
    /// CLI command used for "Launch Claude in IDE". Defaults to "code" (VS Code).
    /// </summary>
    public string PreferredIde { get; set; } = "code";

    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".derivity", "runtime-meter", "settings.json");

    public static MeterSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new MeterSettings();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<MeterSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new MeterSettings();
        }
        catch { return new MeterSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
