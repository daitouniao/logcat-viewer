/**
 * 上游生命周期与调用路由。
 *
 * 一个上游 = 一个常驻子进程 + 一条客户端会话。代理为每个已启用上游维护一份，
 * 进程内共享（下游是标准输入输出，一个实例同时只服务一个下游客户端）。
 */
import { spawn } from 'node:child_process';

import { Client } from '@modelcontextprotocol/client';
import type { CallToolResult } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';

import { ToolErrorCode, type ToolErrorCodeValue } from './contract.js';
import { resolveEntryEnv, type ProxyConfig, type UpstreamEntry } from './config.js';
import { ToolRegistry, type ToolEntry, type UpstreamToolDefinition } from './registry.js';
import { SERVER_NAME } from './version.js';

/** 上游就绪时限：拉起后多久内没握手成功即判不可用。 */
export const DEFAULT_READY_TIMEOUT_MS = 10_000;

/** 上游调用时限：超过即中止并按上游不可用返回。 */
export const DEFAULT_CALL_TIMEOUT_MS = 60_000;

/** 每个上游保留的诊断输出尾部行数。 */
export const DIAGNOSTICS_TAIL_LINES = 200;

/** 上游运行状态。 */
export type UpstreamStatus = 'stopped' | 'starting' | 'running' | 'failed';

/** 上游状态快照：页面与内置工具看的就是它。 */
export interface UpstreamSnapshot {
  id: string;
  status: UpstreamStatus;
  toolCount: number;
  lastError?: string;
  diagnostics: string[];
}

/** 代理层的失败：带稳定错误码。 */
export class ProxyError extends Error {
  readonly code: ToolErrorCodeValue;

  constructor(code: ToolErrorCodeValue, message: string) {
    super(message);
    this.name = 'ProxyError';
    this.code = code;
  }
}

/** 调用被客户端取消。 */
export class CallCancelledError extends Error {
  constructor() {
    super('Call cancelled by client');
    this.name = 'CallCancelledError';
  }
}

/** 超时：由代理自己计时，不依赖 SDK 内部实现。 */
export class UpstreamTimeoutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'UpstreamTimeoutError';
  }
}

interface UpstreamRuntime {
  entry: UpstreamEntry;
  status: UpstreamStatus;
  lastError?: string;
  diagnostics: string[];
  client?: Client;
  transport?: StdioClientTransport;
  toolCount: number;
}

export interface UpstreamManagerOptions {
  config: ProxyConfig;
  readyTimeoutMs?: number;
  callTimeoutMs?: number;
}

export class UpstreamManager {
  private readonly registry = new ToolRegistry();
  private readonly runtimes = new Map<string, UpstreamRuntime>();
  private readonly readyTimeoutMs: number;
  private readonly callTimeoutMs: number;

  constructor(options: UpstreamManagerOptions) {
    this.readyTimeoutMs = options.readyTimeoutMs ?? DEFAULT_READY_TIMEOUT_MS;
    this.callTimeoutMs = options.callTimeoutMs ?? DEFAULT_CALL_TIMEOUT_MS;
    for (const entry of options.config.upstreams) {
      this.runtimes.set(entry.id, {
        entry,
        status: 'stopped',
        diagnostics: [],
        toolCount: 0,
      });
    }
  }

  /** 拉起全部已启用的上游；并发执行，单个失败不影响其他上游。 */
  async startAll(): Promise<void> {
    const pending: Array<Promise<unknown>> = [];
    for (const runtime of this.runtimes.values()) {
      if (runtime.entry.enabled) {
        pending.push(this.start(runtime.entry.id));
      }
    }
    await Promise.all(pending);
  }

  /** 拉起单个上游。 */
  async start(id: string): Promise<boolean> {
    const runtime = this.runtimes.get(id);
    if (runtime === undefined) {
      return false;
    }
    if (runtime.status === 'running') {
      return true;
    }

    runtime.status = 'starting';
    runtime.lastError = undefined;

    const transport = new StdioClientTransport({
      command: runtime.entry.command,
      args: runtime.entry.args,
      env: resolveEntryEnv(runtime.entry),
      stderr: 'pipe',
      ...(runtime.entry.cwd ? { cwd: runtime.entry.cwd } : {}),
    });

    this.attachDiagnostics(runtime, transport);
    transport.onclose = () => {
      if (runtime.status === 'running') {
        runtime.status = 'failed';
        runtime.lastError = 'upstream process exited';
      }
    };

    const client = new Client({ name: SERVER_NAME, version: '0.1.0' });

    try {
      const timeout = runtime.entry.readyTimeoutMs ?? this.readyTimeoutMs;
      await withTimeout(
        client.connect(transport, { timeout }),
        timeout,
        `upstream '${id}' did not become ready within ${timeout}ms`,
      );

      const listed = await client.listTools();
      const tools = (listed.tools ?? []) as UpstreamToolDefinition[];
      const result = this.registry.register(id, tools);
      if (result.conflict !== undefined) {
        throw new ProxyError(
          ToolErrorCode.UpstreamUnavailable,
          `upstream '${id}' tool name conflicts with '${result.conflict.existingUpstreamId}': ${result.conflict.downstreamName}`,
        );
      }

      runtime.client = client;
      runtime.transport = transport;
      runtime.status = 'running';
      runtime.toolCount = result.added.length;
      return true;
    } catch (error) {
      runtime.status = 'failed';
      runtime.lastError = describeError(error);
      runtime.client = undefined;
      runtime.transport = undefined;
      runtime.toolCount = 0;
      this.registry.removeUpstream(id);
      await closeQuietly(client, transport);
      return false;
    }
  }

  /** 停止单个上游：终止进程、从对应表移除其工具。 */
  async stop(id: string): Promise<void> {
    const runtime = this.runtimes.get(id);
    if (runtime === undefined) {
      return;
    }
    const { client, transport } = runtime;
    runtime.client = undefined;
    runtime.transport = undefined;
    runtime.status = 'stopped';
    runtime.toolCount = 0;
    // 停用而非删除：名字仍可解析，调用时能报「上游不可用」而不是「工具不存在」。
    this.registry.deactivate(id);
    await closeQuietly(client, transport);
  }

  /** 重启单个上游：先停后起，工具集合随之刷新。 */
  async restart(id: string): Promise<boolean> {
    await this.stop(id);
    return this.start(id);
  }

  /** 新增或替换一个上游条目，并按启用状态拉起。 */
  async upsert(entry: UpstreamEntry): Promise<void> {
    const existing = this.runtimes.get(entry.id);
    if (existing !== undefined) {
      await this.stop(entry.id);
    }
    this.runtimes.set(entry.id, {
      entry,
      status: 'stopped',
      diagnostics: existing?.diagnostics ?? [],
      toolCount: 0,
    });
    if (entry.enabled) {
      await this.start(entry.id);
    }
  }

  /** 移除一个上游条目（连带终止其进程）。 */
  async remove(id: string): Promise<void> {
    await this.stop(id);
    this.runtimes.delete(id);
  }

  /** 全部上游的状态快照。 */
  snapshots(): UpstreamSnapshot[] {
    return [...this.runtimes.values()].map((runtime) => ({
      id: runtime.entry.id,
      status: runtime.status,
      toolCount: runtime.toolCount,
      ...(runtime.lastError ? { lastError: runtime.lastError } : {}),
      diagnostics: [...runtime.diagnostics],
    }));
  }

  /** 聚合后的全部工具条目。 */
  toolEntries(): ToolEntry[] {
    return this.registry.list();
  }

  /** 按下游名查对应关系。 */
  resolveTool(downstreamName: string): ToolEntry | undefined {
    return this.registry.resolve(downstreamName);
  }

  /** 某上游的诊断输出尾部。 */
  diagnostics(id: string): string[] {
    return [...(this.runtimes.get(id)?.diagnostics ?? [])];
  }

  /** 路由调用：按对应表转发给上游。 */
  async callTool(
    downstreamName: string,
    args: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<CallToolResult> {
    const entry = this.registry.resolve(downstreamName);
    if (entry === undefined) {
      throw new ProxyError(ToolErrorCode.NotFound, `Tool not found: ${downstreamName}`);
    }

    const runtime = this.runtimes.get(entry.upstreamId);
    const client = runtime?.client;
    if (runtime === undefined || client === undefined || runtime.status !== 'running') {
      throw new ProxyError(
        ToolErrorCode.UpstreamUnavailable,
        `Upstream '${entry.upstreamId}' is not available${
          runtime?.lastError ? `: ${runtime.lastError}` : ''
        }`,
      );
    }

    const timeout = runtime.entry.callTimeoutMs ?? this.callTimeoutMs;
    try {
      const result = await withTimeout(
        client.callTool({ name: entry.upstreamName, arguments: args }, { timeout, ...(signal ? { signal } : {}) }),
        timeout,
        `Upstream '${entry.upstreamId}' timed out after ${timeout}ms`,
      );
      if (result.isError === true) {
        // 上游自己报告的失败：码归一为「上游工具失败」，原文保留在描述里。
        throw new ProxyError(
          ToolErrorCode.UpstreamToolFailed,
          `Upstream '${entry.upstreamId}' failed: ${textOfContent(result.content)}`,
        );
      }
      return result;
    } catch (error) {
      if (error instanceof CallCancelledError || isAbortError(error)) {
        throw new CallCancelledError();
      }
      if (error instanceof ProxyError) {
        throw error;
      }
      if (error instanceof UpstreamTimeoutError) {
        throw new ProxyError(ToolErrorCode.UpstreamUnavailable, error.message);
      }
      throw new ProxyError(
        ToolErrorCode.UpstreamToolFailed,
        `Upstream '${entry.upstreamId}' failed: ${describeError(error)}`,
      );
    }
  }

  /** 退出收尾：终止全部上游进程，不留孤儿。 */
  async shutdown(): Promise<void> {
    const ids = [...this.runtimes.keys()];
    for (const id of ids) {
      await this.stop(id);
    }
  }

  /** 收集上游的标准错误输出尾部。 */
  private attachDiagnostics(runtime: UpstreamRuntime, transport: StdioClientTransport): void {
    const stream = transport.stderr;
    if (stream === null) {
      return;
    }
    let buffer = '';
    stream.on('data', (chunk: Buffer | string) => {
      buffer += typeof chunk === 'string' ? chunk : chunk.toString('utf8');
      const lines = buffer.split(/\r?\n/);
      buffer = lines.pop() ?? '';
      for (const line of lines) {
        if (line.length > 0) {
          runtime.diagnostics.push(line);
        }
      }
      while (runtime.diagnostics.length > DIAGNOSTICS_TAIL_LINES) {
        runtime.diagnostics.shift();
      }
    });
  }
}

/** 超时包装：到点即中止等待。 */
async function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<never>((_resolve, reject) => {
        timer = setTimeout(() => reject(new UpstreamTimeoutError(message)), timeoutMs);
      }),
    ]);
  } finally {
    if (timer !== undefined) {
      clearTimeout(timer);
    }
  }
}

/** 关闭客户端与传输：先终止子进程树，再关闭通道。 */
async function closeQuietly(client: Client | undefined, transport: StdioClientTransport | undefined): Promise<void> {
  if (transport !== undefined) {
    const pid = transport.pid;
    // 先杀进程树：子进程若自行驻留（不响应退出），关闭通道会等到它结束。
    if (pid !== null && pid !== undefined) {
      killProcessTree(pid);
    }
    try {
      await transport.close();
    } catch {
      // 关闭失败不应阻断收尾。
    }
  }
  if (client !== undefined) {
    try {
      await client.close();
    } catch {
      // 同上。
    }
  }
}

/** 终止进程树：Windows 用 taskkill /T，其余平台用进程组。 */
export function killProcessTree(pid: number): void {
  try {
    if (process.platform === 'win32') {
      const killer = spawn('taskkill', ['/pid', String(pid), '/T', '/F'], { stdio: 'ignore' });
      killer.on('error', () => {
        // 进程可能已经退出。
      });
    } else {
      process.kill(-pid, 'SIGKILL');
    }
  } catch {
    // 进程可能已经退出；清理失败不阻断收尾。
  }
}

/** 把任意错误转成一句可读的英文描述。 */
export function describeError(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }
  return String(error);
}

/** 是否是取消（AbortError）：客户端主动取消调用。 */
function isAbortError(error: unknown): boolean {
  return (
    typeof error === 'object' &&
    error !== null &&
    'name' in error &&
    (error as { name?: unknown }).name === 'AbortError'
  );
}

/** 取内容块里的文本，用于错误描述。 */
export function textOfContent(content: unknown): string {
  const parts: string[] = [];
  if (!Array.isArray(content)) {
    return '';
  }
  for (const block of content) {
    if (typeof block === 'object' && block !== null && 'text' in block) {
      const text = (block as { text?: unknown }).text;
      if (typeof text === 'string') {
        parts.push(text);
      }
    }
  }
  return parts.join('\n');
}
