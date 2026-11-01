# 探索：加载期分块读入 · 分块解析 · 渐进显示 · 后台续载 · 异步过滤

- 状态：**探索完成，决策已定**（D1–D12 全部已定，见 §8；未建 OpenSpec change，未改动仓库代码）
- 日期：2026-10-30
- 关联提交：无（探针为临时工程 `.tmp-test/ChunkProbe`，已被 `.gitignore` 忽略，不入库）
- 验收结论：不适用（未实施）

## 1. 结论先行

> **分块读入 + 分块解析 + 渐进显示这条路是可行的，且解析层已有现成原语。**
> 探针实测：分块路径与整份路径 **15,823,120 行逐元素零差异**（4/16/64 MB 三种块长均如此）；
> 4 MB 块首屏 **212 ms**（现状首屏 = 全量 12.5 s）、内存峰值 **663 MB**（现状 3,596 MB）。
> 「后台续载」= 现有分块管线的自然延伸，无需新机制；
> 「异步过滤」在**列预筛段**已验证零成本（逐块累计 23 ms ≈ 整份 19 ms）；**Message 关键词段的冲突已解**（D9=C′ 增量块扫描，见 §6.2 修正与 §8 D9）。

前置事实修正：`openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` 写「`ScanRange` 一次 `new byte[rangeSize]` 读全文件」——**该描述已过时**。当前 `ScanRange` 用 `FileStream` 逐行读，单次只持有区间大小的 `byte[]`（`Services/LogDocument.cs` L519–L528）。当时是整份扫描，故区间 = 全文件；**区间化能力本身早已具备**。

## 2. 现有原语盘点（代码事实）

分块方案**不是从零造管线**，以下原语均已在主工程落地且在实时采集路径上跑通：

| 原语 | 位置 | 对分块方案的作用 |
| --- | --- | --- |
| `ScanRange(path, start, end, …)` | `Services/LogDocument.cs` L439 | **核心**：区间扫描，两端自动对齐行边界；`start`/`end` 可传任意区间 |
| `ScanPart.CarryLines` / `EndPrevHead` / `EndJoined` | 同上 L425–L431 | **跨块续行合并状态**：批末状态可传给下一批 |
| `LogDocument.Append(newSize, …)` | 同上 L879 | **增量并入**：消费 carry 回写 `_lens[_n-1]`、tag 重映射、崩溃并入 |
| `LogListView.AppendRows(rows)` | `Controls/LogListView.cs` L94 | **尾部追加**：虚拟列表增量上屏 |
| `FilterEngine.FilterTail(doc, spec, start)` | `Services/FilterEngine.cs` L856 | **增量过滤**：只扫 `[start, RowCount)` |
| `frmMain.ApplyIncremental(doc)` | `frmMain.cs` L1257 | **增量上屏编排**：FilterTail + AppendRows + 统计刷新 |

**结论**：分块方案的三个支柱（P1 记录边界切块 / P2 定稿缓冲 / P3 列预分配）在代码上**已有对应物**，不需要新发明。

## 3. 探针口径

- 探针工程：`.tmp-test/ChunkProbe`（`dotnet run -c Release --project .tmp-test/ChunkProbe -- <file> [blockBytes]`，子命令 `carry` / `equiv` / `mem` / `afilter`）。**不入库**；本笔记记录口径与结果，供后续复现时重建。
- 样本：`testdata/big_test.log`，1024 MB，15,823,120 行 / 15,823,120 条记录，145 tag，592 崩溃事件（与既有基线一致）。
- 机器：8 逻辑核可见（`Environment.ProcessorCount`）。
- 手法：**反射**访问 `LogDocument` 的 internal 成员，**未改动主工程源码**。
- 基准文件不入库，秒数仅本地复现、不进 CI；CI 只守功能等价性不变量。

## 4. 实测数据

### 4.1 等价性（本轮最关键结论）

分块扫描 + **消费 carry**（复刻 `Append` 的 `_lens[_n-1]` 回写）后，与整份 `Build` 逐元素比对 `(offset, length, level)`：

| 块长 | 块数 | 分块行数 | 整份行数 | 逐元素差异 |
| --- | --- | --- | --- | --- |
| 4 MB | 257 | 15,823,120 | 15,823,120 | **0 行** |
| 16 MB | 65 | 15,823,120 | 15,823,120 | **0 行** |
| 64 MB | 17 | 15,823,120 | 15,823,120 | **0 行** |

**等价性成立**，且块长不敏感。

### 4.2 首屏延迟与内存

| 块长 | 首块完成（首屏可得） | 全程 | 内存峰值 Private（增量） | 块耗时 p50 / p95 / max |
| --- | --- | --- | --- | --- |
| 4 MB | **212 ms** | 13,051 ms | 663 MB（+656 MB） | 43 / 81 / 211 ms |
| 16 MB | 539 ms | 12,977 ms | 717 MB（+710 MB） | 188 / 254 / 538 ms |
| 64 MB | 1,446 ms | 13,030 ms | 1,097 MB（+1,091 MB） | 775 / 1,445 / 1,445 ms |

**现状对照**（整份 `Build`）：12,224–15,683 ms（多次实测区间），首屏 = 全量（无增量发布），内存峰值 Private **3,546–3,596 MB**。

结论：
- **首屏**：4 MB 块 212 ms vs 现状 ~12,500 ms → **约 59× 改善**；
- **内存**：663 MB vs 3,596 MB → **约 5.4× 改善**（分块峰值与块长近似线性，64 MB 块即回到 1.1 GB）；
- **总时长**：分块不比现状快（12.9–13.1 s vs 12.2–12.6 s），**持平略慢 2–5%**（多出的 AlignEnd 回扫与块边界开销）。分块买的是首屏与内存，不是总时长。

### 4.3 逐块过滤（列预筛段）

以「Level ≠ 0 且分钟列 ≠ 哨兵」为示例条件（等价 `FilterEngine` 的 Level/Minutes 列访问段）：

| 路径 | 命中 | 预筛耗时 |
| --- | --- | --- |
| 逐块预筛（16 MB × 65 块） | 15,470,278 | **23 ms** |
| 整份预筛（Build 后一次扫） | 15,470,278 | **19 ms** |

命中完全一致；**逐块过滤的预筛段成本可忽略**（多 4 ms）。

### 4.4 视图行号数组的拼接成本（`AppendRows` 整表拷贝）

模拟 `LogListView.AppendRows` 的 `new int[oldLen+newLen]` 整表拷贝：

| 块长 | 块数 | 拼接累计耗时 | 占分块总耗时 |
| --- | --- | --- | --- |
| 16 MB | 65 | 345 ms | 2.5% |
| 4 MB | 257 | 1,533 ms | 9.0% |
| 1 MB | 1,025 | 5,180 ms | 25.9% |

**O(n²) 确认成立**——块长越小越贵，1 MB 块时已占四分之一以上。对策见 D10。

## 5. 关键发现：跨块续行的等价性陷阱（新增，须在设计中正面处理）

首轮探针出现 **2 行差异**（分块 15,823,122 vs 整份 15,823,120），差异集中在 idx=13,596,562（块 56 边界）。逐层定位结论：

1. **不透传 carry 状态时**（`prevHeadDoc`/`joinedDoc` 每批都传 `false`/`0`）：分块行数比整份**多 2 行**——块首续行被当成独立记录，多算了 2 条。
2. **透传 carry 状态时**（`prevHeadDoc = part.EndPrevHead`、`joinedDoc = part.EndJoined`）：行数**完全一致**（15,823,120），但**仍有 1 行 `(off, len)` 不同**：

   ```
   分块: off=922744830 len=76   [D/WeChat] read 229 bytes / intent act #197
   整份: off=922744830 len=116  [D/WeChat] read 229 bytes / intent act #197 / query 627 rows in 627ms / reload page 521
   ```

   两路径记录起点完全一致，差的是这条记录**覆盖的长度**：整份路径把 2 行续行并入了本记录，分块路径没有。

3. **根因**：`ScanRange` 在批首遇到续行时**不新建记录**，而是记入 `CarryLines` / `CarryEndOff`，由 `Append` 回写 `_lens[_n-1]` 把上一批末条记录拉长。**探针只统计 `Offs` 未消费 carry，才出现这 1 行差**。补上 `Append` 的 carry 回写逻辑后 → **逐元素零差异**。

**对设计的约束**（三条都是硬约束）：
- 分块管线**必须透传 `prevHeadDoc` / `joinedDoc`**，否则每块边界都会多算记录；
- 分块管线**必须走 `Append` 消费 carry**（或等价的 `_lens[_n-1]` 回写），否则末条记录长度错、Message 匹配结果可能翻转；
- `LastAppendCarried` 已由 `Append` 置位（`Services/LogDocument.cs` L894）——加载期每块都要判它：carry 意味着**刚发布的末行 Message 内容可能还会变**，这是 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D3 选项 B「允许末行刷新」的禁区，等价于要求 **D3=A 滞后一条发布**。

> 补充：`Assemble` 的注释写着「曾经的多分片合并（按 BLOCK 分块扫描 + 拼接数组 + 全局 tag 重映射）已随分块方案一起删除」（`Services/LogDocument.cs` L796–L797）。该删除发生在**首次提交之前**（公开 git 历史无细节，`git log -S "BLOCK"` 只命中 `017af89 首次提交`），**回滚原因不可考**。本轮实测证明分块解析本身等价，但「多分片 tag 全局重映射 + 拼接」那套组装逻辑的坑仍未验证——设计时须正面处理 tag 重映射（`Append` 已有 `remap` 实现，见 L927–L942）。

## 6. 异步过滤：可与不可的边界

「异步过滤」有两种含义，代价与可行性截然不同：

### 6.1 列预筛段（Level / PID / TID / 分钟 / Tag / MarkedOnly）——**可行，成本可忽略**

纯列访问，O(N) 但极快。逐块执行 vs 整份执行：**23 ms vs 19 ms**（§4.3），命中完全一致。

### 6.2 Message 关键词段——**语义冲突已解（D9=C′ 增量块扫描）**

`FilterEngine.MsgFilter` 内部有两条路径（`Services/FilterEngine.cs`）：

| 路径 | 适用 | 增量适配性 |
| --- | --- | --- |
| 逐条匹配（零拷贝） | 小文档、少候选 | 适配增量（只扫尾部） |
| **原始字节块扫描**（并行 SIMD） | 大文档、多候选 | 整轮是全文档，但**可拆成增量区间轮**（见下方修正） |

`FilterTail` 里写着「增量固定走逐条匹配：行数少，逐条已是最优，块扫描的整文档语义不适配增量」（L888–L890）——首轮笔记据此判定冲突。

**修正（2026-10-30 复核块扫描实现后）**：「整文档语义」定性过重，两点事实：

1. 扫描上界 `dataEnd = min(cap, Offs[n-1] + Lens[n-1])`（`FilterEngine.cs` L427）本来就是「最后一条**已登记记录**的末尾」而非文件末尾——文档只含已发布前缀时，上界自动收缩到前缀；
2. 每个块的扫描体本身是区间语义：`BlockHitSink` 只计「行首落在本块」的记录、重叠区不重复计行（`FilterEngine.cs` L514–L516）。

因此块扫描缺的不是「前缀语义」，只是把 `MsgFilterBlockScan` 的「全文档一轮」拆成「可指定起止区间的增量一轮」：`counts` / `nonAscii` 数组跨轮持久、每轮只扫 `[上轮末尾, 本轮末尾)`、`VerdictPass` 只对新候选行跑。函数形状级别的改动，不是新造语义。这也是 D9=C′ 的依据。

首轮笔记记录的冲突原文（保留作历史）：加载期逐块过滤 = 增量语义 = 每块都走逐条匹配，而全量路径装了块扫描加速；沿用 `FilterTail` 则加载期走慢路径、体感倒退，改用块扫描则「只有前缀」的文档不成立（尾块未闭合记录）。

## 7. 后台续载：无需新机制

「支持后台继续载入」在分块管线里是**自然延伸**，不需要新架构：

- 现有 `StopWorker()` + `_workerGen` 换代机制已能取消在途加载（`frmMain.cs` L1318–L1325）；
- 分块管线天然可在**任意块边界**暂停 / 恢复 / 取消；
- `Append` 已支持「文档打开后追加新内容」的语义（实时采集在用），分块加载复用同一套即可。

**唯一新增要求**：加载期必须有一个「当前已发布到第几块」的状态，让用户能知道进度、让取消能干净回退（D4=B 整份清空）。

## 8. 待决策项

### D1 分块加载的适用范围 —— **已定：先做本地文件，代理路径后续**

- 理由：代理路径先 spool 后 Build（D5=A 已定，2026-10-24），spool 完成后的本地文件与本地文件走同一条管线即可；本期不碰跨进程重叠。
- 影响：`BuildAsync(path)` 与 `BuildAsync(spool 后本地文件)` 两个入口共用同一条流式管线。

### D2 块长策略 —— **已定：自适应，首屏优先 → 吞吐优先两段式**

- 探针锚点：4 MB 块首块 212 ms / p50 43 ms；16 MB 首块 539 ms；64 MB 首块 1,446 ms。
- 设计待定：首块阈值、稳定期块长上限。倾向首块用小块（≤4 MB）抢首屏，后续放大到 16–64 MB 提吞吐。

### D3 加载期 join 语义与末条发布 —— **已定：A 滞后一条发布**（沿用 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D3）

- 本轮实测强化了这条决策的必要性：`LastAppendCarried` 在每块都可能置位，carry 意味着末行 Message 内容会变 → 若照发（选项 B）需新增「末行刷新」能力，且**会与 §6.2 的 Message 过滤语义冲突**。
- 影响：选 A 不需要新增单行刷新能力，`Append` 现成的 carry 语义正好承载「压一条」；EOF 时必须无条件 flush 最后一条。

### D4 取消语义 —— **已定：B 整份清空**（沿用 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D4）

### D5 代理路径是否边 spool 边解析 —— **已定：A 本期不做**（沿用 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D5）

### D6 加载期功能可用性 —— **已定：A 过滤器/时间过滤随发布增量生效，导出与崩溃面板置灰到完成**（沿用 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D6）

### D7 存量私有设计处置 —— **已定：A 以公开笔记为事实基座**（沿用 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D7）

### D8 小文件是否走同一条流式管线 —— **已定：A 统一流式**（沿用 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D8）

### D9 加载期 Message 过滤走哪条路径 —— **已定：C′ 增量块扫描**（2026-10-30 复核实现后定）

- 原方案 A/B/C 及其矛盾见 §6.2；复核块扫描实现后重新评估，两条事实修正：
  - 在途 change `filter-msgfilter-block-scan` 已 9/9 完成、代码已在 master（提交 8145561，未归档）——分派逻辑已落地，「归档后再动 C 需二次改动分派逻辑」的风险不存在；
  - 「整文档语义」定性过重（见 §6.2 修正），C 的实现代价远低于首轮预估。
- **C′（= C 的落地形态）增量块扫描**：把 `MsgFilterBlockScan` 拆成「可指定字节区间的增量一轮」——`counts` / `nonAscii` 跨轮持久、每轮只扫 `[上轮 dataEnd, 本轮 dataEnd)`、`VerdictPass` 只对新候选行跑；调度参数（每块一轮或每 N 块一轮摊薄轮次开销）在 design 阶段定，块内 SIMD 单线程对 4–16 MB 是毫秒级/词，可摊销。
- 两条硬前提（进 design 的依赖清单）：
  - **D3=A 滞后一条发布**正好保证已发布前缀字节不再变（§5 第三条约束），是增量扫描正确性的前提；
  - 块扫描要 mmap 基指针（`MessageSession` 持 `doc._view`，`LogDocument.cs` L291–L298）——分块管线若走 FileStream 读自有缓冲，须保证加载期文档也能提供 raw 视图（保留整文件映射，或给块扫描加自有缓冲形态）。
- B 降级理由：「加载完成后自动块扫描重过滤」语义上多余——在途 change 的 spec 已保证两条路径命中集合一致，逐条增量结果 = 全量结果，重过滤无消费方；且逐条总量 ≈ 19 s（容量扫描修复前基线）摊在加载期仍是最慢段。B 仅剩「实现最省事」的价值。
- A 保留为回退：若 raw 视图这条硬依赖代价过高，加载期退回 A（不含 Message 关键词的条件增量生效，含 Message 的完成后全量应用）。

### D10 视图行号数组的拼接成本 —— **已定：B 现状 `AppendRows` 整表拼接在小块长下不可接受，须改为可增量追加结构**

- 实测（模拟 `AppendRows` 的 `new int[oldLen+newLen]` 整表拷贝）：

  | 块长 | 块数 | 拼接累计耗时 | 占分块总耗时 |
  | --- | --- | --- | --- |
  | 16 MB | 65 | 345 ms | **2.5%** |
  | 4 MB | 257 | 1,533 ms | **9.0%** |
  | 1 MB | 1,025 | 5,180 ms | **25.9%** |

- 结论：**O(n²) 确认成立**。块长 ≤ 4 MB 时拼接已占近一成，≤ 1 MB 时占四分之一以上。
- 倾向：**B**——把 `LogListView._rows` 从「整表替换」改为可增量追加的结构（如分段块数组 + 段索引，避免每次整表复制）。理由：D2 倾向首块用 ≤ 4 MB 抢首屏，而 4 MB 已达 9%；若为了「不改 `AppendRows`」而被迫用 16 MB 块，首屏从 212 ms 劣化到 539 ms——为了迁就 UI 拼接结构而牺牲首屏不划算。
- 影响面：`LogListView._rows` 是虚拟列表的模型索引数组，`SetRows` / `AppendRows` / `CaptureAnchor` / `PinAnchor` / `ModelRowOf` / `DocRow` 均依赖它，改结构需一并覆盖这些访问点（`Controls/LogListView.cs`）。
- 备选：若改动面被认为过大，替代方案是「块长下限设 16 MB + 首屏单独用一个 4 MB 首块」——首屏快、稳态用大块把拼接占比压回 2.5%，且不改 `LogListView`。此备选需维护者在 D2 一并拍板。

### D11 加载期视图跟随语义 —— **已定：A 严格按「跟随尾部」开关**（沿用 `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` D10，2026-10-30）

- 与 D9 的耦合注记更新：D9=C′ 下前缀视图的行号会随每块过滤结果变化；开关默认关闭（视图停在顶部）时该影响弱化，锚点重钉需求在 design 阶段按 D11=A 复核即可。

### D12 验收线 —— **已定：按建议值写进 specs 场景**（2026-10-30）

- 建议值（写进 specs 场景）：
  - **首屏**：本地 1 GB、冷缓存下 ≤ 0.5 s 出现首批行（本轮热缓存实测 212 ms，留冷读余量）。
  - **等价性**：同一文件分块加载与整份加载的记录集合**逐行完全一致**（行数、offset、length、级别列）——本轮已实测三种块长零差异，可直接作为 CI 可断言的不变量。
  - **总时长**：不差于现状（≤ 现状 × 1.05，本轮实测 1.02–1.05 恰在边界）。
  - **内存**：峰值 ≤ 1.0 GB（4 MB 块实测 663 MB）。
  - **取消**：整份清空并回到空文档状态（与 D4=B 一致）。
- 待补：4 MB 块 × 257 块的 `AppendRows` 拼接占比需复测（D10 保留条件）。

## 9. 下一步

- 已定：D1–D12 全部（D9=C′、D11=A、D12 按建议值，2026-10-30）。
- **可走 `/opsx-propose` 建 change**（proposal + specs delta + design + tasks）；design 阶段待定项：D2 具体阈值（首块阈值、稳态块长上限）、C′ 调度粒度（每块/每 N 块一轮）、raw 视图的实现形态（整文件映射 vs 块扫描自有缓冲形态）。
- 与在途 change `filter-msgfilter-block-scan` 的协同：其分派逻辑已在 master（未归档），C′ 落点是其 `MsgFilterBlockScan` 的函数形状拆分；它先归档也不构成二次改动。
- 遗留待维护者决策：重启卡 B1（已归档，`openspec/notes/archive/2026-10-30-BACKLOG-BIGFILE-LOAD-PERF.md`）的总时长目标 ≤ 10 s 是否下调（本轮实测 12.9–13.1 s，见 §10）。
- 编码只在 change 的 apply 阶段发生（AGENTS.md §2.2 阶段门禁；apply 在专属 worktree 实施）。
- 本笔记为探索产物；change 归档时同批移入 `openspec/notes/archive/`。

## 10. 与既有笔记的关系

| 笔记 | 关系 |
| --- | --- |
| `openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` | **本笔记是它的续篇**：D1/D3/D4/D5/D6/D7/D8/D10 沿用其结论（D5–D8 本轮补记为已定），修正其「一次读全文件」的过时描述，补充 §5 等价性陷阱、§6 异步过滤边界、D9 新议题 |
| `openspec/notes/archive/2026-10-30-BACKLOG-BIGFILE-LOAD-PERF.md`（重启卡 B1，已归档） | **命题已由 change `chunked-progressive-load` 承接**：其 22 条决策方向与本笔记一致；其目标「首屏 ≤ 0.15 s / 全量 ≤ 10 s / 内存 ≤ 2.0 GB」中，**首屏与内存本轮实测已可达成**（212 ms / 663 MB），总时长 12.9 s 未达 10 s 目标——需确认该目标是否下调 |
| `BIDIRECTIONAL-PROGRESSIVE-FILTER.md` | **不冲突**：其 §7.1 判据 2「C 要造的 UI，流式加载也要造」在本轮得到印证——D11 的锚点/跟随语义是同一套契约 |
| `UTF8-BYTE-SIMD-SEARCH.md` | **直接相关**：D9=C 的实现依赖该路线的块扫描能力，且需与在途 change 协同 |
| `filter-msgfilter-block-scan`（在途 change） | **已协同**：该 change 已实现未归档（9/9，master 8145561）；D9=C′ 落点在其 `MsgFilterBlockScan` 的函数形状拆分 |
