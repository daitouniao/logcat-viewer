import assert from 'node:assert/strict';
import { request as httpRequest } from 'node:http';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, describe, it } from 'node:test';

import { MASK } from '../src/config.js';
import { startManagementServer } from '../src/management.js';
import {
  configOf,
  fakeUpstream,
  shutdownAllManagers,
  TrackedUpstreamManager as UpstreamManager,
} from './helpers/support.js';

after(async () => {
  await shutdownAllManagers();
});

async function startWith(entries = [fakeUpstream('fs', ['--tools', 'echo'])]) {
  const directory = await mkdtemp(join(tmpdir(), 'mcp-proxy-mgmt-'));
  const configPath = join(directory, 'config.json');
  const config = configOf(...entries);
  const manager = new UpstreamManager({ config });
  await manager.startAll();
  const handle = await startManagementServer({ manager, config, configPath });
  return { handle, manager, configPath, directory };
}

describe('ManagementApiTests', () => {
  it('ListCreateUpdateDelete', async () => {
    const { handle, manager, configPath } = await startWith();
    const base = `http://127.0.0.1:${handle.port}`;

    const list = await fetch(`${base}/api/servers`, { headers: { 'x-proxy-token': handle.token } });
    assert.equal(list.status, 200);
    const listed = (await list.json()) as { servers: Array<{ id: string }> };
    assert.equal(listed.servers.length, 1);

    const created = await fetch(`${base}/api/servers`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-proxy-token': handle.token },
      body: JSON.stringify({ id: 'db', command: 'node', args: [], env: {}, enabled: false }),
    });
    assert.equal(created.status, 200);

    const updated = await fetch(`${base}/api/servers/db`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json', 'x-proxy-token': handle.token },
      body: JSON.stringify({ id: 'db', command: 'node2', args: [], env: {}, enabled: false }),
    });
    assert.equal(updated.status, 200);

    const persisted = JSON.parse(await readFile(configPath, 'utf8')) as {
      upstreams: Array<{ id: string; command: string }>;
    };
    assert.equal(persisted.upstreams.length, 2);
    assert.equal(persisted.upstreams.find((item) => item.id === 'db')?.command, 'node2');

    const deleted = await fetch(`${base}/api/servers/db`, {
      method: 'DELETE',
      headers: { 'x-proxy-token': handle.token },
    });
    assert.equal(deleted.status, 200);
    const after = JSON.parse(await readFile(configPath, 'utf8')) as { upstreams: unknown[] };
    assert.equal(after.upstreams.length, 1);

    await handle.close();
    await manager.shutdown();
  });

  it('StartsStopsRestarts', async () => {
    const { handle, manager } = await startWith();
    const base = `http://127.0.0.1:${handle.port}`;

    const stopped = await fetch(`${base}/api/servers/fs/stop`, {
      method: 'POST',
      headers: { 'x-proxy-token': handle.token },
    });
    assert.equal(stopped.status, 200);
    assert.equal(manager.snapshots()[0]?.status, 'stopped');

    const started = await fetch(`${base}/api/servers/fs/start`, {
      method: 'POST',
      headers: { 'x-proxy-token': handle.token },
    });
    assert.equal(started.status, 200);
    assert.equal(manager.snapshots()[0]?.status, 'running');

    const restarted = await fetch(`${base}/api/servers/fs/restart`, {
      method: 'POST',
      headers: { 'x-proxy-token': handle.token },
    });
    assert.equal(restarted.status, 200);
    assert.equal(manager.snapshots()[0]?.status, 'running');

    await handle.close();
    await manager.shutdown();
  });

  it('ReturnsDiagnosticTail', async () => {
    const { handle, manager } = await startWith([
      fakeUpstream('fs', ['--noise', 'diagnostic line']),
    ]);
    const base = `http://127.0.0.1:${handle.port}`;
    await new Promise((resolve) => setTimeout(resolve, 200));

    const response = await fetch(`${base}/api/servers/fs/diagnostics`, {
      headers: { 'x-proxy-token': handle.token },
    });
    assert.equal(response.status, 200);
    const payload = (await response.json()) as { diagnostics: string[] };
    assert.ok(payload.diagnostics.some((line) => line.includes('diagnostic line')));

    await handle.close();
    await manager.shutdown();
  });

  it('RejectsMissingToken', async () => {
    const { handle, manager } = await startWith();
    const response = await fetch(`http://127.0.0.1:${handle.port}/api/servers`);
    assert.equal(response.status, 401);
    await handle.close();
    await manager.shutdown();
  });

  it('RejectsInvalidToken', async () => {
    const { handle, manager } = await startWith();
    const response = await fetch(`http://127.0.0.1:${handle.port}/api/servers`, {
      headers: { 'x-proxy-token': 'not-the-token' },
    });
    assert.equal(response.status, 401);
    await handle.close();
    await manager.shutdown();
  });

  it('RejectsForeignHostHeader', async () => {
    const { handle, manager } = await startWith();
    // fetch 不允许改写 Host 头（受限头），这里用裸 http 请求发一个伪造的 Host。
    const status = await new Promise<number>((resolve, reject) => {
      const request = httpRequest(
        {
          host: '127.0.0.1',
          port: handle.port,
          path: '/api/servers',
          method: 'GET',
          headers: { 'x-proxy-token': handle.token, host: 'evil.example.com' },
        },
        (response) => {
          response.resume();
          resolve(response.statusCode ?? 0);
        },
      );
      request.on('error', reject);
      request.end();
    });
    assert.equal(status, 403);
    await handle.close();
    await manager.shutdown();
  });

  it('MasksSecrets', async () => {
    const { handle, manager } = await startWith([
      fakeUpstream('fs', ['--tools', 'echo'], { env: { TOKEN: 'super-secret' } }),
    ]);
    const response = await fetch(`http://127.0.0.1:${handle.port}/api/servers`, {
      headers: { 'x-proxy-token': handle.token },
    });
    const text = await response.text();
    assert.ok(!text.includes('super-secret'));
    assert.ok(text.includes(MASK));
    await handle.close();
    await manager.shutdown();
  });

  it('PreservesMaskedValuesOnSave', async () => {
    const { handle, manager, configPath } = await startWith([
      fakeUpstream('fs', ['--tools', 'echo'], { env: { TOKEN: 'super-secret' } }),
    ]);
    const base = `http://127.0.0.1:${handle.port}`;

    await fetch(`${base}/api/servers/fs`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json', 'x-proxy-token': handle.token },
      body: JSON.stringify({
        id: 'fs',
        command: process.execPath,
        args: [],
        env: { TOKEN: MASK },
        enabled: false,
      }),
    });

    const persisted = JSON.parse(await readFile(configPath, 'utf8')) as {
      upstreams: Array<{ env: Record<string, string> }>;
    };
    assert.equal(persisted.upstreams[0]?.env['TOKEN'], 'super-secret');

    await handle.close();
    await manager.shutdown();
  });

  it('UsesRandomPortByDefault', async () => {
    const first = await startWith();
    const second = await startWith();
    assert.notEqual(first.handle.port, 0);
    assert.ok(first.handle.port > 0);
    assert.ok(second.handle.port > 0);
    await first.handle.close();
    await first.manager.shutdown();
    await second.handle.close();
    await second.manager.shutdown();
  });

  it('PrintsTokenizedPageUrlToStderr', async () => {
    const { handle, manager } = await startWith();
    assert.ok(handle.url.includes(`token=${handle.token}`));
    assert.ok(handle.url.startsWith('http://127.0.0.1:'));
    await handle.close();
    await manager.shutdown();
  });
});

describe('ManagementPageTests', () => {
  it('ServesPageWithUpstreamList', async () => {
    const { handle, manager } = await startWith();
    const response = await fetch(`http://127.0.0.1:${handle.port}/`);
    assert.equal(response.status, 200);
    const html = await response.text();
    assert.ok(html.includes('MCP Proxy'));
    assert.ok(html.includes('/api/servers'));
    await handle.close();
    await manager.shutdown();
  });

  it('EscapesUpstreamNames', async () => {
    const { handle, manager } = await startWith();
    const response = await fetch(`http://127.0.0.1:${handle.port}/`);
    const html = await response.text();
    // 渲染走 textContent，不拼 innerHTML——上游名是外部输入，不能进 HTML 字符串。
    assert.ok(!html.includes('innerHTML'));
    assert.ok(html.includes('textContent'));
    await handle.close();
    await manager.shutdown();
  });
});
