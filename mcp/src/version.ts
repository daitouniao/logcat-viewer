import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const PACKAGE_NAME = 'logcat-mcp';

/**
 * 读取工程自身声明的版本。
 *
 * 本刀不发布，工程清单里的版本字段为空串——这里如实回读，不编造版本号。
 * 找不到工程清单时返回空串，而非抛错：版本缺失不应阻断服务端启动。
 */
export function readServerVersion(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let depth = 0; depth < 5; depth += 1) {
    const candidate = join(dir, 'package.json');
    if (existsSync(candidate)) {
      try {
        const parsed = JSON.parse(readFileSync(candidate, 'utf8')) as {
          name?: string;
          version?: string;
        };
        if (parsed.name === PACKAGE_NAME) {
          return parsed.version ?? '';
        }
      } catch {
        return '';
      }
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return '';
}
