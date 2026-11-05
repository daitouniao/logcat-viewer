# Proposal

## Why

分块渐进加载收尾缺「最终整批上屏」：加载完成时仍滞留在 100 ms 节流缓冲里的发布操作被 `flushTimer.Stop()` 直接丢弃，实测 39 MB / 212594 行日志无过滤只显示 22543 行（恰为首块 4 MB 的行数）。该缺陷已由 change `fix-chunked-tail-loss` 完成修复（提交 `5a5c7bc`，含 2 条自动化用例，已验收归档、spec 已入主库），**但修复提交只落在 `wt/fix-chunked-tail-loss` 分支上，从未并入主分支**——change 归档时收尾。后果：主分支及其 `V0.1.7` 发布构建仍带此缺陷；仅 `V0.1.7-r1-fdd`（从该分支打出）含修复。

## What Changes

- 将修复提交 `5a5c7bc`（fix(load): 分块加载收尾强制消费）移植回主分支：`RunChunkedLoadAsync` 的 `finally` 在完成且未跨代时 `TakeAll()` 无条件取走缓冲全部发布操作，复用既有上屏路径；缓冲为空且文档已接管时补一次 `UpdateStat()` 消除状态栏中途快照。
- 随提交恢复 2 条收尾路径自动化用例（`frmMainChunkedTailTests`：加载完成后视图行数与文档行数一致、状态栏总数等于文档行数）。
- 修复落地后清除已并入的遗留分支 `wt/fix-chunked-tail-loss`（git 写操作，实施时请示授权）。

## Capabilities

### New Capabilities

（无——行为契约已存在于 `openspec/specs/chunked-file-load/`：Requirement「已发布前缀稳定」的收尾整批上屏语义与 Requirement「加载完成后状态栏统计反映最终值」，本 change 仅为实现追赶既有 spec，无 spec 级行为变化。）

### Modified Capabilities

（无。）

## Impact

- 代码：`frmMain.Load.cs`（+25 行级别，纯收尾路径）、`tests/logcat.Tests/frmMainChunkedTailTests.cs`（新增，+78 行级别）。
- 修复基线 `ec675c8` 至今主分支上 `frmMain.Load.cs` 零漂移，移植可干净套用；测试工程自基线以来有演进（`TestHelpers` 等），移植后需全量回归确认。
- 不改公开 API、不改数据模型、不加依赖；不涉及 `V0.1.8` 分支（该分支如需同步此修复属发版决策，不在本 change 范围）。

## Out of Scope（非目标）

- 不改节流窗口与块长参数、不改发布粒度（按块发布维持现状）。
- 不改取消/失败语义（仍整份清空）。
- 不处理 0 行文件的展示（既有 design D5 边界维持）。
- 不自动推进任何版本的发布动作（阶段 8 需用户明确指令）。
