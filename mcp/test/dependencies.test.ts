/**
 * 依赖与源码层面的引擎无关性检查。
 *
 * 「不依赖日志引擎」不能只靠行为测试反推——依赖清单里多一个引擎绑定、
 * 源码里多一处引擎调用，都是行为测试看不见的耦合。故此处直接查清单与源码。
 */
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const here = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(here, '..', '..');
const srcRoot = path.join(projectRoot, 'src');

/** 递归列出源文件。 */
function listSourceFiles(dir: string): string[] {
  const files: string[] = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...listSourceFiles(full));
    } else if (entry.name.endsWith('.ts')) {
      files.push(full);
    }
  }
  return files;
}

describe('依赖清单不含日志引擎', () => {
  const manifest = JSON.parse(
    fs.readFileSync(path.join(projectRoot, 'package.json'), 'utf8'),
  ) as {
    dependencies?: Record<string, string>;
    devDependencies?: Record<string, string>;
  };

  it('依赖与开发依赖均不含引擎绑定', () => {
    const all = { ...(manifest.dependencies ?? {}), ...(manifest.devDependencies ?? {}) };
    const suspicious = Object.keys(all).filter((name) =>
      /logcat|logcore|log-|engine|ffi|koffi|native|rust/i.test(name),
    );

    assert.deepEqual(suspicious, []);
  });

  it('SDK 与模式校验库按精确版本锁定', () => {
    const deps = manifest.dependencies ?? {};
    assert.equal(deps['@modelcontextprotocol/server'], '2.4.0');
    assert.equal(deps.zod, '4.6.7');
    for (const version of Object.values(deps)) {
      assert.doesNotMatch(version, /[\^~><*]/, '依赖应锁定精确版本');
    }
  });

  it('不引入 lint 框架与格式化工具', () => {
    const all = { ...(manifest.dependencies ?? {}), ...(manifest.devDependencies ?? {}) };
    const lintTools = Object.keys(all).filter((name) =>
      /eslint|prettier|biome|standard|oxlint|stylelint/i.test(name),
    );

    assert.deepEqual(lintTools, []);
  });
});

describe('源码不含引擎调用', () => {
  it('文件系统访问只出现在版本读取处，且只读工程清单', () => {
    const files = listSourceFiles(srcRoot);
    assert.ok(files.length > 0);

    for (const file of files) {
      const text = fs.readFileSync(file, 'utf8');
      const usesFs = /from ['"]node:(fs|fs\/promises)['"]/.test(text);

      if (!usesFs) {
        continue;
      }
      // 允许的唯一文件系统访问是读取工程自身清单的版本字段。
      assert.equal(path.basename(file), 'version.ts');
      assert.doesNotMatch(text, /koffi|ffi|dlopen/i);
      assert.match(text, /'package\.json'/);
      // 不得出现除工程清单以外的数据文件字面量。
      const literals = [...text.matchAll(/'([^']*\.[A-Za-z0-9]+)'/g)].map((m) => m[1] ?? '');
      for (const literal of literals) {
        assert.equal(literal, 'package.json');
      }
    }
  });

  it('源码不引用日志引擎的可执行名或库名', () => {
    for (const file of listSourceFiles(srcRoot)) {
      const text = fs.readFileSync(file, 'utf8');
      assert.doesNotMatch(
        text,
        /logcat\.exe|logcore|adb\s+logcat/i,
        `${path.basename(file)} 不应引用日志引擎`,
      );
    }
  });

  it('源码不读取引擎产出的日志数据文件', () => {
    for (const file of listSourceFiles(srcRoot)) {
      const text = fs.readFileSync(file, 'utf8');
      assert.doesNotMatch(text, /\.log['"`]/, `${path.basename(file)} 不应读取日志文件`);
    }
  });
});
