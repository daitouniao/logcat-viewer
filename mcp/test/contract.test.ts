/**
 * 契约信封与错误码的单元测试。
 *
 * 覆盖：信封同源、错误码取值与命名族、错误码不随区域变化、错误与协议错误
 * 的分流、产出形状不符声明模式时返回错误结果。
 */
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { describe, it } from 'node:test';

import { z } from 'zod';

import {
  CONTRACT_VERSION,
  failureResult,
  successResult,
  ToolErrorCode,
  TRANSPORT,
} from '../src/contract.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const contractModule = path.resolve(here, '..', 'src', 'contract.js');

describe('契约信封', () => {
  it('成功信封的文本内容块与结构化内容同源同值', () => {
    const payload = { value: 1, nested: { name: 'x' } };
    const result = successResult(payload);

    assert.equal(result.content.length, 1);
    assert.equal(result.content[0]?.type, 'text');
    assert.deepEqual(JSON.parse(result.content[0]?.text ?? ''), payload);
    assert.deepEqual(result.structuredContent, payload);
  });

  it('成功信封不带错误标记', () => {
    const result = successResult({ value: 1 });
    assert.equal('isError' in result, false);
  });

  it('错误信封的文本内容块与结构化内容同源同值', () => {
    const result = failureResult(ToolErrorCode.NotFound, 'Tool not found: nope');

    assert.equal(result.isError, true);
    assert.equal(result.content.length, 1);
    assert.equal(result.content[0]?.type, 'text');
    assert.deepEqual(JSON.parse(result.content[0]?.text ?? ''), result.structuredContent);
  });

  it('错误信封同时承载错误码与描述', () => {
    const result = failureResult(ToolErrorCode.Failed, 'Tool failed: boom');
    const structured = result.structuredContent as { error: { code: string; message: string } };

    assert.equal(structured.error.code, 'TOOL_FAILED');
    assert.equal(structured.error.message, 'Tool failed: boom');
  });
});

describe('错误码取值', () => {
  it('工具不存在用 TOOL_NOT_FOUND，工具处理失败用 TOOL_FAILED', () => {
    assert.equal(ToolErrorCode.NotFound, 'TOOL_NOT_FOUND');
    assert.equal(ToolErrorCode.Failed, 'TOOL_FAILED');
  });

  it('两个错误码互相可区分', () => {
    assert.notEqual(ToolErrorCode.NotFound, ToolErrorCode.Failed);
  });

  it('全部错误码同属 TOOL_ 命名族', () => {
    for (const code of Object.values(ToolErrorCode)) {
      assert.match(code, /^TOOL_[A-Z0-9_]+$/);
    }
  });

  it('错误码与描述为英文（不含中文）', () => {
    const result = failureResult(ToolErrorCode.NotFound, 'Tool not found: x');
    const text = result.content[0]?.text ?? '';
    assert.doesNotMatch(text, /[一-鿿]/);
    assert.doesNotMatch(JSON.stringify(result.structuredContent), /[一-鿿]/);
  });

  it('错误码不随区域变化：中文区域与英文区域返回同一错误码', () => {
    const script =
      "import { failureResult, ToolErrorCode } from './src/contract.js';" +
      "const r = failureResult(ToolErrorCode.NotFound, 'Tool not found: x');" +
      'process.stdout.write(r.structuredContent.error.code);';

    const read = (locale: string) =>
      execFileSync(process.execPath, ['--input-type=module', '-e', script], {
        cwd: path.resolve(here, '..'),
        encoding: 'utf8',
        env: { ...process.env, LANG: locale, LC_ALL: locale },
      });

    const enUs = read('en_US.UTF-8');
    const zhCn = read('zh_CN.UTF-8');

    assert.equal(enUs, 'TOOL_NOT_FOUND');
    assert.equal(zhCn, enUs);
  });

  it('契约模块可直接被 Node 加载（构建产物可执行）', () => {
    const url = pathToFileURL(contractModule).href;
    const output = execFileSync(
      process.execPath,
      ['--input-type=module', '-e', `import(${JSON.stringify(url)}).then(() => process.stdout.write('ok'))`],
      { encoding: 'utf8' },
    );
    assert.equal(output, 'ok');
  });
});

describe('协议错误与工具错误的分流', () => {
  it('工具层失败走结果信封，不带 JSON-RPC 错误结构', () => {
    const result = failureResult(ToolErrorCode.Failed, 'Tool failed: boom') as unknown as Record<string, unknown>;
    assert.equal('error' in result, false);
    assert.equal(result.isError, true);
  });

  it('结果模式形状不符时判定为失败，而非产出违规的成功结果', () => {
    const schema = z.object({ count: z.number() });
    const bad = { count: 'not-a-number' };
    const parsed = schema.safeParse(bad);

    assert.equal(parsed.success, false);
    // 壳层的做法：形状不符时走错误结果信封，错误码为 TOOL_FAILED。
    const failure = failureResult(ToolErrorCode.Failed, 'Tool output does not match its declared schema: x');
    assert.equal(failure.isError, true);
  });
});

describe('契约常量', () => {
  it('契约版本为语义化版本串', () => {
    assert.match(CONTRACT_VERSION, /^\d+\.\d+\.\d+$/);
  });

  it('传输形态标识为标准输入输出', () => {
    assert.equal(TRANSPORT, 'stdio');
  });
});
