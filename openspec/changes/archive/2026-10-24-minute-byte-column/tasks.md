# Tasks

> 本次 apply 在独立 worktree 实施：分支 `wt/minute-byte-column`（路径以 `git worktree list` 定位；2026-10-24 用户授权创建）。

## 1. 解析侧透传分钟字段

- [x] 1.1 解析结果结构增加分钟字段：三条时间路径（`mm-dd` 快路、`yyyy-mm-dd` 快路、方括号正则慢路）透出行内原始分钟（0-59），无时间 / 解析失败行为 -1；秒字段 60 的闰秒容忍行按原始分钟（59）透出。验证：新增 `LogParserTests` 用例 `ParseLine_透传分钟字段`（18:06 → 6、闰秒 `23:59:60` → 59、brief 无时间行 → -1、非法时间行 → -1），`dotnet test` 口径跑 `LogParserTests` 全过并声明数字。
- [x] 1.2 修正全部解析结果构造点（索引扫描的 catch 空结果分支等）与测试中的构造点。验证：编译通过、`LogParserTests` 全过。

## 2. 列存储增加分钟列（与绝对毫秒列并存）

- [x] 2.1 列存储新增 byte 分钟列（字段 `_min`、只读视图 `Minute`；无有效时间行哨兵 255）：列字段、只读视图、`SetColumns` / `EnsureCap` / 扫描分部结构的 Minute 收集、`Assemble` / `Append` 写入五处同步；绝对毫秒列暂保留不动。验证：新增 `LogDocumentTests` 用例 `Build_分钟列各值正确`（三种时间格式行取到分钟、无时间戳行 255）与 `Append_分钟列增量对齐`（1 行 Build + 一次追加 1100 行触发扩容路径后逐行比对），跑 `LogDocumentTests` 全过。
- [x] 2.2 崩溃事件的时间戳回填暂维持现状（仍取自绝对毫秒列），保证本组不改变崩溃行为。验证：`CrashTests` 全过（无改动）。

## 3. 过滤侧切换分钟列

- [x] 3.1 过滤引擎两处分钟判定（全量主路与实时追尾路）改为字节集合成员判定，去除法与负时间分支；语义按 spec「分钟列与分钟过滤语义」。验证：既有分钟过滤用例全过（全量命中、无时间戳行不命中、FilterTail 组合、多条件组合），并新增 `FilterEngineTests` 用例 `分钟过滤_闰秒行按59命中`（`23:59:60.500` 行 + 集合 {59} 命中、{0} 不命中），`dotnet test` 口径声明数字。
- [x] 3.2 主窗口右键「按此分钟过滤」改读分钟列，菜单生成的过滤值与选中行 Time 列显示的分钟一致。验证：走查确认（选中 `18:06` 行 → 过滤值 6）；如 `frmMainHelperTests` 有右键覆盖则同步改。

## 4. Time 列显示修复与兜底移除

- [x] 4.1 行首时间戳字节提取增加 `[ ` 前缀识别（跳过 2 字节后按既有两种日期形态判定），覆盖方括号 long 格式。验证：新增 `LogDocumentTests` 用例 `HeaderBytes_方括号long格式跳过前缀`，既有 `HeaderBytes保留高精度小数秒` 等用例不回归。
- [x] 4.2 列表 Time 列渲染删除绝对毫秒兜底分支，统一走行首原文（无时间戳行显示空）；按 spec「Time 列行首原文渲染」四场景核对。验证：渲染用例 / 走查覆盖 threadtime、time、ymd、方括号 long、brief 五种行形态。

## 5. 崩溃侧行序化与时间原文化

- [x] 5.1 崩溃聚合改行序：分组排序「次数降序 + 首次行号升序」，组内末次取行序最后的事件；`CrashGroup` 删 `FirstTs`/`LastTs`、新增 `LastRow`，`CrashEvent` 删绝对时间戳字段，汇总视图不再引用基准年。验证：改写 `CrashTests` 原乱序用例（100→300→200→-1，期望末次改为行序最后的 200），新增 `组内时间乱序时末次取行序最后` 与 `多组崩溃按首次行号排序`，跑 `CrashTests` 全过。
- [x] 5.2 崩溃时间文本改行首原文：汇总行显示到秒、列表摘要显示时分秒、行首无时间戳显示 `-`。验证：`CrashTests` 新增呈现用例（组到秒 / 摘要时分秒 / 无时间戳占位三种）。

## 6. 删除绝对毫秒列与年份机制全家

- [x] 6.1 列存储删除绝对毫秒列（列字段、只读视图、`SetColumns` / `EnsureCap` / 分部结构 / `Assemble` / `Append` 五处回退为八列）。验证：编译通过（0 警告 0 错误）；`LogDocumentTests` 增量对比两处 `Ts[i]` 断言改 `Minute[i]` 后全过。
- [x] 6.2 删除年份机制：时间戳终算（跨年检测与绝对年份推算）、基准年推测、年份偏移表及静态缓存、跨批合并状态缓存、`HasYear` / `BaseYear` 公开属性、`TsToText` / `TsFromParts` / `NearestPos` 公开方法；扫描停止收集年内毫秒与年份临时列。验证：全仓库 `.cs` 搜索无残留引用（`FinalizeTs`/`GuessYear`/`YearTables`/`NearestPos`/`TsToText`/`TsFromParts`/`HasYear`/`BaseYear`/`_yearLast`/`_withinLast`/`YEAR_LO`/`YEAR_HI`/`HALF_DAY`/`doc.Ts` 均零匹配）；编译通过。
- [x] 6.3 测试删改：删除跨年套件（回退跨年三条、混合年份锚点一条）、`TsToText` 两条、`TsFromParts` 全部（含 Theory）、`NearestPos` 两条及依赖基准年的用例，共删除 16 个用例方法（9 个 Fact + 7 个 Theory 共 14 行数据，合计 23 条用例）；`HasYear` / `BaseYear` 断言用例删除。验证：`dotnet test` 口径全量 **650/650 通过**（Group 5 收束时 673 → 650，净删 23 条），无新增失败。

## 7. 工具与文档同步

- [x] 7.1 变异测试脚本维护：删除 5 条失效锚点（HALF_DAY 跨年阈值、NearestPos、GuessYear、TsFromParts-adj、YearTables-adj）；改写 FilterEngine 两条分钟守卫 LINE 锚点（`if (ts < 0) continue;` 已不存在，行号 268/335 → 277/342，pid/tid 锚点同步校正 254/261/325/330 → 263/270/332/337，TagOf 243 → 246）；新增 9 条分钟列/行序化变异目标（哨兵 255→0、ScanRange 无时间戳行存 0、ParseSlow 两条分钟透传、HeaderBytes 前缀、崩溃排序/末次行号两条、ApplyFilter/FilterTail 哨兵守卫两条）。验证：干跑 95 TEXT + 10 LINE 条目全部命中；runner 改指 worktree 后端到端实跑本 change 新增 9 条**全部 KILLED**（每条 1~3 个用例抓住）。附带修复脚本 revert 机制（`git checkout` 改为内存内容写回——原实现在未提交树上会打回 HEAD 抹掉改动，2026-10-24 实测两次丢失；同时修正 CRLF 读写，原实现会把变异文件写成 LF）。
- [x] 7.2 单测审计文档同步：移除跨年阈值条目与跨年场景分析段，记录本 change 的测试删改与新增。验证：文档无失效引用、与实现一致。（`docs/UT-AUDIT.md`：新增 §0c 第三轮速览 + §0c.1 两个新坑；§0 表 N3/C5 与未覆盖表 FinalizeTs 行标注作废；§4.5 roll/within 整段移除并留墓碑；§5.1 行号 254/261/268/325/330/335 → 263/270/277/332/337/342 且分钟守卫改集合判定描述；§2 回滚描述与 runner 指向说明更新；§5.2/§5.3 手动 checkout 旧纪律补正。）
- [x] 7.3 测试文档口径更新：用例数与结论随删改刷新。验证：数字与 `dotnet test` 实际输出一致。（`docs/TESTING.md`：§0 基线表 663 → 650；§0.1 / §1.1 / §3.2 三处口径 663 → 650；新增 10-24 minute-byte-column 增量段 663 → 673 → 650 并注明覆盖率数字为 10-23 重采值、本轮未重采。）

## Workflow follow-up

- 实现完成后由用户验收；「验收通过」后询问是否归档。
- 归档时执行 OpenSpec archive（delta 并入主规范）；同批将关联探索笔记 `openspec/notes/YEAR-TABLES-ELIMINATION.md` 移入 `openspec/notes/archive/`（`git mv` + 归档日期前缀），并同批清除本 change 的 worktree（`git worktree remove`，随归档指令一并授权）。
- `docs/FEATURES.md` 与 `docs/archive/` 归档索引已停止维护（统一删除中），归档时不同批改动。
- 归档前确认 `dotnet test` 口径数字已写入 `docs/TESTING.md` 且与实测一致。
