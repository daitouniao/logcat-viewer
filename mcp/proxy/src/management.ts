/**
 * 管理面：只绑回环地址的 HTTP 服务，带启动随机令牌。
 *
 * 绑回环挡得住局域网访问，但挡不住 DNS rebinding（恶意网页可让浏览器把请求
 * 发向 127.0.0.1），故另有令牌与 Host 校验两道闸。
 */
import { randomBytes } from 'node:crypto';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';

import {
  maskEntry,
  mergeMaskedEntry,
  saveConfigAtomic,
  validateConfig,
  type ProxyConfig,
  type UpstreamEntry,
} from './config.js';
import { MANAGEMENT_PAGE_HTML } from './page.js';
import type { UpstreamManager } from './upstream.js';

export interface ManagementServerOptions {
  manager: UpstreamManager;
  config: ProxyConfig;
  configPath: string;
  /** 0 或缺省表示随机空闲端口。 */
  port?: number;
}

export interface ManagementServerHandle {
  port: number;
  token: string;
  url: string;
  close(): Promise<void>;
}

/** 令牌头名。 */
export const TOKEN_HEADER = 'x-proxy-token';

/** 启动管理面。 */
export async function startManagementServer(
  options: ManagementServerOptions,
): Promise<ManagementServerHandle> {
  const token = randomBytes(24).toString('hex');
  const config: ProxyConfig = { upstreams: [...options.config.upstreams] };

  const server = createServer((request, response) => {
    void handleRequest(request, response, { token, config, options });
  });

  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(options.port ?? 0, '127.0.0.1', () => {
      server.removeListener('error', reject);
      resolve();
    });
  });

  const address = server.address();
  const port = typeof address === 'object' && address !== null ? address.port : (options.port ?? 0);

  return {
    port,
    token,
    url: `http://127.0.0.1:${port}/?token=${token}`,
    close: () =>
      new Promise<void>((resolve) => {
        server.close(() => resolve());
      }),
  };
}

interface RequestContext {
  token: string;
  config: ProxyConfig;
  options: ManagementServerOptions;
}

async function handleRequest(
  request: IncomingMessage,
  response: ServerResponse,
  context: RequestContext,
): Promise<void> {
  const url = new URL(request.url ?? '/', 'http://127.0.0.1');
  const method = request.method ?? 'GET';

  // Host 校验：只接受指向本机的请求（防 DNS rebinding）。
  const host = request.headers.host ?? '';
  if (!isLocalHostHeader(host)) {
    respondJson(response, 403, { error: 'Host header is not accepted' });
    return;
  }

  if (url.pathname === '/' || url.pathname === '/index.html') {
    respondHtml(response, 200, MANAGEMENT_PAGE_HTML);
    return;
  }

  if (!url.pathname.startsWith('/api/')) {
    respondJson(response, 404, { error: 'Not found' });
    return;
  }

  const supplied = request.headers[TOKEN_HEADER];
  const headerToken = Array.isArray(supplied) ? supplied[0] : supplied;
  const token = headerToken ?? url.searchParams.get('token') ?? '';
  if (token !== context.token) {
    respondJson(response, 401, { error: 'Invalid or missing token' });
    return;
  }

  try {
    await route(request, response, url, context);
  } catch (error) {
    respondJson(response, 500, {
      error: error instanceof Error ? error.message : String(error),
    });
  }
}

async function route(
  request: IncomingMessage,
  response: ServerResponse,
  url: URL,
  context: RequestContext,
): Promise<void> {
  const method = request.method ?? 'GET';
  const { config, options } = context;
  const manager = options.manager;

  if (url.pathname === '/api/servers' && method === 'GET') {
    respondJson(response, 200, {
      servers: manager.snapshots().map((snapshot) => {
        const entry = config.upstreams.find((item) => item.id === snapshot.id);
        return {
          ...snapshot,
          entry: entry ? maskEntry(entry) : undefined,
        };
      }),
    });
    return;
  }

  if (url.pathname === '/api/servers' && method === 'POST') {
    const body = await readJson(request);
    const entry = toEntry(body);
    if (config.upstreams.some((item) => item.id === entry.id)) {
      respondJson(response, 409, { error: `Upstream '${entry.id}' already exists` });
      return;
    }
    config.upstreams.push(entry);
    await persistAndApply(context, entry.id);
    respondJson(response, 200, { ok: true, id: entry.id });
    return;
  }

  const match = /^\/api\/servers\/([^/]+)(?:\/(start|stop|restart|diagnostics))?$/.exec(url.pathname);
  if (match !== null) {
    const id = decodeURIComponent(match[1] ?? '');
    const action = match[2];

    if (action === undefined && method === 'GET') {
      const snapshot = manager.snapshots().find((item) => item.id === id);
      if (snapshot === undefined) {
        respondJson(response, 404, { error: `Upstream '${id}' is not configured` });
        return;
      }
      respondJson(response, 200, snapshot);
      return;
    }

    if (action === undefined && method === 'PUT') {
      const body = await readJson(request);
      const incoming = toEntry(body);
      const existing = config.upstreams.find((item) => item.id === id);
      if (existing === undefined) {
        respondJson(response, 404, { error: `Upstream '${id}' is not configured` });
        return;
      }
      const merged = mergeMaskedEntry(existing, incoming);
      const index = config.upstreams.indexOf(existing);
      config.upstreams[index] = merged;
      await persistAndApply(context, id);
      respondJson(response, 200, { ok: true, id });
      return;
    }

    if (action === undefined && method === 'DELETE') {
      const index = config.upstreams.findIndex((item) => item.id === id);
      if (index < 0) {
        respondJson(response, 404, { error: `Upstream '${id}' is not configured` });
        return;
      }
      config.upstreams.splice(index, 1);
      await saveConfigAtomic(options.configPath, config);
      await manager.remove(id);
      respondJson(response, 200, { ok: true, id });
      return;
    }

    if (action === 'diagnostics' && method === 'GET') {
      respondJson(response, 200, { id, diagnostics: manager.diagnostics(id) });
      return;
    }

    if (action !== undefined && method === 'POST') {
      if (action === 'start') {
        const ok = await manager.start(id);
        respondJson(response, ok ? 200 : 500, { ok, id });
        return;
      }
      if (action === 'stop') {
        await manager.stop(id);
        respondJson(response, 200, { ok: true, id });
        return;
      }
      if (action === 'restart') {
        const ok = await manager.restart(id);
        respondJson(response, ok ? 200 : 500, { ok, id });
        return;
      }
    }
  }

  respondJson(response, 404, { error: 'Not found' });
}

/** 落盘并把条目应用到运行中的管理器。 */
async function persistAndApply(context: RequestContext, id: string): Promise<void> {
  await saveConfigAtomic(context.options.configPath, context.config);
  const entry = context.config.upstreams.find((item) => item.id === id);
  if (entry !== undefined) {
    await context.options.manager.upsert(entry);
  }
}

/** 把请求体转成条目并校验。 */
function toEntry(body: unknown): UpstreamEntry {
  const validated = validateConfig({ upstreams: [body] });
  const entry = validated.upstreams[0];
  if (entry === undefined) {
    throw new Error('Invalid upstream entry');
  }
  return entry;
}

/** 读取 JSON 请求体。 */
async function readJson(request: IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(String(chunk)));
  }
  const text = Buffer.concat(chunks).toString('utf8');
  if (text.trim().length === 0) {
    return {};
  }
  try {
    return JSON.parse(text);
  } catch (error) {
    throw new Error(`Request body is not valid JSON: ${(error as Error).message}`);
  }
}

/** Host 头是否指向本机。 */
export function isLocalHostHeader(host: string): boolean {
  if (host.length === 0) {
    return false;
  }
  const withoutPort = host.startsWith('[')
    ? host.slice(0, host.indexOf(']') + 1)
    : (host.split(':')[0] ?? '');
  return withoutPort === '127.0.0.1' || withoutPort === 'localhost' || withoutPort === '[::1]';
}

function respondJson(response: ServerResponse, status: number, payload: unknown): void {
  const body = JSON.stringify(payload);
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
  });
  response.end(body);
}

function respondHtml(response: ServerResponse, status: number, html: string): void {
  response.writeHead(status, {
    'content-type': 'text/html; charset=utf-8',
    'cache-control': 'no-store',
  });
  response.end(html);
}
