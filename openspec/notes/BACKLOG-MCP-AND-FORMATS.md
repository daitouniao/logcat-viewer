# 待重启卡 B3：MCP 服务与多格式日志支持（DLT 优先）

- 状态：**待评审**（面向分发；新格式优先级 DLT）（重启卡：未结项条目一卡一条 `openspec/notes/BACKLOG-*.md`，结项后移入 `openspec/notes/archive/`）
- 日期：2026-10-13 起稿；2026-10-21 登记；2026-10-29 从登记簿拆为独立卡片
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）
- 原设计：`docs/private/design/MCP-AND-FORMATS-DESIGN.md`（主）+ `docs/private/design/MCP-AND-FORMATS-DESIGN-DISCUSSION.md`（补充讨论，同仓库三工程 Core/GUI/Mcp 已决策）；私有目录 git-ignored，**换机器即丢**；本卡自包含需求与范围

## 基础需求

**用户故事**：

1. **AI 直接查日志、不贴上下文**：300 MB 日志不可能贴进对话，AI 客户端通过 MCP 调 `search` / `get_context` / `summarize` / `crashes`，按需取数。
2. **给别人用**：MCP server 作为独立分发的工具包（zip / NuGet 工具包），别人配一行 `mcp.json` 就能连。
3. **不止 Android logcat**：优先级新格式是 **DLT**（AUTOSAR/COVESA 标准二进制格式，车载 ECU 日志）。

**范围**：

| 做 | 不做 |
| --- | --- |
| 同仓库三工程：Core / GUI / Mcp | 单开仓库或对外发 Core NuGet 包 |
| `ILogFormat` 抽象，logcat 先泛化适配 | 强行把文本/二进制统一成单一行接口 |
| MCP 正则过滤（Core 能力，仅 MCP 暴露） | GUI 恢复正则 UI（人用版本维持既有决定） |
| DLT verbose 全解析、非 verbose 半支持 | FIBEX 描述文件驱动的非 verbose 全解析 |
| MCP 工具 v1：list / search / context / summarize / crashes | follow 实时 tail、HTTP transport |
| 分发安全：路径白名单、只读、输出上限 | 多因素认证 / 远端部署（stdio 本机模型） |
| 三包发布（GUI 双包 + MCP 包） | 跨文件聚合搜索（已被 B2 否决） |

**关键决策（已定）**：

- **不单开库**：MCP server 是薄壳，价值 100% 在解析/索引/过滤/聚合；单开库只剩「复制解析器」或「发内部 NuGet 两仓库锁步发版」两条路，单人维护纯负担。分发是**发布形态**问题（三包 + 文档），不是代码归属问题。
- **Core TFM** = `net10.0`（去掉 `-windows`，保留跨平台可能），GUI 保持 `net10.0-windows`；抽取时审计 Windows-only API。

## 已知前置与硬伤（重启时复核）

1. 现有架构是**文本行扫描专属**：`LooksLikeNewRecord` 记录头判定、banner 行、续行 join、`ScanRange` 行对齐、块起点表等均为 logcat 文本格式假设——`ILogFormat` 抽象的主要工作量在这里。
2. `YearTables` 无锁静态可变缓存曾列为硬伤；该表已随 change `2026-10-24-minute-byte-column`（已归档）删除，重启时按现状重新核对（不复用旧结论）。
3. SMB 路径读性能：见 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md`（网络路径以 IO 为主）与已结项的网络过滤慢根因（与网络无关，属 `MsgFilter` CPU 侧，已修）。

## 重启条件

维护者排期 → 转 OpenSpec change（建议先立 Core 抽取 + `ILogFormat` 的最小切片，再叠 MCP 壳与 DLT）。
