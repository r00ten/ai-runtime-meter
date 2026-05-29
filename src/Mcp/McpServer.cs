using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using DerivityMeter.Models;
using DerivityMeter.Store;

namespace DerivityMeter.Mcp;

/// <summary>
/// Read-only JSON-RPC 2.0 MCP server over TCP (newline-delimited).
/// Listens on localhost:McpPort (default 7891).
/// Contract §12: exposes runtime metadata only. No writes, no shell, no cloud.
/// </summary>
public class McpServer : IDisposable
{
    private readonly RuntimeStore _store;
    private readonly MeterSettings _settings;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    private static readonly HashSet<string> KnownMethods = new()
    {
        "get_current_runtime_usage",
        "get_last_request_usage",
        "get_session_usage",
        "get_cache_warning",
        "get_otel_status",
        "list_recent_runtime_events",
        "resources/read"
    };

    public int BoundPort { get; private set; }

    public McpServer(RuntimeStore store, MeterSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    public void Start(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            BoundPort = port;
            _serverTask = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch { /* port in use — MCP unavailable, overlay still works */ }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClient(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        while (!ct.IsCancellationRequested && client.Connected)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch { break; }

            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            await writer.WriteLineAsync(Dispatch(line));
        }
    }

    private string Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetRawText() : "null";
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";

            if (!KnownMethods.Contains(method))
                return Error(id, -32601, $"Method not found: {method}");

            var result = method switch
            {
                "get_current_runtime_usage"  => (object?)_store.Current,
                "get_last_request_usage"     => _store.LastRequest,
                "get_session_usage"          => GetSessionUsage(root),
                "get_cache_warning"          => GetCacheWarning(),
                "get_otel_status"            => GetOtelStatus(),
                "list_recent_runtime_events" => ListRecent(root),
                "resources/read"             => ReadResource(root),
                _                            => null
            };

            return Ok(id, result);
        }
        catch (Exception ex)
        {
            return Error("null", -32700, ex.Message);
        }
    }

    private object GetSessionUsage(JsonElement root)
    {
        string? sessionId = null, projectPath = null;
        if (root.TryGetProperty("params", out var p))
        {
            if (p.TryGetProperty("sessionId", out var s)) sessionId = s.GetString();
            if (p.TryGetProperty("projectPath", out var pp)) projectPath = pp.GetString();
        }

        var session = _store.Sessions?.Get(sessionId, projectPath);
        if (session is null)
            return new { requests = 0, inputTokens = 0L, cacheCreationInputTokens = 0L,
                         cacheReadInputTokens = 0L, outputTokens = 0L,
                         estimatedCostUsd = (double?)null, pressureLevel = "normal" };

        return new
        {
            requests = session.Requests,
            lastModel = session.LastModel,
            inputTokens = session.InputTokens,
            cacheCreationInputTokens = session.CacheCreationInputTokens,
            cacheReadInputTokens = session.CacheReadInputTokens,
            outputTokens = session.OutputTokens,
            estimatedCostUsd = (double?)session.EstimatedCostUsd,
            costReportedUsd = session.CostReportedUsd,
            costEstimatedUsd = session.CostEstimatedUsd,
            pressureLevel = session.PressureLevel.ToString().ToLower()
        };
    }

    private object GetCacheWarning()
    {
        var m = _store.Current;
        if (m is null)
            return new { level = "normal", reason = "No data yet.",
                         lastCacheReadTokens = 0L,
                         recommendation = "Start DerivityMeter before your Claude Code session." };

        var level = RuntimeDerivedMetric.ComputePressure(m, _settings);
        var t = _settings.Thresholds;

        return new
        {
            level = level.ToString().ToLower(),
            reason = level switch
            {
                PressureLevel.Critical => $"Cache read {m.CacheReadInputTokens:N0} tokens exceeds critical threshold ({t.CacheReadCritical:N0})",
                PressureLevel.Warning  => $"Cache read {m.CacheReadInputTokens:N0} tokens exceeds warning threshold ({t.CacheReadWarning:N0})",
                PressureLevel.Watch    => $"Cache read {m.CacheReadInputTokens:N0} tokens approaching warning threshold",
                _                      => "Cache read within normal range."
            },
            lastCacheReadTokens = m.CacheReadInputTokens,
            recommendation = level switch
            {
                PressureLevel.Critical => "Consider running /compact or starting a new project context.",
                PressureLevel.Warning  => "Monitor cache growth. Consider /compact if context is stale.",
                PressureLevel.Watch    => "Cache read growing. No action needed yet.",
                _                      => "Normal."
            }
        };
    }

    private object GetOtelStatus()
    {
        var failure = _store.LastOtelParseFailure;
        return new
        {
            parseFailure = failure,
            note = failure is not null
                ? "Set OTEL_EXPORTER_OTLP_PROTOCOL=http/json to use this receiver."
                : (object?)null
        };
    }

    private object ListRecent(JsonElement root)
    {
        int limit = 20;
        if (root.TryGetProperty("params", out var p) && p.TryGetProperty("limit", out var l))
            limit = Math.Clamp(l.GetInt32(), 1, 100);
        return _store.GetRecent(limit);
    }

    private object? ReadResource(JsonElement root)
    {
        string? uri = null;
        if (root.TryGetProperty("params", out var p) && p.TryGetProperty("uri", out var u))
            uri = u.GetString();

        return uri switch
        {
            "derivity-runtime://current"      => (object?)_store.Current,
            "derivity-runtime://last-request" => _store.LastRequest,
            "derivity-runtime://sessions"     => _store.Sessions?.Sessions,
            "derivity-runtime://warnings"     => _store.GetRecent(100)
                .Where(m => RuntimeDerivedMetric.ComputePressure(m, _settings) >= PressureLevel.Warning)
                .ToList(),
            _ => null
        };
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string Ok(string id, object? result) =>
        JsonSerializer.Serialize(
            new { jsonrpc = "2.0", id = ParseId(id), result },
            _jsonOpts);

    private static string Error(string id, int code, string message) =>
        JsonSerializer.Serialize(
            new { jsonrpc = "2.0", id = ParseId(id), error = new { code, message } },
            _jsonOpts);

    private static object? ParseId(string raw)
    {
        try { return JsonSerializer.Deserialize<object>(raw); }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _serverTask?.Wait(500);
    }
}
