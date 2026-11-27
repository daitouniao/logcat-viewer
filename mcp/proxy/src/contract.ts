/**
 * 对下游的结果信封与错误码族。
 *
 * 成功的工具调用返回结构化结果：文本内容块承载产出的 JSON 文本，结构化内容
 * 与之同源同值。失败返回带错误标记的结果，错误码为稳定英文标识。
 */
import { z } from 'zod';

/** 工具层错误码：沿用 `TOOL_` 族与代理特有的 `UPSTREAM_` 族。 */
export const ToolErrorCode = {
  /** 工具不存在（无此上游、上游不提供该工具、未知工具名）。 */
  NotFound: 'TOOL_NOT_FOUND',
  /** 入参不符合工具输入模式。 */
  Failed: 'TOOL_FAILED',
  /** 上游不可用：未运行、拉起失败、连接断开或调用超时。 */
  UpstreamUnavailable: 'UPSTREAM_UNAVAILABLE',
  /** 上游工具自身处理失败。 */
  UpstreamToolFailed: 'UPSTREAM_TOOL_FAILED',
} as const;

export type ToolErrorCodeValue = (typeof ToolErrorCode)[keyof typeof ToolErrorCode];

/** 内容块：文本类型。 */
export const textContentSchema = z.object({ type: z.literal('text'), text: z.string() });

/** 错误载荷：码与描述同源同值。 */
export type ErrorPayload = {
  error: {
    code: string;
    message: string;
  };
};

/**
 * 成功结果信封。
 *
 * 文本内容块与结构化内容同源：两者都由同一个对象派生，避免「模型看到的」与
 * 「程序读到的」不一致。
 */
export function successResult<T extends Record<string, unknown>>(payload: T) {
  return {
    content: [{ type: 'text' as const, text: JSON.stringify(payload) }],
    structuredContent: payload,
    isError: false as const,
  };
}

/** 失败结果信封：错误标记置位，错误码与描述同时进两个载体且同源。 */
export function failureResult(code: ToolErrorCodeValue, message: string) {
  const payload: ErrorPayload = { error: { code, message } };
  return {
    isError: true as const,
    content: [{ type: 'text' as const, text: JSON.stringify(payload) }],
    structuredContent: payload,
  };
}
