# 探索结论：过滤加速 —— 零拷贝 MsgFilter + 显式大小写不敏感搜索

- 状态：**探索完成，实测已复核，方案已定（D1 目标① / D2），可建 change**（未建 OpenSpec change，未改动仓库代码）
- 日期：2026-10-24 首版（探索笔记《过滤加速缓存结构》）→ 2026-10-25 实测复核 → 2026-10-25 拆分为本篇（过滤加速）
- 关联提交：无
- 验收结论：不适用
- 探针：`.tmp-test/BenchLog/`（`FilterSplit.cs` / `StructAndLeak.cs`），已 git-ignore，不入库
- **本篇只覆盖过滤加速（change A）**。缓存加速（sidecar）见 `SIDECAR-CACHE-INDEX.md`；两者共用本文「实测基线」节。

## 1. 目标与范围

**问题**：每次修改 message 过滤条件，`ApplyFilter` 全量重跑，1 GB 文件上段 2 耗时 2.3–2.9 s。

**本 change 做什么**：把段 2 的「每行 new byte[] + mmap ReadArray 拷贝」换成「mmap 指针直接切 Span」，并解决随之而来的大小写不敏感搜索问题。

**本 change 不做什么**：
- 不改列布局（SoA 9 列保持不变，见 §3 结论 2）
- 不做落盘缓存（属 `SIDECAR-CACHE-INDEX.md`）
- 不做 SIMD 块扫描（属 `UTF8-BYTE-SIMD-SEARCH.md`，已定位为后续路线）

## 2. 现状（代码事实）

### 过滤两段流水（`Services/FilterEngine.cs` `ApplyFilter`）

```
ApplyFilter（每次改过滤条件全量重跑）
    |
    +- 1 预筛：O(n) 逐行碰 lvl/pid/tid/min/tagId 五个数组
    |      命中条件为「列访问」，纯内存读
    |
    +- 2 MsgFilter：对每条候选行执行
           doc.MessageBytes(r) = 每行 new byte[] 分配 + _view.ReadArray 拷贝
           → 忽略大小写时还要拷进 scratch 就地折叠
           → MatchAny 对每个关键词跑 hay.IndexOf(needle)（Span<byte>.IndexOf 已向量化）
```

`LogDocument` 是 `MemoryMappedFile` + `CreateViewAccessor(0, 0)`：文本本体留在磁盘，内存只有 9 条定长列（`_offs` / `_lens` / `_moff` / `_min` / `_pid` / `_tid` / `_lvl` / `_tagId` / `_flags`）。

### 瓶颈定位（结论，来自实测）

- 段 1 预筛**只占全量过滤的 4–9%**（1 GB 上 98–291 ms）。
- 段 2 是**纯 CPU 瓶颈**：2.3–2.9 s，其中每行 `new byte[]` 分配 + `ReadArray` 拷贝占绝大部分。
- 原文「瓶颈在段 2 的每行分配+拷贝，不在段 1 的布局」判断**实测完全正确**。

## 3. 实测基线（2026-10-25）

样本：`testdata/big_test.log`，1.02 GB，15,823,120 行，145 tag，热缓存，三次运行取区间。300 MB 样本（4,635,207 行）同步验证。

| 项 | 1 GB 耗时 | 300 MB 耗时 | 命中数（1 GB） |
| --- | --- | --- | --- |
| 段 1 预筛 empty spec（全量候选） | 98–130 ms | 40 ms | 15,823,120 |
| 段 1 窄过滤 minutes=[6] | 94–200 ms | 25 ms | 255,900 |
| 段 1 窄过滤 levels=[5] | 203–241 ms | 61 ms | 1,892,273 |
| 段 1 tag=[AndroidRuntime] | 206–291 ms | 61 ms | 86,143 |
| **段 2 现状 case-sensitive** | **1791–2089 ms** | 550 ms | 362,871 |
| **段 2 现状 case-insensitive（UI 默认）** | **2284–2945 ms** | 733 ms | 362,895 |
| 变体 F：复用缓冲（去每行分配，留拷贝） | 1102–1387 ms | 388 ms | 362,871 |
| **变体 G：mmap 指针切 Span（零分配零拷贝）** | **453–692 ms** | **129 ms** | 362,871 |
| 变体 F'：复用缓冲 + 就地 ASCII 折叠 | 1219–1534 ms | 345 ms | 362,895 |
| **变体 G'：零拷贝 + 朴素字节级忽略大小写** | **945–1270 ms** | **267 ms** | 362,895 |
| 变体 H：全文顺序字节扫（SIMD 路线地板） | 149–160 ms | 49 ms | 362,871 |

**结论 1 · 零拷贝路线成立**：变体 G **不依赖任何布局改动**——只需现有 `_offs` / `_moff` / `_lens` 三列（msgStart = offs+moff，msgEnd = offs+lens，可推导），命中数与现状**逐个一致**（362,871）。case-sensitive 路径 1791–2089 ms → 453–692 ms = **3–4.9×**。

**结论 2 · AoS 结构体化对过滤加速无独立贡献**：段 1 只占 4–9%，即使把它优化到零，总收益上限也只有 9%。且窄过滤（只筛分钟）时 AoS 要读全 32 字节/行，内存流量 15.8 MB → 506 MB（**32 倍**，非原文的 16 倍），绝对代价 ~50 ms 但方向是变差。→ **本 change 不动列布局**。

> 补充：`Unsafe.SizeOf` 实测结构体真实字节 —— 现状 9 列直接打包 40（漏 padding）、长者在前重排 32、紧凑 5 字段 24、提案 5 字段 + 补齐 4 字段 48。原文「5 字段与 9 字段同为 32 字节 → 补齐 lvl/pid/tid/flags 是免费的」论证**不成立**。本 change 不涉及结构体，此处仅作记录以免后续重复论证。

**结论 3 · 附带收益面**：`FilterTail`（实时采集增量过滤，每秒调用）同样走 MsgFilter → 零拷贝改造对该路径同样受益。

## 4. 大小写不敏感：不能靠「needle 小写化」蒙混

### 问题

UI 默认 message 过滤**忽略大小写**（`_ckMsgCase` 默认不勾）。现状实现：拷进 scratch 缓冲 → 就地小写化（ASCII）→ 搜小写 needle；含非 ASCII 的行走 Unicode 回退。

零拷贝后指针指向 mmap 内存，**不能就地改写** → 必须显式实现大小写不敏感搜索。

### 关键实测：原推荐的「预筛用小写 needle」会漏命中

| needle | 正确忽略大小写命中 | 预筛(小写) | 预筛(原样) |
| --- | --- | --- | --- |
| `error` | 362,895 | 362,871（漏 24） | 362,871 |
| `Error` | 362,895 | 362,871（漏 24） | 24 |
| `Exception` | 120,545 | **36（漏 120,509 / 99.97%）** | 120,343 |
| `WARNING` | 0 | 0 | 0 |

原因：文件里是 `Exception`（首字母大写），小写 needle 的字节串**根本不在原始字节里**。「小写 needle 覆盖所有大小写命中」不成立。

这条同时证伪了 `UTF8-BYTE-SIMD-SEARCH.md` §4.2 的原推荐（已按 D4 修订该笔记）。

### 已定方案

**显式字节级大小写不敏感搜索**：比较时两侧 OR 0x20 后逐字节比对（等价于现状「折叠 haystack」的语义，但不物化折叠副本）。

- 朴素实现实测（变体 G'）：945–1270 ms，已优于现状 CI 2284–2945 ms；SIMD 化后仍有空间。
- **排除**：大写/小写双模式（漏混合大小写 `eRRor` → 与现状语义不符）；保留拷贝+就地折叠（放弃零拷贝潜力）。
- **不可省略**：非 ASCII 行仍须走 Unicode 回退路径（`ß/ẞ`、`İ/ı` 等长宽变化规则不能破坏）→ 正确性测试必须覆盖这些字符。

## 5. 实施要点（design 参考）

- **`LogDocument` 需暴露只读 Span 访问器**：现状 `MessageBytes(row)` 返回新分配的 `byte[]`，无法零拷贝。需新增返回 `ReadOnlySpan<byte>` 的路径（指向 mmap 内存）。
- **视图生命周期**：`_view` 是 private，且 `OpenMap()` 在 `Append` 时会换 view（`_retired` 机制）→ **持指针读期间换 view = 悬垂指针**。当前靠 `frmMain` 的 `_docBusy` 串行化（`ApplyFilter` / `ApplyIncremental` 与 `Append` / `Reload` 互斥）保证不并发，design 需正面处理这一约束。
- **`Capacity` 与 `PointerOffset`**：数据起点是 `ptr + accessor.PointerOffset`，有效长度是 `Capacity` —— 与仓库既有 `LineIndexProbe` 的 `AcquirePointer` 用法一致。
- **多关键词**：`MatchAny` 的 and/or 语义保持不变；每个关键词仍走 `Span<byte>.IndexOf`（.NET 8+ 已向量化），不引入 `SearchValues`（字节域无多子串重载，见 `UTF8-BYTE-SIMD-SEARCH.md` §4.1）。
- **进度与取消**：`BATCH` 分批、进度上报、`ct.ThrowIfCancellationRequested()` 现状均非瓶颈，不动。

## 6. 验收线（建议）

- **正确性**：同一输入 + 同一 `FilterSpec`，新旧路径**行号集合完全一致**（含空 needle、and/or、忽略大小写、中文、混合大小写、`ß/ẞ`/`İ/ı` 等 Unicode 回退字符）。
- **性能**（1 GB / 1582 万行，热缓存）：case-sensitive 段 2 ≤ 700 ms；case-insensitive 段 2 ≤ 1300 ms（即不劣于变体 G' 实测上限）。300 MB 样本同步验证。
- **不改列布局**：本 change 后 `LogDocument` 的 9 条列数组与公开列视图 API 保持不变（`SIDECAR-CACHE-INDEX.md` 依赖此前提）。

## 7. 决策记录

| 项 | 结论 | 日期 |
| --- | --- | --- |
| D1 目标 | 目标①「过滤加速」+ 目标②「缓存加速」两项都做；本 change 只做目标① | 2026-10-25 |
| D2 大小写不敏感 | 方案 A：显式字节级大小写不敏感搜索（两侧 OR 0x20 比较） | 2026-10-25 |
| D4 关联笔记 | 修订 `UTF8-BYTE-SIMD-SEARCH.md`（已执行） | 2026-10-25 |
| 实施顺序 | 先本 change（改动小、语义零变化），后缓存 change | 2026-10-25 |
| 拆分 | 两个 change 独立走 propose → apply → archive | 2026-10-25 |

## 8. 下一步

- **本 change 无阻塞决策项，可直接 `/opsx:propose`**。建议 change 名：`filter-msgfilter-zero-copy`。
- **落地顺序（2026-10-25 复核确定）**：本 change（A 零拷贝）→ 缓存 sidecar（`SIDECAR-CACHE-INDEX.md`）→ UTF-8 块扫描 SIMD（`UTF8-BYTE-SIMD-SEARCH.md`）。**双向渐进显示方向已搁置**（`BIDIRECTIONAL-PROGRESSIVE-FILTER.md` §7：其收益全在感知延迟、不减少总工作量，现状典型规模已是几百毫秒），不排在其中。
- 范围：`Services/FilterEngine.cs`（MsgFilter 路径）+ `Services/LogDocument.cs`（暴露只读 Span 访问器）+ 回归测试。**不动列布局**，与 `STREAMING-FILE-LOAD.md` 的 `ScanPart` 改造无冲突。
- 本笔记为探索产物；本 change 归档时同批移入 `openspec/notes/archive/`（与 `SIDECAR-CACHE-INDEX.md` 分批归档，各跟各的 change）。
