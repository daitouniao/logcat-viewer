/**
 * 契约常量与信封构造。
 *
 * 本文件是 MCP 端对外行为契约的实现处：结果信封、错误信封、错误码命名族
 * 与契约版本标识都在此定义。工具层只负责产出业务数据，不自行拼装信封。
 */

/** 契约版本标识：语义化版本串；契约发生不向后兼容的变更时递增。 */
export const CONTRACT_VERSION = '0.1.0';

/** 传输形态标识：本能力下只承载标准输入输出。 */
export const TRANSPORT = 'stdio';

/**
 * 工具层错误码：稳定英文标识，同属 `TOOL_` 前缀命名族。
 *
 * 错误码是调用方可按值分支的对外契约，故取值写死在此，不随实现漂移。
 */
export const ToolErrorCode = {
  /** 调用的工具不存在。 */
  NotFound: 'TOOL_NOT_FOUND',
  /** 工具处理失败：入参非法、处理抛错，或产出不符其声明的结果模式。 */
  Failed: 'TOOL_FAILED',
} as const;

export type ToolErrorCodeValue = (typeof ToolErrorCode)[keyof typeof ToolErrorCode];

/** 错误载荷：错误码与面向调用方的英文描述。 */
export interface ErrorPayload {
  error: {
    code: ToolErrorCodeValue;
    message: string;
  };
}

/**
 * 成功结果信封：文本内容块与结构化内容同源同值。
 *
 * 文本内容块供模型直接消费，结构化内容供客户端程序化处理；两者由同一对象
 * 派生，避免出现「模型看到的」与「程序读到的」不一致。
 */
export function successResult<T extends Record<string, unknown>>(payload: T) {
  return {
    content: [{ type: 'text' as const, text: JSON.stringify(payload) }],
    structuredContent: payload,
  };
}

/**
 * 错误结果信封：错误标记置位，错误码与英文描述同时进两个载体且同源。
 *
 * 工具层失败走这里（而非协议错误通道），以便模型读到失败内容并自行纠正。
 */
export function failureResult(code: ToolErrorCodeValue, message: string) {
  const payload: ErrorPayload = { error: { code, message } };
  return {
    isError: true as const,
    content: [{ type: 'text' as const, text: JSON.stringify(payload) }],
    structuredContent: payload,
  };
}
