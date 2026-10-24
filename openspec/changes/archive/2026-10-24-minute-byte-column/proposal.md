# Proposal

## Why

分钟过滤是系统里唯一基于时间的过滤，却为它维护着 8 B/row 的绝对毫秒列，外加一整套年份机制：闰年日序编码、跨年回退检测、`YearTables` 静态缓存（无锁，并发构建不同 baseYear 的文档有竞态）、`GuessYear`、±55 年人为窗口 + `Math.Clamp` 静默容错。这套约 250 行的逻辑，生产上只服务两类场景：过滤时的每分钟一次取模，和崩溃事件的绝对时间戳（约占 0.01% 行）。

分钟本来就是解析器已经算出的字段。把时间列换成原始分钟 byte 列后，过滤直接查表（语义严格等价，见 design），内存降约 18%，整套年份机制可整体删除。

## What Changes

- **新增**：每条记录的分钟 byte 列（0-59；无有效时间的记录存哨兵值，分钟过滤视为不命中）。解析结果透传分钟字段（解析器内部本就算出，零新增解析成本）。
- **新增（显示修复）**：Time 列渲染识别 `[ ` 前缀（`-v long` 方括号格式），与 threadtime / time / ymd 格式一致显示行首原始时间戳文本。
- **修改**：分钟过滤从「绝对毫秒取模」改为「字节集合成员判定」；崩溃汇总组排序从「首次时间戳」改为「首次发生行号」，组内末次从「时间最大」改为「行序最后」；崩溃行 / 组的时间文本改取自行首原始时间戳字节，缺失显示 `-`。
- **BREAKING（公开 API 移除）**：`LogDocument.Ts`、`LogDocument.BaseYear`、`LogDocument.HasYear`、`LogDocument.TsToText`、`LogDocument.TsFromParts`、`LogDocument.NearestPos`；`CrashEvent.Ts`、`CrashGroup.FirstTs` / `CrashGroup.LastTs`。
- **移除（内部机制）**：`FinalizeTs` 跨年检测与绝对年份推算、`GuessYear`、`YearTables` 及其静态缓存、`_yearLast` / `_withinLast` 跨批缓存；`ScanRange` 不再收集 `within` / `years` 临时列。
- **行为变化（须记录，均为向「显示与过滤一致」方向修正）**：
  1. `s=60` 闰秒容忍行（如 `23:59:60.500`）：分钟过滤命中从分钟 0 桶移到分钟 59 桶——此前绝对毫秒把 `:60` 溢出到下一分钟，与 Time 列显示的 `:60` 割裂（"看得见、滤不出"）；改后过滤与显示一致。
  2. 时钟回跳（组内时间乱序）的崩溃组：末次事件从「组内最大时间」改为「行序最后的事件」；时间未知（-1）排最后的语义随之消失。

## Capabilities

### New Capabilities

- `time-column`: 记录时间的列存储、时间过滤与显示契约——覆盖分钟列的存储与过滤语义、Time 列的行首原文渲染（含 `-v long` 前缀识别）、崩溃事件时间的行序与原文呈现。

### Modified Capabilities

（无。`fileproxy` 能力不受影响；时间行为此前无 spec 契约，本 change 以新能力确立变更后契约。）

## Impact

- **代码**：`Services/LogParser.cs`（结果结构加分钟字段）、`Services/LogDocument.cs`（列存储替换 + 删除年份机制）、`Services/FilterEngine.cs`（两处分钟过滤改字节判定）、`Services/CrashAggregator.cs`（聚合改行序）、`Controls/LogListView.cs`（Time 列与崩溃摘要时间）、`Forms/CrashSummaryForm.cs`（组排序与时间显示）、`frmMain.cs`（右键「按此分钟过滤」）。
- **测试**：`LogDocumentTests` 跨年套件、`TsToText` / `TsFromParts` / `NearestPos` 相关约 20 条重写或删除；`CrashTests` 乱序 `LastTs` 用例调整；`FilterEngineTests` 分钟过滤用例不动（等价性安全网）。
- **工具与文档**：`scripts/mutation-test.py` 4 条锚点（NearestPos / GuessYear / TsFromParts-adj / YearTables-adj）随实现改写；`docs/UT-AUDIT.md` 跨年阈值条目与跨年场景分析段同步清理。
- **无新增依赖**，csproj 不动；除 `-v long` 行 Time 列显示修复外无 UI 交互变化。
