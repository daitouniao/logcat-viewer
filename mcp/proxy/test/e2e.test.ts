import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { after, describe, it } from 'node:test';

import {
  configOf,
  shutdownAllManagers,
  TrackedUpstreamManager as UpstreamManager,
} from './helpers/support.js';

after(async () => {
  await shutdownAllManagers();
});

/**
 * 端到端：以仓库内既有的 `logcat-mcp`（真实 MCP 服务端）为上游。
 *
 * 前置：先在 `mcp/` 执行 `npm ci && npm run build`——构建产物不入库，
 * 本地与 CI 都须先构建。
 */
const REPO_MCP = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const LOGCAT_MCP_ENTRY = join(REPO_MCP, 'dist', 'src', 'index.js');

function logcatUpstream() {
  return {
    id: 'logcat',
    command: process.execPath,
    args: [LOGCAT_MCP_ENTRY],
    env: {},
    enabled: true,
  };
}

describe('E2eTests', () => {
  it('AggregatesRealUpstreamTool', async () => {
    assert.ok(
      existsSync(LOGCAT_MCP_ENTRY),
      `logcat-mcp is not built: ${LOGCAT_MCP_ENTRY} (run "npm ci && npm run build" in mcp/)`,
    );
    const manager = new UpstreamManager({ config: configOf(logcatUpstream()) });
    await manager.startAll();
    const names = manager.toolEntries().map((entry) => entry.downstreamName);
    assert.ok(names.includes('logcat__info'));
    await manager.shutdown();
  });

  it('CallSThroughProxy', async () => {
    const manager = new UpstreamManager({ config: configOf(logcatUpstream()) });
    await manager.startAll();
    const result = await manager.callTool('logcat__info', {});
    const blocks = result.content as Array<{ text?: string }>;
    assert.ok((blocks[0]?.text ?? '').includes('contractVersion'));
    await manager.shutdown();
  });

  it('DisappearsAfterStop', async () => {
    const manager = new UpstreamManager({ config: configOf(logcatUpstream()) });
    await manager.startAll();
    await manager.stop('logcat');
    assert.equal(manager.toolEntries().length, 0);
    await manager.shutdown();
  });

  it('ReturnsAfterRestart', async () => {
    const manager = new UpstreamManager({ config: configOf(logcatUpstream()) });
    await manager.startAll();
    await manager.restart('logcat');
    const names = manager.toolEntries().map((entry) => entry.downstreamName);
    assert.ok(names.includes('logcat__info'));
    await manager.shutdown();
  });
});
