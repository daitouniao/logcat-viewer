/**
 * 启动参数解析。
 *
 * 只有两个参数：`--config <路径>` 与 `--port <端口>`。不引入子命令解析层——
 * 这个体量的命令行不值得。
 */

/** 解析结果。 */
export interface ParsedArgs {
  configPath?: string;
  port?: number;
  help: boolean;
  /** 无法解析时的错误描述。 */
  error?: string;
}

/** 解析命令行参数。 */
export function parseArgs(argv: string[]): ParsedArgs {
  const parsed: ParsedArgs = { help: false };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') {
      parsed.help = true;
      continue;
    }
    if (arg === '--config') {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith('--')) {
        return { help: false, error: '--config requires a path' };
      }
      parsed.configPath = value;
      index += 1;
      continue;
    }
    if (arg?.startsWith('--config=')) {
      const value = arg.slice('--config='.length);
      if (value.length === 0) {
        return { help: false, error: '--config requires a path' };
      }
      parsed.configPath = value;
      continue;
    }
    if (arg === '--port') {
      const value = argv[index + 1];
      const port = value === undefined ? Number.NaN : Number(value);
      if (!Number.isInteger(port) || port < 0 || port > 65535) {
        return { help: false, error: '--port requires a port number between 0 and 65535' };
      }
      parsed.port = port;
      index += 1;
      continue;
    }
    if (arg?.startsWith('--port=')) {
      const port = Number(arg.slice('--port='.length));
      if (!Number.isInteger(port) || port < 0 || port > 65535) {
        return { help: false, error: '--port requires a port number between 0 and 65535' };
      }
      parsed.port = port;
      continue;
    }
    return { help: false, error: `Unknown argument: ${arg}` };
  }

  return parsed;
}

/** 用法文本。 */
export const USAGE = `logcat-mcp-proxy — MCP proxy (aggregates stdio upstreams behind one stdio server)

Usage:
  logcat-mcp-proxy [--config <path>] [--port <port>]

Options:
  --config <path>  Configuration file (default: user config directory)
  --port <port>    Management page port (default: a random free port)
  -h, --help       Show this help
`;
