#!/usr/bin/env node
/**
 * MCP 手工验收台（开发期工具，非契约产物）。
 *
 * 用途：MCP 走 stdio，浏览器够不着。本脚本把 stdio 服务端桥接成 HTTP，
 * 再用一张网页把「有哪些工具、调用结果长什么样、原始流量长什么样」摊开给人看。
 *
 * 边界：
 * - 只做桥接与展示，不改变服务端契约，不新增任何工具；
 * - 桥接层自动完成握手，网页侧无需关心 initialize；
 * - 只监听 127.0.0.1，不对外暴露。
 *
 * 用法：node mcp/dev/web-harness.mjs [--port 8787]
 * 先决条件：服务端已编译（npm --prefix mcp run build）。
 */
import { spawn } from 'node:child_process';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { LATEST_PROTOCOL_VERSION } from '@modelcontextprotocol/server';

const here = path.dirname(fileURLToPath(import.meta.url));
const SERVER_ENTRY = path.resolve(here, '..', 'dist', 'src', 'index.js');

/** 解析端口：支持 `--port 8787` 与 `--port=8787` 两种写法。 */
function parsePort(argv) {
  const withEquals = argv.find((arg) => arg.startsWith('--port='));
  if (withEquals) {
    return Number(withEquals.slice('--port='.length));
  }
  const index = argv.indexOf('--port');
  if (index >= 0 && argv[index + 1]) {
    return Number(argv[index + 1]);
  }
  return 8787;
}

const PORT = parsePort(process.argv);

/* ------------------------------------------------------------------ *
 * stdio 桥：把 MCP 服务端接到 HTTP 上
 * ------------------------------------------------------------------ */

const traffic = []; // 双向原始报文，供页面展示
const sseClients = new Set();

function pushTraffic(direction, payload) {
  const entry = { direction, at: new Date().toISOString(), payload };
  traffic.push(entry);
  if (traffic.length > 200) {
    traffic.shift();
  }
  const line = `data: ${JSON.stringify(entry)}\n\n`;
  for (const client of sseClients) {
    client.write(line);
  }
}

const child = spawn(process.execPath, [SERVER_ENTRY], { stdio: ['pipe', 'pipe', 'pipe'] });

child.stdout.setEncoding('utf8');
child.stderr.setEncoding('utf8');
child.stderr.on('data', (chunk) => pushTraffic('stderr', chunk.trimEnd()));

let stdoutBuffer = '';
const pending = new Map();
let nextId = 1;

child.stdout.on('data', (chunk) => {
  stdoutBuffer += chunk;
  let index = stdoutBuffer.indexOf('\n');
  while (index >= 0) {
    const line = stdoutBuffer.slice(0, index).trim();
    stdoutBuffer = stdoutBuffer.slice(index + 1);
    if (line) {
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        pushTraffic('stdout', line);
        index = stdoutBuffer.indexOf('\n');
        continue;
      }
      pushTraffic('stdout', message);
      if (typeof message.id === 'number' && pending.has(message.id)) {
        pending.get(message.id)(message);
        pending.delete(message.id);
      }
    }
    index = stdoutBuffer.indexOf('\n');
  }
});

/** 向服务端发一条 JSON-RPC 请求，返回其响应。 */
function request(method, params) {
  const id = nextId++;
  const message = { jsonrpc: '2.0', id, method };
  if (params !== undefined) {
    message.params = params;
  }
  pushTraffic('stdin', message);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`请求超时：${method}`));
    }, 15000);
    pending.set(id, (response) => {
      clearTimeout(timer);
      resolve(response);
    });
    child.stdin.write(`${JSON.stringify(message)}\n`);
  });
}

/** 向服务端发一条通知（无响应）。 */
function notify(method, params) {
  const message = { jsonrpc: '2.0', method };
  if (params !== undefined) {
    message.params = params;
  }
  pushTraffic('stdin', message);
  child.stdin.write(`${JSON.stringify(message)}\n`);
}

/** 桥接层自动握手；页面侧不需要再发 initialize。 */
const handshake = await request('initialize', {
  protocolVersion: LATEST_PROTOCOL_VERSION,
  capabilities: {},
  clientInfo: { name: 'logcat-mcp-web-harness', version: '0.0.0' },
});
notify('notifications/initialized');

const serverInfo = {
  serverInfo: handshake.result?.serverInfo ?? null,
  protocolVersion: handshake.result?.protocolVersion ?? null,
  capabilities: handshake.result?.capabilities ?? null,
  instructions: handshake.result?.instructions ?? null,
};

/* ------------------------------------------------------------------ *
 * HTTP：页面 + 桥接端点
 * ------------------------------------------------------------------ */

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = '';
    req.on('data', (chunk) => {
      body += chunk;
    });
    req.on('end', () => resolve(body));
    req.on('error', reject);
  });
}

const PAGE = `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<title>MCP 手工验收台 · logcat-mcp</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; font: 14px/1.6 "Segoe UI", "Microsoft YaHei", sans-serif; background: #14161a; color: #e6e6e6; }
  header { padding: 16px 24px; border-bottom: 1px solid #2a2e35; display: flex; gap: 24px; align-items: baseline; flex-wrap: wrap; }
  h1 { font-size: 16px; margin: 0; }
  .meta { color: #9aa4b2; font-size: 12px; }
  .meta b { color: #e6e6e6; font-weight: 600; }
  main { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; padding: 16px 24px; }
  section { background: #1b1e24; border: 1px solid #2a2e35; border-radius: 8px; padding: 14px 16px; min-width: 0; }
  section.wide { grid-column: 1 / -1; }
  h2 { font-size: 13px; margin: 0 0 10px; color: #9aa4b2; text-transform: uppercase; letter-spacing: .06em; }
  button { background: #2f6feb; color: #fff; border: 0; border-radius: 6px; padding: 6px 12px; cursor: pointer; font-size: 13px; }
  button.secondary { background: #333a45; }
  button:hover { filter: brightness(1.12); }
  pre { background: #101216; border: 1px solid #262a31; border-radius: 6px; padding: 10px; overflow: auto; max-height: 340px; margin: 0; font-size: 12px; white-space: pre-wrap; word-break: break-all; }
  textarea, select, input { width: 100%; box-sizing: border-box; background: #101216; color: #e6e6e6; border: 1px solid #2a2e35; border-radius: 6px; padding: 8px; font-family: Consolas, monospace; font-size: 12px; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid #262a31; vertical-align: top; }
  th { color: #9aa4b2; font-weight: 600; font-size: 12px; }
  .row { display: flex; gap: 8px; margin-bottom: 10px; align-items: center; }
  .row > * { flex: none; }
  .row > textarea, .row > select { flex: 1; }
  .tag { display: inline-block; padding: 1px 7px; border-radius: 10px; background: #23303f; color: #7fb3ff; font-size: 11px; }
  .err { color: #ff7b72; }
  .ok { color: #7ee787; }
</style>
</head>
<body>
<header>
  <h1>MCP 手工验收台 · logcat-mcp</h1>
  <span class="meta">服务端 <b id="s-name">–</b> <span id="s-version"></span></span>
  <span class="meta">协议版本 <b id="s-proto">–</b></span>
  <span class="meta">能力集 <b id="s-caps">–</b></span>
</header>

<main>
  <section class="wide">
    <h2>工具清单</h2>
    <div class="row">
      <button id="btn-tools">刷新工具清单</button>
      <span class="meta">来源：tools/list</span>
    </div>
    <table id="tools"><thead><tr><th style="width:140px">名称</th><th style="width:160px">标题</th><th>描述</th><th style="width:260px">输入模式</th></tr></thead><tbody></tbody></table>
  </section>

  <section>
    <h2>调用工具</h2>
    <div class="row"><select id="tool-select"></select></div>
    <div class="row"><textarea id="tool-args" rows="3">{}</textarea></div>
    <div class="row">
      <button id="btn-call">调用</button>
      <button id="btn-info" class="secondary">快查 info</button>
    </div>
    <pre id="call-out">（尚未调用）</pre>
  </section>

  <section>
    <h2>原始 RPC</h2>
    <div class="row"><input id="rpc-method" value="tools/list" /></div>
    <div class="row"><textarea id="rpc-params" rows="3">{}</textarea></div>
    <div class="row"><button id="btn-rpc">发送</button></div>
    <pre id="rpc-out">（尚未发送）</pre>
  </section>

  <section class="wide">
    <h2>实时流量</h2>
    <div class="row"><button id="btn-clear" class="secondary">清空</button><span class="meta">stdin / stdout / stderr 全量原始报文</span></div>
    <pre id="traffic"></pre>
  </section>
</main>

<script>
const $ = (id) => document.getElementById(id);
const pretty = (v) => JSON.stringify(v, null, 2);

async function boot() {
  const meta = await (await fetch('/meta')).json();
  $('s-name').textContent = meta.serverInfo?.name ?? '–';
  $('s-version').textContent = meta.serverInfo?.version ? '(v' + meta.serverInfo.version + ')' : '';
  $('s-proto').textContent = meta.protocolVersion ?? '–';
  $('s-caps').innerHTML = '<span class="tag">' + Object.keys(meta.capabilities ?? {}).join(', ') + '</span>';
  await loadTools();
}

async function loadTools() {
  const res = await (await fetch('/tools')).json();
  const body = $('tools').querySelector('tbody');
  body.innerHTML = '';
  for (const tool of res.tools ?? []) {
    const tr = document.createElement('tr');
    tr.innerHTML = '<td><b>' + tool.name + '</b></td><td>' + (tool.title ?? '') + '</td><td>' + (tool.description ?? '') + '</td><td>' + pretty(tool.inputSchema) + '</td>';
    body.appendChild(tr);
  }
  const select = $('tool-select');
  select.innerHTML = '';
  for (const tool of res.tools ?? []) {
    const opt = document.createElement('option');
    opt.value = tool.name;
    opt.textContent = tool.name;
    select.appendChild(opt);
  }
}

async function callTool(name, args) {
  const out = $('call-out');
  out.textContent = '调用中…';
  const res = await (await fetch('/call', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name, arguments: args }) })).json();
  out.textContent = pretty(res);
  out.className = res.error ? 'err' : (res.isError ? 'err' : 'ok');
}

$('btn-tools').onclick = loadTools;
$('btn-call').onclick = () => {
  let args;
  try { args = JSON.parse($('tool-args').value || '{}'); }
  catch (e) { $('call-out').textContent = '入参不是合法 JSON：' + e.message; $('call-out').className = 'err'; return; }
  callTool($('tool-select').value, args);
};
$('btn-info').onclick = () => callTool('info', {});
$('btn-rpc').onclick = async () => {
  const out = $('rpc-out');
  let params;
  try { params = JSON.parse($('rpc-params').value || '{}'); }
  catch (e) { out.textContent = '参数不是合法 JSON：' + e.message; out.className = 'err'; return; }
  const res = await (await fetch('/rpc', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ method: $('rpc-method').value, params }) })).json();
  out.textContent = pretty(res);
  out.className = res.error ? 'err' : 'ok';
};

const stream = new EventSource('/events');
stream.onmessage = (event) => {
  const entry = JSON.parse(event.data);
  const line = '[' + entry.at + '] ' + entry.direction + '  ' + (typeof entry.payload === 'string' ? entry.payload : JSON.stringify(entry.payload)) + '\\n';
  $('traffic').textContent += line;
  $('traffic').scrollTop = $('traffic').scrollHeight;
};
$('btn-clear').onclick = () => { $('traffic').textContent = ''; };

boot();
</script>
</body>
</html>`;

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);

  if (req.method === 'GET' && url.pathname === '/') {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(PAGE);
    return;
  }

  if (req.method === 'GET' && url.pathname === '/meta') {
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(serverInfo));
    return;
  }

  if (req.method === 'GET' && url.pathname === '/tools') {
    const response = await request('tools/list');
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(response.result ?? { error: response.error }));
    return;
  }

  if (req.method === 'POST' && url.pathname === '/call') {
    const body = JSON.parse((await readBody(req)) || '{}');
    const response = await request('tools/call', { name: body.name, arguments: body.arguments ?? {} });
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(response.result ?? { error: response.error }));
    return;
  }

  if (req.method === 'POST' && url.pathname === '/rpc') {
    const body = JSON.parse((await readBody(req)) || '{}');
    if (typeof body.method !== 'string') {
      res.writeHead(400, { 'content-type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({ error: 'method 必须是字符串' }));
      return;
    }
    const response = await request(body.method, body.params);
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(response.result ?? { error: response.error }));
    return;
  }

  if (req.method === 'GET' && url.pathname === '/events') {
    res.writeHead(200, {
      'content-type': 'text/event-stream; charset=utf-8',
      'cache-control': 'no-cache',
      connection: 'keep-alive',
    });
    res.write(': connected\n\n');
    sseClients.add(res);
    req.on('close', () => sseClients.delete(res));
    return;
  }

  res.writeHead(404, { 'content-type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify({ error: 'not found' }));
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`MCP 手工验收台已启动：http://127.0.0.1:${PORT}`);
  console.log(`桥接的服务端：${SERVER_ENTRY}`);
});

const shutdown = () => {
  child.kill();
  server.close();
  process.exit(0);
};
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
