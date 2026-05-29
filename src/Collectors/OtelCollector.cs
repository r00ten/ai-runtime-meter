using System.Net;
using System.Text;
using System.Text.Json;
using DerivityMeter.Models;
using DerivityMeter.Store;

namespace DerivityMeter.Collectors;

/// <summary>
/// Minimal OTLP/HTTP receiver. Port is resolved externally via LoopbackEndpointSelector.
/// Accepts POST /v1/logs and /v1/metrics from the Claude Code OTLP exporter.
/// Supports JSON payloads only. Protobuf payloads are reported, not parsed.
///
/// All token/cache aliases are normalized into RuntimeUsageMetric's canonical internal
/// fields (via TokenUsageNormalizer) BEFORE the metric reaches the store — so JSONL
/// persistence, session aggregation, the overlay, and MCP all see identical values.
///
/// Never stores prompt text, source code, tool output bodies, or conversation content.
/// </summary>
public class OtelCollector : IDisposable
{
    private static readonly string DebugDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".derivity", "runtime-meter");
    private static readonly string DebugPath = Path.Combine(DebugDir, "otel-debug.jsonl");

    private readonly MeterSettings _settings;
    private readonly RuntimeStore _store;
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public bool IsRunning { get; private set; }
    public int BoundPort { get; private set; }

    public OtelCollector(MeterSettings settings, RuntimeStore store)
    {
        _settings = settings;
        _store = store;
    }

    public void Start(int port)
    {
        try
        {
            var prefix = $"http://127.0.0.1:{port}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            BoundPort = port;
            IsRunning = true;
            _task = Task.Run(() => ListenLoop(_cts.Token));
        }
        catch
        {
            IsRunning = false;
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && (_listener?.IsListening ?? false))
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleRequest(ctx), ct);
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var contentType = ctx.Request.ContentType ?? "";
            var path = ctx.Request.Url?.AbsolutePath ?? "";

            // Protobuf detection — do not attempt to parse, report honestly
            if (contentType.Contains("protobuf") || contentType.Contains("application/x-protobuf"))
            {
                var buf = new byte[4096];
                while (await ctx.Request.InputStream.ReadAsync(buf, _cts.Token) > 0) { }

                _store.ReportOtelParseFailure("protobuf payload received — parser not supported in v1. " +
                    "Set OTEL_EXPORTER_OTLP_PROTOCOL=http/json for this receiver.");

                ctx.Response.StatusCode = 415; // Unsupported Media Type
                ctx.Response.Close();
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            var dbg = new OtelParseDiagnostics();
            List<RuntimeUsageMetric> metrics;
            string signal;

            if (path.Contains("logs")) { metrics = ParseLogPayload(body, dbg); signal = "logs"; }
            else if (path.Contains("metrics")) { metrics = ParseMetricPayload(body, dbg); signal = "metrics"; }
            else { metrics = new(); signal = "unknown"; }

            foreach (var m in metrics) _store.Ingest(m, _settings);

            MaybeWriteDebug(signal, path, dbg, metrics.Count);

            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    // ── Parsing (static + testable; no store/IO side effects) ──────────────────

    internal static List<RuntimeUsageMetric> ParseLogPayload(string body, OtelParseDiagnostics? dbg = null)
    {
        var result = new List<RuntimeUsageMetric>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("resourceLogs", out var resourceLogs)) return result;

            foreach (var rl in resourceLogs.EnumerateArray())
            {
                var resourceAttrs = GetAttributeMap(rl, "resource");
                if (!rl.TryGetProperty("scopeLogs", out var scopeLogs)) continue;

                foreach (var sl in scopeLogs.EnumerateArray())
                {
                    if (!sl.TryGetProperty("logRecords", out var records)) continue;
                    foreach (var record in records.EnumerateArray())
                    {
                        var attrs = GetAttributeMap(record, null);
                        if (dbg is not null) { dbg.RecordsSeen++; dbg.SeenKeys.UnionWith(attrs.Keys); }

                        var metric = BuildFromAttributes(attrs, resourceAttrs, dbg);
                        if (metric is not null) result.Add(metric);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    internal static List<RuntimeUsageMetric> ParseMetricPayload(string body, OtelParseDiagnostics? dbg = null)
    {
        var result = new List<RuntimeUsageMetric>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("resourceMetrics", out var resourceMetrics)) return result;

            foreach (var rm in resourceMetrics.EnumerateArray())
            {
                var resourceAttrs = GetAttributeMap(rm, "resource");
                if (!rm.TryGetProperty("scopeMetrics", out var scopeMetrics)) continue;

                foreach (var sm in scopeMetrics.EnumerateArray())
                {
                    if (!sm.TryGetProperty("metrics", out var metrics)) continue;
                    foreach (var m in metrics.EnumerateArray())
                    {
                        var metric = BuildFromMetric(m, resourceAttrs, dbg);
                        if (metric is not null) result.Add(metric);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    // ── Builders ───────────────────────────────────────────────────────────────

    private static RuntimeUsageMetric? BuildFromAttributes(
        Dictionary<string, string> attrs,
        Dictionary<string, string> resourceAttrs,
        OtelParseDiagnostics? dbg)
    {
        var totals = new TokenTotals();

        // Tokens are only taken from the record's own attributes (resource attributes
        // never carry per-request counts) — this avoids any chance of double counting.
        foreach (var kv in attrs)
        {
            var kind = TokenUsageNormalizer.ClassifyAttributeKey(kv.Key);
            if (kind != TokenKind.None)
            {
                if (TokenUsageNormalizer.TryParseCount(kv.Value, out var v)) totals.Add(kind, v);
            }
            else if (dbg is not null && TokenUsageNormalizer.IsUnknownTokenLike(kv.Key))
            {
                dbg.UnknownTokenLikeKeys.Add(kv.Key);
            }
        }

        if (!totals.HasAny) return null;
        return NewMetric(totals, attrs, resourceAttrs);
    }

    private static RuntimeUsageMetric? BuildFromMetric(
        JsonElement metric,
        Dictionary<string, string> resourceAttrs,
        OtelParseDiagnostics? dbg)
    {
        var name = metric.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (!name.Contains("token", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("usage", StringComparison.OrdinalIgnoreCase))
            return null;

        JsonElement dataPoints = default;
        if (!((metric.TryGetProperty("sum", out var sum) && sum.TryGetProperty("dataPoints", out dataPoints)) ||
              (metric.TryGetProperty("gauge", out var gauge) && gauge.TryGetProperty("dataPoints", out dataPoints))))
            return null;

        var totals = new TokenTotals();
        Dictionary<string, string>? pointAttrs = null;

        foreach (var dp in dataPoints.EnumerateArray())
        {
            var attrs = GetAttributeMap(dp, null);
            pointAttrs ??= attrs;
            if (dbg is not null) { dbg.RecordsSeen++; dbg.SeenKeys.UnionWith(attrs.Keys); }

            long val = 0;
            if (dp.TryGetProperty("asInt", out var ai)) TokenUsageNormalizer.TryParseCount(ai.GetRawText().Trim('"'), out val);
            else if (dp.TryGetProperty("asDouble", out var ad) && double.TryParse(ad.GetRawText(), out var dv)) val = (long)dv;

            // Explicit type dimension first; fall back to the metric name only if absent.
            var typeValue = StrFrom(attrs, resourceAttrs, "gen_ai.token.type", "llm.token.type", "token_type", "type");
            var kind = TokenUsageNormalizer.ClassifyTypeValue(typeValue);
            if (kind == TokenKind.None) kind = TokenUsageNormalizer.ClassifyTypeValue(name);

            if (kind != TokenKind.None) totals.Add(kind, val);
            else if (dbg is not null && !string.IsNullOrEmpty(typeValue)) dbg.UnknownTokenLikeKeys.Add($"type={typeValue}");
        }

        if (!totals.HasAny) return null;
        return NewMetric(totals, pointAttrs ?? new Dictionary<string, string>(), resourceAttrs);
    }

    private static RuntimeUsageMetric NewMetric(
        TokenTotals t,
        Dictionary<string, string> attrs,
        Dictionary<string, string> resourceAttrs) => new()
    {
        Provider = RuntimeProvider.Anthropic,
        Source = RuntimeSource.ClaudeCodeOtel,
        Model = StrFrom(attrs, resourceAttrs, "gen_ai.response.model", "llm.model", "model"),
        SessionId = StrFrom(attrs, resourceAttrs, "claude.session_id", "session.id", "session_id", "sessionId"),
        RequestId = StrFrom(attrs, resourceAttrs, "gen_ai.request.id", "request.id", "request_id", "requestId"),
        ProjectPath = StrFrom(attrs, resourceAttrs, "process.cwd", "cwd", "project.path", "project_path"),
        InputTokens = t.Input,
        CacheCreationInputTokens = t.CacheCreation,
        CacheReadInputTokens = t.CacheRead,
        OutputTokens = t.Output,
        Timestamp = DateTime.UtcNow.ToString("o")
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, string> GetAttributeMap(JsonElement element, string? containerKey)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        JsonElement attrArray;
        if (containerKey is not null)
        {
            if (!element.TryGetProperty(containerKey, out var container)) return result;
            if (!container.TryGetProperty("attributes", out attrArray)) return result;
        }
        else
        {
            if (!element.TryGetProperty("attributes", out attrArray)) return result;
        }

        if (attrArray.ValueKind != JsonValueKind.Array) return result;

        foreach (var attr in attrArray.EnumerateArray())
        {
            if (!attr.TryGetProperty("key", out var keyProp)) continue;
            var key = keyProp.GetString() ?? "";
            if (!attr.TryGetProperty("value", out var val)) continue;

            var strVal =
                val.TryGetProperty("stringValue", out var sv) ? sv.GetString() :
                val.TryGetProperty("intValue", out var iv) ? iv.GetRawText().Trim('"') :
                val.TryGetProperty("doubleValue", out var dv) ? dv.GetRawText() :
                null;

            if (strVal is not null) result[key] = strVal;
        }

        return result;
    }

    private static string? StrFrom(Dictionary<string, string> a, Dictionary<string, string> b, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (a.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (b.TryGetValue(k, out var v2) && !string.IsNullOrEmpty(v2)) return v2;
        }
        return null;
    }

    private void MaybeWriteDebug(string signal, string path, OtelParseDiagnostics dbg, int ingested)
    {
        // Always log when explicit debug is on; otherwise only when a payload carried
        // token-like fields we could not map (the real "silent zero" failure signal).
        // Benign non-usage events (user_prompt, tool_result) stay silent.
        var captureFailed = ingested == 0 && dbg.UnknownTokenLikeKeys.Count > 0;
        if (!_settings.Otel.Debug && !captureFailed) return;

        if (captureFailed)
            _store.ReportOtelParseFailure(
                $"{signal}: received token-like fields that did not map: " +
                string.Join(", ", dbg.UnknownTokenLikeKeys));

        try
        {
            Directory.CreateDirectory(DebugDir);
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                signal,
                path,
                ingested,
                recordsSeen = dbg.RecordsSeen,
                attributeKeys = dbg.SeenKeys.OrderBy(x => x).ToArray(),
                unknownTokenLikeKeys = dbg.UnknownTokenLikeKeys.OrderBy(x => x).ToArray()
            });
            File.AppendAllText(DebugPath, line + Environment.NewLine);
        }
        catch { /* never crash on debug write */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _task?.Wait(500);
    }
}
