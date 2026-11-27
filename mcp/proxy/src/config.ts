/**
 * 配置模型、校验与持久化。
 *
 * 配置放在用户配置目录（不进仓库），条目里的环境变量可用 `"env:VAR"` 形式
 * 引用外部环境变量——引用形式不落盘。
 */
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';

/** 上游条目：描述一个以标准输入输出承载的 MCP 服务端怎么起。 */
export interface UpstreamEntry {
  /** 唯一标识，同时是下游工具名的命名空间前缀。 */
  id: string;
  /** 启动命令。 */
  command: string;
  /** 命令行参数。 */
  args: string[];
  /** 透传给上游子进程的环境变量；值可为 `"env:VAR"` 引用形式。 */
  env: Record<string, string>;
  /** 工作目录，缺省继承代理进程的工作目录。 */
  cwd?: string;
  /** 是否在启动时拉起。 */
  enabled: boolean;
  /** 就绪时限（毫秒），缺省取全局默认。 */
  readyTimeoutMs?: number;
  /** 调用时限（毫秒），缺省取全局默认。 */
  callTimeoutMs?: number;
}

/** 代理配置。 */
export interface ProxyConfig {
  upstreams: UpstreamEntry[];
}

/** 标识的字符约束：小写字母数字开头，可含下划线与连字符，但不得含连续下划线。 */
export const ID_PATTERN = /^[a-z0-9][a-z0-9_-]*$/;

/** 敏感值在页面上的掩码。 */
export const MASK = '••••••';

/** 配置校验失败：一次报出全部问题，便于用户一次改完。 */
export class ConfigError extends Error {
  readonly issues: string[];

  constructor(issues: string[]) {
    super(`Invalid configuration: ${issues.join('; ')}`);
    this.name = 'ConfigError';
    this.issues = issues;
  }
}

/** 引用外部环境变量的前缀。 */
export const ENV_REFERENCE_PREFIX = 'env:';

/**
 * 校验配置。
 *
 * 校验失败抛出 {@link ConfigError}，其中列出全部问题；不合法的条目不会被静默
 * 丢弃或覆盖——重复标识尤其不能「取最后一个」。
 */
export function validateConfig(raw: unknown): ProxyConfig {
  const issues: string[] = [];

  if (!isRecord(raw)) {
    throw new ConfigError(['configuration must be a JSON object']);
  }

  const rawUpstreams = raw['upstreams'];
  if (!Array.isArray(rawUpstreams)) {
    throw new ConfigError(['"upstreams" must be an array']);
  }

  const upstreams: UpstreamEntry[] = [];
  const seen = new Set<string>();

  rawUpstreams.forEach((item, index) => {
    const label = `upstreams[${index}]`;
    if (!isRecord(item)) {
      issues.push(`${label} must be an object`);
      return;
    }

    const id = item['id'];
    if (typeof id !== 'string' || id.length === 0) {
      issues.push(`${label}.id must be a non-empty string`);
    } else if (!ID_PATTERN.test(id)) {
      issues.push(`${label}.id "${id}" must match ${ID_PATTERN.source}`);
    } else if (id.includes('__')) {
      issues.push(`${label}.id "${id}" must not contain consecutive underscores`);
    } else if (seen.has(id)) {
      issues.push(`${label}.id "${id}" is duplicated`);
    } else {
      seen.add(id);
    }

    const command = item['command'];
    if (typeof command !== 'string' || command.trim().length === 0) {
      issues.push(`${label}.command must be a non-empty string`);
    }

    const args = item['args'];
    if (args !== undefined && (!Array.isArray(args) || args.some((a) => typeof a !== 'string'))) {
      issues.push(`${label}.args must be an array of strings`);
    }

    const env = item['env'];
    if (env !== undefined && !isRecord(env)) {
      issues.push(`${label}.env must be an object of string values`);
    } else if (isRecord(env)) {
      for (const [key, value] of Object.entries(env)) {
        if (typeof value !== 'string') {
          issues.push(`${label}.env.${key} must be a string`);
        }
      }
    }

    const cwd = item['cwd'];
    if (cwd !== undefined && cwd !== null && (typeof cwd !== 'string' || cwd.length === 0)) {
      issues.push(`${label}.cwd must be a non-empty string when present`);
    }

    const enabled = item['enabled'];
    if (enabled !== undefined && typeof enabled !== 'boolean') {
      issues.push(`${label}.enabled must be a boolean`);
    }

    const readyTimeoutMs = item['readyTimeoutMs'];
    if (readyTimeoutMs !== undefined && (typeof readyTimeoutMs !== 'number' || readyTimeoutMs <= 0)) {
      issues.push(`${label}.readyTimeoutMs must be a positive number when present`);
    }

    const callTimeoutMs = item['callTimeoutMs'];
    if (callTimeoutMs !== undefined && (typeof callTimeoutMs !== 'number' || callTimeoutMs <= 0)) {
      issues.push(`${label}.callTimeoutMs must be a positive number when present`);
    }

    if (typeof id === 'string' && typeof command === 'string' && command.trim().length > 0) {
      const entry: UpstreamEntry = {
        id,
        command,
        args: Array.isArray(args) ? (args as string[]) : [],
        env: isRecord(env) ? (env as Record<string, string>) : {},
        enabled: enabled === undefined ? true : (enabled as boolean),
      };
      if (typeof cwd === 'string' && cwd.length > 0) {
        entry.cwd = cwd;
      }
      if (typeof readyTimeoutMs === 'number') {
        entry.readyTimeoutMs = readyTimeoutMs;
      }
      if (typeof callTimeoutMs === 'number') {
        entry.callTimeoutMs = callTimeoutMs;
      }
      upstreams.push(entry);
    }
  });

  if (issues.length > 0) {
    throw new ConfigError(issues);
  }

  return { upstreams };
}

/** 配置的默认位置：用户配置目录。 */
export function defaultConfigPath(): string {
  if (process.platform === 'win32') {
    const appData = process.env['APPDATA'];
    if (appData && appData.length > 0) {
      return join(appData, 'logcat', 'mcp-proxy', 'config.json');
    }
  }
  return join(homedir(), '.config', 'logcat', 'mcp-proxy', 'config.json');
}

/** 解析配置路径：显式路径优先，否则用默认位置。 */
export function resolveConfigPath(explicit?: string): string {
  return explicit && explicit.length > 0 ? explicit : defaultConfigPath();
}

/** 读取配置；文件不存在时返回空配置。 */
export async function loadConfig(path: string): Promise<ProxyConfig> {
  let raw: string;
  try {
    raw = await readFile(path, 'utf8');
  } catch (error) {
    if (isNodeError(error) && error.code === 'ENOENT') {
      return { upstreams: [] };
    }
    throw error;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (error) {
    throw new ConfigError([`configuration file is not valid JSON: ${(error as Error).message}`]);
  }

  return validateConfig(parsed);
}

/**
 * 原子落盘：先写临时文件再改名。
 *
 * 改名在同一文件系统内是原子的——写到一半被杀掉不会留下半个配置文件，那会让
 * 用户下次启动时全部上游都起不来。
 */
export async function saveConfigAtomic(path: string, config: ProxyConfig): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  const temporary = `${path}.${process.pid}.tmp`;
  await writeFile(temporary, `${JSON.stringify(config, null, 2)}\n`, 'utf8');
  await rename(temporary, path);
}

/**
 * 解析条目的环境变量。
 *
 * 取值形如 `"env:VAR"` 时从代理进程的环境变量取值（不落盘）；其余按字面量。
 */
export function resolveEntryEnv(
  entry: UpstreamEntry,
  env: NodeJS.ProcessEnv = process.env,
): Record<string, string> {
  const resolved: Record<string, string> = {};
  for (const [key, value] of Object.entries(entry.env)) {
    if (value.startsWith(ENV_REFERENCE_PREFIX)) {
      const name = value.slice(ENV_REFERENCE_PREFIX.length);
      const actual = env[name];
      if (actual !== undefined) {
        resolved[key] = actual;
      }
    } else {
      resolved[key] = value;
    }
  }
  return resolved;
}

/** 取值是否引用了外部环境变量（引用形式不落盘，页面据此掩码）。 */
export function isEnvReference(value: string): boolean {
  return value.startsWith(ENV_REFERENCE_PREFIX);
}

/** 把条目里的敏感值掩码，供页面展示。 */
export function maskEntry(entry: UpstreamEntry): UpstreamEntry {
  const env: Record<string, string> = {};
  for (const [key, value] of Object.entries(entry.env)) {
    env[key] = isEnvReference(value) ? value : MASK;
  }
  return { ...entry, env };
}

/**
 * 合并页面提交的条目与已有条目。
 *
 * 页面拿到的敏感值是掩码，用户原样提交时保留原值，不被掩码覆盖成空。
 */
export function mergeMaskedEntry(existing: UpstreamEntry | undefined, incoming: UpstreamEntry): UpstreamEntry {
  if (!existing) {
    return incoming;
  }
  const env: Record<string, string> = { ...incoming.env };
  for (const [key, value] of Object.entries(incoming.env)) {
    if (value === MASK && existing.env[key] !== undefined) {
      env[key] = existing.env[key];
    }
  }
  return { ...incoming, env };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNodeError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && 'code' in error;
}
