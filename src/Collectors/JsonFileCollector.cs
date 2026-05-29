using System.Text.Json;
using DerivityMeter.Models;
using DerivityMeter.Store;

namespace DerivityMeter.Collectors;

public class JsonFileCollector : IDisposable
{
    private readonly MeterSettings _settings;
    private readonly RuntimeStore _store;
    private readonly System.Threading.Timer _timer;
    private string? _lastContent;

    public JsonFileCollector(MeterSettings settings, RuntimeStore store)
    {
        _settings = settings;
        _store = store;
        _timer = new System.Threading.Timer(_ => Poll(), null,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(settings.JsonPollIntervalMs));
    }

    private void Poll()
    {
        try
        {
            var path = _settings.JsonSourcePath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppContext.BaseDirectory, path);

            if (!File.Exists(path)) return;

            var content = File.ReadAllText(path);
            if (content == _lastContent) return;
            _lastContent = content;

            var source = JsonSerializer.Deserialize<ManualJsonSource>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (source is null) return;

            var metric = source.ToMetric();
            _store.Ingest(metric, _settings);
        }
        catch { /* bad JSON or file lock — skip tick */ }
    }

    public void Dispose() => _timer.Dispose();
}
