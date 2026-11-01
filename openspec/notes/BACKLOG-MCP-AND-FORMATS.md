# 待重启卡 B3：MCP 服务与多格式日志支持（DLT 优先）

- 状态：**待评审**（面向分发；新格式优先级 DLT）；**2026-10-30 完成代码事实复核，新增 §需要维护者决策（D1–D9 待拍板）；2026-10-31 DLT 已另立 change `dlt-log-format` 并行实施（见 D2/D7 修订）**
- 日期：2026-10-13 起稿；2026-10-21 登记；2026-10-29 从登记簿拆为独立卡片；2026-10-30 事实复核
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）
- 原设计：`docs/private/design/MCP-AND-FORMATS-DESIGN.md`（主）+ `...-DISCUSSION.md`（补充）——private 目录已停止维护（AGENTS §3.6），**不作为依据**；本卡自包含需求与范围

## 基础需求

**用户故事**：

1. **AI 直接查日志、不贴上下文**：300 MB 日志不可能贴进对话，AI 客户端通过 MCP 调 `search` / `get_context` / `summarize` / `crashes`，按需取数。
2. **给别人用**：MCP server 作为独立分发的工具包（zip / NuGet 工具包），别人配一行 `mcp.json` 就能连。
3. **不止 Android logcat**：优先级新格式是 **DLT**（AUTOSAR/COVESA 标准二进制格式，车载 ECU 日志）。

**范围**：

| 做 | 不做 |
| --- | --- |
| 同仓库多工程：日志引擎 Core / GUI / Mcp（工程边界见 D1） | 单开仓库或对外发 Core NuGet 包 |
| `ILogFormat` 抽象，logcat 先泛化适配 | 强行把文本/二进制统一成单一行接口 |
| MCP 正则过滤（Core 能力，仅 MCP 暴露） | GUI 恢复正则 UI（人用版本维持既有决定） |
| DLT verbose 全解析、非 verbose 半支持 | FIBEX 描述文件驱动的非 verbose 全解析 |
| MCP 工具 v1：list / search / context / summarize / crashes | follow 实时 tail、HTTP transport |
| 分发安全：路径白名单、只读、输出上限 | 多因素认证 / 远端部署（stdio 本机模型） |
| GUI 双包（自包含 + 框架依赖，现状）+ MCP 发布物（形态见 D6） | 跨文件聚合搜索（已被 B2 否决） |

**关键决策（已定）**：

- **不单开库**：MCP server 是薄壳，价值 100% 在解析/索引/过滤/聚合；单开库只剩「复制解析器」或「发内部 NuGet 两仓库锁步发版」两条路，单人维护纯负担。分发是**发布形态**问题，不是代码归属问题。
- **Core TFM 目标 = `net10.0`**（去掉 `-windows`，保留跨平台可能），GUI 保持 `net10.0-windows`；抽取时审计 Windows-only API。

## 现状核对（2026-10-30，代码事实）

1. **仍是单工程单 exe**：`logcat.csproj` 的 `<TargetFramework>` = `net10.0-windows`（L6），`<Version>` = `0.1.6`（L16），全仓库只有这一个 csproj；`tests/logcat.Tests` 经 `ProjectReference` 直接引用它，并用编译期 `RepoRoot` 做源码扫描测试（L73–L84）。
2. **日志引擎本身已与 UI 解耦**：`LogDocument` / `FilterEngine` / `LogParser` / `CrashExtractor` / `CrashAggregator` 及 `Models/` 均无 `System.Windows.Forms` / `System.Drawing` 引用。反过来，`Services/AdbManager.cs`（L2–L3）、`Services/AppSettings.cs`（L2）、`Services/Loc.cs`（L1）带 `System.Drawing` —— **「日志引擎 Core」与「UI/ADB 共享库」是两批代码**，抽库边界可据此切（D1）。
3. **现有架构确实是「文本行扫描专属」**：`LogParser.IsBanner`（`Services/LogParser.cs` L358）、`LogParser.LooksLikeNewRecord`（L383）；`LogDocument` 的扫描在 L575 判横幅、L582 做续行合并。`ILogFormat` 抽象的主要工作量仍在这里，与旧结论一致。
4. **`YearTables` 已删除**（全仓库无该符号），随 change `2026-10-24-minute-byte-column` 归档；旧硬伤不复存在，改为分钟列（`Minute` 哨兵 `MINUTE_NONE`）。
5. **过滤没有正则能力**：`Models/FilterSpec.cs` 只有 Levels / Tags / Msg / Pids / Tids / Minutes / MarkedOnly 七类字段，无正则字段；全仓库正则只出现在 `LogParser.ReLong`（long 格式回退解析）。→ 范围表里的「MCP 正则过滤」是**新增 Core 能力**，不是复用。
6. **MCP 所需的查询面已存在**：`LogDocument` 公开 `RowCount` / 九列只读视图（L96–L106）/ `LineBytes`(L199) / `MessageBytes`(L208) / `HeaderBytes`(L225) / `TagOf`(L251) / `OpenMessageSession`(L276) / `Build`+`BuildAsync`(L380–L387) / `Reload`(L842)；`FilterEngine.ApplyFilter`+`ApplyFilterAsync`(L772–L778) / `FilterTail`(L856) / `ExportRows`(L906)；崩溃面 `LogDocument.Crashes`(L73)、`CrashEvent`、`CrashExtractor.TryExtract`(L59)、`CrashAggregator`(L20)。薄壳可复用，无需重写解析。
7. **分发现状是「GUI 双包」**：csproj 注释（L35–L53）声明每版发自包含 + 框架依赖两包，未发布过 NuGet 工具包，无任何 `Pack` 配置。
8. **在途与在研工作会碰到同一批文件**：
   - 在途 change `chunked-progressive-load`（0/13 任务）要改 `LogDocument` 的加载编排、`FilterEngine` 的块扫描、`Controls/LogListView`、`frmMain`；
   - 探索笔记 `SPLIT-ANDROID-TOOLS-EXE.md` 要做 `logcat.Core` 共享库（含 `AdbManager` / `Loc` / 设备 Forms），与本卡的 Core 抽取是同一片地基；
   - 探索笔记 `SIDECAR-CACHE-INDEX.md` 的 D8 已拍板「后置到 `chunked-progressive-load` 归档之后」。
9. **SMB 性能疑点已关闭**：慢根因在 `MsgFilter` CPU 侧（已修），与网络无关；相关结论见 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md`。

## 需要维护者决策（2026-10-30）

建议切片顺序（本卡内部，见 D7）：

```
[Core 抽取] --纯搬迁，不改行为--> [MCP 壳 logcat-only] --稳定 API--> [ILogFormat 泛化 + DLT]
     ^
     └─ 必须先定 D1（库怎么切）与 D2（与既有工作的先后）
```

### D1 库边界与 TFM（阻塞）

- **为什么必须定**：`AdbManager` / `AppSettings` / `Loc` 依赖 `System.Drawing`，`SPLIT` 卡的共享库会把这些收进去；若与日志引擎合成一个 Core，MCP 也被迫带 WinForms/Drawing，`net10.0` 跨平台目标直接落空。
- **选项**：
  - **甲（推荐）双库**：`logcat.Core`（日志引擎 + Models，`net10.0`，无 UI 依赖）+ `logcat.UiCore`（`AdbManager` / `Loc` / 设备 Forms，`net10.0-windows`，可与 SPLIT 卡共用）。MCP 只引前者，能跨平台。
  - **乙 单库**：一个 Core（`net10.0-windows`），MCP 也 Windows-only。改动最少，但放弃跨平台，且将来拆 Android 工具时仍要再切一刀。
  - **丙** 等 SPLIT 卡先拍板，B3 依赖其结论。代价是 B3 被另一张卡卡住。
- **倾向**：甲。日志引擎四件套已无 UI 依赖（现状核对 2），切成两库是机械操作；乙会把「跨平台」这条已写进关键决策的目标当场作废。

### D2 与既有工作的先后（阻塞）

- **为什么必须定**：Core 抽取是 `git mv` 型搬迁；`chunked-progressive-load` 正在改同一批文件的内部形态，SPLIT 卡也要搬同一批文件。顺序不定，tasks 里的路径与基线都写不准。
- **选项**：
  - **甲（推荐）`chunked-progressive-load` 归档 → 抽 Core → MCP**：搬迁在代码安静窗口做，chunked 的 tasks/spec 一行不用改，基线稳定。
  - **乙 先抽 Core → chunked 再改**：chunked 四件套（0/13 未 apply）里所有文件路径与 impact 段落要 update 一次，工作量转嫁给它。
  - **丙 并行 worktree**：两边动同一批文件，合并必冲突。
- **倾向**：甲。
- **修订（2026-10-31）**：DLT 切片不在上述顺序约束内——`dlt-log-format`（自研解码 + DLT 文本分支）只碰入口分流与 `ParseLine`，按独立 worktree 与 `chunked-progressive-load` 并行（见 `openspec/notes/DLT-INTEGRATION.md` §10 C4 乙）。
- **衍生（D2b）**：`SIDECAR-CACHE-INDEX` 的 change 也在 chunked 之后。MCP 与 sidecar 谁先？倾向 **MCP 先**：MCP 的 stdio 服务端通常随客户端会话常驻（外部通用做法，非本项目代码事实），重开同一文件的成本不在关键路径上；sidecar 是纯性能优化，MCP 落地后价值不变。

### D3 MCP 输出契约与本地化（阻塞 MCP 设计）

- **为什么必须定**：`Loc` 是进程内静态、按系统区域决定语言（`Services/Loc.cs`），`FilterSpec.Describe` 也走 `Loc`。MCP 的消费者是 AI 客户端，输出必须稳定可解析；跟随 zh-CN/en-US 会让同一请求在两地返回不同文本标签。
- **选项**：**甲（推荐）固定英文 JSON 契约，MCP 不引 `Loc`**；乙 跟随系统区域；丙 加 `lang` 参数由客户端指定。
- **倾向**：甲。字段名、枚举值、错误信息全部英文且进 spec 场景；本地化只属于 GUI。

### D4 正则过滤的作用面与安全（阻塞 Core 能力设计）

- **为什么必须定**：范围表只写了「MCP 正则过滤」，没写匹配哪一段文本、如何防灾难性回溯；这决定 `FilterSpec` 的扩展字段与测试面。
- **选项（作用面）**：**甲（推荐）仅 message 体**；乙 整行（含时间/pid/tag 头）；丙 tag + msg 两段分别可配。
- **倾向**：甲 + 大小写开关 + 排除开关 + **匹配超时**（.NET `Regex` 必须带 timeout），超时按「该行不命中」处理并留痕。整行匹配可等实际需求再加。

### D5 MCP v1 工具语义（阻塞 tool schema）

- **为什么必须定**：五个工具名已定，但语义边界未定，写进 spec 前必须落清，否则实现会各自发挥。
- **待定子项与倾向**：
  1. `summarize`：**推荐只做统计聚合**（按级别/tag/分钟/pid 的计数 + 崩溃计数），不调 LLM、不生成自然语言摘要；
  2. `get_context`：**推荐按行窗口**（前后 N 行，N 有上限），可选「同 tag / 同 pid / 同 tid」邻近模式；
  3. `crashes`：**推荐直接复用** `CrashEvent`（`Row` / `ExcId` / `PkgId` / `Pid` / `Tid` / `Kind` / `LineCount`）+ `CrashAggregator` 分组，不新增崩溃识别逻辑；
  4. `list`：返回已打开文档与元信息，还是接受路径现场打开？**推荐 v1 允许显式路径 + 进程内文档缓存**（与 D9 白名单口径联动）。

### D6 MCP 发布形态（阻塞发布文档）

- **为什么必须定**：MCP 客户端按「命令 + 参数」拉起子进程，分发形态直接决定 `mcp.json` 示例与发版流程；GUI 现有双包口径（csproj L35–L53）不覆盖它。
- **选项**：
  - 甲 单文件 zip（自包含，与 GUI 同套发布流程）；
  - **乙（推荐）NuGet dotnet tool**（`dotnet tool install -g`，MCP 配置里直接写命令名，跨平台天然，见 D1 甲）；
  - 丙 两者都发。
- **倾向**：乙。甲作为兜底可后补；丙在单人维护下会把发版面扩大一倍。

### D7 本卡内部切片（阻塞 propose）

- **为什么必须定**：三块（Core 抽取 / MCP 壳 / ILogFormat+DLT）任一块都能独立验收；合成一个大 change 会同时改工程结构、对外 API 与解析抽象，评审面过大。
- **倾向**：**三个独立 change，按图顺序**：
  1. `core-extraction`：纯搬迁 + TFM 拆分，行为零变化，测试基线必须完全一致；
  2. `mcp-server`：只支持 logcat 文本，复用现状核对 6 的 API；
  3. `log-format-abstraction` + DLT：抽象落地 + DLT verbose 全解析。
- **不倾向**：把 1 和 3 合并（搬迁与重构混在一起，diff 不可读）。
- **修订（2026-10-31，DLT 先行）**：DLT 解码与文本行解析已由独立 change `dlt-log-format` 提前实施（`openspec/notes/DLT-INTEGRATION.md` §10 C4 乙 / P5），不占本卡顺序；届时切片 3 只剩 `ILogFormat` 抽象与 MCP 侧复用，**不再包含 DLT 解码器实现**。

### D8 版本号与测试工程（可默认）

- **为什么需要定（弱）**：多工程后版本号来源与 tests 引用结构需要口径。
- **倾向**：抽 `Directory.Build.props` 统一版本号（避免 AGENTS §2.8 的「多处同步」扩成更多处）；tests 仍单工程，分别引用 Core / exe。

### D9 安全边界的具体数值（可默认）

- **为什么需要定（弱）**：范围已定「路径白名单 / 只读 / 输出上限」，但数值与口径未定。
- **倾向**：只允许调用方**显式传入的绝对路径**（不做目录递归、不做通配）；只读打开；单次返回 ≤ 500 行且 ≤ 2 MB；拒绝 spool / 代理临时目录之外的写操作；错误信息不回显本机绝对路径以外的敏感内容。

## 重启条件

拍板 **D1 / D2 / D7** 后即可走 `/opsx:propose`（建议 change 名 `core-extraction`，先做纯搬迁切片）。D3–D6 在 `mcp-server` 的 propose 前补齐即可，D8 / D9 可给默认值。编码前不在探索阶段动手。
