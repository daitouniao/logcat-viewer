/**
 * 端到端链路、通道纪律与退出行为的测试。
 *
 * 全部以子进程方式拉起服务端：标准输出、标准错误与进程退出码都是线上
 * 真实行为，不做进程内替换。
 */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { LATEST_PROTOCOL_VERSION } from '@modelcontextprotocol/server';

import { request, startServer, type ServerProcess } from './helpers/server-process.js';

/** 完成初始化握手，返回握手结果。 */
async function initialize(server: ServerProcess, id: number): Promise<Record<string, unknown>> {
  server.send(
    request(id, 'initialize', {
      protocolVersion: LATEST_PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: 'e2e-test-client', version: '0.0.0' },
    }),
  );
  const response = await server.waitForResponse(id);
  server.send({ jsonrpc: '2.0', method: 'notifications/initialized' });
  return (response.result ?? {}) as Record<string, unknown>;
}

describe('端到端链路', () => {
  it('依次完成初始化、工具列举与自述工具调用', async () => {
    const server = startServer();
    try {
      const handshake = await initialize(server, 1);
      assert.equal((handshake.serverInfo as { name: string }).name, 'logcat-mcp');

      server.send(request(2, 'tools/list'));
      const listed = (await server.waitForResponse(2)).result as { tools: Array<{ name: string }> };
      assert.equal(listed.tools.length, 1);
      assert.equal(listed.tools[0]?.name, 'info');

      server.send(request(3, 'tools/call', { name: 'info', arguments: {} }));
      const called = (await server.waitForResponse(3)).result as {
        content: Array<{ type: string; text: string }>;
        structuredContent: Record<string, unknown>;
      };

      assert.ok(called.content.length > 0);
      const text = called.content[0]?.text ?? '';
      assert.ok(text.length > 0);
      assert.deepEqual(JSON.parse(text), called.structuredContent);
      assert.equal(called.structuredContent.contractVersion, '0.1.0');
      assert.equal(called.structuredContent.transport, 'stdio');
    } finally {
      server.kill();
    }
  });
});

describe('通道纪律', () => {
  it('标准输出的每一行都是合法协议消息', async () => {
    const server = startServer();
    try {
      await initialize(server, 1);
      server.send(request(2, 'tools/list'));
      await server.waitForResponse(2);
      server.send(request(3, 'tools/call', { name: 'info', arguments: {} }));
      await server.waitForResponse(3);

      assert.ok(server.stdoutLines.length > 0);
      for (const line of server.stdoutLines) {
        const parsed = JSON.parse(line) as Record<string, unknown>;
        assert.equal(parsed.jsonrpc, '2.0');
        assert.ok('method' in parsed || 'result' in parsed || 'error' in parsed);
      }
    } finally {
      server.kill();
    }
  });

  it('诊断信息出现在标准错误上，标准输出无诊断内容', async () => {
    const server = startServer();
    try {
      await initialize(server, 1);
      server.send(request(2, 'tools/call', { name: 'info', arguments: {} }));
      await server.waitForResponse(2);

      // 诊断信息走标准错误。
      assert.match(server.stderrText(), /\[logcat-mcp\]/);

      // 标准输出里不含任何诊断文本。
      for (const line of server.stdoutLines) {
        assert.doesNotMatch(line, /\[logcat-mcp\]/);
      }
    } finally {
      server.kill();
    }
  });
});

describe('进程退出', () => {
  it('客户端断开后进程在 5 秒内结束', async () => {
    const server = startServer();
    try {
      await initialize(server, 1);
      server.closeStdin();

      const outcome = await server.waitForExit(5000);
      assert.equal(outcome.code, 0);
    } finally {
      server.kill();
    }
  });

  it('退出前未向标准输出写入非协议内容', async () => {
    const server = startServer();
    try {
      await initialize(server, 1);
      server.closeStdin();
      await server.waitForExit(5000);

      for (const line of server.stdoutLines) {
        const parsed = JSON.parse(line) as Record<string, unknown>;
        assert.equal(parsed.jsonrpc, '2.0');
      }
    } finally {
      server.kill();
    }
  });
});

describe('引擎无关性', () => {
  it('在没有日志引擎的环境中仍可完成握手与列举', async () => {
    const server = startServer({
      // 刻意不提供任何日志引擎相关的环境变量。
      LOGCAT_PATH: '',
      LOGCAT_BIN: '',
    });
    try {
      const handshake = await initialize(server, 1);
      assert.equal((handshake.serverInfo as { name: string }).name, 'logcat-mcp');

      server.send(request(2, 'tools/list'));
      const listed = (await server.waitForResponse(2)).result as { tools: unknown[] };
      assert.ok(Array.isArray(listed.tools));
      assert.equal(listed.tools.length, 1);
    } finally {
      server.kill();
    }
  });

  it('握手结果中未声明任何网络传输形态，能力集只含工具能力', async () => {
    const server = startServer();
    try {
      const handshake = await initialize(server, 1);
      const capabilities = handshake.capabilities as Record<string, unknown>;

      assert.deepEqual(Object.keys(capabilities), ['tools']);
      assert.equal('resources' in capabilities, false);
      assert.equal('prompts' in capabilities, false);
      assert.equal('http' in capabilities, false);
      assert.equal('sse' in capabilities, false);
    } finally {
      server.kill();
    }
  });
});
