import assert from 'node:assert/strict';
import { after, describe, it } from 'node:test';

import { ToolErrorCode } from '../src/contract.js';
import { ProxyError } from '../src/upstream.js';
import { validateArguments } from '../src/validation.js';
import { configOf, fakeUpstream, shutdownAllManagers, TrackedUpstreamManager as UpstreamManager } from './helpers/support.js';

after(async () => {
  await shutdownAllManagers();
});

async function managerWith(...args: string[]): Promise<UpstreamManager> {
  const manager = new UpstreamManager({
    config: configOf(fakeUpstream('fs', args)),
  });
  await manager.startAll();
  return manager;
}

describe('RoutingTests', () => {
  it('RoutesToMatchingUpstream', async () => {
    const manager = await managerWith('--tools', 'read_file');
    const result = await manager.callTool('fs__read_file', { text: 'x' });
    const blocks = result.content as Array<{ text?: string }>;
    assert.ok((blocks[0]?.text ?? '').includes('read_file'));
    await manager.shutdown();
  });

  it('ReturnsUpstreamResultUnchanged', async () => {
    const manager = await managerWith('--tools', 'echo');
    const result = await manager.callTool('fs__echo', { text: 'payload' });
    assert.equal(result.isError, undefined);
    const blocks = result.content as Array<{ text?: string }>;
    assert.ok((blocks[0]?.text ?? '').includes('payload'));
    await manager.shutdown();
  });

  it('UsesRegistryNotStringSplit', async () => {
    const manager = await managerWith('--tools', 'read__file');
    // 规范化后下游名是 fs__read_file；按表查得到原始工具 read__file。
    const entry = manager.resolveTool('fs__read_file');
    assert.equal(entry?.upstreamName, 'read__file');
    const result = await manager.callTool('fs__read_file', { text: 'x' });
    const blocks = result.content as Array<{ text?: string }>;
    assert.ok((blocks[0]?.text ?? '').includes('read__file'));
    await manager.shutdown();
  });

  it('UnknownNamespaceReportsToolNotFound', async () => {
    const manager = await managerWith('--tools', 'echo');
    await assert.rejects(
      () => manager.callTool('ghost__anything', {}),
      (error: unknown) => {
        assert.ok(error instanceof ProxyError);
        assert.equal(error.code, ToolErrorCode.NotFound);
        return true;
      },
    );
    await manager.shutdown();
  });

  it('UpstreamFailureReportsUpstreamToolFailed', async () => {
    const manager = await managerWith('--tools', 'boom', '--fail', 'boom');
    await assert.rejects(
      () => manager.callTool('fs__boom', { text: 'x' }),
      (error: unknown) => {
        assert.ok(error instanceof ProxyError);
        assert.equal(error.code, ToolErrorCode.UpstreamToolFailed);
        assert.ok(error.message.includes('upstream failure detail'));
        return true;
      },
    );
    await manager.shutdown();
  });

  it('UnavailableUpstreamReportsUpstreamUnavailable', async () => {
    const manager = await managerWith('--tools', 'echo');
    await manager.stop('fs');
    await assert.rejects(
      () => manager.callTool('fs__echo', { text: 'x' }),
      (error: unknown) => {
        assert.ok(error instanceof ProxyError);
        assert.equal(error.code, ToolErrorCode.UpstreamUnavailable);
        return true;
      },
    );
    await manager.shutdown();
  });

  it('KeepsUpstreamErrorText', async () => {
    const manager = await managerWith('--tools', 'boom', '--fail', 'boom');
    await assert.rejects(
      () => manager.callTool('fs__boom', { text: 'x' }),
      (error: unknown) => {
        assert.ok(error instanceof ProxyError);
        assert.ok(error.message.includes('upstream failure detail'));
        assert.ok(error.message.includes("'fs'"));
        return true;
      },
    );
    await manager.shutdown();
  });

  it('ResolveAmbiguousIdAndToolNames', async () => {
    const manager = new UpstreamManager({
      config: configOf(
        fakeUpstream('a', ['--tools', 'b__c']),
        fakeUpstream('a_b', ['--tools', 'c']),
      ),
    });
    await manager.startAll();
    // 两个上游的名字形似：路由靠对应表，不靠切分名字。
    const first = manager.resolveTool('a__b_c');
    const second = manager.resolveTool('a_b__c');
    assert.equal(first?.upstreamId, 'a');
    assert.equal(first?.upstreamName, 'b__c');
    assert.equal(second?.upstreamId, 'a_b');
    assert.equal(second?.upstreamName, 'c');
    await manager.shutdown();
  });

  it('LaterUpstreamMarkedUnavailableOnNameCollision', async () => {
    // 一个上游里两个原始名规范化后撞名：整个上游判不可用并记冲突原因，
    // 先注册者（此处是另一个上游）不受影响。
    const manager = new UpstreamManager({
      config: configOf(
        fakeUpstream('ok', ['--tools', 'search']),
        fakeUpstream('clash', ['--tools', 'a b,a_b']),
      ),
    });
    await manager.startAll();
    const byId = new Map(manager.snapshots().map((item) => [item.id, item]));
    assert.equal(byId.get('ok')?.status, 'running');
    assert.equal(byId.get('clash')?.status, 'failed');
    assert.ok((byId.get('clash')?.lastError ?? '').includes('conflicts'));
    await manager.shutdown();
  });

  it('InvalidArgumentsRejectedLocally', () => {
    const problem = validateArguments(
      {},
      { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] },
    );
    assert.ok(problem !== undefined);
    assert.ok(problem.includes('text'));
    assert.equal(
      validateArguments({ text: 'ok' }, { type: 'object', required: ['text'] }),
      undefined,
    );
  });

  it('TimesOutLongRunningUpstreamCall', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'slow', '--slow', 'slow'])),
      callTimeoutMs: 400,
    });
    await manager.startAll();
    await assert.rejects(
      () => manager.callTool('fs__slow', { text: 'x' }),
      (error: unknown) => {
        assert.ok(error instanceof ProxyError);
        assert.equal(error.code, ToolErrorCode.UpstreamUnavailable);
        assert.ok(error.message.includes('timed out'));
        return true;
      },
    );
    await manager.shutdown();
  });

  it('ReturnsTimeoutErrorCode', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'slow', '--slow', 'slow'])),
      callTimeoutMs: 300,
    });
    await manager.startAll();
    await assert.rejects(
      () => manager.callTool('fs__slow', { text: 'x' }),
      (error: unknown) => {
        assert.ok(error instanceof ProxyError);
        assert.equal(error.code, 'UPSTREAM_UNAVAILABLE');
        return true;
      },
    );
    await manager.shutdown();
  });

  it('ClientCancellationAbortsWithoutResult', async () => {
    const manager = new UpstreamManager({
      config: configOf(fakeUpstream('fs', ['--tools', 'slow', '--slow', 'slow'])),
      callTimeoutMs: 5000,
    });
    await manager.startAll();
    const controller = new AbortController();
    const pending = manager.callTool('fs__slow', { text: 'x' }, controller.signal);
    setTimeout(() => controller.abort(), 50);
    await assert.rejects(pending, (error: unknown) => {
      assert.ok(error instanceof ProxyError || error instanceof Error);
      return true;
    });
    await manager.shutdown();
  });
});
