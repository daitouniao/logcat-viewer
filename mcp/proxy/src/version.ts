/**
 * 版本与身份标识。
 *
 * 服务端名称出现在握手结果里；契约版本在契约发生不向后兼容的变更时递增。
 */
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** 服务端名称：出现在握手结果与下游客户端看到的工具描述里。 */
export const SERVER_NAME = 'logcat-mcp-proxy';

/** 契约版本：语义化版本串，不向后兼容的契约变更时递增。 */
export const CONTRACT_VERSION = '0.1.0';

/**
 * 读取工程自身的版本声明。
 *
 * 本工程不发布，版本字段留空比编一个数字诚实；读不到时返回空串而非抛错——
 * 版本号不该成为进程启动的障碍。
 */
export function readServerVersion(): string {
  try {
    const here = dirname(fileURLToPath(import.meta.url));
    const raw = readFileSync(join(here, '..', '..', 'package.json'), 'utf8');
    const parsed = JSON.parse(raw) as { version?: unknown };
    return typeof parsed.version === 'string' ? parsed.version : '';
  } catch {
    return '';
  }
}
