# MCP 功能探索（合并稿）

- 状态：**待评审**（合并两套既有草案，给出统一口径建议；D 系列决策待拍板）
- 日期：2026-11-06
- 关联：合并自 `openspec/notes/BACKLOG-MCP-AND-FORMATS.md`（B3 卡）与 `openspec/notes/MCP-LOG-ANALYSIS-API.md`；格式侧依赖 `openspec/notes/DLT-INTEGRATION.md`
- 验收结论：不适用（未实施）

> 本文只做探索与口径收敛，不实施。两份旧笔记保留原位作为输入与历史，本稿是后续 propose 的工作参考。

## 1 要解决的问题

1. **AI 直接查日志、不贴上下文**：几百 MB 的日志无法贴进对话。AI 客户端通过 MCP 按需取数：检索、取上下文、统计、崩溃清单。
2. **给别人用**：MCP server 作为独立分发的工具包，别人在 `mcp.json` 里配一行就能连。
3. **不止 Android logcat**：后续接入 DLT（车载 ECU 日志），本稿的 v1 只覆盖 logcat 文本。

```
AI 客户端 (MCP client)
        |
        | JSON-RPC / stdio
        v
logcat MCP server (薄壳，只做工具编排与输出契约)
        |
        | 进程内直接调用
        v
logcat.Core 日志引擎（解析 / 列式索引 / 过滤 / 崩溃提取）
        |
        v
源日志文件（mmap，只读）
```

## 2 范围

| 做 | 不做 |
| --- | --- |
| 同仓库新增 MCP 工程，进程内复用日志引擎 | 单开仓库、对外发布 Core NuGet 包 |
| v1 只支持 logcat 文本文件 | DLT / 其他格式（另立 change，见 DLT 笔记） |
| 检索 + 统计聚合 + 崩溃清单 | 让服务端调 LLM 生成自然语言摘要 |
| 固定英文 JSON 契约（不跟随界面语言） | 跟随系统区域输出中文标签 |
| 只读、显式绝对路径、输出上限 | 目录递归扫描、通配符、写文件 |
| stdio 本机 transport | HTTP transport、远端部署、多因素认证 |
| 发布为可安装的 MCP 工具包（形态见 §6.3） | follow / 实时 tail |
| 显式路径打开 + 会话内文档缓存 | 跨文件聚合搜索（既有 B2 决策已否决） |

## 3 代码事实复核（2026-11-06）

以下为当前仓库事实，供后续 design 直接引用。

1. **工程结构已变**：`logcat.UiCore` 已由拆分卡落地（`openspec/changes/archive/2026-11-05-split-android-tools-exe`），承载 `AdbManager`、`Loc` + `LocTable.*`、设备对话框、文件面板、设备侧 Models 等；`logcat.AndTools` 独立 exe 也在。**`logcat.Core` 仍不存在**，日志引擎仍住在主 exe 工程。解决方案文件 `logcat.slnx` 目前只列 `logcat.csproj` 与 `tests/logcat.Tests`。
2. **引擎与 UI 已直接解耦**：`Services/LogDocument.cs`、`Services/FilterEngine.cs`、`Services/LogParser.cs`、`Services/CrashExtractor.cs`、`Services/CrashAggregator.cs`、`Services/CrashTime.cs`、`Models/CrashEvent.cs`、`Models/FilterSpec.cs` 均无 `System.Windows.Forms` / `System.Drawing` 引用（本次复核逐文件确认）。
3. **但仍经 `Loc` 传递依赖**：`LogDocument` 的进度文案（如 `Loc.T("扫描行…")`、`Loc.F("合并索引…")`）、`FilterEngine` 的进度文案、`CrashKind.Name` 的类别显示名、`FilterSpec.Describe` 的描述文本都走 `Loc`。`Loc` 现在整体在 `logcat.UiCore`，且混合体未拆（纯译文半 + 约 24 个 WinForms 辅助成员）。这是引擎降到 `net10.0` 的真正前置，不是 `git mv`。
4. **MCP 所需查询面已存在**：
   - `LogDocument.RowCount` 与九列只读视图（`Offs` / `Lens` / `Moff` / `Minute` / `Pid` / `Tid` / `Lvl` / `TagId`）；
   - 行内容读取 `LineBytes` / `MessageBytes` / `HeaderBytes` / `ReadMessage` / `OpenMessageSession`；
   - 构建 `Build` / `BuildAsync` / `BuildChunkedAsync`；增量 `Reload`；
   - 过滤 `FilterEngine.ApplyFilter` / `ApplyFilterAsync` / `FilterTail` / `ExportRows`；
   - 崩溃面 `LogDocument.Crashes`、`CrashEvent`、`CrashAggregator.Aggregate`。
5. **过滤没有用户侧正则**：`Models/FilterSpec.cs` 只有 Levels / Tags / Msg / Pids / Tids / Minutes / MarkedOnly 七类条件；`FilterEngine` 明确注明匹配走字节级子串，正则只用于词法切分；`LogParser` 的正则只用于 long 格式回退解析。→ MCP 正则过滤是**新增能力**。
6. **正文解码只有 UTF-8**：`LogDocument.Decode` 直接 `Encoding.UTF8.GetString`，构造函数接收 `encoding` 但只存入 `EncodingName`，不参与解码；`TextProbe` 只判文本/二进制，不做编码判定。→ 旧分析草案里的「UTF-8 / GBK 双编码自动判定」**不存在**，属新增工作。
7. **崩溃识别可复用**：`CrashExtractor` 是纯函数，`CrashAggregator` 按「类别 × 异常类型 × 包名」分组并给出次数与首末行号，MCP 的 `crashes` 不需要新的识别逻辑。
8. **sidecar 索引缓存正在做**：在途 change `sidecar-cache-index`（0/12 任务）会把列式索引落盘并支持快速重开，触及 `Services/LogDocument.cs` 与主窗体装载编排。MCP 不重复实现索引缓存；落地后可作为 MCP 会话内重开的加速路径。
9. **另一在途修复与 MCP 弱相关**：`fix-close-dispose-slow` 修的是 WinForms 视图销毁慢，不改变 `LogDocument` 的读取语义；MCP 不需要视图，但需注意「会话内长期持有文档」的关闭成本要单独实测。
10. **无任何 MCP 代码与依赖**：全仓库检索 `mcp` 只命中文档，无 MCP SDK 引用、无 `Pack` 配置。现有分发是 GUI 双包 zip（自包含 + 框架依赖）。
11. **线程安全未声明**：`LogDocument` 在构建完成后的列视图是只读的，但 `Reload`、mmap 视图与 `MessageSession` 指针生命周期属于可写/资源路径；MCP 若并行处理工具调用，需要按文档串行化（这是待验证的假设，不是代码保证）。
12. **版本号单一来源**：`Directory.Build.props` 当前 `0.1.7`，各工程继承。

## 4 两套工具面

### 4.1 既有草案 A：检索原语（B3 卡 D5）

`list` / `search` / `context` / `summarize` / `crashes`。出发点是「AI 查日志的检索动作」，名字已定，但语义边界未冻结。

### 4.2 既有草案 B：分析原语（`MCP-LOG-ANALYSIS-API.md`）

`log_probe` / `log_count` / `log_window` / `log_trace_field` / `log_cross_align`。出发点是「日志分析统计原语」，强调流式全行扫描、逐关键词独立计数、pid 分布、双编码判定、字段序列提取、多源时间对齐。

### 4.3 关系与重叠

| 草案 B 工具 | 草案 A 中的对应 | 关系 |
| --- | --- | --- |
| `log_probe` | `list`（文档元信息） | 重叠：文件大小、行数、时间范围、编码、pid 分布 |
| `log_count` | `search` / `summarize` | 部分重叠：B 只返回计数与模板，不返回行；A 返回行 |
| `log_window` | `context`（按行窗口） | 部分重叠：B 支持时间窗 / 关键词邻域，A 只按行窗口 |
| `log_trace_field` | 无 | 独有：按正则捕获组抽数值序列、差分 |
| `log_cross_align` | 无 | 独有：多源日志按时间对齐 |

两套不是「谁替换谁」，而是**同一批引擎能力的两种输出形态**：A 偏「取原文」，B 偏「取统计」。合并的关键不是合并工具名，而是先把「取原文」与「取统计」的边界定死，避免同一个问题有两个工具各答一半。

## 5 统一口径建议（v1）

建议 **v1 保留草案 A 的五个工具名，把草案 B 的统计语义并进去**，B 的两个独有工具后置。理由：工具名已定、客户端提示词成本低；统计能力通过参数开关表达，不需要第二套命名。

### 5.1 工具清单

| 工具 | 职责 | 吸收草案 B 的部分 |
| --- | --- | --- |
| `list` | 列出会话内已打开文档 + 元信息；传 `path` 时打开并返回元信息 | `log_probe` 的 total_lines / ts_range / encoding / pid_dist |
| `search` | 按关键词/正则检索，返回行或仅计数；支持级别/tag/pid/tid/分钟/时间范围过滤 | `log_count` 的逐关键词独立计数、pid 分布、首末时间 |
| `context` | 以行号、时间点或关键词为锚，取前后 N 行；可限定同 tag / 同 pid / 同 tid | `log_window` 的时间窗与关键词邻域 |
| `summarize` | 统计聚合：按级别 / tag / 分钟 / pid 的分布 + Top N + 崩溃计数；不生成自然语言 | `log_count` 的分钟分布、`log_probe` 的行样式计数 |
| `crashes` | 崩溃事件清单与分组汇总，直接复用 `CrashEvent` + `CrashAggregator` | 无 |

后置（v2 候选）：`trace_field`（正则捕获组取值序列 / 差分）、`cross_align`（多源时间对齐）。`cross_align` 与「跨文件聚合搜索已否决」的既有决策相邻，需先澄清它是否属于同一类被否决能力。

### 5.2 `search` 的两种输出

同一工具、两种模式，避免 `search` 与 `count` 两套语义：

```
mode = "rows"   -> 返回命中行（受输出上限约束，带分页游标）
mode = "count"  -> 只返回聚合结果（逐关键词总数、pid 分布、首末时间），不返回行文本
```

`count` 模式的价值：几百 MB 文件上「某关键词出现几次、在哪些 pid、时间分布」是 AI 最常问的问题，返回行既超限也无用。

### 5.3 公共输入参数（建议）

| 参数 | 说明 |
| --- | --- |
| `path` | 显式绝对路径；仅 `list` 首次打开时使用 |
| `doc` | 会话内文档 id（`list` 返回），后续调用优先用它 |
| `encoding` | 显式编码名，v1 支持 `utf-8`；缺省 `utf-8`（见 §8 Q3） |
| `query` | `{ text, mode: "substring" \| "regex", case, exclude }`；v1 建议正则只作用于 message 体 |
| `filters` | `levels` / `tags` / `pids` / `tids` / `minutes` / `time_range` |
| `pagination` | `offset` / `limit`（默认 limit 200，上限 500） |
| `max_bytes` | 单次返回文本上限，默认 2 MB |

### 5.4 输出契约

- **固定英文 JSON**：字段名、枚举值、错误码全英文；不引 `Loc`，不跟随界面语言。
- 行号：**0-based**，与文档行号一致，响应里显式写 `row_base: 0`。
- 时间：原样字符串 `ts`（保留文件内格式）+ 可选 `epoch_ms`；不做时区换算。
- 分页：`total` / `returned` / `truncated` / `next_offset`。
- 扫描量：`scanned_rows`、`elapsed_ms`，便于 AI 判断结果是否被截断。
- 错误：`{ "error": { "code": "...", "message": "..." } }`，错误码用稳定英文标识（如 `DOC_NOT_FOUND`、`PATH_NOT_ALLOWED`、`OUTPUT_LIMIT`、`REGEX_TIMEOUT`）。
- 不返回本机敏感信息；只回显调用方传入的路径。

### 5.5 安全默认值

- 只接受调用方**显式传入的绝对路径**，不做目录递归、不做通配符。
- 只读打开；v1 不提供 `save_to` 或其他写操作（旧分析草案的 `save_to` 与「只读」边界冲突，建议删除）。
- 单次返回 ≤ 500 行且 ≤ 2 MB；超限返回 `truncated` 与游标。
- 正则必须带超时（建议 200 ms），超时按「该行不命中」处理并在响应中留痕。
- 不暴露 adb、进程控制、文件删除/写入等能力。

## 6 工程落点与顺序

### 6.1 依赖链

```
[split-android-tools-exe]  已完成（logcat.UiCore 在场）
          |
          v
[core-extraction]
  1a 引擎搬进 logcat.Core（暂 net10.0-windows，行为零变化）
  1b 拆 Loc（纯译文半进 Core），Core 降到 net10.0
          |
          v
[mcp-server]  (v1：logcat 文本 + 上述五工具)
          |
          v
[log-format-abstraction + DLT]  （DLT 解码可由独立 change 先行，不占此序）
```

- MCP 必须引用 `logcat.Core`，不能引用 `logcat.UiCore`；否则 WinForms/Drawing 被拖进 MCP，跨平台目标落空。
- `Loc` 的拆法是 1b 的核心工作量；`CrashKind.Name` 与 `FilterSpec.Describe` 两处也要一并处理（要么 Core 保留纯译文半，要么 MCP 侧改用中性标签）。
- `core-extraction` 与在途 `sidecar-cache-index` 都触碰 `LogDocument`，顺序需要拍板（见 §8 Q6）。

### 6.2 MCP 工程形态

- 新工程建议 `logcat.Mcp`，`TargetFramework=net10.0`，引用 `logcat.Core`。
- 使用官方 C# SDK（包名 `ModelContextProtocol`）；stdio 是本机客户端的默认形态。
- **stdout 只留给 JSON-RPC**：日志与诊断一律写 stderr，否则客户端解析失败（官方模板与社区实践一致，2026-11-06 检索）。
- 工具处理函数保持薄：参数校验 → 打开/取缓存文档 → 调用引擎 → 组装英文 JSON；不放业务规则。
- 文档缓存：按「规范路径 + size + mtime」做键，限制同时驻留文档数（建议默认 2–4，可配置），超限按 LRU 关闭。

### 6.3 发布形态

| 选项 | 说明 | 取舍 |
| --- | --- | --- |
| 甲 NuGet dotnet tool | `dotnet tool install -g` 后 `mcp.json` 直接写命令名；跨平台；版本可管理 | **推荐**；依赖目标机有 .NET 10 运行时 |
| 乙 单文件/目录 zip 自包含 | 与 GUI 同套发布流程，目标机无运行时也能跑 | 兜底；体积大，MCP 用户多为开发者 |
| 丙 两者都发 | 覆盖最广 | 单人维护发布面翻倍，不建议 v1 |

`mcp.json` 示意（以最终发布名为准）：

```json
{
  "mcpServers": {
    "logcat": {
      "command": "logcat-mcp",
      "args": []
    }
  }
}
```

外部依据（2026-11-06 检索）：官方 .NET 快速上手提供 `dotnet new mcpserver` 模板与 stdio / HTTP 两种形态，并给出发布到 NuGet 的流程；模板包当前仍标 preview，SDK 版本演进需在 propose 时锁定具体版本。

## 7 决策台账（D1–D9 更新）

| 编号 | 议题 | 现状 | 本稿建议 |
| --- | --- | --- | --- |
| D1 | 库边界与 TFM | **已定**（2026-11-01）：双库，`logcat.UiCore` 已建，`logcat.Core` 名字留白给引擎切片 | 维持 |
| D2 | 与既有工作的先后 | 倾向：拆分卡 → `core-extraction` → `mcp-server` | 维持；补一条与 `sidecar-cache-index` 的顺序 |
| D3 | 输出契约与本地化 | 未拍板 | 固定英文 JSON，MCP 不引 `Loc` |
| D4 | 正则作用面与安全 | 未拍板 | 仅 message 体 + 大小写/排除开关 + 超时 |
| D5 | v1 工具语义 | 未拍板，且与草案 B 冲突 | 见 §5：五工具 + `search` 双模式；`trace_field` / `cross_align` 后置 |
| D6 | 发布形态 | 未拍板 | NuGet dotnet tool（甲） |
| D7 | 切片 | 倾向三个独立 change；切片 1 再分 1a/1b | 维持 |
| D8 | 版本号与测试工程 | 可默认 | 版本号沿用 `Directory.Build.props`；测试继续单工程引用 Core |
| D9 | 安全边界数值 | 可默认 | 显式绝对路径、只读、≤500 行 / ≤2 MB、正则 200 ms 超时、无写操作 |

## 8 待澄清问题

每条按「为什么需要澄清 / 有哪些方案 / 当前偏向」写。

**Q1 工具面是否按 §5 合并**
- 为什么：两套草案并存会让 AI 客户端面对两套命名，且 `search`/`count`/`window` 语义交叉。
- 方案：甲 五工具合并（§5）；乙 检索 v1 + 分析 v2 两套并存；丙 只做分析型工具面。
- 偏向：甲。若担心统计能力被参数开关稀释，可把 `search mode=count` 的返回结构单独冻结。

**Q2 正则进不进 v1**
- 为什么：`FilterSpec` 无正则，落地是新能力；灾难性回溯是安全面。
- 方案：甲 v1 只做子串，正则后置；乙 v1 就做 message 正则 + 超时；丙 只做正则不做子串。
- 偏向：乙（沿用 D4 倾向），但必须把超时与「超时按不命中」写进 spec 场景。

**Q3 编码**
- 为什么：现有解码只有 UTF-8；中文环境存在 GBK 日志，旧分析草案承诺的自动判定并不存在。
- 方案：甲 v1 只支持 UTF-8，显式 `encoding` 参数留位；乙 v1 加 UTF-8/GBK 自动判定；丙 在面板/GUI 侧先做。
- 偏向：甲。自动判定涉及解码层与 tag 字典，属于 Core 能力变更，单独评估。

**Q4 `save_to` 与写边界**
- 为什么：旧分析草案让服务端写结果文件，与「只读」安全口径冲突。
- 方案：甲 删除 `save_to`；乙 只允许写入配置的输出目录；丙 保留任意路径写入。
- 偏向：甲。

**Q5 `cross_align` 是否越界**
- 为什么：既有决策「跨文件聚合搜索已被否决」，多源时间对齐与它相邻。
- 方案：甲 v1 与 v2 都不做；乙 明确它是「对齐」不是「聚合搜索」，另立 change；丙 并入 v1。
- 偏向：甲，先记录不动。

**Q6 与 `sidecar-cache-index` 的顺序**
- 为什么：两者都改 `LogDocument` 装载路径；sidecar 能让 MCP 重开文件近零成本。
- 方案：甲 MCP 先做（stdio 会话常驻，重开不在关键路径）；乙 sidecar 先做，MCP 直接受益；丙 MCP 只依赖 Core 的稳定 API，与 sidecar 并行。
- 偏向：乙或丙；若 MCP 先做，需接受首次打开全量解析的成本。

**Q7 文档缓存与并行调用**
- 为什么：stdio server 常驻会持有 mmap 文件；并行工具调用可能触碰同一文档。
- 方案：甲 每文档串行 + 全局 LRU 上限；乙 每次工具调用即开即关（慢）；丙 只允许单文档驻留。
- 偏向：甲。线程安全现状未声明（§3.11），需在 design 前用探针验证。

**Q8 打包形态与运行时**
- 为什么：NuGet tool 依赖 .NET 运行时；zip 自包含无依赖但体积大。
- 方案：甲 NuGet tool；乙 zip；丙 双发。
- 偏向：甲（D6）。

**Q9 是否要 HTTP / 远端**
- 为什么：影响鉴权、部署、路径语义。
- 方案：甲 只 stdio；乙 stdio + 本机 HTTP；丙 远端 HTTP。
- 偏向：甲，与「不做远端部署 / 鉴权」范围一致。

## 9 风险与未知

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| 全量扫描成本 | 300 MB–1 GB 文件上 `search`/`count` 走流式全扫描，首次结果慢 | 复用列式索引与块扫描；返回 `scanned_rows` / `elapsed_ms`；必要时要求先打开再查询 |
| 索引常驻内存 | 多文档驻留 + 大索引 | 文档数上限 + LRU；sidecar 落地后改为按需装载 |
| mmap 长期持有 | Windows 上长期持有文件映射，U 盘拔出 / 文件被替换 | 出错即关闭文档；不做 follow |
| 文件被修改 | size/mtime 变化后旧索引失效 | 缓存键含 size + mtime；变化则重建 |
| 正则回溯 | 恶意或过宽正则拖死进程 | 超时 + 仅 message 体 + 结果留痕 |
| 编码误判 | GBK 日志正文乱码 | v1 显式 UTF-8；后续单独评估自动判定 |
| SDK 演进 | MCP C# SDK 与模板仍在演进 | propose 时锁定包版本；工具处理函数与 SDK 解耦，便于升级 |
| 与 GUI 并发 | 同一文件被 GUI 与 MCP 同时 mmap | 只读共享打开；若冲突则 MCP 报错并提示关闭 GUI |

## 10 下一步

1. 先拍板 Q1（工具面合并）与 Q3（编码边界）——这两项决定 spec 的需求条目数量。
2. 拍板 Q6（与 sidecar 的顺序）与 Q4（写边界）——决定 `core-extraction` 与 `mcp-server` 的 tasks 依赖。
3. 顺序上仍建议：`core-extraction`（含 Loc 拆分）→ `mcp-server`；`mcp-server` 提出前补齐 D3–D6 的具体取值。
4. 本稿作为 `/opsx:propose` 的输入；propose 只写 change 四件套，不在探索阶段动代码。

## 附录 A 代码证据索引

| 事实 | 位置（仓库相对路径） |
| --- | --- |
| 引擎类无 WinForms/Drawing 引用 | `Services/LogDocument.cs`、`Services/FilterEngine.cs`、`Services/LogParser.cs`、`Services/CrashExtractor.cs`、`Services/CrashAggregator.cs`、`Services/CrashTime.cs`、`Models/CrashEvent.cs`、`Models/FilterSpec.cs` |
| 进度文案走 `Loc` | `Services/LogDocument.cs`（`Loc.T` 多处）、`Services/FilterEngine.cs`（`Loc.T` / `Loc.F` 多处） |
| 崩溃类别显示名走 `Loc` | `Models/CrashEvent.cs` 的 `CrashKind.Name` |
| 过滤描述走 `Loc` | `Models/FilterSpec.cs` 的 `Describe` |
| 列式只读视图与行读取 | `Services/LogDocument.cs`（`RowCount`、`Offs`…`TagId`、`LineBytes` / `MessageBytes` / `HeaderBytes` / `ReadMessage`） |
| 构建与增量 | `Services/LogDocument.cs`（`Build` / `BuildAsync` / `BuildChunkedAsync` / `Reload`） |
| 过滤入口 | `Services/FilterEngine.cs`（`ApplyFilter` / `ApplyFilterAsync` / `FilterTail` / `ExportRows`） |
| 过滤无用户侧正则 | `Services/FilterEngine.cs` 文件头注释；`Models/FilterSpec.cs` 字段清单 |
| 正文解码固定 UTF-8 | `Services/LogDocument.cs` 的 `Decode`（`Encoding.UTF8.GetString`） |
| 崩溃提取与聚合 | `Services/CrashExtractor.cs`、`Services/CrashAggregator.cs` |
| 文本/二进制嗅探（非编码判定） | `Services/TextProbe.cs` |
| 版本号单一来源 | `Directory.Build.props` |
| 在途索引缓存 | `openspec/changes/sidecar-cache-index/` |
| 在途关闭修复 | `openspec/changes/fix-close-dispose-slow/` |

## 附录 B 外部依据

- [Create a minimal MCP server using C# and publish to NuGet（Microsoft Learn）](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-server)：stdio / HTTP 模板、`dotnet new mcpserver`、NuGet 发布、`mcp.json` 配置；2026-11-06 检索。
- [NuGet 包 ModelContextProtocol（官方 C# SDK）](https://www.nuget.org/packages/ModelContextProtocol)：包名与版本来源；2026-11-06 检索。
