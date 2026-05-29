// mcp-bridge.mjs — stdio MCP bridge to DerivityMeter TCP server
// Spawned by Claude Code as an MCP stdio server.
// Translates MCP tools/call requests into DerivityMeter JSON-RPC TCP calls.

import { createConnection } from 'net';
import { createInterface } from 'readline';
import { readFileSync } from 'fs';
import { join } from 'path';

const STATUS_FILE = join(process.env.USERPROFILE ?? '', '.derivity', 'runtime-meter', 'runtime-status.json');
const DEFAULT_MCP_PORT = 7891;

const TOOLS = [
  { name: 'get_current_runtime_usage',  description: 'Current Claude Code token usage and cost for the active request.',       inputSchema: { type: 'object', properties: {} } },
  { name: 'get_last_request_usage',     description: 'Token usage and cost for the previous request.',                          inputSchema: { type: 'object', properties: {} } },
  { name: 'get_session_usage',          description: 'Aggregate token usage and cost for a session or project path.',           inputSchema: { type: 'object', properties: { sessionId: { type: 'string' }, projectPath: { type: 'string' } } } },
  { name: 'get_cache_warning',          description: 'Cache read pressure level and recommendation (normal/watch/warning/critical).', inputSchema: { type: 'object', properties: {} } },
  { name: 'get_otel_status',            description: 'DerivityMeter OTEL receiver status and any parse failure details.',        inputSchema: { type: 'object', properties: {} } },
  { name: 'list_recent_runtime_events', description: 'List recent token usage events (newest first).',                          inputSchema: { type: 'object', properties: { limit: { type: 'number', description: 'Max events to return (1–100, default 20)' } } } },
  { name: 'resources_read',             description: 'Read a DerivityMeter resource URI (derivity-runtime://current, //last-request, //sessions, //warnings).', inputSchema: { type: 'object', properties: { uri: { type: 'string' } }, required: ['uri'] } },
];

// Map tool name → DerivityMeter method name
const METHOD = {
  resources_read: 'resources/read',
};
const method = (name) => METHOD[name] ?? name;

function getMcpPort() {
  try {
    const s = JSON.parse(readFileSync(STATUS_FILE, 'utf8'));
    if (s?.Mcp?.Running && s?.Mcp?.Url) {
      const m = s.Mcp.Url.match(/:(\d+)$/);
      if (m) return parseInt(m[1], 10);
    }
  } catch { /* file missing or meter not started */ }
  return DEFAULT_MCP_PORT;
}

function callMeter(port, rpcMethod, params) {
  return new Promise((resolve, reject) => {
    const sock = createConnection({ host: '127.0.0.1', port }, () => {
      const req = JSON.stringify({ jsonrpc: '2.0', id: 1, method: rpcMethod, ...(params ? { params } : {}) });
      sock.write(req + '\n');
    });
    let buf = '';
    sock.on('data', (d) => { buf += d.toString(); });
    sock.on('end', () => {
      try { resolve(JSON.parse(buf.trim())); }
      catch { reject(new Error('Invalid JSON from meter')); }
    });
    sock.on('error', reject);
    setTimeout(() => { sock.destroy(); reject(new Error('Meter TCP timeout')); }, 5000);
  });
}

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function ok(id, result) {
  send({ jsonrpc: '2.0', id, result });
}

function err(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

rl.on('line', async (line) => {
  line = line.trim();
  if (!line) return;

  let req;
  try { req = JSON.parse(line); } catch { return; }

  const { id, method: m, params } = req;

  if (m === 'initialize') {
    ok(id, {
      protocolVersion: req.params?.protocolVersion ?? '2024-11-05',
      capabilities: { tools: {} },
      serverInfo: { name: 'derivity-meter', version: '1.0.0' },
    });
    return;
  }

  if (m === 'notifications/initialized') return;

  if (m === 'tools/list') {
    ok(id, { tools: TOOLS });
    return;
  }

  if (m === 'tools/call') {
    const toolName = params?.name;
    const toolArgs = params?.arguments ?? {};
    const tool = TOOLS.find(t => t.name === toolName);
    if (!tool) { err(id, -32601, `Unknown tool: ${toolName}`); return; }

    const port = getMcpPort();
    try {
      const resp = await callMeter(port, method(toolName), Object.keys(toolArgs).length ? toolArgs : undefined);
      if (resp.error) {
        ok(id, { content: [{ type: 'text', text: JSON.stringify(resp.error) }], isError: true });
      } else {
        ok(id, { content: [{ type: 'text', text: JSON.stringify(resp.result, null, 2) }] });
      }
    } catch (e) {
      ok(id, { content: [{ type: 'text', text: `DerivityMeter not reachable: ${e.message}` }], isError: true });
    }
    return;
  }

  err(id, -32601, `Method not found: ${m}`);
});
