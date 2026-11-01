# 探索结论：缓存加速 —— 落盘 sidecar 索引（重开同一文件跳过解析）

- 状态：**探索完成，实测已复核；D3–D12、D15 已于 2026-10-30 拍板；D13 待定；D5-1 / 结论 2 / D14 因「删除 join」作废**（未建 OpenSpec change，未改动仓库代码）
- 日期：2026-10-24 首版（探索笔记《过滤加速缓存结构》）→ 2026-10-25 实测复核 → 2026-10-25 拆分为本篇（缓存加速）→ **2026-10-30 代码事实复核 + 补 4 个决策项 → 2026-10-30 维护者拍板 D4–D10 → 同日拍板 D5-1/D6-1/D6-2/D7-1/D11/D12/D15 → 同日决定删除 join（另立笔记，§10）**
- 关联提交：无
- 验收结论：不适用
- 探针：`.tmp-test/BenchLog/`（`SidecarFacts.cs` / `SidecarLoad.cs`），已 git-ignore，不入库
- **本篇只覆盖缓存加速（change B）**。过滤加速见《过滤加速·零拷贝》探索笔记（已随 `filter-msgfilter-zero-copy` 归档，现于 `openspec/notes/archive/2026-10-28-FILTER-ACCEL-ZERO-COPY.md`）；两者共用该笔记的「实测基线」节。
- **2026-10-30 复核摘要**：① §2 代码事实有 1 处过期（`FinalizeTs` 已随年份机制删除）；② 在途 change `chunked-progressive-load` 四件套已齐备（apply 未开始），改变了本方向的先后与分工 → D8；③ 发现既有维护者决策「文件关闭即清理」与 sidecar 前提**直接冲突** → D4；④ 落盘格式/压缩/读回方式与验收线 → D9、D10。
- **2026-10-30 后续（同日）**：维护者决定**删除 join（续行合并）机制**，索引改按自然行 —— 见 `openspec/notes/REMOVE-JOIN-MERGE.md`。对本篇的连带影响见 **§10**（D5-1 / 结论 2 / D14 作废，执行顺序调整）。

## 1. 目标与范围

**问题**：重开一个 1 GB 日志文件，`LogDocument.Build` 需 14.8–18.5 s（解析 + 九列物化 + 终算），期间界面无内容。

**本 change 做什么**：把建好的列索引落盘成 sidecar 文件，重开同一文件时直接加载索引、跳过解析。

**本 change 不做什么**：
- 不做过滤加速（属《过滤加速·零拷贝》探索笔记，已随 `filter-msgfilter-zero-copy` 归档）
- 不做流式加载 / 首屏快（属 `openspec/notes/CHUNKED-PROGRESSIVE-LOAD.md`，D1–D12 已拍板，已转入在途 change `chunked-progressive-load`）
- **不改列布局**（见 §3 结论 1：sidecar 沿用现状 SoA 9 段）
- **不改加载管线形态**（分块/流式由 `chunked-progressive-load` 承接，见 D8）
- **不做 UI 改动**（D12：命中静默，不新增提示、不新增开关）
- **不做删除 join**（属独立命题，见 `openspec/notes/REMOVE-JOIN-MERGE.md`；但 join 删除后本篇的 D5-1 / 结论 2 / D14 作废，见 §10）

## 2. 现状（代码事实，2026-10-30 复核）

`LogDocument.DoBuild`（`Services/LogDocument.cs` L396–L408）：

```
DoBuild(join, progress, ct)
  ├─ Size = new FileInfo(SourcePath).Length      L398
  ├─ OpenMap()                                   L399   ← 建 mmap 视图（文本本体）
  ├─ ScanRange(SourcePath, 0, Size, join, …)     L401   ← 区间扫描：整段读入 byte[] → 逐行 ParseLine → 写 9 个 List
  ├─ _prevHeadAtEnd / _joinedAtEnd = part. …      L404–L405
  ├─ Assemble(part, progress)                    L406   ← 字典全局化 + ToArray 物化 + SetColumns + 崩溃落表
  └─ IndexedSize = Size                          L407
```

> ⚠️ **本节 2026-10-30 修正**：原笔记写「`Assemble`（`FinalizeTs` 终算 → …）」——`FinalizeTs` **已随年份机制删除**（`docs/UT-AUDIT.md` L57/L125 记载，2026-10-24）。当前 `Assemble`（L798–L839）只做 tag/exc/pkg 字典全局化 + `SetColumns` + 崩溃落表，**无终算阶段**。

> ⚠️ **另一处口径**：原笔记与《流式加载》归档笔记都称 `ScanRange`「一次读全文件」。精确说法是——`ScanRange` **把整个扫描区间读进一个 `byte[]`**（L519–L528，`new byte[rangeSize]`），再在内存中逐行解析。**区间化能力本身早已具备**（`start`/`end` 任意可传、两端自动对齐行边界），整份 Build 时区间 = 全文件，故表现为「读全文件」。

1 GB 文件实测分解（`openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` 探针口径）：逐行 `ParseLine` 6–9 s + 九列物化 4–7 s + 组装 2–4 s；本地 SSD 冷读仅 2.5 s。→ **纯 CPU 瓶颈，可被缓存跳过**。

### 2.1 列与字典的内存布局（= sidecar 的落盘对象）

`LogDocument` 字段声明（L53–L61），9 段 SoA，元素类型逐项核实：

| 段 | 字段 | 类型 | 只读视图 |
| --- | --- | --- | --- |
| offs | `_offs` | `long[]` | `Offs`（L96） |
| lens | `_lens` | `int[]` | `Lens` |
| moff | `_moff` | `int[]` | `Moff` |
| min | `_min` | `byte[]` | `Minute`（哨兵 `MINUTE_NONE = 255`，L24） |
| pid | `_pid` | `int[]` | `Pid` |
| tid | `_tid` | `int[]` | `Tid` |
| lvl | `_lvl` | `byte[]` | `Lvl` |
| tagId | `_tagId` | `int[]` | `TagId` |
| flags | `_flags` | `byte[]` | `Flags`（`FLAG_MULTILINE = 0x01`，L22） |

- 只读视图均为 `ReadOnlySpan<T>`（L96–L105，`AsSpan(0, _n)`）；`_n`（有效行数）/ `_cap`（容量）L62–L63。
- 字典：`Tags`（`List<string>` L66）+ `TagIndex`（L67）、`ExcTypes`（L76/L77）、`Pkgs`（L78/L79）。扫描期 tag 以 UTF-8 `byte[]` 存于局部 `tagList`（L453），`Assemble` 才解码成 `string`（L818）→ **sidecar 按 `string` 落盘即可**。
- 崩溃事件：`Models/CrashEvent.cs` L28–L43 是 **`struct`**（值类型），字段 `Row`（文档行号，0 基）/ `ExcId` / `PkgId` / `Pid` / `Tid` / `Kind`（`CrashKind`：Java=0/Native=1/Anr=2/Tombstone=3）/ `LineCount`；容器 `LogDocument._crashes`（L71），对外 `Crashes`（L73）。**时间文本不存**，展示侧现取 `HeaderBytes(Row)`。

### 2.2 文本本体、join 与增量路径

- **列与文本完全分离**：类注释 L16–L17 明写「文本本体始终留在磁盘（mmap），只有定长列进入内存」。mmap 成员 L82–L85，`OpenMap()` L147–L171（`MemoryMappedFile.CreateFromFile` + `CreateViewAccessor`）。
  → **sidecar 命中后仍必须 `OpenMap()` 源文件**：文本不在 sidecar 里。
- **join 语义影响行号/记录边界**：详见 §2.5。
- **增量路径**：`public string Reload(bool? join = null, …)` L842（返回 `"unchanged"` / `"rebuilt"` / `"appended"`）；`string Append(long newSize, …)` L879（**private**，外部只能经 `Reload` 触达）；`TrimPartial` L1011–L1027。→ sidecar 只在静态文件场景有意义（D7）。

### 2.3 入口与降级

- 打开入口：`frmMain.StartIndex(string path)` L950（`async void`）→ 直接分支 L1009–L1017 调 `LogDocument.BuildAsync(path, _join, progress, ct)`；代理分支 L1062 → spool 后 L1099 同样 `BuildAsync(result.SpoolFilePath, _join, …)`；adb 采集分支 L2230 + 增量 L2256 `doc.Reload(join: _join, …)`。
- **切「续行合并」菜单**（L165）→ `_join = …; StartIndex(_doc.Path)` → **整份重建** → 同一文件会先后以 join=true/false 各建一次索引。
- **现状失败路径是弹错误框**（L1045–L1048 `ShowError`），**不是静默降级**。→ sidecar 的「写失败/加载失败静默走原路径」需要**新分支**（§7 降级验收线）。

### 2.4 可复用 / 需新建

| 能力 | 现状 |
| --- | --- |
| exe 同级落点 | `AppSettings.cs` L30–L31 `settings.json`、`CommandStore.cs` L37–L38、`FavoritesStore.cs` L46–L47、`StartupLog.cs` L14–L15 `Log\startup.log`、`FileProxyService.cs` L390–L394 `SpoolPaths.DefaultRoot = temp\logcat-proxy` |
| 「不可写则吞异常降级」范式 | `AppSettings.SaveTo` L162–L175（catch → `StartupLog.Write`）；`StartupLog.Write` L17–L27（catch 空吞，注释「诊断日志决不能影响主流程」） |
| 目录级管理 / 活性锁 | `SpoolPaths.NewSessionDir` L400–L424（`FileShare.Delete` 句柄锁）、`IsDirAlive` L437+、`CleanupStaleSpoolDirs` L303–L320（枚举 → 活性判定 → 删除 → 计数） |
| **二进制读写器** | **无**——全仓库零 `BinaryWriter`/`BinaryReader`，无 `.idx`/`.cache` 读写器，无 Deflate/GZip/Brotli 用法 → sidecar 读写器**需从零实现** |
| 可抄的范式 | mmap 建视图（L147–L171）、`FileStream` 落盘（`FileProxyService.cs` L236）、JSON 持久化模板（`AppSettings` L24–L28/L162–L175） |

### 2.5 join（续行合并）语义速查

> ⚠️ **2026-10-30 起本节转为历史参考**：维护者已决定**删除 join 机制**（见 §10 与 `openspec/notes/REMOVE-JOIN-MERGE.md`）。本节保留用于理解「为什么 sidecar 曾需要 join 维度」，以及删 join 时**哪些语义必须保留**（空行/横幅「不索引」、`LooksLikeNewRecord` 的去留）。

**一句话**：索引里的「记录（row）」是**逻辑事件**，不是一个物理行。一个事件在文件里占多行时，`join=true` 把它们**折叠成一条记录**，`join=false` 则每行各自成一条。

**判「这一行是不是新记录」**：`LogParser.LooksLikeNewRecord`（`Services/LogParser.cs` L383–L396）——返回 true = 新记录，false = 续行（折叠进上一条）。

| 判为「新记录」的特征 | 例子 | 代码 |
| --- | --- | --- |
| 空行 | `""` | L386 |
| 横幅行 | `--------- beginning of main` | L387（`IsBanner`） |
| 以 `[` 开头（long 格式） | `[ 09-23 18:06:48.123  1: 2 I/Tag ] msg` | L390 |
| 9 连横线开头 | `---------1` | L391（`DASHES`） |
| `mm-dd hh:mm:ss.` 位置模式（threadtime） | `09-23 18:06:48.123  1  2 I Tag: m` | L392 |
| `yyyy-mm-dd hh:mm:ss.` 位置模式（ymd） | `2026-09-23 18:06:48.123 …` | L393 |
| `L/Tag:` 简写（brief） | `I/Tag: msg` | L394 |

| 判为「续行」（被折叠） | 例子 |
| --- | --- |
| 以空格 / TAB 开头 | `    at com.demo.Main(X.java:12)` |
| 不符合任何格式前缀的普通文本 | `plain second line`（既有测试 `LogDocumentTests.cs` L237 记录了这一行为） |

**合并的四个前提**（`LogDocument.cs` L582）：

```
join && prevHead && joined < MAX_JOIN(200) && !LooksLikeNewRecord(line)
```

- `join`：开关本身（UI 菜单「续行合并」→ `_join` → 整份重建，L165）。
- `prevHead`：**上一条记录有资格接收续行** —— 由 L656 决定：`pr.WithinMs >= 0 || pr.Level > 0`（上一条解析出了时间戳，或级别 > 0）。一个「无时间戳、无级别」的普通行**不能**接收续行。
- `joined < MAX_JOIN`：单条记录最多合并 200 行续行（`MAX_JOIN = 200`，L21）。
- `!LooksLikeNewRecord`：本行不是新记录。

**合并做了什么**（L586–L588）：

- `lens[prev] = currentOff + lineLen - offs[prev]` → 把上一条记录的**长度**延长到覆盖本行（**记录的内容边界变了**）
- `flags[prev] |= FLAG_MULTILINE` → 标记该记录为多行
- `joinCounts[prev]++` → 最终成为崩溃事件的 `LineCount`（L679 / L703：`LineCount = joinCounts[Row] + 1`）

**两种「不索引」的特例，语义相反**（最易记错处）：

| 行类型 | 是否索引 | 是否打断合并 | 代码 |
| --- | --- | --- | --- |
| 空行 | 否 | **是**（`prev` / `prevHead` / `joined` 全部重置） | L557–L569 |
| 横幅行 | 否 | **否**（崩溃堆栈中间的横幅不切断堆栈） | L575–L579 |

**为什么「同一个文件」在两种 join 下结果不同**（= 结论 2 的直观解释）：

```
文件物理行（堆栈行带缩进、无独立时间戳）：
  L1  12-24 10:00:01.123 E AndroidRuntime: FATAL EXCEPTION: main
  L2      at com.demo.Main(X.java:12)          <- 缩进 -> 续行

join = false                        join = true
  记录 0 = L1                        记录 0 = L1 + L2（一条记录）
  记录 1 = L2                          lens 覆盖两行 / flags=MULTILINE
  → 2 条记录                           LineCount = 2
```

→ join 改变的是**记录边界（lens）与记录条数**，于是**行号（offs 下标）完全不同** → 崩溃事件的 `Row` 指向不同记录 → 同一份 sidecar 不能同时服务两种 join → **键必须含 join（D5-1）**。

> 补充两点容易踩的事实：
> - **threadtime 格式下 join 基本不起作用**：每行都带时间戳 → 每行都是「新记录」→ 各成一条。join 真正起作用的场景是 brief / long 格式、以及缩进或无前缀的续行（崩溃堆栈常见形态）。
> - **崩溃识别与 join 的关系是双向的**：a 轨崩溃头识别「与 Join 无关」（L633–L635 注释明写）；但 c 轨批末补扫（L685–L706）要靠 `FLAG_MULTILINE` 找「崩溃头缩进并入父记录」的形态 → join=true 才会产生这类事件。

## 3. 实测基线

样本：`testdata/big_test.log`，1.02 GB，15,823,120 行，145 tag，热缓存。

### 3.1 sidecar 落盘与加载（2026-10-25）

| 环节 | SoA 9 段布局 | AoS 定长记录 |
| --- | --- | --- |
| 写 sidecar | 1435 ms | 989 ms |
| sidecar 体积 | **0.457 GB** | 0.472 GB |
| **加载 sidecar（= 缓存命中的重开）** | **459 ms** | 509 ms |
| 相对首次 Build（14.8 s） | **32× 提速** | 29× 提速 |

- 加载后立即做零拷贝过滤：915 ms → 验证 sidecar 命中后仍走零拷贝路径可行。
- sidecar 命中的重开**仍需 mmap 源文件**（文本本体不在 sidecar 里），页读按需发生。

**结论 1 · sidecar 落盘应沿用现状 SoA 9 段，不改 AoS**

原文建议「B 的落盘格式可直接采用 A 的结构体（AoS 定长记录）」。**实测推翻**：

| | SoA 9 段（现状布局） | AoS 定长记录 |
| --- | --- | --- |
| sidecar 体积 | **0.457 GB** | 0.472 GB |
| **加载（缓存命中的重开）** | **459 ms** | 509 ms |
| 加载后内存布局 | **与现状完全相同** | 变成 AoS，需重建成结构体数组 |
| 与过滤加速 change 的关系 | **无冲突** | **冲突**：内存布局变了，段 1 预筛要重写 |

- SoA 加载更快（459 vs 509 ms）且体积更小：SoA 读回后**直接就是内存里那 9 个数组**（零转换）；AoS 还要把连续记录重建成结构体数组（多一次 0.47 GB 拷贝）。
- **关键收益是解耦**：内存布局不变 → 与过滤加速 change **互不牵制**，可独立实施、独立验证、独立回滚。
- 代价：sidecar 是「9 段变长段」而非「定长记录流」，失去「流式追加友好」。但 sidecar 是**一次性整体写、整体读**的离线文件，不需要流式追加 → 该代价不成立。
- 原文「AoS 是 B 的价值所在」随之作废：AoS 唯一还站得住的场景是**流式加载时的逐块发布**（`openspec/notes/archive/2026-10-30-STREAMING-FILE-LOAD.md` 摩擦点 1、2），与 sidecar 无关。

### 3.2 基线更新：整份 Build vs 分块加载（2026-10-30 探针，来源 `openspec/notes/CHUNKED-PROGRESSIVE-LOAD.md`）

| 路径 | 首屏 | 全量就绪 | 内存峰值 |
| --- | --- | --- | --- |
| 现状整份 Build | ≈ 全量（无增量发布） | 12.2–15.7 s | 3.6–4.6 GB |
| 分块加载（`chunked-progressive-load`，4 MB 首块 / 32 MB 稳态） | **212 ms** | 12.9–13.1 s（持平略慢 2–5%） | **663 MB** |
| sidecar 命中 | 459 ms（一次到位，非渐进） | **459 ms** | 9 列 ≈ 0.46 GB + mmap 源文件 |

→ sidecar 的价值从「唯一解」变为「**总时长的解**」：分块买首屏与内存，sidecar 买「重开即全量就绪」。两者互补、不互斥（D8）。

### 3.3 sidecar 需落盘的数据量与体积要素

1 GB 文件实测：

| 要素 | 数量 | 体积占比 |
| --- | --- | --- |
| 行记录（9 列 SoA） | 15,823,120 | **0.457 GB（源文件的 47.2%）** |
| tag 字典 | 145 项 | 可忽略 |
| 异常类型字典 | 13 项 | 可忽略 |
| 包名字典 | 17 项 | 可忽略 |
| 崩溃事件（稀疏） | 592 项 | 可忽略 |

**结论 2 · sidecar 必须连带记录 join 模式**：崩溃事件用「文档行号 + 字典 id」定位，而 join=true/false 的行号语义不同（join=true 会把续行合并进上一条记录）→ 同一源文件在两种 join 下的索引不可混用（直观解释见 §2.5）。

> ⚠️ **2026-10-30 连带失效**：维护者决定删除 join 机制 → **本结论作废**（不再有 join 维度，sidecar 无需记录 join 模式）。详见 §10 与 `openspec/notes/REMOVE-JOIN-MERGE.md`。

**结论 3（2026-10-30 推导）· sidecar 体积可精确预测 = 行数 × 31 B**

9 列定宽合计 8 + 4 + 4 + 1 + 4 + 4 + 1 + 4 + 1 = **31 B/行**。实测校验：15,823,120 行 × 31 B = 490,516,720 B = **0.4568 GiB**，与实测 sidecar 体积 0.457 GB 一致（字典与头部为可忽略量级）。

→ 由此可得两条可直接使用的推论：
- **「是否装得下 1 GB 上限」可在建索引时直接判定**（行数已知 → 体积已知），无需试写再回滚；1 GiB ÷ 31 B ≈ **34.6 M 行**为单文件上限。
- **体积与 join 模式相关**：join=true 的行数少于 join=false（续行被并入上一条），故两份 join 缓存体积不等。

## 4. 已定：落点 = exe 同级

- **结论（2026-10-25，维护者）**：sidecar 缓存放 **exe 同级**。
- **与现有惯例一致**：exe 同级已有 `settings.json`、`commands.json`、`favorites.json`、`Log/startup.log`、`temp/logcat-proxy/`；`SpoolPaths` 文档明写「exe 目录可写是既有前提」。
- **实测支撑**：SMB 实测 7 MB/s → 缓存若放共享目录，读回 0.457 GB ≈ **71 s**；而 SMB 冷读原文件 1 GB ≈ 146 s + 解析 13–18 s ≈ 161 s。**「亚秒级重开」只在缓存落本地盘（exe 同级）时成立** → 落点决策与收益承诺绑定，不可分开。
- **写失败降级**：exe 目录不可写（权限 / 只读部署 / U 盘写保护）时须静默降级为「无缓存、走原路径」，不得影响打开流程。`AppSettings.SaveTo` 与 `StartupLog.Write` 已有同类先例：异常被吞 + 留痕。
- **目录形态**：子目录 `cache/`（D11，2026-10-30）。

## 5. 决策项

### 5.1 已定（2026-10-30 维护者拍板）

| 编号 | 结论 | 关键理由 / 口径 |
| --- | --- | --- |
| **D4** | **A** —— 区分「文件本体」与「派生索引」：2026-10-22 的「文件关闭即清理」约束**只针对被缓存的文件本体副本**（当时的 `network-file-local-cache` 方向）；sidecar 是从文件派生的索引，**不在该约束对象内**，按 D6 的 LRU 保留 | 该约束原文见 `openspec/notes/archive/2026-10-29-RESTART-BACKLOG-CLOSED.md` L25（明写「对旁车索引缓存仍有约束力」）。口径澄清：sidecar 的价值前提就是「关掉再重开仍命中」，照字面执行会使功能归零。「不做手动清理按钮 / 目录写死 / 不加开关 / 静态文件不需增量同步」**继续适用** |
| **D5** | **A** —— 按源文件路径派生名 + 文件身份校验（size + mtime + 首尾哈希，任一失效即重建） | 路径哈希 + 可读前缀 + size/mtime 摘要：既能区分同目录不同文件，又不在文件名里暴露本机绝对路径（AGENTS §一.2）。B（内容哈希）与「打开要快」自相矛盾，排除；C（只 size+mtime）需首尾哈希兜底 |
| **D5-1** | ~~**join 模式进键**~~ —— **作废**（2026-10-30 删除 join 后不再有 join 维度，见 §10） | 原决策理由：切「续行合并」会整份重建（`frmMain` L165）；若键不含 join，切回来会命中另一套行号语义的索引 → **显示错内容**（§2.5）。join 机制删除后此风险不复存在 |
| **D6** | **A** —— 总量上限 + LRU 淘汰，**上限 1 GB** | sidecar = 源文件 47.2% → 1 GB 上限约容 2 个 1 GB 级源文件。淘汰不得删正在使用的缓存（活性锁范式 `SpoolPaths` L400–L424）。「最近使用」的记录方式见 D6-2 |
| **D6-1** | **单个 sidecar 超过 1 GB 上限则不缓存**；写入时先按 LRU 淘汰直到装得下，**新写的这份必留** | 体积可精确预测（结论 3）：3 GB 源文件 → sidecar ≈ 1.4 GB > 上限 → 不缓存，不做「写了再删」 |
| **D6-2** | **「最近使用」= 刷新 sidecar 自身文件 mtime**（不另存时间戳） | 无额外状态可失同步。sidecar 的 mtime 与「源文件身份校验」用的 mtime 是两个不同对象，无冲突 |
| **D7** | **A** —— 增长中的文件不写 sidecar；只对静态文件落缓存 | 采集路径本身是增量扫描，不慢，增量更新 sidecar（追加写 + carry 状态）复杂度高、收益低。衍生：代理 spool 文件与 adb 采集临时文件**显式排除**（生命周期短 / 持续增长） |
| **D7-1** | **静态判定 = 构建开始与结束各取一次 size + mtime，不一致则跳过落盘**；采集 / spool 靠**来源显式排除**，不靠「size 稳不稳」猜 | 纯 stat，无锁、无额外 IO；刚停止写入的文件会被「稳不稳」误判，来源标记更可靠 |
| **D8** | **A** —— **后置**：等 `chunked-progressive-load` apply 完成并归档后再建 sidecar change | 分块管线会重写 `DoBuild` 内部形态，sidecar 的接入点、失效语义、验收基线都建立在分块管线上；两条 change 各自验收、互不干扰。C（合并）变更面过大、验收线互相干扰，排除。sidecar 命中路径**不走**逐块发布（一次 459 ms 到位，分块发布无收益） |
| **D9** | **不压缩 + 分段读直接填列** | 9 段是定长数值列（熵低），压缩收益有限而加载变慢；压缩须先探针实测，不能拿 459 ms 赌解压开销。读回：一次性 `ReadAllBytes`（0.457 GB LOH + 再拷一遍 ≈ 2× 瞬时内存）→ 改**分段顺序读、直接填进目标数组**（峰值可控、零额外拷贝）。头部固定信息：magic + 版本 + 9 段元素宽度 + 行数 + **join 模式** + 源文件身份摘要 + 三字典 + 崩溃事件表 |
| **D10** | **验收线照写进 specs 场景** | 数值见 §7。与 `chunked-file-load` 的做法一致（性能数值进 spec、等价性作为可断言不变量）。CI 无 1 GB 基准文件 → CI 只守等价性/失效/降级不变量，性能线人工实测 |
| **D11** | **缓存目录 = exe 同级子目录 `cache/`** | 与既有 `Log/`、`temp/` 惯例一致；LRU 扫描与清理集中在一处；不把缓存文件混进 exe 目录（便携工具的 exe 目录常与日志文件同放） |
| **D12** | **命中静默** —— 不弹提示、不加状态栏文字、不加开关；仅写 `startup.log` 留痕 | 符合「不加开关 / 不做手动入口」的既有偏好；也意味着本 change **不触碰 UI 交互**（§1 非目标） |
| **D15** | **后台异步写** —— 索引建完即返回，缓存在后台落盘；**应用退出时不等待**（写一半由「临时文件 + rename」保证不污染缓存） | 写 0.46 GB 的耗时随盘而变：本地 SSD ≈ 1.4 s、U 盘 ≈ 15 s、SMB ≈ 66 s；同步写挂在打开路径上会让「打开要快」的目标自相矛盾 |

### 5.2 待定（1 项）

| 编号 | 问题 | 倾向 |
| --- | --- | --- |
| **D13** | 何时建 change（衍生自 D8=A）：现在建四件套、apply 等 chunked 归档后，还是等 chunked 归档后再建 | 现在建四件套（spec 写能力契约，不绑定分块管线内部形态）；apply 等 chunked 归档后 |

> **D14（同源多份 join 缓存是否都保留）已作废** —— join 删除后同一源文件不再有两份缓存（见 §10）。

### 5.3 实现要点（不必拍板，进 design）

- **原子写**：写临时文件 + `rename` 替换，避免半截 sidecar 被当成有效缓存读到。
- **完整性校验**：头部记录各段期望长度与总长度；加载时长度/版本不符即丢弃并重建（磁盘满、写入中断）。
- **多实例并发**：两个 logcat 进程同时打开同一文件 → 各自写各自的临时文件后原子替换即可，无需锁。
- **降级静默**：sidecar 缺失 / 损坏 / 版本不识别 / 目录不可写 → 一律静默走原路径（现状是 `ShowError` 弹窗，需新分支）。
- **命中后仍需 `OpenMap()`**：文本本体不在 sidecar 里（§2.2）。
- **首尾哈希取样量**：建议首尾各 64 KB（1 GB 文件的 0.006%，识别「同 size 同 mtime 的替换文件」足够；FAT32/exFAT 的 mtime 精度低（2 s），哈希兜底正是为此）。

## 6. 实施要点（design 参考）

- **落盘格式**：9 段变长段（offs / lens / moff / pid / tid / tagId / min / lvl / flags），段内为定长原始类型字节，段间连续。加载时**直接读进现成的 9 个数组**（零转换）。需带版本号以便格式演进。
- **元素宽度**（照 §2.1 核实）：offs = 8 B、lens / moff / pid / tid / tagId = 4 B、min / lvl / flags = 1 B；**合计 31 B/行**（结论 3）。
  → ⚠️ 若 join 删除时一并清理 `flags` 列（`REMOVE-JOIN-MERGE.md` 的 R1），本段降为 **8 段 / 30 B/行**，体积公式与 sidecar 格式同步调整。
- **字典**：tag / 异常类型 / 包名三个字典随 sidecar 落盘（数量小，可忽略体积）；落盘用 `string`（扫描期的 UTF-8 `byte[]` 形态已在 `Assemble` 解码）。
- **崩溃事件**：按「行号 + 字典 id」落盘（592 项，可忽略体积）；~~必须连 join 模式一起记录（结论 2）~~ → **join 删除后作废**，sidecar 头部不再需要 join 字段（见 §10）。
- **身份校验**：size + mtime + 首尾哈希，任一失效即重建（D5）。
- **接入点**：`LogDocument.Build` 之后判定是否落盘；加载路径在 `DoBuild` 之前分流（命中则跳过 `ScanRange` + `Assemble`，直接 `SetColumns` + 恢复字典与崩溃事件）。
- **读写器**：需从零实现二进制段读写（可抄 `OpenMap` / `FileStream` / JSON 持久化三个范式）。
- **体积预判**：落盘前按「行数 × 31 B」估算，超过上限直接不写（D6-1 + 结论 3）。
- **异步落盘**：写入在后台线程进行，不阻塞打开流程；失败静默（D15）。
- **与流式加载的关系**：sidecar 命中时流式解析被绕过；两者目标不同（重开全量就绪 vs 首屏快）但同属「打开加速」，design 需说明先后与互斥点（D8）。

## 7. 验收线（已定，D10）

- **正确性**：sidecar 命中路径产出的文档与「从零 Build」路径**完全等价** —— 行数、9 列内容、tag 字典、崩溃事件、记录边界语义全部一致（需专项对比测试，作为可自动化断言的不变量）。
  > join 删除后，「join 语义一致」这一项退化为「**自然行边界一致**」（记录条数与每条记录的起止位置一致）；`flags` 列是否保留取决于 R1。
- **性能**（1 GB 文件）：sidecar 加载 ≤ 600 ms（实测 459 ms）；首次 Build 不劣于现状（基线见 §3.2，分块落地后取实际值 × 1.05）。
- **降级**：exe 目录不可写、sidecar 损坏或版本不识别时**静默走原路径**，打开流程不受影响（现状是弹错误框，需新分支）。
- **失效**：源文件被修改（size / mtime / 首尾哈希任一变化）后必须重建，不得命中过期缓存；join 模式不同不得命中。
- **体积**：sidecar 总量不超 **1 GB**；单个 sidecar 超过上限则不落盘（D6-1）；淘汰不误删使用中的缓存。

## 8. 决策记录

| 项 | 结论 | 日期 |
| --- | --- | --- |
| D1 目标 | 目标①「过滤加速」+ 目标②「缓存加速」两项都做；本 change 只做目标② | 2026-10-25 |
| D3 落点 | exe 同级 | 2026-10-25 |
| 落盘格式 | 沿用现状 SoA 9 段，不改 AoS（结论 1，实测） | 2026-10-25 |
| 实施顺序 | 后过滤加速 change（《过滤加速·零拷贝》探索笔记，已归档）落地 | 2026-10-25 |
| 拆分 | 两个 change 独立走 propose → apply → archive | 2026-10-25 |
| 代码事实复核 | §2 更新（`FinalizeTs` 已删、列类型/崩溃结构/入口/降级核实、无二进制读写器） | 2026-10-30 |
| 基线更新 | 引入 `chunked-progressive-load` 探针基线（首屏 212 ms / 全量 12.9–13.1 s / 内存 663 MB） | 2026-10-30 |
| **D4** | **A** —— 「文件关闭即清理」只约束文件本体副本；派生索引（sidecar）按 LRU 保留 | 2026-10-30 |
| **D5** | **A** —— 路径派生名 + size/mtime + 首尾哈希 | 2026-10-30 |
| **D5-1** | ~~join 模式进键~~ —— **作废**（删除 join，见 §10） | 2026-10-30 |
| **D6** | **A** —— 总量上限 + LRU，上限 **1 GB** | 2026-10-30 |
| **D6-1** | 单个 sidecar > 上限则不缓存；新写的必留 | 2026-10-30 |
| **D6-2** | 「最近使用」= 刷新 sidecar 自身 mtime | 2026-10-30 |
| **D7** | **A** —— 只对静态文件落缓存（采集/spool 显式排除） | 2026-10-30 |
| **D7-1** | 构建前后各取 size+mtime；采集/spool 靠来源显式排除 | 2026-10-30 |
| **D8** | **A** —— 后置，等 `chunked-progressive-load` 归档后再建 sidecar change | 2026-10-30 |
| **D9** | **不压缩 + 分段读直接填列** | 2026-10-30 |
| **D10** | **验收线照写进 specs 场景** | 2026-10-30 |
| **D11** | **子目录 `cache/`** | 2026-10-30 |
| **D12** | **命中静默（仅 startup.log 留痕）** | 2026-10-30 |
| **D15** | **后台异步写，退出不等待** | 2026-10-30 |
| 结论 3 | sidecar 体积 = 行数 × 31 B（实测校验：15,823,120 × 31 B = 0.4568 GiB） | 2026-10-30 |
| 附录 | §2.5 join 语义速查（判据 / 合并前提 / 两个「不索引」特例 / 为何键必须含 join） | 2026-10-30 |

## 9. 下一步

- **D13 拍板后**即可走 `/opsx:propose`。建议 change 名：`doc-index-sidecar-cache`。
- **执行顺序（2026-10-30 调整，因删除 join）**：

```
[删 join change]  ──▶  [chunked-progressive-load 修订并 apply]  ──▶  [sidecar 缓存 change]
   remove-join-merge        （③ 的规划产物修订在此之后）              键不再带 join
```

- 范围：sidecar 落盘/加载（SoA 9 段或 8 段，取决于 R1，exe 同级 `cache/`）+ 键与失效（D5）+ 体积管理（D6/D6-1/D6-2）+ 静态文件边界（D7/D7-1）+ 异步落盘（D15）+ 与 `chunked-progressive-load` 的先后（D8/D13）+ 格式与读回（D9）+ 命中静默（D12）+ 验收线（D10）+ 专项对比测试。
- 本笔记为探索产物；本 change 归档时同批移入 `openspec/notes/archive/`（与《过滤加速·零拷贝》笔记分别归档，各跟各的 change；后者已于 2026-10-28 归档）。

## 10. 连带影响：删除 join（2026-10-30 维护者决定）

维护者决定**删除「续行合并」机制**，索引改按自然行 —— 独立命题，见 `openspec/notes/REMOVE-JOIN-MERGE.md`（影响清单、已定 ①②、待决 R1–R5）。对本篇的连带影响：

| 本篇条目 | 变化 |
| --- | --- |
| **结论 2**（sidecar 必须记录 join 模式） | **作废** —— 不再有 join 维度；sidecar 头部无需记录 join 模式 |
| **D5-1**（join 模式进键） | **作废** —— 键不含 join；「切 join 命中错语义索引」的风险不复存在 |
| **D14**（同源多份 join 缓存是否都保留） | **作废** —— 同一源文件只可能有一份缓存；D6 的 1 GB 上限压力减半 |
| 体积公式（结论 3） | 「行数」含义从**记录数**改为**自然行数**（除空行与横幅行外的物理行）；若 `flags` 列一并清理（R1）→ **8 段 / 30 B/行**，否则仍 9 段 / 31 B/行 |
| 顺序 | 由「等 chunked 归档后再建」扩展为「删 join → chunked 修订并 apply → sidecar」（见 §9） |

> **2026-10-31 实测更正**：本节原称「删 join 对 sidecar 本身几乎没有收益」（依据是「15,823,120 行 = 15,823,120 条记录 → join 一行都没合并」）—— **该依据已被实测推翻**：`15,823,120` 是**记录数**，样本物理行为 **17,832,235**，即 join 实际合并 **2,009,093 行（11.27%）**（真实 SMB 日志同比例 11.26%）。
> → 删 join 后 sidecar **体积上涨**：9 段 0.5148 GiB（+12.7%）、8 段 0.4982 GiB（+9.1%）；「1 GiB 上限 ≈ 34.6 M 行」应改为「≈ 35.8 M 自然行」。**删 join 的正当理由仍是机制复杂度本身（且能让在途 chunked change 大幅简化），但要意识到体积是负收益。**
> 详见 `openspec/notes/REMOVE-JOIN-MERGE.md` §1.1 与待决策项 **D1**（D1=B 则命题需重新评估）。
