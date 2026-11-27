/**
 * 代理自带的两个管理工具。
 *
 * 它们不参与上游命名空间（没有 `id__tool` 形状），因此不会与任何上游工具撞名。
 */
import { z } from 'zod';

import { ToolErrorCode } from './contract.js';
import type { UpstreamManager } from './upstream.js';

/** 内置工具定义。 */
export interface BuiltinTool {
  name: string;
  title: string;
  description: string;
  inputSchema: z.ZodType;
  handle(args: Record<string, unknown>, manager: UpstreamManager): Promise<unknown>;
}

/** 列出上游状态的入参模式。 */
export const listServersInputSchema = z.object({});

/** 重启上游的入参模式。 */
export const restartUpstreamInputSchema = z.object({ id: z.string().min(1) });

/** 内置工具清单。 */
export const BUILTIN_TOOLS: BuiltinTool[] = [
  {
    name: 'proxy_servers',
    title: 'List proxied MCP servers',
    description:
      'List every upstream MCP server managed by this proxy: identifier, run state, exposed tool count and the most recent failure reason.',
    inputSchema: listServersInputSchema,
    handle: async (_args, manager) => ({ servers: manager.snapshots() }),
  },
  {
    name: 'proxy_restart',
    title: 'Restart an upstream MCP server',
    description:
      'Restart one upstream MCP server by identifier. Its tool set is refreshed and other upstreams are unaffected.',
    inputSchema: restartUpstreamInputSchema,
    handle: async (args, manager) => {
      const id = args['id'];
      if (typeof id !== 'string' || id.length === 0) {
        throw Object.assign(new Error('id must be a non-empty string'), {
          code: ToolErrorCode.Failed,
        });
      }
      const ok = await manager.restart(id);
      if (!ok) {
        throw Object.assign(new Error(`Upstream '${id}' is not configured`), {
          code: ToolErrorCode.NotFound,
        });
      }
      const snapshot = manager.snapshots().find((item) => item.id === id);
      return {
        id,
        status: snapshot?.status ?? 'stopped',
        toolCount: snapshot?.toolCount ?? 0,
      };
    },
  },
];

/** 按名称找内置工具。 */
export function findBuiltinTool(name: string): BuiltinTool | undefined {
  return BUILTIN_TOOLS.find((tool) => tool.name === name);
}
