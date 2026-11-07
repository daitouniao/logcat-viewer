# 结论：message 不建正文索引，实际是物理行列式元数据索引

- 状态：**结论文档**（只读探索，未编码）
- 日期：2026-11-07
- 事实基准：**2026-11-07 核对代码**（HEAD）
- 关联：`openspec/specs/message-filter/`、`openspec/specs/time-column/`、`openspec/specs/sidecar-index-cache/`、`openspec/specs/chunked-file-load/`；已归档 design `remove-join-merge`（D1 索引口径）、`sidecar-cache-index`（D2/D3/D5/D9）、`chunked-progressive-load`
- 归档：无对应 change，留在 `openspec/notes/` 作常驻参考

## 1. 结论

1. **不存在 message 正文索引**：无倒排表、无词表、无关键词索引；message 正文始终留在源文件，靠 `MemoryMappedFile` 按需回读。
2. 索引的真实形态是**行级列式（SoA）元数据索引**：key = 文档行号（0 基），value = 8 段定宽列（偏移/行长/正文偏移/分钟/PID/TID/等级/TagId）。
3. 「定位 message」= 用 `Offs + Moff … Offs + Lens` 切出字节区间；索引只负责定位，不负责存文本。
4. 文档主索引是**单条连续可增长数组**，不按块分区；分块只存在于加载编排（4/32 MB）、Message 块扫描（32 MB）、UI 过滤行号的分段结构。
5. 索引常驻内存；可在 exe 同级 `cache/` 落一份 sidecar（约 30 B/行），用于重开同文件跳过解析。

## 2. 建索引的四条链路

| 路径 | 入口 | 核心 |
| --- | --- | --- |
| 本地首次（sidecar miss） | `frmMain.cs` L952 `StartIndex` → `frmMain.Sidecar.cs` L25 `LoadLocalFileAsync` | `frmMain.Load.cs` L37 `RunChunkedLoadAsync` → `LogDocument.cs` L480 `BuildChunkedAsync` → L491 `BuildChunkedCore` → L512 `ChunkScan` |
| 本地重开（sidecar hit） | `frmMain.Sidecar.cs` L63 `TryLoadSidecarHit` | `LogDocument.cs` L200 `LoadIndexed`：不扫描，直接填 8 列 + 字典 + 崩溃表 + `OpenMap()`，`IndexedSize = Size`（L217） |
| 代理 spool | `frmMain.cs` L1160 `Reload` 中转重建 / `StartIndex` 代理分支 | `BuildChunkedAsync(spool, publish: null, …)`，与本地同一 `ChunkScan` |
| ADB 实时 | `frmMain.cs` L2228 `AdbAutoReload` | 首包 `BuildAsync`（整份 `DoBuild`）；增长 `LogDocument.cs` L946 `Reload` → `ChunkScan(IndexedSize, newSize)` → `MergePart` |

### 2.1 本地首次：分块建索引

- 块长：首块 4 MB 出首屏、稳态 32 MB（`LogDocument.cs` L465–L467）。
- 每块：`TrimPartial` 对齐换行（L1082）→ `ScanRange(start, scanEnd, indexEnd)`（L609）→ `MergePart`（L998）→ `publish(from,to)`。
- 发布回调在 worker 上做增量过滤并写发布缓冲：`frmMain.Load.cs` L110 `OnChunkPublished`（`FilterTail` 调用 L143），UI 100 ms 节流消费（L159 `FlushLoadOps`）。
- 首块后按实测行密度一次 `EnsureCap` 预分配（L549、L126）。
- 扫描是「整区间读进 `byte[] data` 再按 `\n` 切行」（L680–L690），构建期 LOH 瞬态峰值来源。

### 2.2 sidecar 命中：跳过解析

- `LoadLocalFileAsync` 先取身份 `SidecarIdentity`（`frmMain.cs` L1035），再 `TryLoad`。
- 命中后 `AdoptSidecarHitAsync`（`frmMain.Sidecar.cs` L85）接管文档并 `ApplyFilter` 一次到位，**不进** `RunChunkedLoadAsync` 编排。
- 映射仍必须建：`LoadIndexed` 内 `OpenMap()`（L216）。

### 2.3 ADB 增量：续扫而非重建

`Reload`（L946–L989）三态：

- `size` 未变且映射在 → `unchanged`；
- 变短 / `IndexedSize==0` / 无映射 → `DoBuild` 全量，`rebuilt`；
- 前 64 KB 内容变化 → 全量，`rebuilt`；
- 头部一致且增长 → `TrimPartial` 后 `ChunkScan(IndexedSize, newSize)`，`appended`。

`appended` 只对新增行 `ApplyIncremental`（`frmMain.cs` L1304）；`rebuilt` 回退全量 `ApplyFilter`。

## 3. 索引结构

### 3.1 8 段列（字段 `LogDocument.cs` L52–L63；装配 L108 `SetColumns`）

| 列 | 类型 | 含义 |
| --- | --- | --- |
| `Offs` | long | 记录（物理行）在源文件中的字节偏移，mmap 地址基准 |
| `Lens` | int | 该物理行长度（含头部，不含 `\n`/`\r`） |
| `Moff` | int | message 正文在行内起点；正文区间 `[Offs+Moff, Offs+Lens)` |
| `Minute` | byte | 行首时间戳原始分钟 0–59；无有效时间戳 = `MINUTE_NONE` 255（L22） |
| `Pid` / `Tid` | int | -1 = 未知 |
| `Lvl` | byte | 0 未知，1–7 = V/D/I/W/E/F/A |
| `TagId` | int | 指向 `Tags` 的 id，-1 = 无 tag |

### 3.2 派生数据

- 三字典：`Tags`+`TagIndex`、`ExcTypes`+`ExcTypeIndex`、`Pkgs`+`PkgIndex`（L65–L79），字符串本体在内存，行内只存 id。
- 稀疏崩溃表 `Crashes`：`Row / ExcId / PkgId / Pid / Tid / Kind`（L69–L73）；崩溃在**索引阶段**内联提取（L775–L793、L822–L860），分块时用 3 行前瞻重叠（L528–L532、L558–L583）。

### 3.3 明确不存的内容

- **不存 message 文本**，只存偏移；展示/导出/复制按偏移回读（L264–L279）。
- **不存绝对时间戳**：`ParseResult.WithinMs` 仅解析期用于算分钟，随后丢弃（`LogParser.cs` L58–L73）；Time 列靠 `HeaderBytes` 回读行首原文（L281–L303）。
- 不存行文本副本、不存原始分行结构（join/续行合并已删除，物理行即记录）。

## 4. sidecar 落盘与容量治理

- 落点：exe 同级 `cache/`（`SidecarCacheStore.cs` L31–L40）；文件名 = 源路径哈希 + 可读前缀（`SidecarCache.cs` L128），不暴露本机绝对路径。
- 身份：`size + mtime + 首尾各 64 KB FNV`（`SidecarCache.cs` L39–L116）；任一项不符即 miss。
- 格式：magic `LCSC` + 版本 + 行数 + 8 段宽度 + 段长/总长 + 身份摘要 + 三字典 + 崩溃表（`SidecarCache.cs` L202 `Write`、L267 `Read`）；正文区 8 段定宽顺序排布，约 **30 B/行**。
- 原子性：临时文件 + `File.Move(overwrite)`（`SidecarCacheStore.cs` L160–L162）。
- 容量：单份上限 1 GB（L17）、目录上限 4 GB、LRU 按条目 mtime、跳过使用中条目（L64–L88、L185 `EvictForSpace`）。
- 静态边界：只对本地静态文件落盘；代理 spool、adb 临时文件、已命中装载均不落（`frmMain.Sidecar.cs` L98–L102）。
- 失败静默：身份不符 / 版本不识 / 段长不符 / 损坏 → 走全量并重建（`SidecarCacheStore.cs` L94–L121、`SidecarCache.cs` L267）。

## 5. 查询路径

1. **元数据预筛**：`FilterEngine.cs` L823 `ApplyFilter` 按行号扫 `Lvl/Pid/Tid/Minute/TagId` + 标记，产出候选 `int[]`。
2. **message 匹配**（`FilterEngine.cs` L174 `MsgFilterCore`）：
   - 逐条：`MessageSpan(r)` = `[Offs[r]+Moff[r], Offs[r]+Lens[r])` 的 mmap 切片，字节级子串匹配（`LogDocument.cs` L384）。
   - 块扫描：行数 ≥200 万、候选 > 行数/8、可扫关键词 ≤8 时分派（`FilterEngine.cs` L365–L389）；32 MB 块并行、块间前向重叠 `最长关键词-1` 字节；命中点用 `Offs` 二分 `FirstRowAtOrAfter`（L619）定位行号，并复核命中完整落在 message 段内（`BlockHitSink` L563）；末尾 `VerdictPass`（L758）做 and/or/exclude 判定。
3. **增量**：`FilterTail(start, n)`（L1039）只处理新增行；加载期用 `MsgScanSession`（L915）累计跨块。
4. **展示**：结果是升序文档行号；`LogListView.SetRows`（L107）/`AppendRows`（L121）只存行号（分段 `_segs` L45）；虚拟列表 `OnRetrieveVirtualItem`（L402）→ `MakeItem/MakeFields`（L418–L507）只对可见行回读 `HeaderBytes` 与 `MessageBytes`，解码缓存 2 万行。
5. 结论：**查询阶段不物化全部 message**；索引定位、mmap 取文本、UI 只取可见窗口。

## 6. 与 openspec 规格的对照

### 6.1 一致项

- `message-filter`：行为作用域 = 物理行；头部不参与正文匹配；除空行与横幅行外每物理行一条记录 → `ScanRange` 以 `MsgOffset` 切正文、空行/横幅跳过（L719–L736）。
- `time-column`：分钟列 0–59，无时间戳存「不可能是分钟值」的标记 → `MINUTE_NONE=255`（L22）。
- `chunked-file-load`：跨块等价、已发布前缀稳定、不完整行留待下一块 → `TrimPartial` + `indexEnd` + 3 行前瞻（L507–L532）。
- `sidecar-index-cache`：身份校验、静默降级、后台落盘、原子替换、容量治理 → `SidecarIdentity` / `SidecarFile` / `SidecarCacheStore` 一致。

### 6.2 偏差与未覆盖

1. **索引结构未进任何主 spec**：列定义、`Offs` 基准、`Moff` 语义、分块方式均只在 change design（`remove-join-merge` D1 有意为之：结构不入 spec，只写可观测行为）。
2. **横幅判定无 spec**：`≥5 个 '-'` + 空格/TAB/行尾（`LogParser.cs` L358–L364）属于索引口径，仅有代码与 design 记录。
3. **未知值编码无 spec**：`Minute=255`、`Pid/Tid=-1`、`Lvl=0`、`TagId=-1` 的具体数值没有契约化。
4. **块扫描分派阈值（200 万行 / 候选>1/8 / 关键词≤8）是纯实现决策**，未进 spec，可随时调参。
5. **sidecar 600 ms 装载线是验收指标**，代码只记录 `LastSidecarLoadMs`，不做运行期断言。
6. `LogParser.LooksLikeNewRecord` 保留但生产侧无调用（L378–L397），属设计上的「留 API 不留调用」。
7. **DLT 等二进制格式未实现**（全仓库无 DLT 代码）；索引只处理 UTF-8 文本物理行，接入二进制格式需改「物理行 = 记录」口径。

## 7. 残余风险与可深挖点

1. **身份盲区**：sidecar 只校验 size/mtime/首尾 64 KB；文件中段被原地改写且 size/mtime 不变时会误命中（`sidecar-cache-index` D2 已作为残余风险接受）。
2. **Reload 头校验只取前 64 KB**（L955）：对 adb append-only 足够；若将来用于「就地重写」的普通文件会漏判。
3. **三水位一致性**：`Offs`（字节水位 `IndexedSize`）、`_n`（文档行数）、`_filterDocRows`（UI 已过滤行数）分属不同层，靠 `_docBusy`（`frmMain.cs` L33）与 `_workerGen` 守卫；新增入口（网络流等）需重新审计对齐。
4. **构建期内存**：`ScanRange` 整块 `byte[]` 读取造成 LOH 瞬时峰值（已知非泄漏、自封顶）；如要压峰值，这是首要改造点。
5. **若产品需要「关键词倒排」**：当前架构没有，查询是每次扫描（加载期/采集期增量）。需先明确需求再评估新增索引，否则不要把现状误读为已有全文索引。
