#!/usr/bin/env node
/**
 * MCP 代理入口。
 *
 * 标准输出只写下游协议消息；诊断信息、管理页面地址与访问令牌一律走标准错误。
 * 进程退出时终止全部上游子进程，不留孤儿。
 */
import { StdioServerTransport } from '@modelcontextprotocol/server/stdio';

import { parseArgs, USAGE } from './cli.js';
import { loadConfig, resolveConfigPath } from './config.js';
import { startManagementServer } from './management.js';
import { createProxyServer } from './server.js';
import { UpstreamManager } from './upstream.js';
import { CONTRACT_VERSION, readServerVersion } from './version.js';

const args = parseArgs(process.argv.slice(2));

if (args.help) {
  process.stdout.write(USAGE);
  process.exit(0);
}

if (args.error !== undefined) {
  console.error(`[logcat-mcp-proxy] ${args.error}`);
  console.error(USAGE);
  process.exit(2);
}

const configPath = resolveConfigPath(args.configPath);
const config = await loadConfig(configPath);

const manager = new UpstreamManager({ config });
await manager.startAll();

const management = await startManagementServer({
  manager,
  config,
  configPath,
  ...(args.port !== undefined ? { port: args.port } : {}),
});

const server = createProxyServer({ manager, serverVersion: readServerVersion() });
const transport = new StdioServerTransport();

let shuttingDown = false;

async function shutdown(code: number): Promise<void> {
  if (shuttingDown) {
    return;
  }
  shuttingDown = true;
  try {
    await manager.shutdown();
    await management.close();
  } catch {
    // 收尾失败不应阻断退出。
  }
  process.exit(code);
}

transport.onclose = () => {
  void shutdown(0);
};

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    void shutdown(0);
  });
}

// 启动诊断走标准错误：标准输出只留给协议消息。
console.error(`[logcat-mcp-proxy] contract version ${CONTRACT_VERSION}`);
console.error(`[logcat-mcp-proxy] config ${configPath}`);
console.error(`[logcat-mcp-proxy] 管理页面：${management.url}`);

await server.connect(transport);
