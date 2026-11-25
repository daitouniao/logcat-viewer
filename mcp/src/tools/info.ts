import { z } from 'zod';
import { CONTRACT_VERSION, TRANSPORT } from '../contract.js';

/**
 * 自述工具的结果模式。
 *
 * 五项字段固定：服务端版本、协议版本、契约版本、能力集、传输形态。
 */
export const infoOutputSchema = z.object({
  /** 服务端版本：取自工程自身的版本声明；本刀不发布，故为空串。 */
  serverVersion: z.string(),
  /** 协议版本：与握手协商出的协议版本一致。 */
  protocolVersion: z.string(),
  /** 契约版本：语义化版本串。 */
  contractVersion: z.string(),
  /** 能力集：服务端声明的能力。 */
  capabilities: z.record(z.string(), z.unknown()),
  /** 传输形态：固定为标准输入输出。 */
  transport: z.literal(TRANSPORT),
});

export type InfoOutput = z.infer<typeof infoOutputSchema>;

/** 自述工具的标题与描述：面向模型的说明文本。 */
export const infoToolTitle = 'logcat MCP server info';

/**
 * 自述工具的描述：模型据此判断何时调用。
 *
 * 本工具不读取任何日志文件、不产出任何日志数据，只回吐服务端自身的身份与
 * 契约信息，用于确认连接可用、契约版本为何。
 */
export const infoToolDescription =
  'Report this MCP server\'s identity and contract state: server version, negotiated ' +
  'protocol version, contract version, declared capabilities, and transport. Use it to ' +
  'verify the connection is healthy and to learn which contract version you are talking ' +
  'to. It reads no log data.';

/** 自述工具的输入模式：无需入参。 */
export const infoInputSchema = z.object({});

/** 构造自述工具的产出对象。 */
export function buildInfoOutput(input: {
  serverVersion: string;
  protocolVersion: string;
  capabilities: Record<string, unknown>;
}): InfoOutput {
  return {
    serverVersion: input.serverVersion,
    protocolVersion: input.protocolVersion,
    contractVersion: CONTRACT_VERSION,
    capabilities: input.capabilities,
    transport: TRANSPORT,
  };
}
