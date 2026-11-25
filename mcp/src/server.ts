import { Server } from '@modelcontextprotocol/server';
import { z } from 'zod';

import { failureResult, successResult, ToolErrorCode } from './contract.js';
import {
  buildInfoOutput,
  infoInputSchema,
  infoOutputSchema,
  infoToolDescription,
  infoToolTitle,
} from './tools/info.js';

/** 服务端名称：出现在握手结果与工具描述中。 */
export const SERVER_NAME = 'logcat-mcp';

/**
 * 工具定义。
 *
 * 本壳层自带工具的注册机制；工具的输入与产出都带模式，由壳层在调用前后
 * 校验，校验不过一律以错误结果信封返回。
 */
export interface ToolDefinition {
  readonly name: string;
  readonly title: string;
  readonly description: string;
  readonly inputSchema: z.ZodType;
  readonly outputSchema: z.ZodType;
  handle(args: Record<string, unknown>): Promise<unknown> | unknown;
}

export interface ServerOptions {
  /** 服务端版本：取自工程自身的版本声明。 */
  serverVersion: string;
  /**
   * 是否注册自述工具。
   *
   * 置为 false 可得到一个零工具的服务端，用于验证「零工具注册时列举结果
   * 为空数组而非错误」这一注册机制的一般性。
   */
  includeInfoTool?: boolean;
}

/** 构造自述工具：产出需要读到协商后的协议版本与已声明的能力集，故持有 server 引用。 */
function createInfoTool(server: Server, serverVersion: string): ToolDefinition {
  return {
    name: 'info',
    title: infoToolTitle,
    description: infoToolDescription,
    inputSchema: infoInputSchema,
    outputSchema: infoOutputSchema,
    handle: () =>
      buildInfoOutput({
        serverVersion,
        protocolVersion: server.getNegotiatedProtocolVersion() ?? '',
        capabilities: server.getCapabilities() as Record<string, unknown>,
      }),
  };
}
/** 协议要求的工具输入模式形状：对象的 JSON Schema。 */
type InputJsonSchema = { [key: string]: unknown; type: 'object' };

/**
 * 把工具输入模式转成协议要求的 JSON Schema。
 *
 * 协议要求工具输入模式是对象类型；本壳层的输入模式一律由 `z.object` 定义，
 * 转换后仍断言其类型为对象，不符合即视为编程错误而非静默降级。
 */
function toInputJsonSchema(schema: z.ZodType): InputJsonSchema {
  const json = z.toJSONSchema(schema) as { [key: string]: unknown; type?: string };
  if (json.type !== 'object') {
    throw new Error('Tool input schema must describe an object');
  }
  return { ...json, type: 'object' };
}

/**
 * 会话状态守卫：未完成握手时拒绝请求。
 *
 * 协议层错误走 JSON-RPC 错误通道（非法请求），与工具层失败（带错误标记的
 * 结果）分流。SDK 本身不拦截未握手的工具调用，故由本壳层显式拒绝。
 */
function requireInitialized(server: Server): void {
  if (server.getNegotiatedProtocolVersion() === undefined) {
    throw Object.assign(new Error('Server not initialized'), { code: -32600 });
  }
}

/**
 * 按给定工具集创建服务端。
 *
 * 工具列举与工具调用的处理器由本壳层自带，不依赖 SDK 的高层封装：
 * 这样「零工具时列举为空」与「未注册工具返回 TOOL_NOT_FOUND」两条契约
 * 由本层直接掌控，不受 SDK 内部默认行为影响。
 */
export function createServerWithTools(
  toolFactory: (server: Server) => ToolDefinition[],
  options: { serverVersion: string },
): Server {
  const server = new Server({ name: SERVER_NAME, version: options.serverVersion });

  // 工具集在服务端建好之后才构造：工具产出可能需要读服务端自身的协商结果
  // （如协议版本与能力集），故以工厂形式延迟求值。
  const tools = toolFactory(server);

  // 只声明工具能力，且不声明「工具列表可变更」通知——本壳层的工具集在
  // 进程生命周期内不变。资源与提示能力一律不声明。
  server.registerCapabilities({ tools: { listChanged: false } });

  server.setRequestHandler('tools/list', async () => ({
    tools: tools.map((tool) => ({
      name: tool.name,
      title: tool.title,
      description: tool.description,
      inputSchema: toInputJsonSchema(tool.inputSchema),
    })),
  }));

  server.setRequestHandler('tools/call', async (request) => {
    requireInitialized(server);

    const { name, arguments: args } = request.params;
    const tool = tools.find((candidate) => candidate.name === name);
    if (!tool) {
      return failureResult(ToolErrorCode.NotFound, `Tool not found: ${name}`);
    }

    const parsed = tool.inputSchema.safeParse(args ?? {});
    if (!parsed.success) {
      return failureResult(ToolErrorCode.Failed, `Invalid arguments for tool: ${name}`);
    }

    let output: unknown;
    try {
      output = await tool.handle(parsed.data as Record<string, unknown>);
    } catch {
      return failureResult(ToolErrorCode.Failed, `Tool failed: ${name}`);
    }

    const validated = tool.outputSchema.safeParse(output);
    if (!validated.success) {
      return failureResult(
        ToolErrorCode.Failed,
        `Tool output does not match its declared schema: ${name}`,
      );
    }

    return successResult(validated.data as Record<string, unknown>);
  });

  return server;
}

/**
 * 创建服务端。
 *
 * 默认只注册自述工具；`includeInfoTool` 置 false 可得到一个零工具的服务端，
 * 用于验证「零工具注册时列举结果为空数组而非错误」这一注册机制的一般性。
 */
export function createServer(options: ServerOptions): Server {
  return createServerWithTools(
    (server) =>
      options.includeInfoTool === false
        ? []
        : [createInfoTool(server, options.serverVersion)],
    { serverVersion: options.serverVersion },
  );
}
