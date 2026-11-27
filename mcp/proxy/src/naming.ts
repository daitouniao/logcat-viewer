/**
 * 下游工具名的生成与合规化。
 *
 * 上游（标准输入输出）的工具名不设门槛：合规性由代理对外暴露的名字承担。
 * 原始名写进工具描述，调用仍路由到原始工具（由对应表保证）。
 */

/** 下游工具名的字符约束。 */
export const DOWNSTREAM_NAME_PATTERN = /^[a-zA-Z0-9_-]+$/;

/** 下游工具名的长度上限：贴合客户端界面的惯例，超长名会被截断显示。 */
export const MAX_DOWNSTREAM_NAME_LENGTH = 64;

/** 命名空间分隔符：双下划线。 */
export const NAMESPACE_SEPARATOR = '__';

/** 规范化结果。 */
export interface NormalizedToolName {
  /** 暴露给下游的名称。 */
  downstreamName: string;
  /** 上游的原始工具名。 */
  originalName: string;
  /** 是否发生了改写（规范化痕迹）。 */
  changed: boolean;
}

/**
 * 生成暴露给下游的工具名。
 *
 * 规则：`<id>__<工具名>`；工具名中的非法字符替换为下划线、连续下划线压平；
 * 长度超限时保留完整 id 段、截断工具名段。
 */
export function normalizeToolName(upstreamId: string, upstreamName: string): NormalizedToolName {
  const raw = `${upstreamId}${NAMESPACE_SEPARATOR}${upstreamName}`;

  let toolPart = upstreamName.replace(/[^a-zA-Z0-9_-]/g, '_');
  toolPart = toolPart.replace(/_{2,}/g, '_');
  if (toolPart.length === 0) {
    toolPart = 'tool';
  }

  const budget = MAX_DOWNSTREAM_NAME_LENGTH - upstreamId.length - NAMESPACE_SEPARATOR.length;
  if (budget < 1) {
    // id 本身过长（配置校验会拦住），此处兜底：不暴露该工具。
    return { downstreamName: '', originalName: upstreamName, changed: true };
  }
  if (toolPart.length > budget) {
    toolPart = toolPart.slice(0, budget);
  }

  const downstreamName = `${upstreamId}${NAMESPACE_SEPARATOR}${toolPart}`;
  return { downstreamName, originalName: upstreamName, changed: downstreamName !== raw };
}

/** 名称是否符合下游可用性要求。 */
export function isCompliantDownstreamName(name: string): boolean {
  return (
    name.length > 0 &&
    name.length <= MAX_DOWNSTREAM_NAME_LENGTH &&
    DOWNSTREAM_NAME_PATTERN.test(name)
  );
}
