/**
 * 测试公用件：假上游的路径、构造配置、原始 JSON-RPC 客户端。
 */
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { InMemoryTransport, LATEST_PROTOCOL_VERSION, type JSONRPCMessage } from '@modelcontextprotocol/server';
import type { Server } from '@modelcontextprotocol/server';

import type { ProxyConfig, UpstreamEntry } from '../../src/config.js';
import { UpstreamManager } from '../../src/upstream.js';

/** 假上游脚本（编译后）的绝对路径。 */
export const FAKE_UPSTREAM_SCRIPT = join(dirname(fileURLToPath(import.meta.url)), 'fake-upstream.js');

/** 已创建的管理器：用例失败时也要收尾，否则子进程残留会把整个套件挂住。 */
const trackedManagers: UpstreamManager[] = [];

/** 关闭全部已创建的管理器（用例结束时调用）。 */
export async function shutdownAllManagers(): Promise<void> {
  while (trackedManagers.length > 0) {
    const manager = trackedManagers.pop();
    if (manager !== undefined) {
      await manager.shutdown();
    }
  }
}

/** 创建并登记一个上游管理器。 */
export function createManager(
  entries: UpstreamEntry[],
  options: { readyTimeoutMs?: number; callTimeoutMs?: number } = {},
): UpstreamManager {
  const manager = new UpstreamManager({
    config: configOf(...entries),
    ...(options.readyTimeoutMs !== undefined ? { readyTimeoutMs: options.readyTimeoutMs } : {}),
    ...(options.callTimeoutMs !== undefined ? { callTimeoutMs: options.callTimeoutMs } : {}),
  });
  trackedManagers.push(manager);
  return manager;
}

/**
 * 登记式的上游管理器：构造即登记，套件结束时统一收尾。
 *
 * 用例断言失败时不会走到用例末尾的收尾代码，上游子进程会残留并把整个测试
 * 进程挂住——登记式构造从根上避免这件事。
 */
export class TrackedUpstreamManager extends UpstreamManager {
  constructor(options: ConstructorParameters<typeof UpstreamManager>[0]) {
    super(options);
    trackedManagers.push(this);
  }
}

/** 构造一个指向假上游的条目。 */
export function fakeUpstream(
  id: string,
  extraArgs: string[] = [],
  overrides: Partial<UpstreamEntry> = {},
): UpstreamEntry {
  return {
    id,
    command: process.execPath,
    args: [FAKE_UPSTREAM_SCRIPT, ...extraArgs],
    env: {},
    enabled: true,
    ...overrides,
  };
}

/** 构造只含给定条目的配置。 */
export function configOf(...upstreams: UpstreamEntry[]): ProxyConfig {
  return { upstreams };
}

interface Pending {
  resolve(value: unknown): void;
  reject(error: Error): void;
}

/** 协议响应：带 error 字段表示协议层失败。 */
export interface RpcResponse {
  result?: unknown;
  error?: { code: number; message: string };
}

/** 最小原始 JSON-RPC 客户端：贴近协议本身发字节。 */
export class RawClient {
  private readonly transport: InMemoryTransport;
  private readonly pending = new Map<string | number, Pending>();
  private nextId = 1;

  private constructor(transport: InMemoryTransport) {
    this.transport = transport;
    this.transport.onmessage = (message: JSONRPCMessage) => {
      this.handle(message);
    };
  }

  /** 与本进程内的服务端建立连接。 */
  static async connect(server: Server): Promise<RawClient> {
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    const client = new RawClient(clientTransport);
    await server.connect(serverTransport);
    return client;
  }

  private handle(message: JSONRPCMessage): void {
    const record = message as { id?: string | number; method?: string; result?: unknown; error?: unknown };
    if (typeof record.method === 'string') {
      if (record.id === undefined) {
        return;
      }
      void this.transport.send({
        jsonrpc: '2.0',
        id: record.id,
        error: { code: -32601, message: 'Method not found' },
      } as JSONRPCMessage);
      return;
    }
    if (record.id === undefined) {
      return;
    }
    const pending = this.pending.get(record.id);
    if (pending === undefined) {
      return;
    }
    this.pending.delete(record.id);
    pending.resolve({ result: record.result, error: record.error });
  }

  /** 发起请求并等待响应。 */
  request(method: string, params?: unknown): Promise<RpcResponse> {
    const id = this.nextId++;
    const promise = new Promise<unknown>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
    });
    const message: Record<string, unknown> = { jsonrpc: '2.0', id, method };
    if (params !== undefined) {
      message.params = params;
    }
    void this.transport.send(message as unknown as JSONRPCMessage);
    return promise as Promise<RpcResponse>;
  }

  /** 完成初始化握手。 */
  async initialize(): Promise<RpcResponse> {
    const response = await this.request('initialize', {
      protocolVersion: LATEST_PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: 'raw-test-client', version: '0.0.0' },
    });
    await this.request('notifications/initialized');
    return response;
  }

  /** 调用工具。 */
  callTool(name: string, args?: Record<string, unknown>): Promise<RpcResponse> {
    return this.request('tools/call', { name, arguments: args ?? {} });
  }

  /** 列举工具。 */
  listTools(): Promise<RpcResponse> {
    return this.request('tools/list');
  }

  async close(): Promise<void> {
    await this.transport.close();
  }
}

/** 结果体的便捷读取。 */
export function resultOf(response: RpcResponse): {
  content?: Array<{ type: string; text?: string }>;
  structuredContent?: Record<string, unknown>;
  isError?: boolean;
} {
  return (response.result ?? {}) as {
    content?: Array<{ type: string; text?: string }>;
    structuredContent?: Record<string, unknown>;
    isError?: boolean;
  };
}

/** 错误载荷的便捷读取。 */
export function errorOf(response: RpcResponse): { code: string; message: string } | undefined {
  const result = resultOf(response);
  const structured = result.structuredContent;
  if (structured === undefined) {
    return undefined;
  }
  const error = structured['error'];
  if (typeof error !== 'object' || error === null) {
    return undefined;
  }
  const record = error as { code?: unknown; message?: unknown };
  return {
    code: typeof record.code === 'string' ? record.code : '',
    message: typeof record.message === 'string' ? record.message : '',
  };
}
