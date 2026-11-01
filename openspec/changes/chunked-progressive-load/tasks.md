# Tasks

## 1. 前置与核实

- [x] 1.1 本 change 在专属 git worktree 实施：分支 `wt/chunked-progressive-load`（路径以 `git worktree list` 定位；`git worktree add` 需当轮授权）。分支已随用户指令重置到 master（旧 join 口径 WIP 丢弃），按自然行口径设计从零实施。
- [x] 1.2 探针按自然行口径重跑：首屏时延、内存峰值、总时长、与整份构建的逐元素等价性（多块长）；结果回填 design 决策 2 的默认块长与 spec 验收线基数。探针 `.tmp-test/NaturalProbe`：4/16/64 MB 三块长等价零差异（17,832,213 行）；4 MB 首块 245–299 ms 达标；总时长比 1.03–1.19（机器噪声大，实施后 5.2 复测为准）
- [x] 1.3 核实文档映射生命周期：确认解析推进期间消息读取映射是否已就绪、可取基指针；不成立则实现加载期提前建映射。测试：`LogDocumentTests.加载期可取得消息读取基指针`。事实成立：`DoBuild` 中 `OpenMap` 先于 `ScanRange`，加载期基指针可用，无需提前建映射；测试已过（口径 1/1）

## 2. 视图层：分段行号结构（design 决策 6）

- [x] 2.1 视图行号模型改为分段块数组 + 段索引，覆盖 SetRows / AppendRows / CaptureAnchor / PinAnchor / NearestModelRow / ModelRowOf / DocRow 全部访问点。测试：`LogListViewTests.分段结构与整表结构_ModelRowOf_DocRow_往返一致`、`LogListViewTests.连续AppendRows与一次SetRows行号序列一致`。附带：`Rows` 属性改 `RowCount` + `GetRowsCopy()`（导出取快照），frmMain 11 处消费点同步
- [x] 2.2 加载期发布上屏按时间片节流（约 100 ms 聚合一批），加载完成后做最终整批发布。测试：`LogListViewTests.节流窗口内多次追加只触发一次视图刷新`。实现为 `Controls/LoadPublishBuffer`（线程安全聚合缓冲，时钟可注入），UI 接线随 3.2/4.3

## 3. 分块加载管线（design 决策 1/2/3/10）

- [x] 3.1 增量并入循环化：在 `Append` 既有列拼接 / tag 重映射 / 崩溃事件行号偏移基础上，支持分块循环消费（多次并入同一线程内既有文档）。测试：`LogDocumentTests.多次增量并入与一次并入结果一致`。实现：从 `Append` 提取 `internal MergePart(part, newIndexedSize)`（不触映射、不改 Size），`Append` 语义不变
- [x] 3.2 分块加载编排：自适应两段式块长（首块 4 MB、稳态 32 MB，默认值以 1.2 复测结果定稿），区间扫描推进 + 增量并入 + 块末换行对齐（`TrimPartial` 思路，不完整行留待下一块）+ EOF 收尾。测试：`LogDocumentTests.分块加载与整份构建逐元素等价_多块长`、`LogDocumentTests.跨块边界不产生多余或缺失记录`、`LogDocumentTests.块末不完整行留待下一块发布`。实现：`BuildChunkedAsync/BuildChunkedCore` + 发布回调；实施修订：崩溃前瞻 3 行跨块重叠读（见 design 决策 3 修订），已由 (200,200) 块长等价用例钉住
- [x] 3.3 小文件统一走同一条流式管线（单块即整份，无独立路径）。测试：`LogDocumentTests.小文件单块加载与整份构建等价`（含空文件零发布）
- [x] 3.4 Reload 增长路径统一：头部未变化时增长部分交给分块管线从 `IndexedSize` 续扫，头部变化回退整体重建。测试：`LogDocumentTests.增长续扫与整份重建结果一致`、`LogDocumentTests.头部变化触发整体重建`。实现：提取实例方法 `ChunkScan`，`Reload` 增长分支改走它（旧 `Append` 删除）；沿用既有语义（取消返回 unchanged 不抛、零记录批次返回 unchanged 但推进索引口径）。注：64 KB 头部比对在 Windows 上因 mmap 页缓存共享两端均为当前内容，属跨平台防御代码（测试注释已注明）
- [x] 3.5 取消加载整份清空：worker 换代 + 块边界检查取消，清空文档、视图与过滤结果回到空状态。测试：`LogDocumentTests.取消加载后文档回到空状态`、`frmMainTests.取消加载后列表与过滤结果清空`（+代际守卫防污染新加载）。实现：`CleanupAbortedLoad`（RunChunkedLoadAsync finally 在未完成时调用）

## 4. 加载期过滤（design 决策 4）

- [x] 4.1 块扫描主流程拆出增量形态：会话（累计计数数组）+ 区间轮（只扫 `[上轮末尾, 本轮末尾)`，起点前移重叠量防跨轮漏配）+ 行段判定（只判新增候选行）。测试：`FilterEngineTests.增量区间轮与全量块扫描命中集合一致`、`FilterEngineTests.跨轮边界关键词命中不遗漏不重复`。实现：`ScanBlockRounds`（全量/增量共用）+ `MsgScanSession`；实施核实：行首归属唯一 ⇒ 无有效跨轮关键词（needle 必须完整落在单条 message 段内），区间轮无需起点前移，overlap 仅保留在块内分块（design 已记）
- [x] 4.2 增量过滤接入分派：满足可扫三条件走增量块扫描，否则回退逐条增量；加载期修改过滤条件对已发布前缀走既有全量路径后恢复增量。测试：`FilterEngineTests.加载期含Message关键词增量过滤与全量结论一致`、`FilterEngineTests.非ASCII关键词增量路径回退逐条`、`FilterEngineTests.规模条件不满足时增量回退逐条`、`FilterEngineTests.加载期修改过滤条件_前缀全量后恢复增量`。实现：`FilterTail` 新增可选会话参数（无会话路径行为不变），每轮评估规模条件，关键词可用性在 `TryCreate` 过滤
- [x] 4.3 加载期 UI 状态：过滤器与时间过滤增量生效，导出与崩溃面板置灰至完成，进度指示已发布量。测试：`frmMainTests.加载期导出与崩溃面板置灰加载完成后恢复`（断言 Enabled 状态不涉文案，无需 Loc 双语口径）。实现：frmMain.Load.cs（RunChunkedLoadAsync / OnChunkPublished / FlushLoadOps / SetLoadUiState）；ApplyFilter 加载期分流 pending、Reload 守卫；StartIndex 本地路径走分块管线、代理路径统一管线（无渐进发布）；`LoadPublishBuffer` 支持替换/追加操作队列

## 5. 等价性、性能与全量回归

- [x] 5.1 等价性不变量进 CI：分块加载 vs 整份构建逐元素比对（行数、起始位置、长度、级别），多种块长参数化。测试：`LogDocumentTests.分块加载等价性不变量_参数化块长`。落点调整：归入 `ChunkedEquivalenceTests.分块与整份全维等价`（6 样本形态 × 3 块长 = 18 例 MemberData 参数化，断言含全部列 / tag 字典 / 崩溃事件 / 发布前缀稳定），LogDocumentTests 内不再重复
- [x] 5.2 性能复测（1 GB 探针口径，自然行基数）：首屏 ≤ 0.5 s、总时长 ≤ 整份 × 1.05、内存峰值 ≤ 1.0 GB；验证分段行号结构下拼接开销不再随块数放大；结果与 docs/TESTING.md 数字联动。**实测**（生产管线 `BuildChunkedCore` 4MB/32MB，17,832,213 行）：首屏 296–337 ms ✓；总时长比 1.08–1.23、内存峰值 2.3–2.9 GB vs 整份路径同机 4977 MB。**验收线经用户批准修订**（原 1.05×/1.0GB 源自 join 口径简化探针，从未校准真实管线）：总时长 ≤ 1.20× ✓（修订后实测达标）、内存峰值 ≤ 整份路径峰值 ✓（约 58%）。实施期优化：首个有记录分块后按实测行密度一次性预分配列容量（总时长比 1.23→1.08）。spec.md / design.md 已随改；探针 `.tmp-test/NaturalProbe` 已改为驱动生产管线。TESTING.md 无性能段（只跟踪用例数/覆盖率基线），数字自包含于本条与 design 决策 10
- [x] 5.3 全量回归：dotnet test 全过并报告口径数字（相对基线无新增失败）；必要时 tstrun 交叉验证并声明跳过类别。**结果**：`dotnet test` Debug 口径 **791/791 全过，无跳过**（基线 744 → 791，无新增失败；过程中修复 2 处：节流测试对齐操作队列排空语义、`LocTable.MainUi` 补「加载中，请稍候…」双语键）。tstrun 不需交叉验证（dotnet test 可用），跳过类别声明：`ChunkedEquivalenceTests` 18 例 `MemberData` 参数化若走 tstrun 会被静默跳过（TESTING.md 已注）。覆盖率同批重采并入 TESTING.md（行 97.31% / 分支 88.65% / 方法 95.61%）

## Workflow follow-up

- 验收由用户执行；「验收通过」后等明确归档指令。
- 归档时：`openspec archive chunked-progressive-load` + 同批将 `openspec/notes/CHUNKED-PROGRESSIVE-LOAD.md` 移入 `openspec/notes/archive/`（git mv + 日期前缀）+ 同批清除本 change 的 worktree（git worktree remove）。
- 发版按 AGENTS 阶段 8：版本号同步入库 5 处，需用户当轮明确指令。
