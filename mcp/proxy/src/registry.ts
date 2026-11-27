/**
 * 工具对应表：下游工具名 → (上游标识, 上游工具名)。
 *
 * 路由一律按表查，不从工具名里切分隔符反解——分隔符在 id 与工具名两侧都可能
 * 出现，字符串反解不可靠。
 */
import { normalizeToolName } from './naming.js';

/** 上游声明的工具（只取代理需要的字段）。 */
export interface UpstreamToolDefinition {
  name: string;
  description?: string;
  inputSchema?: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
}

/** 对应表中的一条：暴露给下游的一个工具。 */
export interface ToolEntry {
  /** 暴露给下游的名称。 */
  downstreamName: string;
  /** 来源上游标识。 */
  upstreamId: string;
  /** 上游的原始工具名。 */
  upstreamName: string;
  /** 暴露给下游的描述（含来源与原始名）。 */
  description: string;
  /** 上游声明的输入模式，原样透传。 */
  inputSchema: Record<string, unknown>;
  /** 上游声明的产出模式，原样透传。 */
  outputSchema?: Record<string, unknown>;
  /** 该上游当前是否在运行：停用的上游其工具不再出现在列举结果里。 */
  active: boolean;
}

/** 注册结果：新增条目，或冲突信息。 */
export interface RegisterResult {
  added: ToolEntry[];
  /** 冲突：下游名已被别的上游占用。 */
  conflict?: { downstreamName: string; existingUpstreamId: string };
}

/** 描述里带上来源与原始名：模型与人都能识别它来自哪个上游、原本叫什么。 */
export function composeDescription(upstreamId: string, originalName: string, description?: string): string {
  const suffix = `[upstream: ${upstreamId}] original tool name: ${originalName}`;
  return description ? `${description} ${suffix}` : suffix;
}

export class ToolRegistry {
  private readonly entries = new Map<string, ToolEntry>();

  /**
   * 注册某个上游的全部工具。
   *
   * 先整体检查再整体写入：任何一个工具撞名，该上游**整体**判为不可用（先注册
   * 者保留名称），不发生静默覆盖。
   */
  register(upstreamId: string, tools: UpstreamToolDefinition[]): RegisterResult {
    const prepared: ToolEntry[] = [];
    const pending = new Set<string>();

    // 重启上游：先清掉它上一次的条目，重名判定只看仍在运行的条目。
    this.forget(upstreamId);

    for (const tool of tools) {
      const normalized = normalizeToolName(upstreamId, tool.name);
      if (normalized.downstreamName.length === 0) {
        continue;
      }
      const existing = this.entries.get(normalized.downstreamName);
      if (existing !== undefined && existing.active) {
        return {
          added: [],
          conflict: {
            downstreamName: normalized.downstreamName,
            existingUpstreamId: existing.upstreamId,
          },
        };
      }
      // 同一批次内的重名同样算冲突：两个原始名规范化后落到同一个下游名。
      if (pending.has(normalized.downstreamName)) {
        return {
          added: [],
          conflict: {
            downstreamName: normalized.downstreamName,
            existingUpstreamId: upstreamId,
          },
        };
      }
      pending.add(normalized.downstreamName);
      const entry: ToolEntry = {
        downstreamName: normalized.downstreamName,
        upstreamId,
        upstreamName: tool.name,
        description: composeDescription(upstreamId, tool.name, tool.description),
        inputSchema: tool.inputSchema ?? { type: 'object' },
        active: true,
      };
      if (tool.outputSchema !== undefined) {
        entry.outputSchema = tool.outputSchema;
      }
      prepared.push(entry);
    }

    for (const entry of prepared) {
      this.entries.set(entry.downstreamName, entry);
    }
    return { added: prepared };
  }

  /** 全部**在运行**的上游的工具条目（列举结果只看这些）。 */
  list(): ToolEntry[] {
    return [...this.entries.values()].filter((entry) => entry.active);
  }

  /** 按下游名查对应关系：已停用的上游也查得到，便于报「上游不可用」。 */
  resolve(downstreamName: string): ToolEntry | undefined {
    return this.entries.get(downstreamName);
  }

  /** 停用某个上游：其工具不再出现在列举结果里，但名字仍可解析。 */
  deactivate(upstreamId: string): void {
    for (const entry of this.entries.values()) {
      if (entry.upstreamId === upstreamId) {
        entry.active = false;
      }
    }
  }

  /** 彻底移除某个上游的条目（配置里删掉该上游时调用）。 */
  removeUpstream(upstreamId: string): void {
    this.forget(upstreamId);
  }

  private forget(upstreamId: string): void {
    for (const [name, entry] of [...this.entries]) {
      if (entry.upstreamId === upstreamId) {
        this.entries.delete(name);
      }
    }
  }
}
