import assert from 'node:assert/strict';
import { after, describe, it } from 'node:test';

import { createProxyServer } from '../src/server.js';
import {
  configOf,
  errorOf,
  fakeUpstream,
  RawClient,
  resultOf,
  shutdownAllManagers,
  TrackedUpstreamManager as UpstreamManager,
} from './helpers/support.js';

after(async () => {
  await shutdownAllManagers();
});

describe('DownstreamServerTests', () => {
  it('HandshakeThenListTools', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'read_file'])),
    });
    await manager.startAll();
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);

    const handshake = await client.initialize();
    assert.ok(handshake.result !== undefined);

    const response = await client.listTools();
    const names = (resultOf(response) as unknown as { tools?: Array<{ name: string }> }).tools?.map(
      (tool) => tool.name,
    ) ?? [];
    assert.ok(names.includes('fs__read_file'));
    assert.ok(names.includes('proxy_servers'));

    await client.close();
    await manager.shutdown();
  });

  it('RejectsCallBeforeInitialize', async () => {
    const manager = new UpstreamManager({ config: configOf() });
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);

    const response = await client.callTool('proxy_servers');
    assert.ok(response.error !== undefined);

    await client.close();
    await manager.shutdown();
  });

  it('DeclaresNoNetworkTransport', async () => {
    const manager = new UpstreamManager({ config: configOf() });
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);

    const handshake = await client.initialize();
    const capabilities = (handshake.result as { capabilities?: Record<string, unknown> }).capabilities ?? {};
    assert.equal(capabilities['sse'], undefined);
    assert.equal(capabilities['streamableHttp'], undefined);
    assert.ok(capabilities['tools'] !== undefined);

    await client.close();
    await manager.shutdown();
  });

  it('ServesSingleDownstreamClient', async () => {
    const manager = new UpstreamManager({ config: configOf() });
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const first = await RawClient.connect(server);
    await first.initialize();

    // 同一实例不支持第二个下游客户端：标准输入输出形态的必然结果。
    await assert.rejects(() => RawClient.connect(server));

    await first.close();
    await manager.shutdown();
  });

  it('RejectsInvalidArgumentsLocally', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'echo'])),
    });
    await manager.startAll();
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);
    await client.initialize();

    // 假上游的 echo 要求 text 参数；缺失时由代理本地拒绝，不转发。
    const response = await client.callTool('fs__echo', {});
    const error = errorOf(response);
    assert.equal(error?.code, 'TOOL_FAILED');

    await client.close();
    await manager.shutdown();
  });

  it('RoutesUnknownToolToToolNotFound', async () => {
    const manager = new UpstreamManager({ config: configOf() });
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.callTool('ghost__anything');
    const error = errorOf(response);
    assert.equal(error?.code, 'TOOL_NOT_FOUND');

    await client.close();
    await manager.shutdown();
  });
});
