# 结论：落盘 sidecar 索引（重开同文件跳过解析）

- 状态：**结论文档**（探索已结；D3–D12、D15 已拍板；D13 待拍板）
- 事实基准：**2026-11-02 重新核对代码**（不沿用 2026-10-30 旧笔记）；关联 `remove-join-merge`（已归档）、`chunked-progressive-load`（已归档，spec `chunked-file-load`）
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）
- 归档：随 sidecar change 归档时同批移入 `openspec/notes/archive/`

## 1. 目标与范围

**问题**：重开一个已解析过的大文件，`DoBuild`（ScanRange 解析 + 八列物化 + 崩溃落表）耗时 12–18 s，期间界面无内容。

**本 change 做什么**：把建好的列式索引落盘成 sidecar，重开同一文件时直接加载索引、跳过解析。

**不做**：过滤加速（另立）、流式/首屏加载（`chunked-file-load` 已承接）、改列布局、改加载管线形态、UI 改动、删除 join（已另立归档）。

## 2. 当前代码事实（2026-11-02 复核）

1. **列布局 = 8 段 SoA，无 flags 列**（`LogDocument` L51–L58、L105–L106 `SetColumns` 八参）：`offs`(long,8) / `lens`(int,4) / `moff`(int,4) / `min`(byte,1) / `pid`(int,4) / `tid`(int,4) / `lvl`(byte,1) / `tagId`(int,4)。flags 列已随删除 join 移除（全仓库无 `FLAG_MULTILINE` 引用）。
2. **join（续行合并）已删除**：`LogParser.LooksLikeNewRecord` 保留但生产侧无调用点（L382 注释）；`_join` 参数已从 `Build`/`BuildAsync` 签名移除。`CrashEvent` 现 6 字段（`Row`/`ExcId`/`PkgId`/`Pid`/`Tid`/`Kind`），**无 `LineCount`**。
3. **三字典**：`Tags`+`TagIndex`、`ExcTypes`+`ExcTypeIndex`、`Pkgs`+`PkgIndex`（均为 `List<string>` + `Dictionary<string,int>`，L63–L76）。
4. **加载双路径**：`BuildAsync(path, progress, ct, displayPath)` → `DoBuild` → `ScanRange(0,Size)`+`Assemble`（adb 临时文件入口，`frmMain` L2239）；`BuildChunkedAsync(...)` → `BuildChunkedCore` → `ChunkScan`（spool 入口，`frmMain` L1106）。两条都从 `StartIndex` 分流。
5. **文本本体在 mmap，sidecar 不含文本**：`OpenMap()`（L134）仍必须在 sidecar 命中后调用，命中路径只跳过 `ScanRange`+`Assemble`。
6. **全仓库零二进制读写器 / 零压缩**：无任何 `BinaryWriter`/`BinaryReader`/`BinaryPrimitives`/`Deflate`/`GZip`/`Brotli` → sidecar 读写器需从零实现（可抄 `OpenMap` / `FileStream` 落盘 / JSON 持久化三个范式）。
7. 入口 `frmMain.StartIndex(string path)`（L947）；失败现走 `ShowError` 弹窗，sidecar 静默降级需新分支。

## 3. 已定结论（决策）

| # | 结论 | 关键口径 |
| --- | --- | --- |
| 落点 | exe 同级子目录 `cache/` | 与 `Log/`、`temp/` 惯例一致；SMB 落本地才保亚秒重开 |
| 键与失效 | 源路径派生名 + `size`+`mtime`+首尾各 64 KB 哈希；任一失效即重建 | 路径哈希 + 可读前缀，不暴露本机绝对路径；哈希兜底 FAT32/exFAT 低精度 mtime |
| 体积上限 | 单 sidecar 上限 **1 GB** + **LRU 淘汰**；单份超上限则不缓存（先淘汰到装得下，新写必留） | 「最近使用」= 刷新 sidecar 自身 mtime，无额外状态 |
| 静态边界 | 只对静态文件落盘；增长中 / 采集 / 代理 spool 显式排除 | 构建前后各取一次 `size`+`mtime`，不一致则跳过落盘 |
| 格式 | 沿用现状 SoA **8 段**、**不压缩**、分段顺序读直接填列 | 加载比 AoS 快且零转换（内存布局不变，与过滤/分块互不牵制） |
| 命中静默 | 不弹提示、不加开关，仅 `startup.log` 留痕 | 符合「不加手动入口」偏好；不动 UI |
| 异步写 | 索引建完即返回，后台落盘；**退出不等待** | 临时文件 + `rename` 保证半截不污染缓存 |
| 与 chunked | 后置（chunked 已归档）；命中路径**不走**逐块发布，一次到位 | 在两条 Build 路径之上分流 |
| 体积公式 | **行数 × 30 B**（8 段定宽合计） | 1 GiB ÷ 30 B ≈ 35.8 M 自然行 = 单文件上限 |

**实测支撑（结论，非探索）**：1 GB / 15.8M 行样本，sidecar 命中重开 **459 ms**（相对首次 Build 14.8 s ≈ **32×** 提速）。

## 4. 验收线

- **正确性**：sidecar 命中路径产出文档与「从零 Build」**完全等价**——行数、8 列内容、三字典、崩溃事件、自然行边界全部一致（专项对比测试，作为可自动化断言的不变量）。
- **性能**（1 GB）：sidecar 加载 ≤ 600 ms（实测 459 ms）；首次 Build 不劣于现状（分块落地后取实际值 ×1.05）。
- **降级**：exe 目录不可写 / sidecar 损坏 / 版本不识别 → **静默走原路径**（新分支；现状是弹错误框）。
- **失效**：源文件 `size`/`mtime`/首尾哈希任一变化必须重建，不得命中过期缓存。
- **体积**：总量 ≤ 1 GB；单份超上限不落盘；淘汰不误删使用中的缓存（活性锁范式）。

## 5. 待拍板（仅 D13）

**事实**：D8 的前置（等 `chunked-progressive-load` 归档）**已满足**；`remove-join-merge` 已归档 → 列布局与 `CrashEvent` 形态已定，sidecar 格式无剩余不确定性。

**决策**：现在建 change 四件套，还是仍延后？

- 甲（倾向）：**现在建**。两条硬依赖皆已归档，接入点在 `StartIndex` 两条 Build 之上分流即可，无继续等待的理由。spec 写能力契约，不绑定分块管线内部形态。
- 乙：等某个后续 change 一并——无明确对象，不推荐。

建议 change 名：`doc-index-sidecar-cache`。

## 6. 实施要点（design 参考，精简）

- **落盘格式**：头部（magic + 版本 + 8 段元素宽度 + 行数 + 源文件身份摘要 + 三字典 + 崩溃事件表）+ 8 段变长段（段内定长原始字节，段间连续）。加载直接读进现成 8 个数组（零转换）。
- **接入点**：`frmMain.StartIndex` 在 `BuildAsync`/`BuildChunkedAsync` 之前判定命中；命中 → 加载索引 → `SetColumns` + 恢复三字典与崩溃事件 + `OpenMap()` + `IndexedSize=Size`，**绕过**两条解析管线。
- **原子写 / 完整性**：临时文件 + `rename`；头部位记录各段期望长度与总长，长度/版本不符即丢弃重建。
- **多实例并发**：两进程各写各自临时文件后原子替换，无需锁。
- **降级静默**：缺失 / 损坏 / 版本不识别 / 目录不可写 → 一律走原路径。
- **体积预判**：落盘前按「行数 × 30 B」估算，超上限直接不写。
- **异步落盘**：后台线程写，失败静默；退出不等待。
