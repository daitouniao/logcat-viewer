/**
 * 参数校验：按上游声明的 JSON Schema 做最小但必要的检查。
 *
 * 只查「必填参数缺失」与「基本类型不符」两类——这两类是最常见也最致命的错误；
 * 更深的 JSON Schema 语义交给上游自己判，代理不替它做主。
 */

/** 校验失败时返回错误描述；通过则返回 undefined。 */
export function validateArguments(
  args: Record<string, unknown>,
  schema: Record<string, unknown> | undefined,
): string | undefined {
  if (schema === undefined) {
    return undefined;
  }

  const type = schema['type'];
  if (type !== undefined && type !== 'object') {
    return undefined;
  }

  const properties = schema['properties'];
  const required = schema['required'];
  const propertyMap =
    typeof properties === 'object' && properties !== null
      ? (properties as Record<string, unknown>)
      : {};

  if (Array.isArray(required)) {
    for (const name of required) {
      if (typeof name !== 'string') {
        continue;
      }
      if (!(name in args)) {
        return `missing required parameter '${name}'`;
      }
    }
  }

  for (const [name, value] of Object.entries(args)) {
    const declared = propertyMap[name];
    if (declared === undefined || typeof declared !== 'object' || declared === null) {
      continue;
    }
    const expected = (declared as { type?: unknown }).type;
    if (typeof expected !== 'string') {
      continue;
    }
    if (!matchesType(value, expected)) {
      return `parameter '${name}' must be of type ${expected}`;
    }
  }

  return undefined;
}

/** 基本类型匹配。 */
function matchesType(value: unknown, expected: string): boolean {
  switch (expected) {
    case 'string':
      return typeof value === 'string';
    case 'number':
      return typeof value === 'number';
    case 'integer':
      return typeof value === 'number' && Number.isInteger(value);
    case 'boolean':
      return typeof value === 'boolean';
    case 'array':
      return Array.isArray(value);
    case 'object':
      return typeof value === 'object' && value !== null && !Array.isArray(value);
    case 'null':
      return value === null;
    default:
      return true;
  }
}
