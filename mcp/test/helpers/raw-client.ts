/**
 * 测试用的最小 JSON-RPC 客户端。
 *
 * 不引入客户端 SDK：协议交互面很窄（初始化、列举、调用），自己发原始
 * JSON-RPC 消息反而让测试更贴近协议本身——被断言的是线上真实字节。
 */
import {
  InMemoryTransport,
  LATEST_PROTOCOL_VERSION,
  type JSONRPCMessage,
} from '@modelcontextprotocol/server';

import type { Server } from '@modelcontextprotocol/server';

interface Pending {
  resolve(value: unknown): void;
  reject(error: Error): void;
}

export interface RpcError {
  code: number;
  message: string;
  data?: unknown;
}

/** 请求返回的结果；带 `error` 字段表示协议层错误。 */
export interface RpcResponse {
  result?: unknown;
  error?: RpcError;
}

/**
 * 端到端客户端：持有传输的一端，按 id 匹配响应。
 *
 * 服务端发来的请求（如 ping）一律以「方法未找到」回绝——本壳层不要求客户端
 * 提供任何能力，回绝比沉默更能让测试暴露协议问题。
 */
export class RawClient {
  private readonly transport: InMemoryTransport;
  private readonly pending = new Map<string | number, Pending>();
  private nextId = 1;

  /** 收到的通知：方法名与参数。 */
  readonly notifications: Array<{ method: string; params?: unknown }> = [];

  constructor(transport: InMemoryTransport) {
    this.transport = transport;
    this.transport.onmessage = (message: JSONRPCMessage) => {
      this.handle(message);
    };
  }

  /** 建立与本进程内服务端的连接。 */
  static async connect(server: Server): Promise<RawClient> {
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    const client = new RawClient(clientTransport);
    await server.connect(serverTransport);
    return client;
  }

  private handle(message: JSONRPCMessage): void {
    const record = message as { id?: string | number; method?: string; result?: unknown; error?: RpcError };
    if (typeof record.method === 'string') {
      if (record.id === undefined) {
        this.notifications.push({ method: record.method, params: (record as { params?: unknown }).params });
        return;
      }
      // 服务端向客户端发来的请求：回绝即可，客户端侧无能力提供。
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
    if (!pending) {
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

  /** 发起通知（无响应）。 */
  notify(method: string, params?: unknown): void {
    const message: Record<string, unknown> = { jsonrpc: '2.0', method };
    if (params !== undefined) {
      message.params = params;
    }
    void this.transport.send(message as unknown as JSONRPCMessage);
  }

  /** 完成初始化握手。 */
  async initialize(): Promise<RpcResponse> {
    const response = await this.request('initialize', {
      protocolVersion: LATEST_PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: 'raw-test-client', version: '0.0.0' },
    });
    this.notify('notifications/initialized');
    return response;
  }

  async close(): Promise<void> {
    await this.transport.close();
  }
}
