/**
 * 对下游的 MCP 服务端：把各上游的工具聚合成一个工具集合暴露出去。
 */
import { Server } from '@modelcontextprotocol/server';
import { z } from 'zod';

import { BUILTIN_TOOLS, findBuiltinTool } from './builtins.js';
import { failureResult, successResult, ToolErrorCode } from './contract.js';
import { CallCancelledError, ProxyError, UpstreamManager } from './upstream.js';
import { validateArguments } from './validation.js';
import { SERVER_NAME } from './version.js';

/** 协议要求的工具输入模式形状：对象的 JSON Schema。 */
type InputJsonSchema = { [key: string]: unknown; type: 'object' };

/** 把 zod 模式转成协议要求的 JSON Schema。 */
function toInputJsonSchema(schema: z.ZodType): InputJsonSchema {
  const json = z.toJSONSchema(schema) as { [key: string]: unknown; type?: string };
  if (json.type !== 'object') {
    throw new Error('Tool input schema must describe an object');
  }
  return { ...json, type: 'object' };
}

/**
 * 创建代理的下游服务端。
 *
 * 工具集是动态的：上游重启后工具集合随之变化，故每次列举都从管理器现取。
 */
export function createProxyServer(options: { manager: UpstreamManager; serverVersion: string }): Server {
  const { manager } = options;
  const server = new Server({ name: SERVER_NAME, version: options.serverVersion });

  server.registerCapabilities({ tools: { listChanged: false } });

  server.setRequestHandler('tools/list', async () => {
    const aggregated = manager.toolEntries().map((entry) => ({
      name: entry.downstreamName,
      description: entry.description,
      inputSchema: entry.inputSchema as InputJsonSchema,
      ...(entry.outputSchema ? { outputSchema: entry.outputSchema } : {}),
    }));

    const builtins = BUILTIN_TOOLS.map((tool) => ({
      name: tool.name,
      title: tool.title,
      description: tool.description,
      inputSchema: toInputJsonSchema(tool.inputSchema),
    }));

    return { tools: [...aggregated, ...builtins] };
  });

  server.setRequestHandler('tools/call', async (request, ctx) => {
    // 会话状态守卫：未完成握手时拒绝工具调用（协议层错误，非工具层失败）。
    if (server.getNegotiatedProtocolVersion() === undefined) {
      throw Object.assign(new Error('Server not initialized'), { code: -32600 });
    }

    const { name, arguments: args } = request.params;
    const input = (args ?? {}) as Record<string, unknown>;

    const builtin = findBuiltinTool(name);
    if (builtin !== undefined) {
      const parsed = builtin.inputSchema.safeParse(input);
      if (!parsed.success) {
        return failureResult(ToolErrorCode.Failed, `Invalid arguments for tool: ${name}`);
      }
      try {
        const output = await builtin.handle(parsed.data as Record<string, unknown>, manager);
        return successResult(output as Record<string, unknown>);
      } catch (error) {
        const code =
          typeof error === 'object' && error !== null && 'code' in error
            ? String((error as { code: unknown }).code)
            : ToolErrorCode.Failed;
        const message = error instanceof Error ? error.message : String(error);
        return failureResult(
          code === ToolErrorCode.NotFound || code === ToolErrorCode.Failed
            ? code
            : ToolErrorCode.Failed,
          message,
        );
      }
    }

    const entry = manager.resolveTool(name);
    if (entry === undefined) {
      return failureResult(ToolErrorCode.NotFound, `Tool not found: ${name}`);
    }

    const invalid = validateArguments(input, entry.inputSchema);
    if (invalid !== undefined) {
      return failureResult(ToolErrorCode.Failed, `Invalid arguments for tool: ${name}: ${invalid}`);
    }

    try {
      const result = await manager.callTool(name, input, ctx.mcpReq.signal);
      if (result.isError === true) {
        return failureResult(
          ToolErrorCode.UpstreamToolFailed,
          `Upstream '${entry.upstreamId}' failed: ${textOf(result.content)}`,
        );
      }
      return {
        content: result.content,
        ...(result.structuredContent !== undefined
          ? { structuredContent: result.structuredContent }
          : {}),
        isError: false as const,
      };
    } catch (error) {
      if (error instanceof CallCancelledError) {
        // 客户端主动取消：不返回工具结果，改为中止本次调用。
        throw Object.assign(new Error('Call cancelled'), { code: -32800 });
      }
      if (error instanceof ProxyError) {
        return failureResult(error.code, error.message);
      }
      return failureResult(
        ToolErrorCode.UpstreamUnavailable,
        `Upstream '${entry.upstreamId}' is not available: ${
          error instanceof Error ? error.message : String(error)
        }`,
      );
    }
  });

  return server;
}

/** 取内容块里的文本，用于错误描述。 */
function textOf(content: unknown[]): string {
  const parts: string[] = [];
  for (const block of content) {
    if (typeof block === 'object' && block !== null && 'text' in block) {
      const text = (block as { text?: unknown }).text;
      if (typeof text === 'string') {
        parts.push(text);
      }
    }
  }
  return parts.join('\n');
}
