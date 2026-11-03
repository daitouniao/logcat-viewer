# 探索：本地直接读无内容 · 代理路径正常 —— 分块加载收尾丢失末批

- 状态：**探索完成，根因已在真实窗体上实测闭环**（未建 OpenSpec change，未改动仓库代码）
- 日期：2026-11-02（当日三次更新：逐条核对代码事实，修正状态栏误导机制与候选甲线程约束两处表述）
- 事实基准：**2026-11-02 重新核对代码 + 探针实测**（不沿用旧笔记结论）
- 关联：`chunked-file-load`（spec，已归档）、`chunked-progressive-load`（change，已归档）
- 关联提交：无（探针为临时工程 `.tmp-test/LoadProbe`，`.gitignore` 已忽略，不入库）
- 验收结论：不适用（未实施）

## 1. 结论先行

> **本地直接读「不能显示内容」不是加载慢，是加载完成瞬间把还没上屏的内容丢了。**
> 分块加载的发布内容先进 `LoadPublishBuffer`（100 ms 节流窗口），UI 定时器到期才取走上屏。
> 但加载一完成，`RunChunkedLoadAsync` 的 `finally` **立刻 `flushTimer.Stop()`**，
> 没有做「最终整批发布」——**最后一批（乃至唯一一批）内容永远留在缓冲里，视图一行都没有。**
>
> **已在真实 `frmMain` + 真实 WinForms 消息循环上复现**（36.56 MB 样本，636,335 行）：
> **视图只有 69,841 行，缺失 566,494 行（89 %）**；≤4 MB 样本视图 **100 % 空白**。
> 代理路径正常，因为它走的是另一条上屏机制：`publish: null` + 末尾 `await ApplyFilter()` 全量上屏，
> **完全绕开了节流缓冲**。

**这是违反 spec 的实现缺陷，不是设计意图**：
`chunked-file-load` spec 明写「最终行数与内容同整份加载完全一致」「全部完整记录均已发布，无内容滞留」，
归档 change 的 tasks 2.2 也承诺「加载完成后做最终整批发布」——**但 `TakeAll()` 在生产代码中零调用点**（§4）。

## 2. 症状与规模的关系（真实窗体实测，最关键的一张表）

在**真实 `frmMain` + 真实 WinForms 消息循环**上跑完整加载（探针 `gui` 子命令，反射调
`frmMain.StartIndex` 并泵消息循环），读取真实控件 `LogListView.RowCount` 与 `_doc.RowCount`：

| 文件大小 | 文档行数 | **视图行数** | **缺失行数** | 缺失率 | 状态栏显示 | 用户观感 |
| --- | --- | --- | --- | --- | --- | --- |
| 0.5 MB | 8,758 | **0** | 8,758 | **100 %** | 空 | **完全空白** |
| 4 MB | 69,842 | **0** | 69,842 | **100 %** | 空 | **完全空白** |
| 20 MB | 348,321 | 69,841 | 278,480 | 80 % | 总 69,841 条 | 只看到开头一小截 |
| **36.56 MB** | **636,335** | **69,841** | **566,494** | **89 %** | 总 69,841 条 | **与报告一致** |
| 100 MB | 1,740,667 | 1,183,663 | 557,004 | 32 % | 总 1,183,663 条 | 看到大部分 |

**五档全部为真实窗体实测**（`gui` 子命令，真实 `frmMain` + 消息循环），
与模拟时序（`tail` 子命令）数据逐项一致。

规律：**首块（4 MB）的内容恰好是定时器唯一来得及消费的那一批**。
- 文件 ≤ 首块（4 MB）→ 只发布一次，加载在窗口到期前就结束 → **视图 100 % 空白**。
- 文件更大 → 至少能看到首块的 69,841 行，但**之后的内容全部丢失**。
- 用户报告的 36.56 MB 落在"只看到开头一小截"这一档，与"不能显示内容"的描述一致。

**状态栏同样被误导**：`UpdateStat` 的"总"读的是 `_doc?.RowCount`（文档行数，`frmMain.cs` L1649），
"命中"才读 `_listView.RowCount`——但它**只在 `FlushLoadOps`（定时器 tick 消费到批次）里被调用**；
加载完成后没有最终 flush，状态栏就停在**最后一次成功 flush 时刻的快照**。
36.56 MB 样本显示"总 69,841 条"即中途快照，**用户连"文档有 63 万行"都看不到**
（加载完成后只 `ShowStatusRaw` 写 `_lblMsg`，不碰 `_lblStat`，`frmMain.cs` L2414–L2418）。
≤4 MB 时从未有过成功 flush（`_doc` 未接管、`UpdateStat` 未被调过），状态栏全空。

**缺失量与文件大小的关系**：绝对缺失量恒为"末批及以后"的内容；占比随文件增大而下降
（100 MB 时因加载耗时长、定时器多消费了几批，缺失占比反而降到 32 %）。
**但缺失永远不为零**——文件越小越致命（≤4 MB 直接 100 % 空白）。

## 3. 两条路径的上屏机制差异（代码事实）

`frmMain.StartIndex` 按 `useProxy` 分流，**上屏机制根本不同**：

| | 本地路径 | 代理路径 |
| --- | --- | --- |
| 入口 | `RunChunkedLoadAsync`（`frmMain.Load.cs` L37） | `LoadViaProxyAsync`（`frmMain.cs` L1068） |
| 构建 | `BuildChunkedAsync(path, publish: 回调, …)` | spool 到本地临时文件 → `BuildChunkedAsync(spool, publish: **null**, …)` |
| 过滤 | 加载期增量过滤（`OnChunkPublished` → `FilterTail`） | 无（`publish: null` 不触发回调） |
| 上屏 | **仅靠** `FlushLoadOps` → `pub.TakeIfDue()` → `AppendRows` | **末尾 `await ApplyFilter()`**（`frmMain.cs` L1016）→ `SetRows` |
| 收尾 | `finally { flushTimer.Stop(); }` —— **无最终整批发布** | 无定时器，全量直上 |
| 是否受影响 | **是** | 否 |

**代理路径之所以正常，不是因为代理机制更好，而是因为它 `publish: null` + 末尾全量过滤，整条链绕开了节流缓冲。**

## 4. 根因定位（代码事实链）

1. `Controls/LoadPublishBuffer.cs`：
   - `Add()`：`if (_dueAt == 0) _dueAt = _now() + _windowMs;` —— 首批开启 100 ms 窗口
   - `TakeIfDue()`：`if (_ops.Count == 0 || _now() < _dueAt) return null;` —— **窗口未到不取**
   - `Take()`：取走后 `_ops.Clear(); _dueAt = 0;` —— 重置窗口
   - `TakeAll()`：**无条件取走全部** —— 正是"最终整批发布"所需
2. `frmMain.Load.cs` `RunChunkedLoadAsync`：
   - `var flushTimer = StartLoadFlushTimer();` —— 100 ms WinForms 定时器 → `FlushLoadOps`
   - `finally { flushTimer.Stop(); … }`（`frmMain.Load.cs` **L69**）—— **加载完成立刻停表**
   - `finally` 中**无 `TakeAll()`、无强制 `FlushLoadOps()`**
3. `FlushLoadOps` 只在定时器 Tick 时被调用；停表后缓冲里的内容无人再取。
4. **grep 确认：`TakeAll()` 在生产代码中零调用点**，仅 `tests/logcat.Tests/LogListViewTests.cs` 三处使用。
   归档 change 的 tasks 2.2 承诺的"加载完成后做最终整批发布"**未接线**。

## 5. 实测口径与探针

- 探针工程：`.tmp-test/LoadProbe`（子命令 `gui` / `tail` / `timing` / `ftail` / `seq` / `bisect` / `pnull` / `whole`）。
  **不入库**（`.gitignore` 已忽略 `.tmp-test/`）；本节记录口径与结果，供后续复现时重建。
- 样本：由 `.tmp-test/bench_300mb.log`（300 MB 合成日志）截取 0.5 / 4 / 20 / 36.56 / 100 MB 五档。
- 手法：**反射**访问 `LogDocument` 的 internal 成员与 `FilterEngine.MsgScanSession.TryCreate`，
  **未改动主工程源码**。

### 5.0 主证据：真实窗体复现（`gui` 子命令）

反射调 `frmMain.StartIndex(path)`（private async void），随后在 STA 主线程泵
`Application.DoEvents()` 消息循环——**真实 WinForms 定时器、真实 `FlushLoadOps`、真实控件**全部生效。
加载完成判据：`_loading == false` 且 `doc.IndexedSize >= doc.Size`；命中后再多泵 300 ms
给 UI 定时器机会消费末批（正是缺陷发生的地方）。

结果见 §2 表格——**五档规模全部复现，与模拟时序（`tail` 子命令）数据逐项一致**。

**代理路径对照的实测边界（如实记录）**：尝试用 `sample/fileproxy/fileproxy.py` 起真实代理做端到端对照，
`startup.log` 证实**分流判定成功**（`[FileProxy] 转代理读取：…`），但管道连接被拒
（`Access to the path is denied.` —— Python 代理所建管道的 ACL 不允许当前用户连接，**环境权限问题，非产品问题**）。
故代理路径"内容完整"的结论来自**代码机制事实 + §5.1 对照实验**，而非端到端实测。

### 5.1 对照实验（闭合证据）

| 收尾语义 | 视图行数 | 缺失 |
| --- | --- | --- |
| 生产语义（加载完成即停表） | 69,841 | **566,494** |
| 对照（加载完成后补 `TakeAll()`） | **636,335** | **0** |

补上"最终整批发布"后缺失归零 —— **根因确认无疑**。

### 5.2 排除项（实测）

| 怀疑项 | 实测结论 |
| --- | --- |
| `BuildChunkedAsync` 慢 / 解析性能 | 36.56 MB 全程 **1.15 s**，publish 三次全部触发，非瓶颈 |
| `FilterTail` 增量过滤慢 | 整份 636,335 行仅 **16 ms**；累进四轮共 23 ms |
| `MsgScanSession` 建会话导致卡死 | `bisect` 二分排除（见 §5.3） |
| 内存 / LOH 扩容 | 未触发异常；`EnsureCap` 容量预估正常 |
| `publish: null` 本身有问题 | `pnull` 子命令实测 **1.2 s 完成**，与带 publish 无差异 |

### 5.3 二分验证

`bisect` 子命令对 publish 回调内容做组合开关（`{}` / `{f}` / `{s}` / `{sf}` / `{fu}` / `{sfu}`：
f=FilterTail，s=建 session，u=UI 消费线程），**全部 1.1–1.5 s 正常完成**——
排除 session / FilterTail / UI 线程三者导致卡死的可能。

### 5.4 探针自身的坑（避免后人重踩）

1. **STA 死锁**：探针主线程调 `BuildChunkedAsync(...).GetAwaiter().GetResult()` 会挂死——
   `await` 的 continuation 需要 STA 消息泵，而主线程正被 `GetResult()` 阻塞。
   项目代码里已有同样提示（`Services/FileProxyClient.cs` L219：「测试进程无 SynchronizationContext，不会死锁」）。
   **解法**：改用后台线程调用，或直接驱动真实消息循环（`gui` 子命令）。
2. **跨线程碰 WinForms 控件**：消费线程直接调 `AppendRows`（会操作 `VirtualListSize`）会挂死。
   探针改为消费线程只动缓冲、UI 线程回放。
3. **提前退出**：初次 `gui` 运行的"加载完成"判据是 `视图行数 == 文档行数`，
   但加载中途两者恰好都等于首块的 69,841 → 误判完成。改为 `_loading == false` + `IndexedSize >= Size`。

## 6. 修复候选（**未定**，供后续 propose 阶段拍板）

### 候选甲：补「最终整批发布」（倾向）

加载完成后，在 `finally` 里补一次无条件取走并上屏，与归档 tasks 2.2 的承诺对齐。

- 语义：与 spec「最终行数与内容同整份加载完全一致」直接对应；与 `LoadPublishBuffer.TakeAll()` 的
  既有设计意图一致（该方法已存在且有测试，只是没接线）。
- 代价：改动集中在 `frmMain.Load.cs` 一处（`finally` 分支 + 一个"强制消费"入口）。
- 风险：取走后的应用（`AppendRows` / `SetRows`）必须在 **UI 线程**执行（碰 `VirtualListSize`）；
  `TakeAll()` 本身线程安全（类内有锁），但按 `LoadPublishBuffer` 的线程约定
  （"TakeIfDue/TakeAll 供 UI 线程调用"）也应落在 UI 线程——
  即最终整批发布应并入 `FlushLoadOps` 式的 UI 线程路径，而非 worker 侧调用。
  **该前提成立**：`StartIndex` 是 `async void`、从 UI 事件处理器调用（`frmMain.cs` L925 / L937），
  其 `await` 续体回到 UI 线程的 SynchronizationContext，故 `finally` 在 UI 线程。
  且真实窗体实测已证实 UI 定时器在消息循环中正常消费（§2 表格的 69,841 行即定时器所取）。

### 候选乙：对齐代理路径 —— 末尾走一次全量 `ApplyFilter()`

本地路径加载完成后也调 `await ApplyFilter()`，与代理路径口径统一。

- 语义：两条路径行为一致，减少心智负担。
- 代价：大文件多一次全量过滤（36.56 MB 样本实测 `FilterTail` 全量 16 ms，
  但**带 Message 关键词**时是块扫描，成本另计）；且与"加载期已增量构建、无需全量重过滤"的
  既有设计注释（`frmMain.cs` L1021–L1022）直接冲突，需一并改注释与设计文档。

### 候选丙：停表前先排空缓冲

停表前先让 UI 线程把缓冲排空（等价于甲，但表述为"停表前排空"而非"停表后补发"）。

- 与甲实质同构，差别只在叙述与落点（`finally` 之前 vs 之后）。

**三者都指向同一处代码，差异在语义表述与是否引入全量过滤开销。**
**建议在 propose 阶段以 spec delta 形式固化"加载完成后最终行数与整份加载一致"这一不变量，
并补一条自动化断言（视图行数 == 文档行数），把当前缺口钉住。**

## 7. 遗留与影响面

1. **同样的缺陷影响所有本地文件**，与大小无关（只是大小决定缺失占比）。用户用 36.56 MB 样本撞上，
   小文件（≤4 MB）会 100 % 空白——**更早、更容易被当成"文件打不开"**。
2. **测试盲区**：现有 `LogListViewTests.节流窗口内多次追加只触发一次视图刷新` 只测了
   `LoadPublishBuffer` 本身（`TakeAll` 在测试里是手动调用的），**没有覆盖"生产收尾路径"**——
   即 `RunChunkedLoadAsync` 结束后视图行数是否等于文档行数。
   这正是缺陷能通过归档验收的原因。
3. **受影响路径仅一条**（已逐路径核对）：
   - **本地文件分块加载**（`RunChunkedLoadAsync`）—— ❗ **受影响**
   - 代理路径（`LoadViaProxyAsync`）—— 不受影响：`publish: null` + 末尾 `ApplyFilter()` 全量上屏
   - ADB 实时采集临时文件（`frmMain.cs` L2239 附近，`BuildAsync` + `ApplyFilter()`）—— 不受影响
   - Reload（`frmMain.cs` L1141，`doc.Reload` + `ApplyFilter(snap)`）—— 不受影响
   - 实时采集增量同步（`ApplyIncremental`）—— 不受影响：直接 `AppendRows`，不经节流缓冲

   **即缺陷只落在"本地文件 + 分块渐进加载"这一条路径上**，其余路径都有全量上屏兜底。
4. **与 `SIDECAR-CACHE-INDEX` 的关系**：sidecar 笔记记录的"重开命中 459 ms"是 sidecar 路径，
   与本缺陷的本地分块路径不同；但 sidecar 若也走 `RunChunkedLoadAsync`，
   **同样会命中此缺陷**——建 change 时需一并核对该接入点。
5. **回归口径建议**：新增断言「加载完成后视图行数 == 文档行数」（本地路径），
   样本取小（首块以内，4 MB）即可复现 100 % 空白，是最省时的钉子。
   注意该断言需在 **STA + 消息泵**下跑（否则会命中 §5.4 的 STA 死锁），
   可直接沿用 `frmMainTests` 的"真实窗体 + 内部方法驱动"范式。

## 8. 待拍板项

- **D1**：修复方案取甲 / 乙 / 丙？
- **D2**：是否在 propose 阶段同时补"生产收尾路径"的自动化断言（§7.5）？
- **D3**：是否顺带核对 `SIDECAR-CACHE-INDEX` 计划中的 sidecar 接入点是否复用同一条本地加载路径
  （若复用，需一并纳入修复范围）。
