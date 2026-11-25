/**
 * 会话、工具注册与自述工具的单元测试。
 *
 * 覆盖：握手成功、能力集只含工具能力、未握手调用被拒、零工具列举为空、
 * 已注册工具列举内容、未注册工具调用被拒、自述工具五项字段。
 */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { z } from 'zod';

import { createServer, createServerWithTools, SERVER_NAME } from '../src/server.js';
import { RawClient } from './helpers/raw-client.js';

describe('握手与能力声明', () => {
  it('握手返回服务端标识、版本、协议版本与能力声明', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);
    const response = await client.initialize();

    assert.equal(response.error, undefined);
    const result = response.result as {
      protocolVersion: string;
      serverInfo: { name: string; version: string };
      capabilities: Record<string, unknown>;
    };

    assert.equal(result.serverInfo.name, SERVER_NAME);
    assert.equal(result.serverInfo.version, '0.0.0-test');
    assert.match(result.protocolVersion, /^\d{4}-\d{2}-\d{2}$/);
    await client.close();
  });

  it('能力集只含工具相关能力，不声明资源与提示能力', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);
    const response = await client.initialize();
    const capabilities = (response.result as { capabilities: Record<string, unknown> }).capabilities;

    assert.deepEqual(Object.keys(capabilities), ['tools']);
    assert.equal('resources' in capabilities, false);
    assert.equal('prompts' in capabilities, false);
    await client.close();
  });

  it('不声明工具列表变更通知', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);
    const response = await client.initialize();
    const capabilities = (response.result as { capabilities: { tools?: { listChanged?: boolean } } }).capabilities;

    assert.equal(capabilities.tools?.listChanged, false);
    await client.close();
  });
});

describe('工具注册与列举', () => {
  it('列举返回已注册工具的名称、标题、描述与输入模式', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.request('tools/list');
    const tools = (response.result as { tools: Array<Record<string, unknown>> }).tools;

    assert.equal(tools.length, 1);
    const tool = tools[0];
    assert.equal(tool?.name, 'info');
    assert.equal(typeof tool?.title, 'string');
    assert.ok((tool?.title as string).length > 0);
    assert.equal(typeof tool?.description, 'string');
    assert.ok((tool?.description as string).length > 0);
    assert.equal((tool?.inputSchema as { type: string }).type, 'object');
    await client.close();
  });

  it('零工具注册时列举返回空数组而非错误', async () => {
    const server = createServer({ serverVersion: '0.0.0-test', includeInfoTool: false });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.request('tools/list');

    assert.equal(response.error, undefined);
    assert.deepEqual((response.result as { tools: unknown[] }).tools, []);
    await client.close();
  });
});

describe('未注册工具的拒绝路径', () => {
  it('返回带错误标记的结果，错误码为 TOOL_NOT_FOUND，不走协议错误通道', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.request('tools/call', { name: 'nope', arguments: {} });

    assert.equal(response.error, undefined);
    const result = response.result as {
      isError?: boolean;
      content: Array<{ type: string; text: string }>;
      structuredContent: { error: { code: string; message: string } };
    };

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.error.code, 'TOOL_NOT_FOUND');
    assert.deepEqual(JSON.parse(result.content[0]?.text ?? ''), result.structuredContent);
    await client.close();
  });

  it('未注册工具时不执行任何处理', async () => {
    let executed = false;
    // 注入一个带副作用的工具：只有真被调用才会置位。
    const server = createServerWithTools(
      () => [
        {
          name: 'probe',
          title: 'probe',
          description: 'side-effect probe',
          inputSchema: z.object({}),
          outputSchema: z.object({ ok: z.boolean() }),
          handle: () => {
            executed = true;
            return { ok: true };
          },
        },
      ],
      { serverVersion: '0.0.0-test' },
    );

    const client = await RawClient.connect(server);
    await client.initialize();
    await client.request('tools/call', { name: 'nope', arguments: {} });

    assert.equal(executed, false);
    await client.close();
  });
});

describe('工具处理失败的错误结果', () => {
  it('工具处理抛错时返回带错误标记的结果，错误码为 TOOL_FAILED', async () => {
    const server = createServerWithTools(
      () => [
        {
          name: 'boom',
          title: 'boom',
          description: 'always fails',
          inputSchema: z.object({}),
          outputSchema: z.object({ ok: z.boolean() }),
          handle: () => {
            throw new Error('internal detail must not leak');
          },
        },
      ],
      { serverVersion: '0.0.0-test' },
    );

    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.request('tools/call', { name: 'boom', arguments: {} });
    assert.equal(response.error, undefined);
    const result = response.result as {
      isError?: boolean;
      content: Array<{ type: string; text: string }>;
      structuredContent: { error: { code: string; message: string } };
    };

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.error.code, 'TOOL_FAILED');
    assert.deepEqual(JSON.parse(result.content[0]?.text ?? ''), result.structuredContent);
    await client.close();
  });

  it('工具入参不符输入模式时返回 TOOL_FAILED 的错误结果', async () => {
    const server = createServerWithTools(
      () => [
        {
          name: 'typed',
          title: 'typed',
          description: 'takes a number',
          inputSchema: z.object({ count: z.number() }),
          outputSchema: z.object({ count: z.number() }),
          handle: (args) => args,
        },
      ],
      { serverVersion: '0.0.0-test' },
    );

    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.request('tools/call', {
      name: 'typed',
      arguments: { count: 'not-a-number' },
    });
    const result = response.result as {
      isError?: boolean;
      structuredContent: { error: { code: string } };
    };

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.error.code, 'TOOL_FAILED');
    await client.close();
  });

  it('工具产出不符其声明的结果模式时返回 TOOL_FAILED，不返回违规的成功结果', async () => {
    const server = createServerWithTools(
      () => [
        {
          name: 'mismatch',
          title: 'mismatch',
          description: 'returns a wrong shape',
          inputSchema: z.object({}),
          outputSchema: z.object({ ok: z.boolean() }),
          handle: () => ({ ok: 'not-a-boolean' }),
        },
      ],
      { serverVersion: '0.0.0-test' },
    );

    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.request('tools/call', { name: 'mismatch', arguments: {} });
    const result = response.result as {
      isError?: boolean;
      structuredContent: { error: { code: string } };
    };

    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.error.code, 'TOOL_FAILED');
    await client.close();
  });
});

describe('未握手即调用工具', () => {
  it('拒绝该请求，不返回工具结果', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);

    const response = await client.request('tools/call', { name: 'info', arguments: {} });

    assert.ok(response.error, '未握手调用应走协议错误通道');
    assert.equal(response.result, undefined);
    await client.close();
  });
});

describe('自述工具', () => {
  it('产出服务端版本、协议版本、契约版本、能力集与传输形态五项', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);
    const handshake = await client.initialize();

    const response = await client.request('tools/call', { name: 'info', arguments: {} });
    const result = response.result as {
      isError?: boolean;
      content: Array<{ type: string; text: string }>;
      structuredContent: Record<string, unknown>;
    };

    assert.notEqual(result.isError, true);
    const output = result.structuredContent;
    assert.deepEqual(Object.keys(output).sort(), [
      'capabilities',
      'contractVersion',
      'protocolVersion',
      'serverVersion',
      'transport',
    ]);
    assert.equal(output.serverVersion, '0.0.0-test');
    assert.equal(output.contractVersion, '0.1.0');
    assert.equal(output.transport, 'stdio');
    assert.equal(
      output.protocolVersion,
      (handshake.result as { protocolVersion: string }).protocolVersion,
    );
    assert.deepEqual(output.capabilities, { tools: { listChanged: false } });
    assert.deepEqual(JSON.parse(result.content[0]?.text ?? ''), output);
    await client.close();
  });

  it('调用过程不访问日志文件：产出只含自身身份信息', async () => {
    const server = createServer({ serverVersion: '0.0.0-test' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.request('tools/call', { name: 'info', arguments: {} });
    const output = (response.result as { structuredContent: Record<string, unknown> }).structuredContent;

    for (const key of Object.keys(output)) {
      assert.doesNotMatch(key, /path|file|dir|log$/i);
    }
    await client.close();
  });
});
