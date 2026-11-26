/**
 * 终止信号收尾行为的单元测试。
 *
 * 这一组测试不依赖「父进程能否给子进程投递信号」——Windows 上做不到
 * （child.kill('SIGTERM') 是强杀，SIGBREAK 亦然），POSIX 上才成立。
 * 收尾逻辑本身（写诊断、退出、不写标准输出）在任何平台都直接断言。
 */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { createShutdownHandler, installShutdownHandlers } from '../src/shutdown.js';

describe('终止信号收尾', () => {
  it('收到信号时写一条诊断，并以 0 退出', () => {
    const reports: string[] = [];
    const exits: number[] = [];
    const handler = createShutdownHandler({
      report: (message) => reports.push(message),
      exit: (code) => exits.push(code),
    });

    handler('SIGTERM');

    assert.equal(reports.length, 1);
    assert.match(reports[0] ?? '', /received SIGTERM/);
    assert.deepEqual(exits, [0]);
  });

  it('诊断信息只走标准错误，退出前不向标准输出写入任何内容', () => {
    const stdoutWrites: string[] = [];
    const originalWrite = process.stdout.write.bind(process.stdout);
    (process.stdout as unknown as { write: unknown }).write = (
      chunk: string | Uint8Array,
    ): boolean => {
      stdoutWrites.push(typeof chunk === 'string' ? chunk : '<bytes>');
      return true;
    };

    try {
      const reports: string[] = [];
      const handler = createShutdownHandler({
        report: (message) => reports.push(message),
        exit: () => {
          throw new Error('__exit__');
        },
      });

      assert.throws(() => handler('SIGINT'), /__exit__/);
      assert.equal(reports.length, 1);
      assert.deepEqual(stdoutWrites, []);
    } finally {
      (process.stdout as unknown as { write: unknown }).write = originalWrite;
    }
  });

  it('各信号的诊断内容随信号名而变', () => {
    const reports: string[] = [];
    const handler = createShutdownHandler({
      report: (message) => reports.push(message),
      exit: () => undefined,
    });

    handler('SIGINT');
    handler('SIGBREAK');

    assert.match(reports[0] ?? '', /received SIGINT/);
    assert.match(reports[1] ?? '', /received SIGBREAK/);
  });

  it('注册后 SIGINT / SIGTERM 生效，注销后移除', () => {
    const before = {
      sigint: process.listenerCount('SIGINT'),
      sigterm: process.listenerCount('SIGTERM'),
    };

    const dispose = installShutdownHandlers({
      report: () => undefined,
      exit: () => undefined,
    });

    assert.equal(process.listenerCount('SIGINT'), before.sigint + 1);
    assert.equal(process.listenerCount('SIGTERM'), before.sigterm + 1);

    dispose();

    assert.equal(process.listenerCount('SIGINT'), before.sigint);
    assert.equal(process.listenerCount('SIGTERM'), before.sigterm);
  });

  it('Windows 上额外注册 SIGBREAK（控制台 Ctrl+Break）', () => {
    const before = process.listenerCount('SIGBREAK');
    const dispose = installShutdownHandlers({
      report: () => undefined,
      exit: () => undefined,
    });

    const expected = process.platform === 'win32' ? before + 1 : before;
    assert.equal(process.listenerCount('SIGBREAK'), expected);

    dispose();
    assert.equal(process.listenerCount('SIGBREAK'), before);
  });
});
