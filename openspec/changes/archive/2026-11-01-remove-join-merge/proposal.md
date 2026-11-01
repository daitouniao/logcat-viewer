# Proposal：删除「续行合并」（join）机制 —— 索引改按自然行

## Why

现在的索引以「逻辑事件」为单位：一条日志后面的缩进续行（最常见是崩溃堆栈）被折叠进同一条记录。为支撑合并，索引层背着一整套机制——跨块续行 carry、末条滞后发布、批末状态缓存、跨块等价性——规则多、坑多（实测样本中 11.27% 的物理行被合并掉）。在途的 `chunked-progressive-load` 的 carry 语义全部建立在 join 之上，此刻删掉成本最低、简化最大。

改为按**自然行**建索引：除空行与横幅行（`----- beginning of main` 之类）外的每一物理行各自成为一条记录，不再合并。收益：删除一整套机制（预计净减约 200 行代码、9 个变异测试锚点）；在途 chunked change 大幅简化；行号语义只由文件内容决定。

## What Changes

- **BREAKING** 索引口径：从「合并续行的逻辑记录」改为「自然行记录」（空行与横幅行仍不索引）。含无头续行的日志记录数约 +11.3%。
- **BREAKING** 公开 API 删除：`LogDocument.Join` / `Flags` / `IsMultiline(row)` / `LastAppendCarried`；`Build` / `BuildAsync` / `Reload` / `ScanRange` 的 join 与 carry 形参。仓库内无外部消费者。
- 删除合并机制本体：`MAX_JOIN`、`FLAG_MULTILINE`、flags 列整列、joinCounts、批末状态缓存（`_prevHeadAtEnd` / `_joinedAtEnd`）、Append 的 carry 回写、崩溃 c 轨批末补扫整段（崩溃识别由 a 轨直取，识别结果不变）、增量过滤的「carry 回退全量」分支。
- 删除全部相关 UI 入口与设置：设置菜单「续行合并」「导出时单行化」两个菜单项；`AppSettings.Join` / `SingleLineExport` / `NewlineVis`；Message 列 `↵` 换行可视符处理。
- 崩溃：`CrashEvent.LineCount` 字段彻底删除；崩溃摘要删「+N 行」显示分支；崩溃事件识别、异常类型、包名、PID/TID 不变。
- 测试重整：删直接测 join 语义的约 22 条 + 顺带依赖 join 的约 13 条；按能力点补三条真能力用例（自然行计数、空行与横幅仍不索引、无元数据行在元数据过滤下的可见性）+ 一条无头崩溃头用例；清理 9 个 join/singleLine 变异锚点、补 2 个新锚点（空行 / 横幅分支）、修回 8 条已漂移失效的行号型锚点、runner 路径改指权威位置。
- 主规范 `message-filter`：改写「message 匹配的作用域」为物理行作用域；新增「元数据类过滤下的记录可见性」需求。
- 验收基线：结构等式断言（记录数 == 物理行 − 空行 − 横幅）进 CI；1G 样本手测（索引列数组总量 ≤ 1.0 GB、索引耗时 ≤ 删前基线 × 1.15）；覆盖率不低于当前基线；变异测试删前留基线、判据「无新增 SKIP / SURVIVED」。

### 代价（已确认接受）

- 记录数 +11.3% ⇒ sidecar（未来形态）+9.1%、索引列数组内存 +45 MB（490 → 535 MB）。
- 无元数据行（约 11%）在用户显式启用 pid / tid / 分钟 / tag 过滤时不命中；level 过滤默认全选时仍可见。message 关键词过滤不受影响。
- 仅无头续行形态下，崩溃块的续行内容不再并入崩溃头记录（threadtime 形态本就逐行独立，无变化）。

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- `message-filter`：
  - **MODIFIED**「message 匹配的作用域」——「多行记录 SHALL 以合并后的完整正文参与匹配」改写为「message 过滤以物理行为作用域：每条记录的正文即该物理行的日志正文部分，头部信息不参与匹配」；对应 scenario 改写并新增物理行口径场景。
  - **ADDED**「元数据类过滤下的记录可见性」——用户显式启用某类元数据过滤时，不具备该类元数据的记录不命中该类过滤（排除模式视为反向命中自然保留）。minute 的既有契约留在 `time-column`，不复述。
- 其余 capability（`time-column` / `fileproxy` / `adb-capture` / `adb-server` / `device-file-transfer` / `file-browser`）不依赖 join 语义，不改。`time-column` 的「无时间戳 ⇒ 分钟过滤不命中」规则不变，仅适用集合从约 0.6% 变约 11%（design.md 说明，spec 不动）。

## Impact

- **代码**：`Services/LogDocument.cs`（合并机制、flags 列、c 轨、carry 回写）、`Services/FilterEngine.cs`（导出单行化参数）、`Services/AppSettings.cs`（Join / SingleLineExport / NewlineVis）、`Services/LocTable.MainUi.cs`（两条文案键）、`Services/CrashExtractor.cs`（注释术语）、`Services/LogParser.cs`（注释术语；`LooksLikeNewRecord` 保留并加说明注释）、`Models/CrashEvent.cs`（LineCount 字段）、`frmMain.cs`（菜单项、同步逻辑、增量过滤回退分支、带格式复制）、`Controls/LogListView.cs`（↵ 可视符、「+N 行」分支）。
- **测试**：`tests/logcat.Tests/` 多个测试文件删改补；`scripts/mutation-test.py` 锚点清理 / 补充 / 修回 / runner 路径。
- **文档**：README ×2 删「续行合并」与「单行化」提及；`docs/UT-AUDIT.md` 随锚点变动更新（含行号订正）。
- **规范**：`openspec/specs/message-filter/spec.md` 经本 change 的 delta 更新。
- **延后项（本 change 不碰）**：在途 `chunked-progressive-load` 的规划产物修订（本 change 落地后整块重构）；`SIDECAR-CACHE-INDEX.md` 笔记的过时论断订正（随 sidecar 重构）；sidecar 未实现，本 change 无落点。

## Non-goals

- 不实现 sidecar 缓存。
- 不改在途 `chunked-progressive-load` 的任何规划产物。
- 不补完整的元数据过滤规范（只新增 message-filter 一条需求；不新建 capability）。
- 不做崩溃块补偿 UI（若实测反馈不可接受，另立 change）。
- 不改导出格式（只删「单行化」选项）。
- 不统一 Build 与 Append 两条路径（留给 chunked 重构）。
- 不留紧急开关（kill switch）；回滚 = revert 一个 commit。
- 不为 chunked 预留「批末状态」API。
