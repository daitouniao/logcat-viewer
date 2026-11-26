#!/usr/bin/env node
/**
 * MCP 服务端入口：以标准输入输出承载协议消息。
 *
 * 标准输出只写协议消息；诊断信息一律走标准错误。客户端关闭连接（标准输入
 * 到达文件末尾）后，服务端随之结束进程，不留孤儿进程。
 */
import { StdioServerTransport } from '@modelcontextprotocol/server/stdio';

import { CONTRACT_VERSION } from './contract.js';
import { createServer } from './server.js';
import { installShutdownHandlers } from './shutdown.js';
import { readServerVersion } from './version.js';

const server = createServer({ serverVersion: readServerVersion() });
const transport = new StdioServerTransport();

// 启动诊断走标准错误：标准输出只留给协议消息。
console.error(`[logcat-mcp] starting, contract version ${CONTRACT_VERSION}`);

// 客户端断开（标准输入 EOF）时结束进程。传输层关闭自身并触发 onclose。
transport.onclose = () => {
  process.exit(0);
};

// 终止信号：先收尾再退出，且此后不再向标准输出写入任何内容。
installShutdownHandlers({
  report: (message) => console.error(message),
  exit: (code) => process.exit(code),
});

await server.connect(transport);
