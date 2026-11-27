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

describe('BuiltinToolsTests', () => {
  it('ListUpstreams', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'echo'])),
    });
    await manager.startAll();
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.callTool('proxy_servers');
    const result = resultOf(response);
    assert.equal(result.isError, false);
    const servers = result.structuredContent?.['servers'] as Array<{ id: string; toolCount: number }>;
    assert.equal(servers.length, 1);
    assert.equal(servers[0]?.id, 'fs');
    assert.equal(servers[0]?.toolCount, 1);

    await client.close();
    await manager.shutdown();
  });

  it('RestartUpstreamById', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'echo'])),
    });
    await manager.startAll();
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.callTool('proxy_restart', { id: 'fs' });
    const result = resultOf(response);
    assert.equal(result.isError, false);
    assert.equal(result.structuredContent?.['id'], 'fs');
    assert.equal(result.structuredContent?.['status'], 'running');

    await client.close();
    await manager.shutdown();
  });

  it('RestartUnknownUpstreamReportsToolNotFound', async () => {
    const manager = new UpstreamManager({ config: configOf() });
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.callTool('proxy_restart', { id: 'ghost' });
    const error = errorOf(response);
    assert.equal(error?.code, 'TOOL_NOT_FOUND');

    await client.close();
    await manager.shutdown();
  });

  it('AppearInToolListing', async () => {
    const manager = new UpstreamManager({ config: configOf() });
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.listTools();
    const result = resultOf(response);
    const tools = result.structuredContent?.['tools'] as Array<{ name: string }> | undefined;
    const names = (result as unknown as { tools?: Array<{ name: string }> }).tools?.map((tool) => tool.name) ?? [];
    assert.ok(names.includes('proxy_servers'));
    assert.ok(names.includes('proxy_restart'));
    assert.ok(tools === undefined || Array.isArray(tools));

    await client.close();
    await manager.shutdown();
  });

  it('DoNotCollideWithUpstreamNamespace', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('proxy', ['--tools', 'servers'])),
    });
    await manager.startAll();
    const server = createProxyServer({ manager, serverVersion: '0.0.0' });
    const client = await RawClient.connect(server);
    await client.initialize();

    const response = await client.listTools();
    const names = (resultOf(response) as unknown as { tools?: Array<{ name: string }> }).tools?.map(
      (tool) => tool.name,
    ) ?? [];
    assert.ok(names.includes('proxy_servers'));
    assert.ok(names.includes('proxy__servers'));
    assert.equal(names.filter((name) => name === 'proxy_servers').length, 1);

    await client.close();
    await manager.shutdown();
  });
});
