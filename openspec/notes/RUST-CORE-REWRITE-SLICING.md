# Rust Core 重写的拆分顺序与方法

- 状态：**待评审**（探索记录，未编码、未 propose）
- 日期：2026-11-08（本轮修订，取代同日早先版本）
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

## 0. 一句话结论

目标态：**Rust 拥有全部 log 文件操作与结果集，编译成一个 C ABI cdylib，由 C#（P/Invoke）与 Node.js MCP（koffi）共用同一个 dll；C# 只做配置与分页渲染，不持有日志数据、不映射文件。**

实施按七刀推进：刀0 C# 分页接缝 → 刀1 core+cli 索引 → 刀2 dll 接管分页 → 刀3 过滤 → 刀4 MCP → 刀5 获取链 → 刀6 设备取流。

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
| MCP 与 core 走长驻 stdio 子进程（方案甲） | `PROJECT-STRUCTURE-RESTRUCTURE.md` §6 | **改为 dll 进程内**（D3）；但「MCP 会话必须长驻、否则索引反复重建」这条约束原样保留 |
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
| 状态栏只消费四个数 | `frmMain.cs:1673` `UpdateStat`：total / `_listView.RowCount` / `_marked.Count` / 索引ms / 过滤ms。**没有任何调用点消费「各维度计数」**，故上一版 C3 偏向乙属给不存在的需求付钱 |
| 标记是 C# 侧 UI 状态 | `Controls/LogListView.cs:32` `HashSet<int> _marked`（按文档行号） |
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
| 测试基线 848 全过 | `docs/TESTING.md:12`；`dotnet test` 可用且会 await async 用例 |
| 等价对拍资产已存在 | `ChunkedEquivalenceTests`（分块 ≡ 整份）、`SidecarEquivalenceTests`、`FilterEngineBlockScanTests`（SIMD ≡ 标量）、`LogDocumentTests` 增量 ≡ 全量、`FilterEngineTests` `FilterTail` ≡ `ApplyFilter` |
| 引擎用例大量反射私有成员 | `LogDocumentTests` 反射 `OpenMap` / `TrimPartial` / `MergePart` / `ScanRange`；`FilterEngineTests` 同类 |
| CI 无 Rust 工具链 | `.github/workflows/ci.yml` 只有 `windows-latest` + `setup-dotnet` + build/test 两步 |
| 发布是两 exe 先后 publish 到同一目录再压 zip | `docs/BUILDING.md`；产物验证是硬编码 `Test-Path` 清单；禁 `PublishSingleFile`；`SelfContained=true` 绝不能进 csproj（沿 ProjectReference 传染打断 CI） |
| fdd 包当前 2.0 MB | `docs/BUILDING.md` 体积表，其中 1.2 MB 是 README 演示 gif |
| `.gitignore` 全局忽略 dll | `.gitignore:44` `*.dll`；**无 `target/`、无 `node_modules/` 模式** |
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
   - 现代 Rust 中 unwind 逃出 `extern "C"` 会触发 **abort**（要允许跨边界展开须显式 `extern "C-unwind"`）；历史口径称其为 UB。**后果因此是「宿主进程直接死」而非静默损坏**——对 GUI 与 Node 同进程加载的 dll 依然不可接受，防护必须做。（文档口径，未在本机工具链实测，列入刀1 验证项）
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
- 目录落点：`PROJECT-STRUCTURE-RESTRUCTURE.md` §4 方案 A 的 `logcat-core/` 应改写为 Cargo workspace + 一个 C# interop 工程。

## 5. 拆分顺序

按「接缝先于语言、数据所有权先于算法」排。刀数比上一版多，但每刀 diff 更小、各有独立验收面。

### 刀0 C# 内立分页接缝（语言无关）

- **内容**：定义分页查询接口（§4.3 原语），实现层是现有 `LogDocument` / `FilterEngine` 的薄适配；改造 `LogListView` 消费分页——`SetRows` / `AppendRows` / `GetRowsCopy` / `DocRow` / `ModelRowOf` / `NearestModelRow` / `SegLastGe` 全部假设本地持有完整行号数组，须改为查询或删除；缓存键从模型行号改为「查询世代 + 行号」。
- **验收**：滚动、锚点保持（过滤前后屏幕位置不变）、实时追加、标记、导出、F3 全文、切语言清缓存；848 用例无新增失败 + 维护者实机（2K 缩放屏）验证。
- **为什么先做**：把「UI 改造风险」与「语言迁移风险」分开。同时改虚拟列表 + 换语言 + 跨边界是三重变量，对拍失败无法归因（同上一版 S4 的理由）。
- **资产账**：接口与控件改造是**最终资产**（换 Rust 后原样留着，进程内适配几乎原样变成 P/Invoke 实现）；只有那层薄适配将来会死。**价值前提**：它就是 core 边界本身，无论 core 最终是 C# 还是 Rust 都留着；只有连 MCP 与边界拆分一起取消，它才是纯成本。
- **门禁**：属 UI 交互改动，须先出设计评审通过才编码。是否先行见 C5。

### 刀1 `crates/core` + `crates/cli`：索引内核

- **内容**：`LogParser` + `LogDocument` 扫描/装配路径（`ScanRange`、`BuildChunkedCore`、`MergePart`）+ `CrashExtractor`；产出 9 列、tag intern、崩溃打标、整份与分块两种装载；sidecar v1 字节格式写出。
- **对拍**：CLI dump 与 C# 逐位 diff（§6）。
- **必须含分块**：主界面走 `BuildChunkedAsync`；分块 ≡ 整份本身就是验收项。
- **崩溃打标不可后置**：它绑在索引期父行，拆出去要么丢崩溃面板，要么多一遍全量扫描。
- **本刀不动 C#**：Rust 写 sidecar，C# 走已有 `LoadIndexed` 即可验证，零新 C# 代码。

### 刀2 `crates/cabi`：dll 接管分页（索引侧）

- **内容**：C ABI 导出面 + `catch_unwind` 包装 + 句柄/缓冲区/错误码契约；C# 把刀0 的进程内适配换成 P/Invoke。**过滤仍留 C#**。
- **验收**：UI 不动、行为不变；panic 注入测试（导出函数内故意 panic，宿主存活并收到错误码）；页查询与在途扫描并发不阻塞。

### 刀3 过滤入 Rust

- **内容**：`ApplyFilter` / 块扫描 / 排除 / 大小写折叠 / `FilterTail`，实现同一个 `open(filter)` 契约；块扫描分派条件与跨块重叠一并外迁。
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
| `.gitignore` | `:44` 全局 `*.dll`，无 `target/`、无 `node_modules/` | 补两个模式；确认预编译 dll 的分发方式不被 `*.dll` 误伤（C9） |
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

- **为什么需要澄清**：决定 848 用例里那批引擎用例（`LogDocumentTests` / `LogParserTests` / `FilterEngineTests` / `CrashTests` / 等价用例）是迁去 Rust、还是长期留着；也决定迁移完成后是否存在双份语义。
- **有哪些方案**：甲 保留一个发布周期作 fallback（native 加载失败/身份不命中时降级），之后删；乙 刀2 验收即删；丙 长期双实现并存。
- **当前偏向**：甲。丙 是长期净负（双份语义要对齐）；乙 没有退路。

#### C12 平台矩阵

- **为什么需要澄清**：决定 CI 构建矩阵与 npm 包的 os/cpu 声明；GUI 是 WinForms（Windows-only），但 MCP 理论上可跨平台。
- **有哪些方案**：甲 v1 只 win-x64；乙 加 win-arm64；丙 加 linux/darwin（仅 MCP 侧可用）。
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
| koffi async 足以承载 MCP 的粗粒度长任务 | **推断**，未实测；退路是 CLI + stdio（§4.6） |
| fdd 包加 dll 后翻倍到 3 倍 | **推算**，基于 `docs/BUILDING.md` 的 2.0 MB 与评估笔记的 2–5 MB 估计，未实测产物体积 |
| 现代 Rust `extern "C"` unwind 逃逸触发 abort | **文档口径**，未在本机工具链实测，列刀1 验证项 |
| 七刀的评审面是否偏大 | **未定**，取决于每刀实际 diff 规模；刀0 与刀1 可各自独立验收 |

## 11. 关联

- `RUST-CORE-EVALUATION.md`：语言决策、收益成本、对拍契约；其 §4 范围、§5 接缝推荐、§6 内存峰值口径已被本笔记修订（该文件顶部有状态说明）。
- `PROJECT-STRUCTURE-RESTRUCTURE.md`：`logcat-core/` 落点（本笔记改写为 Cargo workspace + C# interop 工程）、§6 MCP 长驻约束（保留）、方案甲 stdio（被 D3 取代为 dll）。
- `BACKLOG-MCP-AND-FORMATS.md`：D2 顺序、D6 分发、D7 切片（均被推翻或沿用理由）。
- `MCP-FUNCTIONALITY.md`、`MCP-LOG-ANALYSIS-API.md`：MCP 工具面两套口径未拍板，属刀4 的待决项；分析型原语与分页原语同源（锚点 + 窗口 + 上下文）。
- `DLT-INTEGRATION.md`：解码归属，范围外。
- `2026-11-07-MESSAGE-INDEX-MECHANISM.md`：索引与取文本机制，刀1 的现状依据。
- `docs/TESTING.md`、`docs/BUILDING.md`：测试基线与发布步骤，§7 的实账来源。

## 12. 下一步

1. 维护者拍板 §8.1 的 C1 / C2 / C3 / C4 / C5 / C8 / C12。
2. C5 定「刀0 先行」后，走 `/opsx:propose` 建刀0 的 change（UI 交互改动，设计评审通过才编码）；C1 若取乙，探针与刀1 并行。
3. §8.2 各条在对应刀的 design 阶段定稿，本笔记不实现任何 FFI 或 Rust 代码。