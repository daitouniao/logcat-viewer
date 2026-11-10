# Rust Core 重写的拆分顺序与方法

- 状态：**待评审**（探索记录，未编码、未 propose）
- 日期：2026-11-08（本轮修订，取代同日早先版本）；2026-11-10 补充审查与输入先行探索（§15）
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）
- **审查状态（2026-11-10）**：发现 2 项阻塞 propose 的契约缺口、1 项会假绿的验收面、若干过期事实，详见 **§14**。§14 对应的 C16 / C17 / C18 未拍板前，刀1～刀3 不得据此进入 propose。
- **顺序修订（2026-11-10）**：维护者提出「第一步 log 输入（① 打开文件含代理 + ② log 采集）」，即把原刀5 / 刀6 前移。与本笔记 §5 的七刀顺序冲突，探索记录见 **§15**。
- **本轮拍板（2026-11-10）**：C19 / C20 已定案——GUI 走 `crates/cabi` 的窄 FFI、MCP 走 `crates/cli` 的 stdio，GUI 主航道 + 三轨并行；验收**不设数字锚**，排序按可感知度。决议与接手点见 **§15.9**。仍待定：C21 与 C16 / C17 / C18。

## 0. 一句话结论

目标态：**Rust 拥有全部 log 文件操作与结果集，编译成一个 C ABI cdylib，由 C#（P/Invoke）与 Node.js MCP（koffi）共用同一个 dll；C# 只做配置与分页渲染，不持有日志数据、不映射文件。**

实施按七刀推进：刀0 C# 分页接缝 → 刀1 core+cli 索引 → 刀2 dll 接管分页 → 刀3 过滤 → 刀4 MCP → 刀5 获取链 → 刀6 设备取流。**（刀序已按维护者 2026-11-10 拍板调整：输入先行 + 三轨并行，见 §15.9；上面的七刀描述保留作对照。）**

本轮相对上一版的关键变化：**分页模型消解了文本所有权三选一与 remap 契约**。上一版把「Rust 拥有 mmap、C# 拿裸指针切片」当作推荐接缝，是因为 C# 自己要随机读行文本；改为「传分页参数或锚定行号、返回固定条数」之后，文本按页拷出，长期裸指针不再存在。

## 1. 本轮已定决策（维护者拍板，2026-11-08）

| 编号 | 决策 | 影响 |
| --- | --- | --- |
| D1 | log 文件操作完全归 Rust，C# 仅配置 + 显示 | 推翻上一版 §5.5「FileProxy / spool / adb / sidecar 留 C#」 |
| D2 | 显示走 SQL 查询窗口式分页：传入分页参数或锚定一个记录行号，返回固定记录数量 | C# 不映射文件、不持有结果集；同时否决本轮曾提议的「C# 保留只读映射作为渲染底座」 |
| D3 | Rust 编译一个 dll，供 C# 与 Node.js MCP 调用 | 进程外 stdio 不作主形态；CLI 仍保留作对拍与退路 |
| D4 | Node 侧走甲：**koffi 调同一个 C ABI dll** | 一个 native 产物，不引 napi-rs 的 Node ABI × 平台构建矩阵 |
| D5 | panic 防护取 `catch_unwind` 逐导出函数包装 | `panic = "abort"` 不是等价替代，口径见 §4.5 |

## 2. 与既有结论的关系

| 既有结论 | 出处 | 本笔记的处理 |
| --- | --- | --- |
| 阶段 2「既定计划：core-extraction → mcp-server」 | `RUST-CORE-EVALUATION.md` §8 | **作废**，替换为 §5 的七刀 |
| `core-extraction` 按 1a/1b 两步走 | `BACKLOG-MCP-AND-FORMATS.md` D7 | **作废**：刀0 直接立分页接缝，不先抽 C# Core |
| 文本所有权推荐方案 A（Rust 拥有 mmap，C# 拿裸指针） | `RUST-CORE-EVALUATION.md` §5 | **作废**：分页按页拷出，A/B/C 三选一不再成立（D2） |
| append remap 两种收法（generation / 主体尾部双映射） | `RUST-CORE-EVALUATION.md` §5、上一版 C2 | **降级**：只剩「查询世代号用于缓存失效」，不再是跨语言指针失效 |
| 过滤结果形状偏向乙（行索引 + 各维度计数） | `RUST-CORE-EVALUATION.md` C4、上一版 C3 | **取消**：已证伪，见 §3.2 |
| 「建索引内存峰值真赢」 | `RUST-CORE-EVALUATION.md` §6 | **口径改准**：dll 在宿主进程内，赢的是「无 LOH 倍增 + 显式释放」，不是「峰值隔离到别的进程」 |
| `Loc` 需拆出 UI 辅助半 | `BACKLOG-MCP-AND-FORMATS.md` §现状核对 10 | **消失**：进度与文案改由 C# 映射错误码/事件码，`Loc` 整体留 `logcat.UiCore` |
| MCP 分发为 NuGet dotnet tool | `BACKLOG-MCP-AND-FORMATS.md` D6 | **失效**：改 npm 包 + 预编译 dll + koffi（D3/D4） |
| MCP 与 core 走长驻 stdio 子进程（方案甲） | （已归档笔记，仅作状态说明，不作依据） | **改为 dll 进程内**（D3）；本笔记沿用「MCP 会话必须长驻、否则索引反复重建」这条约束 |
| MCP 进程内直接调用 core | `MCP-FUNCTIONALITY.md` | **成立**（D3 回到进程内），但调用的是 native dll 而非 C# Core |

语言决策本身仍按 `RUST-CORE-EVALUATION.md` 停在阶段 0 探针；本轮维护者已在细化 Rust 方案，但**是否正式定案、是否跳过探针**未明确，列为 C1（§8.1）。

## 3. 现状事实（HEAD 核对）

### 3.1 引擎与性能

| 事实 | 依据 |
| --- | --- |
| 引擎代码量集中在四个类 | `Services/LogParser.cs` 441 行、`LogDocument.cs` 1100 行、`FilterEngine.cs` 1133 行、`CrashExtractor.cs` 225 行；`CrashAggregator.cs` 59 行、`CrashTime.cs` 32 行、`SidecarCacheStore.cs` 241 行 |
| **过滤已并行 + SIMD**，不是单线程 | `Services/FilterEngine.cs:493` / `:521` `Parallel.For`；`:678` `Vector256.Create`；`:479` 按 `Vector256.IsHardwareAccelerated` 选 SIMD 或标量回退 |
| 块扫描三道分派闸 | `Services/FilterEngine.cs:23` `BLOCK_SCAN_MIN_ROWS = 2_000_000`、`:27` `BLOCK_SCAN_MAX_KEYS = 8`、`:370` 候选比阈值 |
| **建索引才是单线程** | `Services/LogDocument.cs:609` `ScanRange` 顺序扫描；`:440` / `:487` 的 `Task.Run` 只是 offload 到单个工作线程，不是并行 |
| 主界面走分块装载 | `frmMain.Load.cs:57` 调 `BuildChunkedAsync`；整份入口 `BuildAsync` 另有生产调用点 `frmMain.cs:2262`（ADB 自动重载） |
| 过滤有全量与增量两条路径 | `FilterEngine.ApplyFilterAsync`（`:817`）、`ApplyFilter`（`:823`）、`FilterTail`（`:1039`），均返回 `int[]` |
| 崩溃打标绑在索引扫描期、标在父行 | `LogDocument` 扫描路径调 `CrashExtractor.ContainsCrashKeyword`；`CrashExtractor.cs:48` / `:59` |
| 增量追加走 `Reload` | `LogDocument.cs:942`，长度比较 + 64KB 头部比较，返回 `"unchanged"` / `"appended"` / `"rebuilt"` |
| 编码探测**未实现** | 现状硬编码 UTF-8；`Services/TextProbe.cs` 只做二进制/文本嗅探（8KB、控制字符占比），UTF-8/GBK 判定仅存在于规划 |
| 目录时间线 / 多文件合并**未实现** | `openspec/notes/BACKLOG-DIR-TIMELINE.md` 标为未编码；`MergePart` 是文档内分块合并，不是文件合并 |

### 3.2 显示侧（决定 D2 可行性）

| 事实 | 依据 |
| --- | --- |
| **锚点语义已存在**，不是新概念 | `Controls/LogListView.cs:273` `CaptureAnchor()` 返回 `(docRow, offsetFromTop)`；`:291` `PinAnchor` 先 `ModelRowOf`、失败退 `:309` `NearestModelRow`（升序行号序列二分找最近存活行） |
| 控件持有**全部**命中行号 | `Controls/LogListView.cs:107` `SetRows` 取得数组所有权且不复制；`:121` `AppendRows` 原地填尾段；分段结构 + 段前缀和避免 O(n²) 整表拷贝 |
| 行号换算三件套依赖本地数组 | `:344` `DocRow(modelRow)`、`:357` `ModelRowOf(docRow)`、`:309` `NearestModelRow(docRow)`、`:330` `SegLastGe` |
| **`RetrieveVirtualItem` 是同步的** | `:402` 缓存未命中即在 UI 线程 `MakeItem` → `MakeFields` 读 mmap 解码 |
| **`CacheVirtualItems` 钩子已挂但处理函数为空** | `:413`，注释「预缓存：不需要额外操作」——分页预取的天然落点 |
| 占位行形状已存在 | `:468` catch 分支「后台 Reload 与 UI 渲染竞态：返回空行，下次刷新会修正」 |
| 行缓存键是模型行号 | `:61` `Dictionary<int, ListViewItem>`、`:62` `CACHE_SIZE = 20000`，超限删一半；`SetRows` 直接 `ClearCache` |
| 消息截断是显示策略 | `:14` `MSG_LIMIT = 4000`，超出拼 `Loc.F(" …(+{0} 字符)")` |
| **崩溃摘要格式化在控件里跑** | `:525` `CrashSummary` 调 `CrashExtractor.TryExtract` 兜底、`CrashTime.SummaryText`、`Loc.F("☠ {0} · {1} · {2}")` |
| 状态栏只消费计数与耗时 | `frmMain.cs:1673` `UpdateStat`：total / `_listView.RowCount` / `_marked.Count` / 索引ms / 过滤ms。**没有任何调用点消费「各维度计数」**，故上一版 C3 偏向乙属给不存在的需求付钱 |
| 标记既是 C# 侧 UI 状态，**也是过滤输入** | `Controls/LogListView.cs:32` `HashSet<int> _marked`（按文档行号）；`FilterSpec.MarkedOnly` 使集合参与 `ApplyFilter` / `FilterTail` 谓词，见 §14.2 |
| 无排序 | 列固定（`:12`），无 ORDER BY 语义，分页契约 v1 不需要排序 |
| P/Invoke 不是新东西 | `Controls/LogListView.cs:200` 已 `DllImport("user32.dll")`；`logcat.AndTools/WakeUp.cs:30` 同 |
| 导出读的是行文本 | `Services/FilterEngine.cs:1100` `ExportRows` 读 `doc.LineBytes(row)` 写原始字节；行号来自 `Controls/LogListView.cs:159` `GetRowsCopy()` 全量物化 |

### 3.3 文件操作侧（决定 D1 的搬迁面）

| 事实 | 依据 |
| --- | --- |
| **管线编排装在窗体里** | `frmMain.cs:1087` `LoadViaProxyAsync`：建租约 → 拉起代理 → 1GB 确认框 → spool 写盘 → 建索引 → 租约挂到 doc → 失败兜删。整段只有 `MessageBox` 一步是真 UI |
| sidecar 编排也在窗体侧 | `frmMain.Sidecar.cs` 132 行：后台线程 + `_docBusy` 信号量 |
| ADB 重载决策在窗体里 | `frmMain.cs` 1 秒 WinForms 定时器 → `AdbAutoReload` → `doc.Reload` + 增量过滤；无 `FileSystemWatcher` |
| **文件获取本来就已跨进程且语言无关** | `Services/FileProxyClient.cs:39` `NamedPipeClientStream` + JSON 行/原始块帧；对端是用户自带代理 exe，`sample/fileproxy/` 内有 `fileproxy.py` 与 `fileproxy.js` 两份示例 |
| spool 与活性锁 | `Services/FileProxyService.cs:224` `SpoolCoreAsync`（8MB 块、完整性校验、失败删目录）；`:419`–`:451` `.alive.lock` 依赖 **Windows FileShare 语义**做跨进程活性探测 |
| 设备侧是 .NET 依赖 + Windows-only | `Services/LogcatStream.cs:2` `AdvancedSharpAdbClient`、`:65` `"logcat -v threadtime"`、`:89` `ExecuteRemoteEnumerableAsync`；`logcat.UiCore/Services/AdbManager.cs:353`–`:396` 截图用 GDI+ `Bitmap` / `Marshal.Copy`；`AdbManager` 被两个 exe 共享 |
| ADB 取流已经落本地临时文件 | `frmMain.cs:2103` 写 `<exe目录>\Temp\logcat_live_<时间戳>.log`，随后走 `Reload` 增长分支 |
| sidecar 存储治理是纯 IO | `Services/SidecarCacheStore.cs`：exe 侧 `cache/`、temp + `File.Move(overwrite)`、LRU 淘汰、1GB/条目与 4GB/目录上限、in-use 集合，无 UI |
| sidecar 格式钉死且**无 payload 校验** | `Services/SidecarCache.cs:155` `FormatVersion = 1`、`:163` `Widths = {8,4,4,1,4,4,1,4}`、`:242`–`:249` `MemoryMarshal.AsBytes` 直接铺列；只有结构性校验，无校验和，读侧不 bounds-check 字典 id |
| sidecar 命中路径是一等公民 | `frmMain.Sidecar.cs:33` 每次本地打开无条件先读，无大小门槛无开关；`LoadedFromSidecar` 三处用途只关跳过重复落盘 / 计时 / 状态文本，**无功能降级** |
| 命中后 C# 自己映射源文件 | `Services/LogDocument.cs:200` `LoadIndexed` → `:216` `OpenMap`；索引不含文本，文本访问与「谁产的索引」无关 |
| 格式不可追加 | 变长字典在数据区之前、`segLength` 必须等于 `rows * width`，增长即整份重写；且 `Reload` 后从不重新落盘、`LoadedFromSidecar` 不重置 |
| 工具箱文件面板是 Control 亲和 | `LocalFilePane` / `DeviceFilePane` 的枚举、pull/push、删除、改名、`explorer.exe` 调用均在 UI 侧 |

### 3.4 工程与分发

| 事实 | 依据 |
| --- | --- |
| 测试基线以 `docs/TESTING.md` 为准，**本笔记不内联用例数** | `dotnet test` 可用且会 await async 用例；原记 848 已过期，见 §14.5 |
| 等价对拍资产已存在 | `ChunkedEquivalenceTests`（分块 ≡ 整份）、`SidecarEquivalenceTests`、`FilterEngineBlockScanTests`（SIMD ≡ 标量）、`LogDocumentTests` 增量 ≡ 全量、`FilterEngineTests` `FilterTail` ≡ `ApplyFilter` |
| 引擎用例大量反射私有成员 | `LogDocumentTests` 反射 `OpenMap` / `TrimPartial` / `MergePart` / `ScanRange`；`FilterEngineTests` 同类 |
| CI 无 Rust 工具链 | `.github/workflows/ci.yml` 只有 `windows-latest` + `setup-dotnet` + build/test 两步 |
| 发布是两 exe 先后 publish 到同一目录再压 zip | `docs/BUILDING.md`；产物验证是硬编码 `Test-Path` 清单；禁 `PublishSingleFile`；`SelfContained=true` 绝不能进 csproj（沿 ProjectReference 传染打断 CI） |
| fdd 包当前 2.0 MB | `docs/BUILDING.md` 体积表，其中 1.2 MB 是 README 演示 gif |
| `.gitignore` 全局忽略 dll；Cargo / npm 产物目录已加模式 | `*.dll` 全局忽略；`target/`（:17）、`node_modules/`（:18）由目录重组批次补齐（§13.4）；`*.dll` 对预编译 dll 分发的影响见 C9 |
| 版本号唯一来源 | `Directory.Build.props` 的 `<Version>`，发版同步入库 5 处 |
| 无任何 native 产物 | `docs/BUILDING.md` 全篇无 NativeAOT / 原生依赖步骤 |

## 4. 目标边界

### 4.1 归属划分

```
+---------------- C# : 配置 + 显示 ----------------+
| 设置 / MRU / Loc / 对话框 / DPI / 字号            |
| 虚拟列表渲染：颜色、标记、行号列、截断文案         |
| 设备枚举与选择 / 工具箱 UI / 截图(GDI+)           |
| 分页页缓存 + 查询世代                            |
+------------------------+-------------------------+
                         |  C ABI (P/Invoke)
                         v
+---------------- Rust : 全部 log 文件操作 --------+
| probe(文本/编码) -> acquire(本地|代理|设备)        |
| -> spool -> 索引(9列 + intern + 崩溃打标)         |
| -> 结果集持有 -> 过滤(全量/增量) -> 分页供数      |
| -> sidecar 落盘与淘汰 -> 导出 -> 增长/重载策略     |
| -> 会话生命周期与租约                             |
+------------------------+-------------------------+
                         ^
                         |  同一个 dll，koffi (async)
+---------------- Node.js MCP ---------------------+
| JSON 薄壳：probe/count/window/trace_field/...     |
+--------------------------------------------------+
```

### 4.2 三处例外（必须显式写进非目标，否则边界不自洽）

| 例外 | 内容 | 理由 |
| --- | --- | --- |
| 设备侧 | 设备枚举/连接/monitor、server 自愈、截图、工具箱文件面板（pull/push/删除/改名）留 C# | `AdbManager` 在 UiCore 且被两个 exe 共享；截图是 GDI+；文件面板 Control 亲和。**「从设备取日志字节并落文件」归属另议，见 C2** |
| Windows 外壳 | `SaveFileDialog` 等模态框、`explorer.exe` 集成留 C#；命名管道客户端与活性锁归属见 C3 | 模态框必须由 UI 进程呈现；策略在 Rust、呈现在 C#，经一次 consent 往返（Rust 发「需确认(size)」，C# 弹框回答） |
| 配置与文案 | 过滤参数解析（`SplitTerms` / `ParseMinutes` / `ParseInts`）、`Loc`、进度呈现、设置存储 | 按 D1 定义就归 C#；Rust 只回错误码/事件码与结构化参数 |

### 4.3 分页契约原语（SQL 窗口逐项对应）

| SQL 窗口 | 原语 | 现状对应 |
| --- | --- | --- |
| `SELECT ... WHERE <filter>` | `open(source, filter) -> handle, total_count` | `ApplyFilter` → `SetRows` |
| `LIMIT n OFFSET m` | `page(handle, offset, limit, buf, cap)` | `DocRow` + `MakeFields` |
| keyset anchor | `locate(handle, doc_row) -> ordinal \| nearest_ordinal` | `ModelRowOf` / `NearestModelRow` |
| 取完整大字段 | `full_text(doc_row, buf, cap)` | F3 / 复制原文 |
| `COUNT(*)` 增量 | `appended(new_count)` 事件（轮询原子计数） | `AppendRows` |
| `SELECT ... INTO OUTFILE` | `export(handle \| ids, path)` | `GetRowsCopy` + `ExportRows` |
| 语句取消 | `cancel(handle)` + 原子标志 | `CancellationToken` |

**导出一次都不跨界**：Rust 直接写盘，C# 只传查询句柄或标记行号集合。这消掉了上一版「导出要把 8 列拷回 C#」与「千万命中 = 40 MB `int[]` 跨界」两笔成本。

> **契约缺口（2026-11-10 审查，见 §14.1 / §14.2）**：本表假定 `open(filter)` 由 Rust 执行过滤、结果集常驻 Rust。但 §5 刀2 明写「过滤仍留 C#」，该刀下本表第一行不成立；且表内没有承载 `FilterSpec.MarkedOnly` 所需标记集合的原语。**两处未拍板前，本表不得作为刀0 接口的定稿依据。**

### 4.4 缓冲区与字段布局

- **调用方给缓冲区，Rust 填**：`page(..., buf, cap) -> written / needed`。一页 200 行 × ~200 B ≈ 40 KB，按页拷出，微秒级 memcpy。**不返回长期有效的裸指针**，这是 D2 消解 C1/C2 的机制。
- 页内字段建议两段：定长数值列（doc_row / pid / tid / lvl / tag_id / msg_len / 崩溃字段）+ 变长字符串区（offset/len 索引）。复用 sidecar 已有的列式 + 字典形状，避免逐字段 marshal。
- **tag 字典按查询下发一次**，行内只带 tagId（同 sidecar 的 intern 口径）。
- 字符串一律 UTF-8，C# 侧物化为 `string` 后缓存；Rust 不保证缓冲区内容跨调用有效。
- **长消息**：页内带截断后的 message + 原始长度；`MSG_LIMIT = 4000` 与「…(+N 字符)」文案是显示策略，留 C#；全文经 `full_text` 单行按需取。
- **崩溃摘要拆分**：Rust 回结构化 `(exc, pkg, kind, time)`，兜底提取（现 `CrashExtractor.TryExtract`）归 Rust；`Loc.F("☠ …")` 拼字归 C#。

### 4.5 FFI 契约与 panic 安全

分页之后契约面从上一版的 8 条缩到 4 条：

1. **句柄生命周期**：不透明句柄，`open` 创建、`close` 释放；关闭顺序固定「停查询/扫描 → 释放句柄 → 释放内部映射」。C# 侧任何页缓存不得跨句柄关闭存活。
2. **缓冲区约定**：谁分配、容量语义、`written` 与 `needed` 的含义、截断标记。
3. **错误码**：负值/枚举统一口径，两个宿主共用同一张表；panic 有独立码。
4. **panic 边界**（D5，口径核定）：
   - 每个 `extern "C"` 导出函数都是「薄包装 + `catch_unwind`」，真实逻辑放内部函数；捕获后转错误码，**绝不让 unwind 逃出边界**。
   - **`panic = "abort"` 不是等价替代**：abort 模式下 `catch_unwind` 捕获不到，panic 直接终止进程。它是「放弃宿主存活、换体积与速度」的另一条路，与 D3（dll 在宿主进程内）冲突，故不取。
   - 现代 Rust 中 unwind 逃出 `extern "C"` 会触发 **abort**（要允许跨边界展开须显式 `extern "C-unwind"`）；历史口径称其为 UB。**后果因此是「宿主进程直接死」而非静默损坏**——对 GUI 与 Node 同进程加载的 dll 依然不可接受，防护必须做。**该口径已由本机实测坐实（2026-11-08），见 §13.3；panic 注入验证项落在刀2 验收，不在刀1。**
   - `catch_unwind` 需要 `UnwindSafe`，跨可变状态时用 `AssertUnwindSafe`——**这是断言不是证明**，故引出下一条决策 C4：捕获 panic 后句柄是否可继续用。
   - **不用回调**（沿用上一版 §5.4）：进度用原子计数 + 宿主轮询，取消用原子标志。因此不存在「Rust 栈上回调进 C#/Node」的外来异常问题，koffi 侧也不需要 threadsafe function。
   - koffi 无法替宿主捕获 Rust panic，这是 D4 选甲之后必须自付的一项。

**并发硬约束**：页查询不得被在途全量扫描/过滤堵住。dll 在进程内，堵住的直接是 UI 线程或 Node 事件循环。Rust 侧须让读页走已发布索引的快照（arc-swap / RCU 形状），扫描在另一份上跑——对应现状分块加载的「前缀发布」语义。

**Node 侧硬约束**：长扫描必须走 koffi 的 async 调用或 worker_threads，否则堵死事件循环、MCP 整体卡住。MCP 会话必须长驻（索引生命周期跟进程走），沿用 `PROJECT-STRUCTURE-RESTRUCTURE.md` §6 的既有约束。

### 4.6 Cargo 布局与两个宿主

```
crates/core   纯算法 + IO，无 FFI 面
crates/cabi   cdylib：extern "C" + catch_unwind 薄包装  ->  logcat_core.dll
                                                          ^          ^
                                                          |          |
                                                    C# P/Invoke   Node koffi(async)
crates/cli    同一 core 的可执行壳  ->  对拍 oracle / 调试 / MCP 退路
```

- **CLI 不是可选项**：dll 无法直接 diff 文本，语义对拍靠 CLI；同时它是 koffi 万一不可用时的退路（Node 改拉 stdio 子进程，`core` 一行不改）。
- 三个 crate 共用 `core`，导出面只在 `cabi`，`core` 内不出现 `unsafe` FFI 类型。
- 目录落点：`logcat-core/` 按 Cargo workspace + 一个 C# interop 工程建。该结论原见一份已归档的目录重组笔记，此处只作状态说明、不作依据（AGENTS §3.2）；刀0 落地前须在本笔记内重新确认 §14.4 列出的三条约束。

## 5. 拆分顺序

按「接缝先于语言、数据所有权先于算法」排。刀数比上一版多，但每刀 diff 更小、各有独立验收面。

> **顺序已调整（2026-11-10 拍板）**：输入先行 + 三轨并行，见 §15.9。下表保留原始七刀描述供对照，实际推进按 §15.9 的三轨执行；刀0 / 刀2 仍在 GUI 主航道内，先后按 C5 在轨 A 里定。

### 刀0 C# 内立分页接缝（语言无关）

- **内容**：定义分页查询接口（§4.3 原语），实现层是现有 `LogDocument` / `FilterEngine` 的薄适配；改造 `LogListView` 消费分页——`SetRows` / `AppendRows` / `GetRowsCopy` / `DocRow` / `ModelRowOf` / `NearestModelRow` / `SegLastGe` 全部假设本地持有完整行号数组，须改为查询或删除；缓存键从模型行号改为「查询世代 + 行号」。
- **验收**：滚动、锚点保持（过滤前后屏幕位置不变）、实时追加、标记、导出、F3 全文、切语言清缓存；全量用例无新增失败（数字以 `docs/TESTING.md` 当时值为准）+ 维护者实机（2K 缩放屏）验证。
- **为什么先做**：把「UI 改造风险」与「语言迁移风险」分开。同时改虚拟列表 + 换语言 + 跨边界是三重变量，对拍失败无法归因（同上一版 S4 的理由）。
- **资产账**：接口与控件改造是**最终资产**（换 Rust 后原样留着，进程内适配几乎原样变成 P/Invoke 实现）；只有那层薄适配将来会死。**价值前提**：它就是 core 边界本身，无论 core 最终是 C# 还是 Rust 都留着；只有连 MCP 与边界拆分一起取消，它才是纯成本。
- **门禁**：属 UI 交互改动，须先出设计评审通过才编码。是否先行见 C5。

### 刀1 `crates/core` + `crates/cli`：索引内核

- **内容**：`LogParser` + `LogDocument` 扫描/装配路径（`ScanRange`、`BuildChunkedCore`、`MergePart`）+ `CrashExtractor`；产出 9 列、tag intern、崩溃打标、整份与分块两种装载；sidecar v1 字节格式写出。
- **对拍**：CLI dump 与 C# 逐位 diff（§6）。
- **必须含分块**：主界面走 `BuildChunkedAsync`；分块 ≡ 整份本身就是验收项。
- **崩溃打标不可后置**：它绑在索引期父行，拆出去要么丢崩溃面板，要么多一遍全量扫描。
- **本刀不动 C#**：Rust 写 sidecar，C# 侧复用已有 `LoadIndexed` 通路，零产品代码改动。**但「C# 能打开」不等于「Rust 写的 sidecar 被读到了」**：命中失败是静默降级（`frmMain.Sidecar.cs:9-10`，只写启动日志、UI 无感），必须另设硬判据（§14.3 / C18）。
- **sidecar 的隐藏契约（须写成字节级 spec 并作为刀1 交付物）**：除 §3.3 已记的 magic / 版本 / 8 段宽度 / 段长外，还有缓存条目名派生 `SidecarKey.DeriveName`（`GetFullPath` + 文件名主干 `Sanitize` + `FNV-1a(UTF8(path.ToUpperInvariant()))` + `x16`，`SidecarCache.cs:124-129`）与身份摘要 `SidecarIdentity`（64 KB 头尾采样 FNV-1a + `LastWriteTimeUtc.Ticks`，`:42`、`:58-77`）。`ToUpperInvariant` 与 Rust `to_uppercase` 的口径差异与刀3 必须验的小写化问题同源。

### 刀2 `crates/cabi`：dll 接管分页（索引侧）

- **内容**：C ABI 导出面 + `catch_unwind` 包装 + 句柄/缓冲区/错误码契约；C# 把刀0 的进程内适配换成 P/Invoke。**过滤仍留 C#**。
- **验收**：UI 不动、行为不变；panic 注入测试（导出函数内故意 panic，宿主存活并收到错误码）；页查询与在途扫描并发不阻塞。
- **待决（C16，§14.1）**：「过滤仍留 C#」与 §4.3 的 `open(filter) -> handle`、§6.3「刀0 接口即最终契约」不能同时成立——dll 不持有命中行号表就无法回答 `page(offset, limit)`。**本刀的接缝形状未定，不得直接进 propose。**

### 刀3 过滤入 Rust

- **内容**：`ApplyFilter` / 块扫描 / 排除 / 大小写折叠 / `FilterTail`，实现同一个 `open(filter)` 契约；块扫描分派条件与跨块重叠一并外迁。
- **待决（C16，§14.1）**：本刀才是 `open(filter)` 成立的前提。若 C16 取「合并刀2/刀3」，本刀与刀2 同一 change 实施；若取「临时 `set_result(ids)`」，本刀负责删除该接口。另 `C17` 决定标记集合如何随本刀进入契约。
- **定位**：**性能收益≈0**（过滤已贴带宽地板，§3.1），本刀的驱动是「MCP 需要同一套过滤语义」与「结果集归 Rust」。参数解析留 C#。
- **必须验**：`to_lowercase` 与 `ToLowerInvariant` 口径（`ß / ẞ`、土耳其有无点 `i`）；SIMD ≡ 标量；增量 ≡ 全量。

### 刀4 Node koffi + MCP 薄壳

- **内容**：npm 包 + 预编译 dll + koffi async 绑定；长驻会话；工具面按 `MCP-FUNCTIONALITY.md` 与 `MCP-LOG-ANALYSIS-API.md` 的口径取舍（两套口径未拍板，属该 change 的待决项）。
- **前置**：刀3。不必等刀5。

### 刀5 获取链入 Rust

- **内容**：`TextProbe` / 编码判定 / FileProxy 客户端 / spool / 租约与启动清理 / sidecar 存储治理。**诉求「逻辑与显示混合」在这一刀才真正兑现**——把 `frmMain.cs:1087` 那类编排搬出窗体。
- **编码判定是新功能不是迁移项**：口径需先定，见 C7。

### 刀6 设备取流归属

- 见 C2，未拍不实施。

### 不单独切片的两项

- **并行建索引**：语言无关，C# 用 `Parallel` + 现成 `ScanPart` / `Assemble` 同样能拿；在 Rust 侧属刀1 的内部实现选择，不单列一刀，也不在刀1 首版就上（避免「搬语言」与「改算法」同时做）。
- **sidecar 格式**：保持 v1 字节不变（旧缓存不废）；补校验另立 change，见 C6。

## 6. 方法

### 6.1 CLI 先行，对拍直接 diff 文本

dll 不可直接 diff，故 `crates/cli` 提供 `dump` / `selftest`，对拍在文本层做，比在测试框架里比结构体省事。

### 6.2 oracle 用冻结快照 + 现有等价用例

- 刀1 之前用现 C# 代码生成 golden dump（9 列 + tag 字典顺序 + 崩溃行号 + sidecar 字节），小样本入库 `testdata/`，之后**只加不改**。
- 现有等价用例（§3.4）在迁移期原样充当 oracle；进程内 dll 阶段它们仍可跑，直到 C# 引擎实现被删（C8）。
- **时限**：必须在动 `LogDocument` 之前产出快照，否则 oracle 消失。
- **已知缺口**：`SidecarEquivalenceTests` 5 条里有 3 条走内存 `SnapshotIndex` → `LoadIndexed`，根本没落盘；且全部是 C# 写 → C# 读 round trip，**抓不到外部写者的语义错**。必须补「外部写者字节向量」用例。

### 6.3 C# 只换实现不换契约

刀0 立的分页接口就是最终契约；刀2 只把实现从 managed 换成 native。UI 渲染、标记、Loc、设置零改动。

### 6.4 不越界

Rust 只接受本地路径或经其自身获取链产出的本地文件；设备侧与工具箱按 §4.2 例外；`Loc` 整体留 `logcat.UiCore`；DLT 解码不进内核（沿用 `RUST-CORE-EVALUATION.md` C3）。

## 7. 分发与 CI 实账

「编译个 dll」不是一句话的成本，以下逐项都要进 tasks：

| 项 | 现状 | 要做的活 |
| --- | --- | --- |
| CI 工具链 | `.github/workflows/ci.yml` 只有 setup-dotnet | 加 rustup + cargo build + 产物拷贝；GitCode / GitHub 双站都要过；构建缓存与时长 |
| 发布步骤 | 两 exe 先后 publish 到同一目录再压 zip | publish 之后补 cargo 产物拷贝；**sc 与 fdd 两个包各补一条 `Test-Path` 断言** |
| fdd 体积 | 2.0 MB（含 1.2 MB 演示 gif） | 加 2–5 MB dll → **翻倍到 3 倍**，「框架依赖包小」卖点被削弱，需认账 |
| `.gitignore` | 全局忽略 `*.dll`；`target/`（`:17`）、`node_modules/`（`:18`）已由目录重组批次补齐（§13.4） | 只剩一处：确认预编译 dll 的分发方式不被 `*.dll` 误伤（C9） |
| 版本号 | `Directory.Build.props` 唯一来源，发版同步 5 处 | Cargo.toml 版本对齐方式 → **第 6 处**（C10） |
| 属性传染 | `SelfContained=true` 绝不能进 csproj（沿 ProjectReference 传染打断 CI） | dll 拷贝同理别用会传染的条件属性，走 publish 后独立步骤或不传染的 target |
| 覆盖率口径 | `docs/TESTING.md` 只算 Services + Models | Rust 代码不进 coverlet → **覆盖率数字会虚高**，口径必须改（C11） |
| 平台矩阵 | 仅 win-x64 | dll 与 npm 包的 os/cpu 声明（C12） |
| 崩溃隔离 | 无 | dll 在宿主进程内：panic 靠 `catch_unwind` 兜，OOM/abort 直接带走 GUI 或 Node；**没有进程隔离这层保护** |

## 8. 待澄清

### 8.1 需维护者拍板（阻塞 propose）

#### C1 Rust 是否正式定案、阶段 0 探针是否跳过

- **为什么需要澄清**：`RUST-CORE-EVALUATION.md` §8 的判据是「1 GB 上 Rust 并行建索引 vs C# 并行建索引原型」探针后定语言；本轮已在细化 Rust 实施方案，但定案本身未明确。跳过探针等于放弃「C# 并行若达标则性能诉求归零」这条退出路径。
- **有哪些方案**：甲 视为已定案，直接进刀0/刀1；乙 仍跑探针，但探针目的改为「验接缝 + 量净差」，不阻断刀0（刀0 语言无关）；丙 先做 C# 并行索引探针，达标则取消 Rust。
- **当前偏向**：乙——刀0 语言无关可先行，探针与刀1 并行，保留退出路径。

#### C2 设备取流归属

- **为什么需要澄清**：决定 Rust 要不要自己实现一遍 ADB 传输协议（与 UiCore 的 `AdbManager` 并存），也决定 D1「完全归 Rust」有没有例外；阻塞刀6。
- **有哪些方案**：甲 Rust 直连 adb server 取 `logcat -v threadtime`，C# 只管设备选择与展示；乙 C# 继续把字节写进临时文件（现状即如此），Rust 从文件接手。
- **当前偏向**：乙先行，例外显式写进范围；协议稳了再评估甲。甲的代价是设备断连/重连语义要重验。

#### C3 Windows 外壳的边界粒度

- **为什么需要澄清**：命名管道客户端、`.alive.lock` 的 FileShare 活性语义、跨实例启动清理是否入 Rust，决定刀5 的范围与验证面；FileShare 语义在 Rust 侧需重验，不是照抄。
- **有哪些方案**：甲 全入 Rust（windows-rs），活性锁语义重验；乙 管道客户端入 Rust、活性锁与启动清理留 C#；丙 全留 C#，Rust 只吃已 spool 好的本地路径（等于上一版 §5.5，与 D1 冲突）。
- **当前偏向**：甲，但活性锁与启动清理必须在刀5 里单列验证任务。

#### C4 catch 到 panic 之后句柄是否可继续用

- **为什么需要澄清**：`AssertUnwindSafe` 是断言不是证明；panic 可能发生在半更新状态或持锁期间（锁中毒）。继续查询可能读到损坏索引，且 sidecar 无 payload 校验、读侧不 bounds-check 字典 id，损坏会表现为越界而非报错。
- **有哪些方案**：甲 标记句柄 poisoned，返回致命错误码，宿主关闭重开；乙 尝试原地恢复继续服务；丙 直接 abort 宿主（等于放弃 D5）。
- **当前偏向**：甲。

#### C5 刀0 是否先行

- **为什么需要澄清**：刀0 是 UI 交互改动，须设计评审通过才编码；它决定后续所有刀的契约形状，也决定第一笔投入是否语言无关。
- **有哪些方案**：甲 刀0 先行（C# 内立分页接缝并验收，再动 Rust）；乙 刀1 先行（Rust 索引器 + sidecar 对拍），分页接缝与刀2 一起做；丙 只做刀1 验证语义等价，分页改造推迟到语言定案后。
- **当前偏向**：甲。乙会把「UI 改造」与「跨语言对拍」两个变量叠在一起。

#### C8 C# 引擎实现的最终去留

- **为什么需要澄清**：决定全量用例里那批引擎用例（`LogDocumentTests` / `LogParserTests` / `FilterEngineTests` / `CrashTests` / 等价用例）是迁去 Rust、还是长期留着；也决定迁移完成后是否存在双份语义。数字以 `docs/TESTING.md` 为准。
- **有哪些方案**：甲 保留一个发布周期作 fallback（native 加载失败/身份不命中时降级），之后删；乙 刀2 验收即删；丙 长期双实现并存。
- **当前偏向**：甲。丙 是长期净负（双份语义要对齐）；乙 没有退路。

#### C12 平台矩阵

- **为什么需要澄清**：决定 CI 构建矩阵与 npm 包的 os/cpu 声明；GUI 是 WinForms（Windows-only），但 MCP 理论上可跨平台。
- **有哪些方案**：甲 v1 只 win-x64；乙 加 win-arm64；丙 加 linux/darwin（仅 MCP 侧可用）。
- **当前偏向**：甲。

#### C16 刀2 与刀3 之间的接缝（2026-11-10 新增，阻塞 propose）

- **为什么需要澄清**：§5 刀2 明写「过滤仍留 C#」，但 §4.3 的 `open(filter) -> handle` 与 §6.3「刀0 接口即最终契约」都假定结果集在 Rust。dll 不持有命中行号表，就回答不了 `page(offset, limit)`「第 offset 行是谁」。不解决，刀2 没有独立验收面。详见 §14.1。
- **有哪些方案**：甲 刀2 加临时 `set_result(ids)`、刀3 删除（契约定两次，§6.3 作废）；乙 刀2 与刀3 合并 / 刀3 提前（契约只定一次，单刀 diff 变大）；丙 把 §4.3 改成分阶段的 `open(source)` + `query(filter)`，刀2 只实现前者。
- **当前偏向**：乙。

#### C17 标记集合的归属与同步（2026-11-10 新增，阻塞 propose）

- **为什么需要澄清**：`FilterSpec.MarkedOnly` 让 `_marked` 成为过滤输入（`FilterEngine.cs:871`、`:1057`），但 §4.1 / §3.2 把它划为 C# 显示状态；结果集与过滤归 Rust 后，§4.3 契约表与 `C13`～`C15` 都没有同步原语。详见 §14.2。
- **有哪些方案**：甲 标记归 Rust，暴露 `mark(doc_row)` / `unmark(doc_row)` 增量同步；乙 每次查询把标记集合随 filter 下发；丙 MarkedOnly 留 C# 做二次过滤（结果集分裂，不推荐）。
- **当前偏向**：甲；并需把标记变更纳入 `C14` 的查询世代失效条件。

#### C18 刀1 sidecar 的验收判据（2026-11-10 新增，阻塞 propose）

- **为什么需要澄清**：sidecar 命中失败静默降级，「C# 能打开」不构成「Rust 写的缓存被消费」的证据；且缓存键派生与身份摘要是未成文的隐藏契约。详见 §14.3。
- **有哪些方案**：甲 加 `ChunkedLoadInvocations == 0 && LastSidecarLoadMs > 0` 硬断言 + 字节级 spec + 覆盖缓存键的外部写者向量；乙 只做 CLI dump 字节对拍，不验 C# 命中；丙 刀1 不写 sidecar，推迟到刀2。
- **当前偏向**：甲。

### 8.2 design 阶段定（本笔记给偏向，不阻塞 propose）

#### C6 sidecar 是否借机补校验

- **为什么**：现状无 payload 校验和、读侧不 bounds-check 字典 id；外部写者语义错会表现为越界。
- **方案**：甲 保持 v1 字节不变，校验另立 change；乙 升 v2 加校验和（旧缓存全废）。
- **偏向**：甲。格式变更与迁移对拍混在一起会无法归因。

#### C7 编码判定口径

- **为什么**：UTF-8/GBK 判定是新功能不是迁移项，需要行为契约；`MCP-LOG-ANALYSIS-API.md:15` 已写了一套启发式（`utf8_cn_hits` / `gbk_cn_hits`）。
- **方案**：甲 采用该启发式并写成 spec；乙 另定口径；丙 先只做 UTF-8，GBK 后置。
- **偏向**：甲，但必须独立 change 立 spec，不夹在刀5 里。

#### C9 dll 的分发方式

- **为什么**：`.gitignore:44` 全局忽略 `*.dll`；入库预编译产物与「构建产物不入 git」的项目红线冲突。
- **方案**：甲 不入库，CI/本地 cargo build 产出后拷进 publish 目录；乙 入库并开 ignore 例外；丙 走独立包管理。
- **偏向**：甲。

#### C10 版本号第 6 处

- **为什么**：`Directory.Build.props` 是唯一来源，Cargo.toml 独立版本会漂移。
- **方案**：甲 build 脚本从 props 注入 Cargo 版本；乙 发版清单加一处手工同步；丙 dll 不标版本、只随包走。
- **偏向**：乙（与现有 5 处同批手工同步，不引构建期生成物）。

#### C11 覆盖率口径

- **为什么**：Rust 代码不进 coverlet，Services + Models 的分母会缩小，覆盖率数字虚高。
- **方案**：甲 口径改为「C# 侧覆盖率」并显式声明不含 Rust；乙 引 Rust 覆盖率工具合并统计；丙 迁移期暂停覆盖率门禁。
- **偏向**：甲，刀2 落地时同批改 `docs/TESTING.md`。

#### C13 页字段布局与缓冲区细节

- **为什么**：决定 P/Invoke 签名与 marshalling 成本，两个宿主共用同一张表。
- **方案**：甲 定长数值列 + 变长字符串区（§4.4）；乙 逐字段结构化 DTO 序列化；丙 页内直接返回 UTF-8 JSON。
- **偏向**：甲（复用 sidecar 列式形状）；丙 仅用于 MCP 侧，不用于 GUI 热路径。

#### C14 查询世代与缓存失效

- **为什么**：过滤变更 / 追加 / 重载时 C# 页缓存必须失效，否则渲染旧结果。
- **方案**：甲 单调世代号，页响应带世代，不匹配即丢弃；乙 每次变更由 Rust 主动通知清缓存。
- **偏向**：甲（无回调，与 §4.5 一致）。

#### C15 句柄线程安全与并发模型

- **为什么**：C# 可能多线程 P/Invoke，Node 侧 koffi async 天然并发；§4.5 的「页查询不被扫描堵住」需要具体机制。
- **方案**：甲 句柄内 arc-swap 快照 + 导出函数线程安全；乙 全局串行锁；丙 每线程独立句柄。
- **偏向**：甲。乙 直接违反并发硬约束。

## 9. 不要做的事

1. 不要先做 `core-extraction` 1a/1b。
2. 不要与工程目录重组合批（`BACKLOG-MCP-AND-FORMATS.md` D7 理由仍成立）；但**重组宜早于刀1**——`logcat-core/` 要装 Cargo workspace，反过来决定目标布局。
3. 不要在刀1 首版就上并行建索引。
4. 不要顺手迁 `Loc` / DLT / 工具箱文件面板。
5. 不要返回长期有效的裸指针（D2 已消解该需求）。
6. 不要用回调做进度/取消（两个宿主都受影响，Node 侧尤甚）。
7. 不要用 `panic = "abort"` 替代 `catch_unwind`（§4.5）。
8. 不要在迁移期改 sidecar 字节格式（C6）。
9. 不要把过滤外迁当作性能项立项（收益≈0，驱动是 MCP 与结果集归属）。

## 10. 显式猜测

| 内容 | 标注 |
| --- | --- |
| 一页 200 行 ≈ 40 KB、按页拷出为微秒级，因而同步 `RetrieveVirtualItem` 未命中取一页可接受 | **推断**，未实测；需在刀0 用进程内适配先量，刀2 再量 native |
| 快速拖动滚动条不会出现可感知停顿 | **未实测**；进程内 dll 下风险低于进程外，但仍需维护者实机（2K 缩放屏）验证 |
| 刀0 的薄适配将来几乎原样变成 P/Invoke 实现 | **推断**，取决于 C13 的字段布局是否与现有列形状足够接近 |
| 冻结快照 + 现有等价用例足以覆盖迁移期语义 | **推断**，已知缺口是 sidecar 外部写者向量（§6.2） |
| koffi async 足以承载 MCP 的粗粒度长任务 | **API 形态已实测**（koffi 2.16.3 用 `fn.async(args, cb)`，见 §13.2）；**长任务承载能力未实测**，仍属推断；退路是 CLI + stdio（§4.6） |
| fdd 包加 dll 后翻倍到 3 倍 | **推算**，基于 `docs/BUILDING.md` 的 2.0 MB 与评估笔记的 2–5 MB 估计，未实测产物体积 |
| 现代 Rust `extern "C"` unwind 逃逸触发 abort | **已实测（2026-11-08 本机 rustc 1.88.0）**：确认触发 abort，宿主进程直接死；`catch_unwind` 薄包装可存活。详见 §13 |
| 七刀的评审面是否偏大 | **未定**，取决于每刀实际 diff 规模；刀0 与刀1 可各自独立验收 |

## 11. 关联

- `RUST-CORE-EVALUATION.md`：语言决策、收益成本、对拍契约；其 §4 范围、§5 接缝推荐、§6 内存峰值口径已被本笔记修订（该文件顶部有状态说明）。
- `PROJECT-STRUCTURE-RESTRUCTURE.md`：**已归档，仅作状态说明**，不作依据（AGENTS §3.2）；其刀0 关键约束已文字转述进 §14.4，供重新确认。
- `BACKLOG-MCP-AND-FORMATS.md`：D2 顺序、D6 分发、D7 切片（均被推翻或沿用理由）。
- `MCP-FUNCTIONALITY.md`、`MCP-LOG-ANALYSIS-API.md`：MCP 工具面两套口径未拍板，属刀4 的待决项；分析型原语与分页原语同源（锚点 + 窗口 + 上下文）。
- `DLT-INTEGRATION.md`：解码归属，范围外。
- `2026-11-07-MESSAGE-INDEX-MECHANISM.md`：索引与取文本机制，刀1 的现状依据。
- `docs/TESTING.md`、`docs/BUILDING.md`：测试基线与发布步骤，§7 的实账来源。

## 12. 下一步

1. **先拍 §14 的三条审查项**：C16（刀2/刀3 接缝）、C17（标记集合）、C18（刀1 验收判据）。它们决定 §4.3 契约能否定稿；未定不得进入 propose。
2. 维护者拍板 §8.1 的 C1 / C2 / C3 / C4 / C5 / C8 / C12。
3. C5 定「刀0 先行」后，走 `/opsx:propose` 建刀0 的 change（UI 交互改动，设计评审通过才编码）；C1 若取乙，探针与刀1 并行。
4. §8.2 各条在对应刀的 design 阶段定稿，本笔记不实现任何 FFI 或 Rust 代码。
5. §15 的 C19（第一步交付形态）与 C20（顺序）已于 2026-11-10 拍板，见 §15.9；剩 C21（采集字节保真口径）待定。下一步按 §15.9 的「第一个 change」推进。

## 13. 本机环境实测（2026-11-08）

> 用途：回答「Rust 本机是否支持」。全部为**本机实测**，探针工程建在 `.tmp-test/`（git-ignored），未改动任何产品代码。

### 13.1 工具链现状

> 2026-11-08 19:20 升级：stable 由 1.88.0 升到 **1.99.1**（rustup 1.28.2 → 1.29.3）。下表为升级后状态；升级后重跑了 §13.2 / §13.3 全部回归，行为与升级前一致。

| 项 | 实测值 | 结论 |
| --- | --- | --- |
| rustc / cargo | `rustc 1.99.1 (58cf8f9c3 2026-10-13)` / `cargo 1.99.1 (5f94df478 2026-08-27)` | 可用；`rustup check` 显示 up to date |
| rustup | `1.29.3 (edbc831ce 2026-11-02)` | 已自更新到最新 |
| host triple | `x86_64-pc-windows-msvc` | 与项目 win-x64 目标一致 |
| rustup toolchain | `stable-x86_64-pc-windows-msvc`（默认且活跃） | 单工具链，无版本漂移 |
| 已装 target | `x86_64-pc-windows-msvc`、`x86_64-unknown-linux-gnu` | **无 `aarch64`**，故 C12 若取乙（win-arm64）需先 `rustup target add`；C12 偏甲（仅 win-x64）无阻 |
| MSVC 链接器 | VS 18 Community（`C:\Program Files\Microsoft Visual Studio\18\Community`） | rustc 经 vswhere 自动定位，无需手工配 `link.exe`；1.99.1 下自动定位仍生效 |
| 辅助工具 | `rustfmt 1.10.0-stable`、`cargo clippy 0.1.99` | 均随 stable 一并安装，CI 可直接用 |
| 坑 | `link.exe` 在 PATH 上命中的是 Git 自带的 `D:\06.install\Git\usr\bin\link.exe`（非 MSVC） | **不可**手工改 PATH 指向 MSVC；保持 rustc 自带探测即可。写 CI 时不要把 Git 的 bin 目录提前 |
| .NET SDK | 10.0.401（另有 9.0.317） | P/Invoke 宿主侧无阻 |
| Node / npm | v26.8.1 / 11.19.0 | D4 的 koffi 宿主侧就绪 |
| crates.io | 可达（`cargo search` 成功拉取索引） | 依赖可正常下载 |
| 版本固定 | 仓库无 `rust-toolchain.toml` | 升级无阻；**若后续引入 Cargo workspace，建议补一个**以免 CI 与本机版本漂移 |

### 13.2 端到端链路实测（三个宿主形态全通）

| 验证项 | 结果 |
| --- | --- |
| `crate-type = ["cdylib", "rlib"]` 编译 | 通过，产出 `rustprobe.dll`（110 KB）+ `rustprobe.exe` |
| `extern "C"` 导出符号 | `probe_add` / `probe_strlen` 均在 PE 导出表可见 |
| **C# P/Invoke**（`DllImport` + `CallingConvention.Cdecl`） | 通过：`probe_add(20,22) = 42`、`probe_strlen = 13` |
| **Node koffi 同步调用** | 通过：`probe_add = 42`、`probe_strlen = 15` |
| **Node koffi 异步调用**（`fn.async(args, cb)`） | 通过：`= 123`。**注意 API 形态**：koffi 2.16.3 无 `lib.asyncFunc`、无 `koffi.abiAsync`，异步是函数对象的 `.async(...)` 方法，签名 `(err, result)` |
| 第三方依赖（`memmap2` 0.9 / `rayon` 1.10 / `regex` 1.11） | 全部拉取并编译通过（28.8 s，含 LTO），支撑 §4.6 的 mmap + 并行建索引形态 |
| `memmap2::Mmap::map` 打开文件 | 通过（返回文件长度），对标 C# `MemoryMappedFile` |
| **edition 2024** | 通过。**但 `#[no_mangle]` 必须写成 `#[unsafe(no_mangle)]`**（edition 2024 起裸 `#[no_mangle]` 报 `error: unsafe attribute used without unsafe`）。未来的 `crates/cabi` 若用 edition 2024，**每个导出函数都要加 `unsafe(...)` 包裹**，否则全量编译失败 |

### 13.3 panic 边界实测（§10 猜测行的实测结果）

| 形态 | 结果 |
| --- | --- |
| 裸 `extern "C"` 内 panic（无防护） | **宿主进程直接死**。Rust 输出 `panic in a function that cannot unwind` → `thread caused non-unwinding panic. aborting.`，进程退出码 `-1073740791`（0xC0000409）。**不是**静默损坏、不是 C# 异常 |
| `catch_unwind(AssertUnwindSafe(...))` 薄包装（D5 形态） | **宿主存活**：panic 被捕获并转错误码（实测返回 `-999`），同 dll 其余导出函数仍可正常调用（`still_alive = 7`） |

结论：D5 决策（逐导出函数 `catch_unwind` 薄包装）在本机工具链上**确认为必需且有效**；§4.5 中「unwind 逃出 `extern "C"` 触发 abort」的文档口径**已由实测坐实**。C4（捕获后句柄是否可继续用）仍需独立设计——本次只证明「进程不死」，未验证「句柄未损坏」。

### 13.4 仓库侧就绪度

| 项 | 现状 | 缺口 |
| --- | --- | --- |
| `.gitignore` | 已含 `target/`、`node_modules/`（泛化模式，§7 该项已由目录重组批次补齐）、`*.dll`、`.tmp-test/` | C9「预编译 dll 分发」仍需定；`*.dll` 全局忽略会误伤入库方案 |
| CI（`.github/workflows/ci.yml`） | 仅 `windows-latest` + `setup-dotnet` + build/test | **无 Rust 工具链**：需加 `rustup` / `cargo build` / 产物拷贝，GitCode + GitHub 双站都要过（§7） |
| `logcat-core/` 目录 | 不存在 | 需承载 Cargo workspace + C# interop 工程（§4.6） |
| 在途 change | 无（`openspec/changes/` 除 `archive/` 外为空） | 本计划仍停在 explore 阶段，等 §8.1 拍板 |
| 测试基线 | 以 `docs/TESTING.md` 为准（本笔记不内联数字） | 迁移期需保留为 oracle；C11 覆盖率口径待改 |

### 13.5 实测未覆盖（仍为待实测）

- 1 GB 量级的 Rust vs C# 并行建索引净差（§8.1 C1 探针，**尚未做**）
- `to_lowercase` 与 `ToLowerInvariant` 的 Unicode 口径对齐（§7-3，刀3 必须验）
- C# 侧实际 P/Invoke 的 marshalling 开销与页大小匹配（刀0/刀2 量）
- dll 体积实测（§10「fdd 包翻倍到 3 倍」仍是推算，本机探针 dll 仅 110 KB，但那是空壳，不能代表真实内核体积）

## 14. 审查发现（2026-11-10，待维护者拍板）

> 本节是**审查意见**，不是已定决策：没改代码、没改契约，只把「哪里走不通」写清楚。
> 读法：先看 §14.1（最要紧），再看 §14.2；§14.3 是验收陷阱；§14.4 是从归档档回收的约束；§14.5 / §14.6 是文档核对。
> 对应待澄清项已加入 §8.1：**C16 / C17 / C18**。这三条未拍板前，刀1～刀3 不进入 propose。
> 除带 `file:line` 的事实与「已实测」外，本节的方案偏向一律是**建议/推断**，尚未验证。

### 14.1 刀2 中间少了一环：dll 不知道该显示哪些行

先用现在的代码讲一遍「分页」到底分的是什么。过滤完成后，C# 手里有一张**命中行号表**（`int[]`）。虚拟列表的 `VirtualListSize` 就是这张表的长度；滚到第 1000 行时，`DocRow(1000)`（`LogListView.cs:344`）先查表拿到文档行号，再按行号去 mmap 读文本、拼出这一行。也就是说，**「第几行显示什么」完全由这张表决定，谁持有它，谁就得负责分页**。

七刀计划里这张表的归属是这样变的：

| 阶段 | 谁拿着命中行号表 | `page(handle, offset, limit)` 能否回答「第 offset 行是谁」 |
| --- | --- | --- |
| 刀0 | C#：`FilterEngine` 出 `int[]`，薄适配把它包成接口 | 能——接口背后就是这张本地数组 |
| 刀2 | **还是 C#**（§5 明写「过滤仍留 C#」） | **不能**——dll 只有文档句柄，没有这张表 |
| 刀3 | Rust：过滤搬过去，`open(filter)` 才成立 | 能 |

冲突点：§4.3 把 `open(source, filter) -> handle, total_count` 写成最终契约，§6.3 又说「刀0 立的分页接口就是最终契约，刀2 只把实现从 managed 换成 native」。可刀2 时 filter 还在 C# 执行，`handle` 里没有结果集，dll 就无从分页。**「只换实现、契约不动」和「过滤仍留 C#」不能同时成立。**

要让刀2 跑起来，能想到三条路，每条都破坏现有承诺：

- **甲**：临时加一个 `set_result(ids)`，C# 把 `int[]` 推给 dll，刀3 再删掉。→ 契约被定两次、又作废一次，§6.3 不成立；而且 §4.3 刚说「导出一次都不跨界，消掉了千万命中 = 40 MB `int[]` 跨界」，这一刀又把成本请回来了。
- **乙**：把刀3 提前或与刀2 合并，dll 接管分页时一并接管过滤。→ 契约只定一次，但单刀 diff 变大，与 §5「每刀 diff 更小」的初衷有张力。
- **丙**：把 §4.3 改成分阶的 `open(source)` + `query(filter)`，刀2 只实现前者。→ `page` 得退化成「C# 给一批 doc_row、dll 回这批行的文本」，结果集仍留在 C#，与 D2「C# 不持有结果集」冲突。

**审查偏向：乙（建议，未拍板）**。理由：dll 一旦持有结果集，过滤必须在同一侧；硬拆只会造一个注定要删的接口。刀3 自己都写了「性能收益≈0」，单独成刀的驱动本就不强。对应 C16。

**为什么算阻塞**：刀2 的验收写的是「UI 不动、行为不变」。缺了这一步，UI 拿不到正确的行，该刀没有独立验收面，而「每刀各有独立验收面」正是 §5 把七刀拆开的理由。

### 14.2 标记集合被当成「显示状态」，其实它是过滤输入

§3.2 / §4.1 把 `_marked` 归到 C# 渲染侧（行号列的 `●`、背景色）。但代码里它同时是过滤条件：

| 位置 | 作用 |
| --- | --- |
| `frmMain.cs:1343` | 工具栏「仅标记行」写进 `spec.MarkedOnly` |
| `FilterEngine.cs:871` / `:1057` | 全量过滤、增量过滤都判 `spec.MarkedOnly && !marked.Contains(i)` |
| `frmMain.cs:1251` / `:1313`、`frmMain.Load.cs:144` / `:157` | `ApplyFilter` / `FilterTail` 四个调用点都把 `_marked` 传进去 |
| `frmMain.cs:1395` / `:1428` | 「跳到标记」用 `ModelRowOf` 换算；「导出标记行」把集合排序后传出 |

结果集和过滤归 Rust 之后，MarkedOnly 只能由 Rust 执行，可 §4.3 契约表、`C13`～`C15`、§8 待澄清项里**没有任何一条原语负责把标记集合送过去或保持同步**。`export(handle | ids, path)` 已经承认标记行号要跨界，只是没把它接进过滤契约——这是同一类数据在两处口径不一致。

补法建议：把标记重新定性为**数据**而非 UI 状态；Rust 持有集合，暴露 `mark(doc_row)` / `unmark(doc_row)` 增量同步（`ToggleMark` 只跨一个 int，勾选「仅标记行」时不必全量推集合）；并把标记变更纳入 `C14` 的查询世代失效条件。**建议，未拍板**，对应 C17。

### 14.3 刀1 的验收会「假绿」

刀1 原写「Rust 写 sidecar，C# 走已有 `LoadIndexed` 即可验证」。问题在于 sidecar 命中失败是**静默降级**（`frmMain.Sidecar.cs:9-10`：不弹提示、不加开关、只写启动日志），未命中就走分块全量解析（`frmMain.Sidecar.cs:55`），UI 表现完全正常。Rust 只要算错下面任意一项，C# 都会当「缓存不存在」，验证照样显示通过：

| 隐藏契约 | 位置 |
| --- | --- |
| 缓存条目名 = `GetFullPath` + 文件名主干 `Sanitize`（`char.IsLetterOrDigit`，与 Rust 的字母判定不完全一致）+ `FNV-1a(UTF8(path.ToUpperInvariant()))` + `x16` | `SidecarCache.cs:124-129` |
| `ToUpperInvariant` 与 Rust `to_uppercase` 的 Unicode 映射不同（`ß`、土耳其 `i` 等） | 同上；与刀3 必须验的 `ToLowerInvariant` 口径同源 |
| 身份摘要 = 64 KB 头/尾采样 FNV-1a + `FileInfo.LastWriteTimeUtc.Ticks`（.NET ticks，非 Windows FILETIME，需换算） | `SidecarCache.cs:42`、`:58-77` |
| 头部 magic / 版本 / 8 段宽度 / 各段长度 / 总长；读侧严格校验 | `SidecarCache.cs:264-314`（§3.3 已记一部分） |

建议（未拍板，对应 C18）：

1. 刀1 验收加硬判据 `ChunkedLoadInvocations == 0 && LastSidecarLoadMs > 0`（`frmMain.Sidecar.cs:16`、`:19`），或等价的启动日志断言，证明 C# **确实**读了 Rust 写的文件，而不是走了全量解析。
2. §6.2 的「外部写者字节向量」用例要覆盖**缓存键派生**，不只是 payload；否则 Rust 写对了内容、写错了文件名，向量测试仍全绿。
3. 把上表写成字节级 spec，作为刀1 的交付物之一。

### 14.4 从已归档笔记回收的刀0 约束

`PROJECT-STRUCTURE-RESTRUCTURE.md` 已归档。按 AGENTS §3.2「归档后不可引用：不写链接与交叉引用，各文档只作状态说明；原有引用同步降级为文字」，本笔记 §2 / §4.6 / §11 原先以「出处」形式引用它的地方已降级为裸路径状态说明。但下列三条刀0 关键约束此前只存在于那份冻结档里，现行笔记不自足；此处**文字转述**供刀0 重新确认，**不作为依据**：

1. 分页契约与 DTO 放 `logcat-core/interop/` 独立工程；**刀0 的进程内适配不能进 interop**——它会反向引用 viewer 的引擎，形成循环依赖，反过来逼出已被作废的 `core-extraction`。
2. interop 在刀0 只有接口 + record + 错误码表，刀2 才长出 P/Invoke；独立工程买到的是「边界容器」，不是复用单元。
3. 覆盖率口径：程序集名 `logcat.Interop` 会被 `[logcat]*` 纳入，extern 声明无 IL 体不进分母，record 自动属性计入——影响≈0（**推断**，刀0 实测）。

### 14.5 过期事实与自相矛盾（本轮已就地修正）

| 处 | 原写 | 实际（2026-11-10 核对） | 处理 |
| --- | --- | --- | --- |
| §3.4 / 刀0 验收 / §13.4 | 测试基线 848 全过 | `docs/TESTING.md:12` 记 **852**；2026-11-10 发版实测 **859/859** 全过 | 三处改为「以 `docs/TESTING.md` 为准」，不再内联数字，避免再次过期 |
| §4.5 | panic 边界「未实测、列入刀1 验证项」 | §10 / §13.3 已记本机实测坐实；panic 注入实际列在**刀2**验收 | 改为「已实测，见 §13.3；验证项在刀2」 |
| §3.4 / §7 | `.gitignore` 无 `target/` / `node_modules/` | `:17` / `:18` 已由目录重组批次补齐（§13.4 已自更正） | 两处表格改现状，缺口只剩 C9 |
| §2 / §4.6 / §11 | 引用已归档笔记作「出处」 | AGENTS §3.2 规定归档后只作状态说明 | 降级为裸路径状态说明；关键约束转述进 §14.4 |
| §3.2 | 「状态栏只消费四个数」（实际列了五个） | `UpdateStat` 消费 total / RowCount / marked.Count / 索引ms / 过滤ms | 改为「只消费计数与耗时」；结论（不消费各维度计数）不变 |

### 14.6 核对无误（不必改）

`FilterEngine.cs` 的 `Parallel.For`(`:493` / `:521`)、`Vector256.Create`(`:678`)、`BLOCK_SCAN_MIN_ROWS = 2_000_000`(`:23`)、`BLOCK_SCAN_MAX_KEYS = 8`(`:27`)、`Vector256.IsHardwareAccelerated` 分派(`:479`)；`SidecarCache.cs` 的 `FormatVersion = 1`(`:155`)、`Widths = {8,4,4,1,4,4,1,4}`(`:163`)；`LogListView` 的 `CaptureAnchor`(`:273`)、`PinAnchor` / `NearestModelRow`(`:291` / `:309`)、段前缀和、同步 `RetrieveVirtualItem`(`:402`)、空 `CacheVirtualItems`(`:413`)、`MSG_LIMIT = 4000`(`:14`)；`.github/workflows/ci.yml` 只有 setup-dotnet + build/test，无 Rust 工具链。

§13 的本机实测**可信**：rustc / cargo 1.99.1、MSVC 14.34 / 14.51、Windows SDK 10.0.22621.0 / 10.0.26100.0 齐备；D5 的 `catch_unwind` 口径（`panic = "abort"` 不是等价替代、必须逐导出函数薄包装）成立。D1–D5 决策本身也没有问题，问题集中在**落地顺序与契约自洽**，不在方向。

## 15. 输入先行：log 输入两半的探索（2026-11-10，待维护者拍板）

> 本节记录维护者提出的顺序变更——「第一步 log 输入，分两部分：① 打开文件（含代理）② log 采集」。它把原刀5 / 刀6 提到最前，与 §5 的七刀顺序冲突，故单列。
> 本节是**探索记录**：未编码、未 propose；凡未经代码或实测支撑的判断一律显式标注为推断。

### 15.1 诉求映射

| 维护者表述 | 契约对应（`2026-11-08-SOURCE-ACQUIRE-CONTRACT.md` §3） | 原刀序 |
| --- | --- | --- |
| ① 打开文件（包括代理） | `AcquireRequest.kind = File`：探针 → 路由（直读 / 代理 spool）→ 拉起代理 → spool → 租约 | 刀5 的一部分 |
| ② log 采集 | `AcquireRequest.kind = Device`：取流 → 落临时文件 → 追加事件 | 刀6 |

**讨论口径**：两半的产物都是「本地文件路径 + `SourceDescriptor`」，索引与过滤**不在**这一步；代理链路的失败分类文案仍留 C#（B1）。两半的出口形状并不对称，需先钉死：

```
part 1 直读   -> (原路径, channel=Local)                不产生新文件
part 1 代理   -> (原路径, channel=FileProxy, 暂存文件)   显示路径 != 真实路径
part 2 采集   -> (临时路径, channel=AdbCapture,          文件持续增长
                  appendMode=Appendable, leaseKind=Retained)
```

### 15.2 顺序为何站得住（三条支持 + 一条代价）

1. **绕开阻塞项**：C16 / C17 / C18 全部落在输出层（结果集、标记、分页接缝），输入层不依赖它们；而获取契约在 `2026-11-08-SOURCE-ACQUIRE-CONTRACT.md` §3 已成型，输入先行是当前唯一能立刻开工的切片。
2. **验收可用字节判据**：spool 产物 ≡ 源文件、九格判定 ≡ 真值表、采集文件能被现有 C# 索引器原样吃下。不需要先产 9 列 golden dump。
3. **语言无关的硬骨头集中于此**：命名管道客户端与帧协议、FileShare 活性语义、进程树回收、adb 取流——正是「行为零变化」最容易静默失效的地方。
4. **代价（必须认账）**：用户零可见收益；§7 的双工具链实账（CI 加 rustup、两个包各补 dll 断言、版本号第 6 处、fdd 体积翻倍）提前发生。

> **2026-11-10 补正**：维护者拍板**不设数字验收锚**，排序改按可感知度（见 §15.9 R4）。字节等价 / 判定等同仍是对拍的技术判据，但不再充当阶段门禁的量化门槛；另外三轨并行下轨 A 能较早产出可被感知的改善，上面「用户零可见收益」只描述轨 B 单轨推进时的情形。

### 15.3 现状体量（代码账）

| 项 | 现状位置 | 体量 |
| --- | --- | --- |
| 文本嗅探（8 KB / 10% 阈值） | `logcat-viewer/Services/TextProbe.cs` | 约 60 行 |
| 模式与九格判定 | `FileProxyMode.cs`、`FileProxyService.cs:496` | 小 |
| 管道客户端 + 帧协议（JSON 头行 + 长度前缀裸块） | `FileProxyClient.cs` | 15.2 KB |
| 拉起 / 轮询 / 命令行组装 / 杀进程树 | `FileProxyService.cs:49`–`:140`、`:350` | 约 200 行 |
| spool（8 MB 块 / 完整性 / 短读） | `FileProxyService.cs:224` `SpoolCoreAsync` | 约 200 行 |
| 租约 + `.alive.lock` + 启动清理 | `FileProxyService.cs:394`–`:448` | 约 100 行，Windows 语义 |
| 编排（现塞在窗体里） | `frmMain.cs` `LoadViaProxyAsync` | 整段需搬出 |
| ADB 取流 | `Services/LogcatStream.cs` | 140 行（走 AdvancedSharpAdbClient） |
| adb server 自愈 / 设备枚举 / 监控 | `logcat-uicore/Services/AdbManager.cs` | **留 C#（§4.2 例外）** |

规模估算：part 1 约 1200–1800 行 Rust，part 2 约 200–400 行（取决于 adb 走协议还是子进程）。**推断，未实测。**

### 15.4 已识别的四颗雷

**A. `.alive.lock` 的独占探测在 Rust 侧会失效（推断，需本机探针坐实）**

现状靠 Windows 文件共享语义判活：持有方 `FileAccess.ReadWrite + FileShare.Delete`（`FileProxyService.cs:421`），清理方以 `FileShare.None` 试开，打得开即判无主（`:445`）。Rust `std::fs::OpenOptions` 在 Windows 上恒以 `FILE_SHARE_READ|WRITE|DELETE` 打开，无法表达独占：

| 持有者 | 探测者 | 判定结果 |
| --- | --- | --- |
| C# | Rust | 打不开 → 有主（正确） |
| Rust | C# | 打不开 → 有主（正确，巧合） |
| **Rust** | **Rust** | **打得开 → 判无主 → 删掉活着的 spool 目录** |

C3 若取甲（活性锁入 Rust），第三格成为常态，正好打在 `fileproxy` spec「他实例活跃目录跳过」上，且是静默错误。可选收法：`windows-sys` 直接 `CreateFileW(share_mode = 0)`；或改用不依赖共享语义的机制（Job Object / 命名互斥）。归 design 阶段定。

**B. 进程树回收**：现状 `Kill(entireProcessTree: true)`（`FileProxyService.cs:350`）。Rust `Child::kill` 只终止直接子进程，需 Job Object（`KILL_ON_JOB_CLOSE`）或 `taskkill /T`。

**C. 启动命令行组装是隐藏契约**：`BuildLaunchArgs` / `BuildCommandLine`（`FileProxyService.cs:49`–`:63`）承载三条规则——参数含 `{pipe}` 则按替换语义（含空格加引号）、不含则末尾自动追加 `--pipe <名>`、载入旧配置剥除尾部模板。搬 Rust 必须逐字节复刻，否则旧 `settings.json` 行为静默改变（与 §14.3 同类）。

**D. 采集的字节保真口径**：现状是「UTF-8 解码成 `string` → 再以 UTF-8 无 BOM 写回」（`LogcatStream.cs:83`、`:89`），非 UTF-8 字节会被替换成 U+FFFD。Rust 若直传字节，行为更正确但**不再逐字节等价**——第一次对拍必然撞上此差异，需先定「修 bug」还是「保等价」。

### 15.5 与 MCP 的相关性不对称

`MCP-FUNCTIONALITY.md` §5.5 规定：MCP 只读、只接受显式绝对路径、**不暴露 adb 与进程控制**。故：

| 半边 | 对 MCP 的价值 |
| --- | --- |
| part 1 本地直读 | 需要——`list(path)` 的入口 |
| part 1 代理 spool | 大概率不需要（MCP 用户给的是本机绝对路径） |
| part 2 ADB 采集 | **明确在范围外**，纯 GUI 能力 |

结论：若以「反正 MCP 要重建 core」为理由，part 2 缺少依据，它的正当理由只能是「GUI 输入链归属 Rust」；反过来，MCP 真正卡的是索引 / 过滤与分页契约（C16），不是输入。

### 15.6 待拍板项（C19 / C20 已定稿，C21 待定）

#### C19 第一步的交付形态（已定稿）

- **为什么需要澄清**：决定是否立刻要 FFI v1（句柄 / 缓冲 / 错误码 / `catch_unwind` / 进度原子 / 取消标志），也决定 §7 的双工具链成本何时发生。
- **有哪些方案**：甲 CLI 先行（`crates/cli` 跑对拍，不动产品）；乙 dll 直接接线替换现有 C# 输入链（用户无感，但立即需要 FFI v1）；丙 独立进程形态。
- **已拍板（2026-11-10）**：**GUI 走 `crates/cabi` 的窄 FFI（只长 acquire 面），MCP 第一版走 `crates/cli` 的 stdio、完全不碰 FFI。** 原先的「甲 → 乙 两段」被这条取代：CLI 不是过渡形态，而是与 GUI 并行的第二条消费路径；`crates/cabi` 在输入链第一个 change 之后即开始生长，FFI 面先只承载描述符 / 路径 / 事件轮询，不牵 C16 / C17 / C18。D3 / D4（一个 dll + koffi）目标态不变，koffi 后移到 cabi 成熟之后。

#### C20 顺序是否定稿（已定稿）

- **为什么需要澄清**：本节与 §5 的七刀直接冲突；不定稿则刀0 / 刀1 的进场时点无法排。
- **有哪些方案**：甲 输入先行；乙 输入只做 CLI 对拍，产品侧仍按原七刀推进；丙 维持原七刀，输入留到刀5。
- **已拍板（2026-11-10）**：**GUI 主航道 + 三轨并行**（轨 A 可见收益 / 轨 B 入口归位 / 轨 C MCP），并行只发生在 `crates/core` 层，FFI 只为 GUI 长，轨 C 不阻塞轨 A。原七刀顺序作为对照保留，实际推进见 §15.9。
- **连带影响**：`2026-11-08-SOURCE-ACQUIRE-CONTRACT.md` 的 S10 乙（先出 C# 参照实现）在本顺序下被跳过——契约将由 Rust 首个实现直接兑现，等于放弃「契约先经 C# 实现验证」这条缓冲。

#### C21 采集字节保真口径（阻塞 part 2 验收）

- **为什么需要澄清**：见 §15.4 D；不定则对拍基线不可写。
- **有哪些方案**：甲 逐字节复刻现状（解码再编码，保留 U+FFFD 替换）；乙 直传字节（更正确，但需接受迁移期差异并显式声明）。
- **当前偏向**：乙，但必须把差异写成显式验收项，不能混进「等价」里。

### 15.7 与既有编号的关系

| 既有项 | 处理 |
| --- | --- |
| C2 设备取流归属 | 沿用；part 2 的三条路（Rust 直连 5037 / 拉 `adb.exe` 子进程 / 留 C# 写文件）在此仍待拍 |
| C5 刀0 是否先行 | 被 C20 吸收——输入先行时刀0 顺延 |
| S10 参照实现形态 | 被 C19 取代（对象从「C# 参照实现」变成「Rust 首个实现」） |
| S15 / S16 / S17 | 全部落在 part 2（增量驱动 / 临时文件生命周期 / `tempDir` 归属） |
| C3 Windows 外壳边界 | 由 §15.4 A / B 具体化，仍待拍 |

### 15.8 显式猜测

| 内容 | 标注 |
| --- | --- |
| Rust `std::fs::OpenOptions` 恒以全共享模式打开，无法表达独占 | **推断**（据 std Windows 实现），需本机探针坐实：写一个「持锁 + 探测」最小用例 |
| part 1 约 1200–1800 行、part 2 约 200–400 行 | **估算**，按现有 C# 行数折算，未实测 |
| 输入层 FFI 比分页契约轻 | **推断**，取决于描述符字段布局是否与 §4.4 口径一致 |
| 拉 `adb.exe` 读 stdout 与 SDK 走 5037 的输出字节一致 | **未实测**，需以真实设备对拍 |

### 15.9 本轮拍板与接手点（2026-11-10）

#### 已定（5 条）

| 编号 | 决策 | 影响 |
| --- | --- | --- |
| R1 | **并行只发生在 `crates/core` 层**，契约层不并行 | 「一个 dll 同时喂两个宿主」不作为第一版形态；C16 / C17 / C18 因此不进入 v1 关键路径 |
| R2 | **GUI 主航道 + 三轨并行**（轨 A 可见收益 / 轨 B 入口归位 / 轨 C MCP），轨 C 不阻塞轨 A | 优先改善 logcat-viewer；轨 C 等轨 A 的 core 出来再开壳 |
| R3 | **输入先行**：第一步仍是 log 输入两半（① 打开文件含代理 ② 采集） | 原刀5 / 刀6 前移；刀0 / 刀1 / 刀2 顺延到轨 A 内 |
| R4 | **验收不设数字锚**：排序依据改为「多久能上手摸一次」（可感知度） | 不以耗时 / 内存 / 成功率等阈值作门禁；对拍仍作技术判据，不作量化门槛 |
| R5 | 保留两样免费副产品（非验收项）：CLI dump 与 C# 结果的 diff；启动日志标记「本次打开由 Rust / C# 提供」 | 迁移期防止两套语义悄悄分叉 |

三轨的形状（R1 / R2 的落地形态）：

```
轨A 可见收益   C# 并行索引探针 --> 定标 --> Rust 索引内核 --> cabi 分页 --> GUI 接通
轨B 入口归位   输入链(core: 探针/代理/采集) --> cabi acquire 面 --> GUI 接线
轨C MCP        等轨A的 core --> MCP 壳(stdio) --> search / count / context --> 可选切 koffi
                                  ^
                          汇合点: cabi = acquire 面 + index / paging 面
```

#### 未定（换会话后的接手点）

| 项 | 状态 | 备注 |
| --- | --- | --- |
| C21 采集字节保真口径 | 待拍 | 逐字节复刻现状 vs 直传字节；阻塞 part 2 验收 |
| 输入链是否切成三块（直读+探针 / 代理 / 采集） | **建议，未拍** | 一块一个 change，任一块卡住不阻断另两块 |
| 轨 A 第一步是否为「C# 并行建索引探针」 | **建议，未拍** | 它是 C1 的既定出口，也是当前唯一能立刻被感知的改善 |
| 刀0（C# 分页接缝）在轨 A 内的位置 | 待拍（C5） | 若 GUI 直接接 P/Invoke，刀0 可并入刀2 |
| C16 / C17 / C18 | 仍开放 | 等 GUI 分页那一刀再回来；不影响 v1 |

#### 第一个 change 建议（未开建）

范围：`crates/core` + `crates/cli`——本地直读 + 8 KB 文本探针 + 描述符输出，可独立验证、不改动现有代码；`crates/cabi` 在其后接上。

形状：`dump <file>` 打出探测结论 / 路由 / `SourceDescriptor`，与 C# 现状逐字段比对。

入口：`/opsx:propose`。本笔记与应用均未开始实施，explore 阶段不动代码。
