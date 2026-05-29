using DerivityMeter;
using DerivityMeter.Collectors;
using DerivityMeter.Infrastructure;
using DerivityMeter.Mcp;
using DerivityMeter.Models;
using DerivityMeter.Store;

namespace DerivityMeter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // ── Single-instance guard ──────────────────────────────────────────────

        const string MutexName = "Global\\DerivityMeter_SingleInstance";
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            try { EventWaitHandle.OpenExisting("Global\\DerivityMeter_BringToFront").Set(); } catch { }
            return;
        }

        var settings = MeterSettings.Load();
        var store = new RuntimeStore();
        store.Init(settings);

        // ── Port resolution ────────────────────────────────────────────────────

        int mcpPort, otelPort;
        string mcpError = "", otelError = "";

        try { mcpPort = LoopbackEndpointSelector.SelectMcpPort(settings.McpPort); }
        catch (Exception ex) { mcpPort = 0; mcpError = ex.Message; }

        try { otelPort = LoopbackEndpointSelector.SelectOtelPort(settings.Otel.OtelHttpPort); }
        catch (Exception ex) { otelPort = 0; otelError = ex.Message; }

        // ── Start services ─────────────────────────────────────────────────────

        var jsonCollector = new JsonFileCollector(settings, store);

        var otelCollector = new OtelCollector(settings, store);
        if (otelPort > 0) otelCollector.Start(otelPort);

        var mcp = new McpServer(store, settings);
        if (mcpPort > 0) mcp.Start(mcpPort);

        // ── Write runtime-status.json ──────────────────────────────────────────

        var status = new RuntimeStatusFile
        {
            Mcp = new() { Running = mcpPort > 0, Url = mcpPort > 0 ? $"tcp://127.0.0.1:{mcpPort}" : null },
            Otel = new() { Running = otelPort > 0, Url = otelPort > 0 ? $"http://127.0.0.1:{otelPort}" : null }
        };
        status.Save();

        // ── Write Claude Code env instructions ────────────────────────────────

        if (otelPort > 0)
        {
            var storeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".derivity", "runtime-meter");
            Directory.CreateDirectory(storeDir);
            File.WriteAllText(Path.Combine(storeDir, "claude-code-env.ps1"),
                $"""
                # Run these in your shell BEFORE starting Claude Code to send telemetry to DerivityMeter.
                # NOTE: http/json is REQUIRED — this receiver does not parse protobuf/gRPC (it returns 415).
                # Per-request token/cache data arrives via the logs (events) exporter; metrics also supported.
                $env:CLAUDE_CODE_ENABLE_TELEMETRY="1"
                $env:OTEL_LOGS_EXPORTER="otlp"
                $env:OTEL_METRICS_EXPORTER="otlp"
                $env:OTEL_EXPORTER_OTLP_PROTOCOL="http/json"
                $env:OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:{otelPort}"
                # Export quickly so numbers appear within seconds instead of the 60s default.
                $env:OTEL_METRIC_EXPORT_INTERVAL="5000"
                $env:OTEL_LOGS_EXPORT_INTERVAL="2000"
                """);
        }

        // ── Shared exit routine ────────────────────────────────────────────────

        void CleanupAndExit()
        {
            jsonCollector.Dispose();
            otelCollector.Dispose();
            mcp.Dispose();
            RuntimeStatusFile.Clear();
            mutex.ReleaseMutex();
            mutex.Dispose();
            Application.Exit();
        }

        // ── Launch overlay ─────────────────────────────────────────────────────

        var overlay = new OverlayForm(store, settings,
            otelCollector.IsRunning, otelPort,
            mcpPort, mcpError, otelError,
            onExit: CleanupAndExit);

        overlay.Show();
        overlay.Refresh();
        Application.Run(overlay);
    }
}
