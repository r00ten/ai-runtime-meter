using System.Text.Json;
using DerivityMeter.Models;

namespace DerivityMeter.Store;

public class SessionSummary
{
    public string SessionKey { get; set; } = "";
    public string? SessionId { get; set; }
    public string? ProjectPath { get; set; }
    public int Requests { get; set; }
    public long InputTokens { get; set; }
    public long CacheCreationInputTokens { get; set; }
    public long CacheReadInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EstimatedCostUsd { get; set; }
    public PressureLevel PressureLevel { get; set; }
    public string LastSeen { get; set; } = DateTime.UtcNow.ToString("o");
}

public class SessionStore
{
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".derivity", "runtime-meter");
    private static readonly string SessionsPath = Path.Combine(StoreDir, "sessions.json");

    private readonly Dictionary<string, SessionSummary> _sessions = new();
    private readonly MeterSettings _settings;

    public IReadOnlyDictionary<string, SessionSummary> Sessions => _sessions;

    public SessionStore(MeterSettings settings)
    {
        _settings = settings;
        Load();
    }

    public void Ingest(RuntimeUsageMetric m)
    {
        var key = m.SessionId ?? m.ProjectPath ?? m.Cwd ?? "default";
        if (!_sessions.TryGetValue(key, out var s))
        {
            s = new SessionSummary { SessionKey = key, SessionId = m.SessionId, ProjectPath = m.ProjectPath };
            _sessions[key] = s;
        }

        s.Requests++;
        s.InputTokens += m.InputTokens;
        s.CacheCreationInputTokens += m.CacheCreationInputTokens;
        s.CacheReadInputTokens += m.CacheReadInputTokens;
        s.OutputTokens += m.OutputTokens;
        s.EstimatedCostUsd += m.EstimatedCostUsd ?? 0;
        s.LastSeen = m.Timestamp;

        // session-level pressure uses session cost thresholds
        if (s.EstimatedCostUsd > _settings.Thresholds.CostSessionCritical)
            s.PressureLevel = PressureLevel.Critical;
        else if (s.EstimatedCostUsd > _settings.Thresholds.CostSessionWarning)
            s.PressureLevel = PressureLevel.Warning;
        else
            s.PressureLevel = RuntimeDerivedMetric.ComputePressure(m, _settings);

        Save();
    }

    public SessionSummary? Get(string? sessionId, string? projectPath)
    {
        var key = sessionId ?? projectPath ?? "default";
        return _sessions.TryGetValue(key, out var s) ? s : null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SessionsPath)) return;
            var json = File.ReadAllText(SessionsPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SessionSummary>>(json);
            if (loaded is null) return;
            foreach (var kv in loaded) _sessions[kv.Key] = kv.Value;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(SessionsPath,
                JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
