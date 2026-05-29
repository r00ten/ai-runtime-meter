using System.Text.Json;
using DerivityMeter.Models;

namespace DerivityMeter.Store;

public class RuntimeStore
{
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".derivity", "runtime-meter");

    private static readonly string EventsPath = Path.Combine(StoreDir, "runtime-events.jsonl");
    private static readonly string WarningsPath = Path.Combine(StoreDir, "warnings.jsonl");

    private readonly List<RuntimeUsageMetric> _recent = new();
    private readonly int _maxRecent = 100;
    private MeterSettings? _settings;

    public RuntimeUsageMetric? Current { get; private set; }
    public RuntimeUsageMetric? LastRequest { get; private set; }
    public IReadOnlyList<RuntimeUsageMetric> Recent => _recent.AsReadOnly();
    public SessionStore? Sessions { get; private set; }

    public event Action<RuntimeUsageMetric>? OnMetricReceived;

    public void Init(MeterSettings settings)
    {
        _settings = settings;
        Sessions = new SessionStore(settings);
    }

    public void Ingest(RuntimeUsageMetric metric, MeterSettings settings)
    {
        _settings ??= settings;
        Sessions ??= new SessionStore(settings);

        if (metric.EstimatedCostUsd is null or 0)
            metric.EstimatedCostUsd = settings.Pricing.Estimate(metric);

        LastRequest = Current;
        Current = metric;

        _recent.Insert(0, metric);
        if (_recent.Count > _maxRecent) _recent.RemoveAt(_recent.Count - 1);

        Sessions.Ingest(metric);
        Persist(metric, settings);
        OnMetricReceived?.Invoke(metric);
    }

    private void Persist(RuntimeUsageMetric metric, MeterSettings settings)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var line = JsonSerializer.Serialize(metric);
            File.AppendAllText(EventsPath, line + Environment.NewLine);

            var pressure = RuntimeDerivedMetric.ComputePressure(metric, settings);
            if (pressure >= PressureLevel.Warning)
            {
                var warning = new
                {
                    timestamp = metric.Timestamp,
                    level = pressure.ToString(),
                    cacheReadTokens = metric.CacheReadInputTokens,
                    estimatedCostUsd = metric.EstimatedCostUsd
                };
                File.AppendAllText(WarningsPath,
                    JsonSerializer.Serialize(warning) + Environment.NewLine);
            }
        }
        catch { /* never crash on persist */ }
    }

    public IReadOnlyList<RuntimeUsageMetric> GetRecent(int limit = 20) =>
        _recent.Take(limit).ToList().AsReadOnly();

    public string? LastOtelParseFailure { get; private set; }

    public void ReportOtelParseFailure(string reason)
    {
        LastOtelParseFailure = reason;
    }
}
