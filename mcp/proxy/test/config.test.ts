import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

import {
  ConfigError,
  loadConfig,
  MASK,
  mergeMaskedEntry,
  resolveEntryEnv,
  saveConfigAtomic,
  validateConfig,
  type UpstreamEntry,
} from '../src/config.js';

/** 断言校验失败，并返回错误里的问题清单。 */
function expectConfigError(run: () => unknown): string[] {
  try {
    run();
  } catch (error) {
    assert.ok(error instanceof ConfigError);
    return error.issues;
  }
  assert.fail('expected a ConfigError');
}

describe('ConfigValidationTests', () => {
  it('RejectsDuplicateId', () => {
    const issues = expectConfigError(() =>
      validateConfig({
        upstreams: [
          { id: 'fs', command: 'node' },
          { id: 'fs', command: 'node' },
        ],
      }),
    );
    assert.ok(issues.some((issue) => issue.includes('duplicated')));
  });

  it('RejectsIllegalId', () => {
    const issues = expectConfigError(() =>
      validateConfig({ upstreams: [{ id: 'Bad Id', command: 'node' }] }),
    );
    assert.ok(issues.some((issue) => issue.includes('must match')));
  });

  it('RejectsIdWithDoubleUnderscore', () => {
    const issues = expectConfigError(() =>
      validateConfig({ upstreams: [{ id: 'a__b', command: 'node' }] }),
    );
    assert.ok(issues.some((issue) => issue.includes('consecutive underscores')));
  });

  it('RejectsMissingCommand', () => {
    const issues = expectConfigError(() => validateConfig({ upstreams: [{ id: 'fs' }] }));
    assert.ok(issues.some((issue) => issue.includes('command')));
  });

  it('AcceptsMinimalEntry', () => {
    const config = validateConfig({ upstreams: [{ id: 'fs', command: 'node' }] });
    assert.equal(config.upstreams.length, 1);
    const entry = config.upstreams[0] as UpstreamEntry;
    assert.equal(entry.id, 'fs');
    assert.deepEqual(entry.args, []);
    assert.deepEqual(entry.env, {});
    assert.equal(entry.enabled, true);
  });

  it('AcceptsEnvReferenceValue', () => {
    const config = validateConfig({
      upstreams: [{ id: 'fs', command: 'node', env: { TOKEN: 'env:MY_TOKEN' } }],
    });
    const entry = config.upstreams[0] as UpstreamEntry;
    assert.equal(entry.env['TOKEN'], 'env:MY_TOKEN');
    assert.deepEqual(resolveEntryEnv(entry, { MY_TOKEN: 'secret-value' }), {
      TOKEN: 'secret-value',
    });
  });
});

describe('ConfigStoreTests', () => {
  it('WritesAtomically', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mcp-proxy-config-'));
    const path = join(directory, 'config.json');
    await saveConfigAtomic(path, {
      upstreams: [{ id: 'fs', command: 'node', args: [], env: {}, enabled: true }],
    });
    const raw = await readFile(path, 'utf8');
    const parsed = JSON.parse(raw) as { upstreams: unknown[] };
    assert.equal(parsed.upstreams.length, 1);
    // 原子写：目录里不留临时文件。
    const { readdir } = await import('node:fs/promises');
    const files = await readdir(directory);
    assert.deepEqual(files, ['config.json']);
  });

  it('CreatesMissingDirectory', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mcp-proxy-config-'));
    const path = join(directory, 'nested', 'deeper', 'config.json');
    await saveConfigAtomic(path, { upstreams: [] });
    const loaded = await loadConfig(path);
    assert.deepEqual(loaded.upstreams, []);
  });

  it('PersistsAcrossReload', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mcp-proxy-config-'));
    const path = join(directory, 'config.json');
    const entry: UpstreamEntry = {
      id: 'fs',
      command: 'node',
      args: ['server.js'],
      env: { TOKEN: 'plain' },
      enabled: false,
    };
    await saveConfigAtomic(path, { upstreams: [entry] });
    const loaded = await loadConfig(path);
    assert.equal(loaded.upstreams.length, 1);
    assert.deepEqual(loaded.upstreams[0], entry);
  });

  it('KeepsMaskedValuesOnSave', async () => {
    const existing: UpstreamEntry = {
      id: 'fs',
      command: 'node',
      args: [],
      env: { TOKEN: 'real-secret' },
      enabled: true,
    };
    const incoming: UpstreamEntry = {
      ...existing,
      env: { TOKEN: MASK },
    };
    const merged = mergeMaskedEntry(existing, incoming);
    assert.equal(merged.env['TOKEN'], 'real-secret');
  });

  it('ReportsInvalidJson', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'mcp-proxy-config-'));
    const path = join(directory, 'config.json');
    await writeFile(path, '{ not json', 'utf8');
    await assert.rejects(() => loadConfig(path), (error: unknown) => {
      assert.ok(error instanceof ConfigError);
      return true;
    });
  });

  it('AcceptsShippedExampleConfig', async () => {
    const examplePath = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'config.example.json');
    const raw = await readFile(examplePath, 'utf8');
    const parsed = JSON.parse(raw) as unknown;
    const config = validateConfig(parsed);
    assert.equal(config.upstreams.length, 2);
    assert.equal(config.upstreams[0]?.id, 'logcat');
    // 示例里演示了引用写法：引用形式不落盘。
    assert.equal(config.upstreams[1]?.env['API_TOKEN'], 'env:MY_MCP_API_TOKEN');
  });
});
