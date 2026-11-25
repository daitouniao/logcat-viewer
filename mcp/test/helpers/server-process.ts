/**
 * 以子进程方式拉起服务端的测试夹具。
 *
 * 端到端断言必须打在真实字节上：标准输出、标准错误、进程退出码都按
 * 线上行为观测，不做任何进程内替换。
 */
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

/** 子进程服务端的可执行入口（构建产物）。 */
export const SERVER_ENTRY = path.resolve(here, '..', '..', 'src', 'index.js');

export interface ServerProcess {
  /** 进程句柄。 */
  readonly child: ChildProcessWithoutNullStreams;
  /** 标准输出的原始行（按到达顺序）。 */
  readonly stdoutLines: string[];
  /** 标准错误累积文本。 */
  stderrText(): string;
  /** 发送一条 JSON-RPC 消息。 */
  send(message: Record<string, unknown>): void;
  /** 等待某 id 的响应。 */
  waitForResponse(id: number, timeoutMs?: number): Promise<Record<string, unknown>>;
  /** 关闭标准输入（模拟客户端断开）。 */
  closeStdin(): void;
  /** 等待进程结束，返回退出码与信号。 */
  waitForExit(timeoutMs?: number): Promise<{ code: number | null; signal: string | null }>;
  /** 终止进程（清理用）。 */
  kill(): void;
}

/** 拉起一个服务端子进程，并开始收集标准输出与标准错误。 */
export function startServer(env?: Record<string, string>): ServerProcess {
  const child = spawn(process.execPath, [SERVER_ENTRY], {
    stdio: ['pipe', 'pipe', 'pipe'],
    env: env ? { ...process.env, ...env } : process.env,
  }) as ChildProcessWithoutNullStreams;

  const stdoutLines: string[] = [];
  const responses: Array<Record<string, unknown>> = [];
  const waiters: Array<() => void> = [];

  const notifyResponse = () => {
    while (waiters.length > 0) {
      waiters.pop()?.();
    }
  };

  let stdoutBuffer = '';
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', (chunk: string) => {
    stdoutBuffer += chunk;
    let index = stdoutBuffer.indexOf('\n');
    while (index >= 0) {
      const line = stdoutBuffer.slice(0, index).replace(/\r$/, '');
      stdoutBuffer = stdoutBuffer.slice(index + 1);
      if (line.length > 0) {
        stdoutLines.push(line);
        try {
          const parsed = JSON.parse(line) as Record<string, unknown>;
          responses.push(parsed);
        } catch {
          // 非 JSON 行不参与响应匹配：由纯度测试负责断言其不存在。
        }
        notifyResponse();
      }
      index = stdoutBuffer.indexOf('\n');
    }
  });

  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', (chunk: string) => {
    stderr += chunk;
  });

  const exited = new Promise<{ code: number | null; signal: string | null }>((resolve) => {
    child.on('exit', (code, signal) => {
      resolve({ code, signal });
      notifyResponse();
    });
  });

  return {
    child,
    stdoutLines,
    stderrText: () => stderr,
    send(message) {
      child.stdin.write(`${JSON.stringify(message)}\n`);
    },
    async waitForResponse(id, timeoutMs = 5000) {
      const deadline = Date.now() + timeoutMs;
      for (;;) {
        const found = responses.find((message) => message.id === id);
        if (found) {
          return found;
        }
        if (Date.now() > deadline) {
          throw new Error(`Timed out waiting for response id=${id}`);
        }
        await new Promise<void>((resolve) => {
          waiters.push(resolve);
          setTimeout(resolve, 25);
        });
      }
    },
    closeStdin() {
      child.stdin.end();
    },
    async waitForExit(timeoutMs = 5000) {
      const timeout = new Promise<'timeout'>((resolve) => {
        setTimeout(() => resolve('timeout'), timeoutMs);
      });
      const outcome = await Promise.race([exited, timeout]);
      if (outcome === 'timeout') {
        throw new Error(`Server process did not exit within ${timeoutMs}ms`);
      }
      return outcome;
    },
    kill() {
      child.kill();
    },
  };
}

/** 构造一条带 id 的请求。 */
export function request(id: number, method: string, params?: unknown): Record<string, unknown> {
  const message: Record<string, unknown> = { jsonrpc: '2.0', id, method };
  if (params !== undefined) {
    message.params = params;
  }
  return message;
}

/** 取一个未占用过的请求 id。 */
export function nextRequestId(counter: { value: number }): number {
  counter.value += 1;
  return counter.value;
}
