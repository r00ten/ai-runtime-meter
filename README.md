# AI Runtime Meter

> **Runtime meter for Claude Code** — live token, cache, and cost visibility on your desktop, in real time.

A lightweight Windows overlay that monitors Claude Code token usage and cost in real time via OpenTelemetry (OTLP/HTTP) and exposes a local MCP server for in-session querying.

Built to expand beyond Claude Code (OpenAI-compatible CLIs, Derivity S0/CLI, provider stacks, MCP tools).

## Download

[**Download the latest release**](https://github.com/r00ten/ai-runtime-meter/releases/latest) — single self-contained `.exe`, Windows x64, no .NET install required. Unzip and run.

## What it does

- Receives OTLP telemetry from Claude Code (logs + metrics over HTTP/JSON)
- Displays per-request token usage (input, cache write, cache read, output), **tokens in** (input + cache write + cache read), and cost
- Uses **`cost_usd` from OTEL when present** (reported); otherwise **model-aware estimated** pricing from the built-in Anthropic table
- Tracks session-level cumulative totals; **reported** and **estimated** cost kept separate in Session view and MCP
- Exposes a JSON-RPC 2.0 MCP server over TCP so Claude itself can query runtime stats
- Writes `runtime-status.json` and `claude-code-env.ps1` to `~/.derivity/runtime-meter/` on startup

## UI

Borderless always-on-top overlay, bottom-right of screen. 236px wide.

- **Compact bar** (32px): current request **$** · cache read · output tokens. Color = cache **volume** pressure (not dollar alerts). × to exit.
- **Expanded panel** (click to toggle): two tabs — **Live** (per-request detail) and **Session** (cumulative totals). Derivity logo at bottom.
- Opens expanded on launch showing endpoint status. Auto-collapses on first metric received.
- Drag to reposition. Right-click → Clear data / Exit.
- Cursor is always hand.

## Pressure levels

| Level | Cache read tokens |
|---|---|
| Watch | > 50k |
| Warning | > 100k |
| Critical (high) | > 500k |
| Critical | > 1M |

Cost thresholds also apply: warning > $0.25/request, critical > $1.00/request.

## Ports

| Service | Default | Config key |
|---|---|---|
| OTLP HTTP receiver | 14318 | `Otel.OtelHttpPort` |
| MCP TCP server | 7891 | `McpPort` |

Both auto-resolve to free ports if defaults are taken. Actual ports written to `runtime-status.json`.

## MCP tools (via mcp-bridge.mjs)

Claude Code connects via stdio bridge (`scripts/mcp-bridge.mjs`) registered with `claude mcp add`.

- `get_current_runtime_usage` — latest request metrics
- `get_last_request_usage` — previous request metrics
- `get_session_usage` — session aggregate (params: sessionId, projectPath)
- `get_cache_warning` — pressure level + recommendation
- `get_otel_status` — OTLP receiver status
- `list_recent_runtime_events` — last N events (default 20, max 100)
- `resources_read` — read URI: `derivity-runtime://current`, `//last-request`, `//sessions`, `//warnings`

Bridge is read-only, talks only to 127.0.0.1:7891, does not shell out, does not read project files.

## Claude Code OTLP setup

Set these env vars before launching Claude Code:

```powershell
$env:CLAUDE_CODE_ENABLE_TELEMETRY="1"
$env:OTEL_LOGS_EXPORTER="otlp"
$env:OTEL_METRICS_EXPORTER="otlp"
$env:OTEL_EXPORTER_OTLP_PROTOCOL="http/json"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:14318"
# Optional but recommended — show numbers within seconds instead of the 60s default:
$env:OTEL_METRIC_EXPORT_INTERVAL="5000"
$env:OTEL_LOGS_EXPORT_INTERVAL="2000"
```

These are written automatically to `~/.derivity/runtime-meter/claude-code-env.ps1` on startup. Use the exact port the meter chose (printed in the overlay and in `runtime-status.json`).

> **`http/json` is mandatory.** This receiver parses OTLP/HTTP **JSON** only. If `OTEL_EXPORTER_OTLP_PROTOCOL` is unset or `grpc`/`http/protobuf`, Claude Code sends protobuf, the receiver replies `415`, and **every count silently stays 0**. This is the single most common cause of a blank/zero meter.

### How token/cache fields are read

Claude Code reports usage two ways and the meter normalizes both into the same canonical fields at ingest (so JSONL, sessions, overlay, and MCP always agree):

- **Log events** (`claude_code.api_request`, the path that fires in current Claude Code): `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_creation_tokens`.
- **Metrics** (`claude_code.token.usage`): one counter split by a `type` dimension whose values are `input` / `output` / `cacheRead` / `cacheCreation` (camelCase).

The normalizer accepts camelCase, snake_case, dotted, and `*_input_tokens` variants of all four, and ignores config fields like `max_tokens`. (Historic bug: cache counts read as 0 because only `cache_*_input_tokens` / snake-case `cache_read` were matched, never the live `cache_read_tokens` / camelCase `cacheRead` shapes.)

### Troubleshooting: meter shows 0 cache / 0 tokens

1. Confirm `OTEL_EXPORTER_OTLP_PROTOCOL="http/json"` is set in the **same shell** that launches `claude` (protobuf → 415 → zeros).
2. Confirm the endpoint port matches the meter's actual port (overlay / `runtime-status.json`), not a hardcoded 4317.
3. Give it a few seconds, or set the export intervals above.
4. Turn on debug: set `"Otel": { "Debug": true }` in `settings.json`, restart the meter, run one Claude turn, then inspect `~/.derivity/runtime-meter/otel-debug.jsonl` — it lists every attribute key received and any token-like field that did not map (no payload content is written). Unmapped token fields are also reported in the overlay's last-parse-failure line.

## Scripts (`scripts/`)

| Script | Purpose |
|---|---|
| `Start-Meter.ps1` | Launch meter (or bring to front if already running) |
| `Start-WithMeter.ps1` | Launch meter + wait for OTLP ready + set env vars + launch `claude` |
| `Copy-OtelEnv.ps1` | Copy OTLP env var block to clipboard |
| `Show-Status.ps1` | Print current MCP/OTLP endpoint status |
| `Install-MeterMcp.ps1` | Register MCP bridge in Claude Code user settings (`-Uninstall` to remove) |

## Persistence

All data written to `~/.derivity/runtime-meter/`:

| File | Contents |
|---|---|
| `runtime-status.json` | Active MCP/OTLP endpoints (cleared on exit) |
| `runtime-events.jsonl` | Append-only log of all received metrics |
| `sessions.json` | Session-level aggregates keyed by session ID or project path |
| `warnings.jsonl` | Events that hit Warning or above |
| `claude-code-env.ps1` | OTLP env var export script |
| `otel-debug.jsonl` | Diagnostic log of received OTLP attribute keys + parse outcome (only when `Otel.Debug` is true or a token-like field fails to map) |
| `settings.json` | User config (thresholds, pricing, ports) |

Right-click → Clear data truncates events, warnings, and resets sessions.

## Settings (`~/.derivity/runtime-meter/settings.json`)

```json
{
  "Thresholds": {
    "CacheReadWatch": 50000,
    "CacheReadWarning": 100000,
    "CacheReadHighWarning": 500000,
    "CacheReadCritical": 1000000,
    "CostRequestWarning": 0.25,
    "CostRequestCritical": 1.00,
    "CostSessionWarning": 5.00,
    "CostSessionCritical": 15.00
  },
  "Pricing": {
    "InputPerMTok": 3.00,
    "CacheWritePerMTok": 3.75,
    "CacheReadPerMTok": 0.30,
    "OutputPerMTok": 15.00
  },
  "Otel": { "OtelHttpPort": 0, "Debug": false },
  "McpPort": 0,
  "JsonPollIntervalMs": 1000,
  "JsonSourcePath": "meter.json"
}
```

## Project structure

```
ai-runtime-meter/                — self-contained tool folder
├── README.md
├── src/                         — application project
│   ├── DerivityMeter.csproj
│   ├── Program.cs               — entry point, single-instance guard, service startup
│   ├── OverlayForm.cs           — WinForms overlay UI
│   ├── derivity_logo.png        — embedded brand logo (transparent PNG)
│   ├── Models/
│   │   ├── RuntimeMetric.cs     — metric types, pressure logic
│   │   ├── RuntimeCost.cs       — OTEL cost_usd parsing
│   │   ├── ModelPricingCatalog.cs — Anthropic $/M fallback by model
│   │   └── MeterSettings.cs     — settings, thresholds, port config
│   ├── Collectors/
│   │   ├── OtelCollector.cs     — OTLP HTTP receiver (logs + metrics)
│   │   ├── TokenUsageNormalizer.cs — alias → canonical token-field normalization
│   │   └── JsonFileCollector.cs — polls meter.json for manual/external input
│   ├── Store/
│   │   ├── RuntimeStore.cs      — in-memory store, event persistence
│   │   └── SessionStore.cs      — session aggregation, sessions.json
│   ├── Mcp/
│   │   └── McpServer.cs         — JSON-RPC 2.0 TCP MCP server
│   └── Infrastructure/
│       ├── RuntimeStatus.cs     — runtime-status.json read/write
│       └── LoopbackEndpointSelector.cs — port auto-resolution
├── tests/                       — xUnit test project
│   ├── DerivityMeter.Tests.csproj
│   ├── *Tests.cs
│   └── Fixtures/                — representative Claude Code OTLP JSON
└── scripts/                     — PowerShell helpers + MCP bridge
```

## Normal quit path

Use overlay × button or right-click → Exit. This runs cleanup: releases mutex, clears `runtime-status.json`, stops OTLP and MCP listeners. `taskkill` is an emergency fallback only — it skips cleanup.

---

_Not affiliated with, endorsed by, or sponsored by Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic, PBC._

© 2026 [Derivity](https://derivity.io) · Licensed under [Apache-2.0](LICENSE)
