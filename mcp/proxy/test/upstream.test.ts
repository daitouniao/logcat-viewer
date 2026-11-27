import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, describe, it } from 'node:test';

import { TrackedUpstreamManager as UpstreamManager } from './helpers/support.js';
import { configOf, fakeUpstream, shutdownAllManagers } from './helpers/support.js';

after(async () => {
  await shutdownAllManagers();
});

describe('UpstreamLifecycleTests', () => {
  it('StartsEnabledUpstreams', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'read_file,write_file'])),
    });
    await manager.startAll();
    const snapshots = manager.snapshots();
    assert.equal(snapshots.length, 1);
    assert.equal(snapshots[0]?.status, 'running');
    assert.equal(snapshots[0]?.toolCount, 2);
    await manager.shutdown();
  });

  it('FailedUpstreamDoesNotBlockOthers', async () => {
    const manager = new UpstreamManager({
      config: configOf(
        { id: 'broken', command: 'definitely-not-a-real-command-xyz', args: [], env: {}, enabled: true },
        fakeUpstream('good', ['--tools', 'echo']),
      ),
    });
    await manager.startAll();
    const byId = new Map(manager.snapshots().map((item) => [item.id, item]));
    assert.equal(byId.get('broken')?.status, 'failed');
    assert.ok((byId.get('broken')?.lastError ?? '').length > 0);
    assert.equal(byId.get('good')?.status, 'running');
    await manager.shutdown();
  });

  it('RecordsFailureReason', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('broken', ['--tools', 'x'], { command: 'no-such-command-abc' })),
    });
    await manager.startAll();
    const snapshot = manager.snapshots()[0];
    assert.equal(snapshot?.status, 'failed');
    assert.ok((snapshot?.lastError ?? '').length > 0);
    await manager.shutdown();
  });

  it('TimesOutUnreadyUpstream', async () => {
    // 一个永不完成握手的假上游：用一个只打印横幅、不做协议应答的进程模拟。
    const manager = new UpstreamManager({
      config: configOf({
        id: 'silent',
        command: process.execPath,
        args: ['-e', 'setInterval(() => {}, 1000)'],
        env: {},
        enabled: true,
        readyTimeoutMs: 300,
      }),
      readyTimeoutMs: 300,
    });
    await manager.startAll();
    const snapshot = manager.snapshots()[0];
    assert.equal(snapshot?.status, 'failed');
    assert.ok((snapshot?.lastError ?? '').length > 0);
    await manager.shutdown();
  });

  it('SlowUpstreamDoesNotDelayServiceStart', async () => {
    const manager = new UpstreamManager({
      config: configOf(
        {
          id: 'slow',
          command: process.execPath,
          args: ['-e', 'setTimeout(() => {}, 3000)'],
          env: {},
          enabled: true,
          readyTimeoutMs: 300,
        },
        fakeUpstream('fast', ['--tools', 'echo'], { readyTimeoutMs: 10_000 }),
      ),
      readyTimeoutMs: 300,
    });
    const started = Date.now();
    await manager.startAll();
    const elapsed = Date.now() - started;
    const byId = new Map(manager.snapshots().map((item) => [item.id, item]));
    assert.equal(byId.get('fast')?.status, 'running');
    assert.ok(elapsed < 2500, `startAll took ${elapsed}ms`);
    await manager.shutdown();
  });

  it('RestartRefreshesToolSet', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'a,b'])),
    });
    await manager.startAll();
    assert.equal(manager.snapshots()[0]?.toolCount, 2);

    await manager.stop('fs');
    await manager.upsert(fakeUpstream('fs', ['--tools', 'c']));
    assert.equal(manager.snapshots()[0]?.toolCount, 1);
    assert.equal(manager.toolEntries().length, 1);
    assert.equal(manager.toolEntries()[0]?.downstreamName, 'fs__c');
    await manager.shutdown();
  });

  it('StopRemovesTools', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'a,b'])),
    });
    await manager.startAll();
    assert.equal(manager.toolEntries().length, 2);
    await manager.stop('fs');
    assert.equal(manager.toolEntries().length, 0);
    assert.equal(manager.snapshots()[0]?.status, 'stopped');
    await manager.shutdown();
  });

  it('KeepsBoundedDiagnosticTail', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--noise', 'diagnostic line'])),
    });
    await manager.startAll();
    await new Promise((resolve) => setTimeout(resolve, 200));
    const diagnostics = manager.diagnostics('fs');
    assert.ok(diagnostics.length <= 200);
    assert.ok(diagnostics.some((line) => line.includes('diagnostic line')));
    await manager.shutdown();
  });

  it('ExitTerminatesWholeProcessTree', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mcp-proxy-pid-'));
    const pidFile = join(directory, 'pid.txt');
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--pid-file', pidFile])),
    });
    await manager.startAll();
    const pid = Number((await readFile(pidFile, 'utf8')).trim());
    assert.ok(pid > 0);

    await manager.shutdown();

    // 进程树终止后，上游进程应已不在（kill(pid, 0) 抛错即已退出）。
    await new Promise((resolve) => setTimeout(resolve, 500));
    assert.throws(() => process.kill(pid, 0));
  });
});

describe('UpstreamStateTests', () => {
  it('ReportsToolCount', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'a,b,c'])),
    });
    await manager.startAll();
    const snapshot = manager.snapshots()[0];
    assert.equal(snapshot?.toolCount, 3);
    assert.equal(snapshot?.status, 'running');
    assert.equal(snapshot?.lastError, undefined);
    await manager.shutdown();
  });

  it('KeepsBoundedDiagnosticTail', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--noise', 'diagnostic line'])),
    });
    await manager.startAll();
    await new Promise((resolve) => setTimeout(resolve, 200));
    const diagnostics = manager.diagnostics('fs');
    assert.ok(diagnostics.length <= 200);
    assert.ok(diagnostics.some((line) => line.includes('diagnostic line')));
    await manager.shutdown();
  });
});

describe('UpstreamChannelTests', () => {
  it('NoNonProtocolBytesOnUpstreamStdout', async () => {
    // 上游的标准错误有噪声时，其标准输出仍必须只有协议消息——
    // 代理读到的必须是干净的协议流。
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'echo', '--noise', 'noise on stderr'])),
    });
    await manager.startAll();
    const entries = manager.toolEntries();
    assert.equal(entries.length, 1);
    const result = await manager.callTool('fs__echo', { text: 'hello' });
    const blocks = result.content as Array<{ text?: string }>;
    assert.ok((blocks[0]?.text ?? '').includes('called echo'));
    await manager.shutdown();
  });
});
