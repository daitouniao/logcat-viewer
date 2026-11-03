# Design: fix-chunked-tail-loss

## Context

探索结论（`openspec/notes/CHUNKED-TAIL-LOSS.md`，真实窗体复现 + 对照实验闭环）：本地分块加载收尾只 `flushTimer.Stop()`，缓冲内未到节流窗口的发布操作无人消费——`LoadPublishBuffer.TakeAll()` 在生产代码零调用点。相关代码集中在 `frmMain.Load.cs`：

- `RunChunkedLoadAsync`：`finally { flushTimer.Stop(); … }` 是唯一收尾点；`completed == false`（取消/失败）走 `CleanupAbortedLoad` 整份清空。
- `FlushLoadOps`：定时器 tick 的消费入口——接管首次发布文档 → 按序应用 `ReplaceAll/AppendRows` → `UpdateStat()` → 可选跟随滚动。取操作用的是 `TakeIfDue()`（窗口未到期返回 null）。
- 线程事实：`StartIndex` 是 `async void`、从 UI 事件调用，其 `await` 续体回 UI 线程 SynchronizationContext，故 `finally` 落在 **UI 线程**——满足 `LoadPublishBuffer`「TakeIfDue/TakeAll 供 UI 线程调用」的类约定，且与定时器 tick 串行、无并发竞态。

## Goals / Non-Goals

**Goals**：加载完成（且未被新一代抢占）时，缓冲内全部发布操作无条件取走并按既有路径上屏，视图最终行数与文档行数一致；状态栏随即反映最终统计；补生产收尾路径的自动化断言。

**Non-Goals**：不改其余四条加载路径；不动节流窗口与块长参数；不改取消/失败语义（仍整份清空）。

## Decisions

### D1：方案取候选甲——最终整批发布（否决乙、丙）

- **甲**（采用）：收尾时无条件取走缓冲全部操作并复用既有上屏路径。与 spec「加载完成无内容滞留」语义直接对应；复用 `TakeAll()`——该方法既有设计意图即此用途、已有单测，只是未接线；改动集中单文件。
- **乙**（否决）：收尾改走一次全量 `ApplyFilter()`。与「加载期已增量构建、无需全量重过滤」的既有设计冲突，大文件多一次全量过滤开销，还要连带改注释与设计口径——收益仅「与代理路径口径统一」，不值。
- **丙**（不单列）：与甲同构，只是叙述差异（停表前排空 vs 停表后补发），实现上归并到甲。

### D2：落点在 `RunChunkedLoadAsync` 的 `finally`，completed 分支内

`finally` 在 UI 线程（线程事实见 Context），此处执行强制消费即天然满足线程约定，无需跨线程投递。守卫沿用既有模式：

- `completed == false`（取消/失败）不做强制消费——spec「取消加载整份清空」，缓冲内容本就该丢弃。
- `_workerGen != gen`（被新一代抢占）不做——旧代不得触碰新代状态，沿用既有 `if (_workerGen == gen)` 守卫。

### D3：强制消费复用 `FlushLoadOps` 的应用逻辑，仅取操作方式不同

把 `FlushLoadOps` 中「接管首次发布文档 → 按序应用 ops → `UpdateStat()` → 可选跟随」抽成私有方法（如 `ApplyLoadOps(ops)`），两个入口共用：

- 定时器入口：`TakeIfDue()`，null 即返回（现状不变）。
- 收尾入口：`TakeAll()`，空列表时也走一次——若 `_doc` 已接管则 `UpdateStat()` 刷一次快照，消除「状态栏停在中途值」的最后一个窗口（定时器最后一批恰好取完、收尾时缓冲已空的时序）。

接管逻辑必须复用而非旁路：≤ 首块的小文件当前从未有过成功 flush，`_doc` 尚未接管，收尾强制消费是它唯一的上屏机会——接管判断（`_loadPendingDoc` + 代际比对）需原样生效。沿用 D1 备注的线程事实，无需改 `LoadPublishBuffer` 本身。

### D4：测试采用「无泵直驱」方案，两档断言（拍板 2026-11-02）

生产收尾路径的缺陷在「节流定时器来不及消费」的时序上，纯缓冲单测（现有 `LoadPublishBuffer` 用例）测不到。

**事实修正**：探索笔记称可「沿用 `frmMainTests` 的真实窗体 + STA 消息泵范式」——经核实不成立。`frmMainTests.cs` 现有 3 个用例全部是反射置字段 + 直接调内部方法（如 `CleanupAbortedLoad`），没有驱动完整异步加载的基建（无 STA 线程、无消息泵、无 `RunChunkedLoadAsync` 端到端驱动）。

**采用方案**：测试线程直驱 `RunChunkedLoadAsync(...).Wait()`，不泵消息——

- 无 SynchronizationContext 时 `await` 续体走线程池，无 STA 死锁（探索笔记 §5.4 的坑正是「STA 线程上阻塞等待续体」，此处不占 STA 消息泵，不触发）。
- WinForms 定时器**没有消息泵就不 tick**——恰好精确复现「≤ 首块小文件在窗口到期前加载即完成、从未有过成功 flush」的缺陷场景（该档生产实测 100 % 空白），且完全确定、无时序抖动。
- 既有 `frmMainTests` 已证实无句柄的 `frmMain` 可在测试线程直接实例化并操作控件（`SetRows` / `RowCount` 等数据操作不依赖句柄），断言无需跨线程投递。
- 生产口径差异如实声明：测试中 `finally` 跑在线程池续体而非 UI 线程；被测逻辑（取缓冲 → 应用 → 刷状态栏）与生产完全一致，UI 线程落点由生产的 SynchronizationContext 保证、不属被测面。

断言两档：

- **小文件档**（≤ 首块，当前 100 % 空白的钉子）：断言加载完成后 `_listView.RowCount == _doc.RowCount`，且均 > 0（防空列表假阳性）。
- **状态栏档**：断言加载完成后状态栏总数 == `_doc.RowCount`（覆盖「从未有过成功 flush」与「停在中间快照」两种前状态）。

大文件档不进单测——时序依赖节流窗口到期次数，测试不稳定且耗时；小文件档已足以钉死「收尾丢末批」的不变量（该档缺失率为 100 %，最敏感）。

### D5：0 行文件（无任何发布回调）不做特殊处理

无发布即无 `_loadPendingDoc`，收尾强制消费无 ops 可取、无从接管——行为与现状及整份加载路径的一致性由既有代码保证，不在本修复范围（若 0 行文件展示有独立缺陷，另立 change）。

## Risks / Trade-offs

- [收尾强制消费与定时器最后一次 tick 双重消费] → 同在 UI 线程串行执行，`TakeAll` 先于/后于 tick 都只会取走一次（`_ops.Clear()` 幂等），无重复上屏。
- [`finally` 里做 UI 操作，若窗体正在关闭] → 沿用既有守卫（`_workerGen == gen`）；窗体关闭触发的取消走 `completed == false` 分支，不进强制消费。
- [测试不依赖定时器与消息泵（无泵直驱），无 CI 时序抖动] → 定时器全程不 tick 正是被复现的缺陷前提；断言点是「加载完成后」的终态而非过程值；加载完成判据用探索笔记 §5.4 的修正式判据（加载标志复位 + 索引推进到文件尾），不依赖行数巧合。
- [测试中 `finally` 跑在线程池续体而非生产所依赖的 UI 线程] → 被测逻辑与生产一致（取缓冲 → 应用 → 刷状态栏），UI 线程落点由生产的 SynchronizationContext 保证、不属被测面；控件无句柄时数据操作跨线程安全（既有 `frmMainTests` 同口径）。

## Migration Plan

单点行为修复，无数据迁移、无 API 变更。回滚 = revert 对应提交。

## Open Questions

（无——D1/D2/D3 即探索笔记待拍板项 D1/D2 的拍板结论，方案甲与自动化断言已获用户确认（2026-11-02）；D4 测试方案、D5 边缘情况经用户拍板：无泵直驱、0 行文件不处理。sidecar 接入点经核对无现存代码，记入 proposal 非目标。）
