# Rust core 刀2：分页接管契约探索

- 状态：待评审（已提案 `rust-core-paging-read`；未编码）
- 日期：2026-11-20
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）
- 口径说明：本文为探索底稿；Q1–Q5、Q7、Q8、Q10 的最终口径以 `changes/rust-core-paging-read/design.md` 为准，Q6/Q9 不在本期。

## 0. 一句话结论

本 change 的可编码范围已经清楚（core 分页读取面 + cabi 导出 + C# 绑定；`set_result` 与 poisoning 已移出本期），契约的四个形状已定：**页寻址（ord）、返回编码（打包二进制）、页行载荷（原文 + 偏移）、文本来源（装载时常驻只读句柄）**。探索另发现三处硬缺口：采集源当前没有任何列/文本读取面、`LiveIndex` 发布顺序存在可见窗口、C# 列序与列声明不一致——前两处必须在本 change 内补，列序归 UI 接入 change。

## 1. 范围与非目标

范围内（最小切片，见 §4）：文件源分页读取（页组装、布局、只读句柄）、`rowCount` / `page` / 释放导出、句柄状态矩阵补 `RowCount` / `Page`、C# P/Invoke 绑定的分页部分与 IT、`docs/TESTING.md` 的 C11 口径。

非目标：过滤语义与正则、`set_result(ids)`、标记与 `.lcm`（刀3）；线程级 panic poisoning（C4，后续刀）；MCP 壳（刀4）；桌面 UI 接入（轨A 刀2 之后的独立 change）；导出；DLT 解码；C# 旧引擎删除（切流）；`open(filter)` 最终契约；采集源分页读取（统一快照，留采集读取刀）。

## 2. 事实

### 2.1 列存与快照：可读面已具备

- 段化列存按行区间持有 8 列（`offs/lens/moff/minutes/pids/tids/lvls/tag_ids`），`Store` 维护 tag / 异常类型 / 包名三本字典与崩溃表（`crates/core/src/segment.rs:23-64`）。
- `LiveIndex = ArcSwap<Vec<Arc<Segment>>> + rows 原子`：装载线程整份替换发布，读侧可无锁取快照（`crates/core/src/segment.rs:206-236`）。
- 整份 `Payload` 与段同源（8 列 + 三字典 + `crashes`），文件源装载完成后常驻于源状态（`crates/core/src/source.rs:218`、`crates/core/src/sidecar.rs:37-50`）；文本不常驻，常驻只读文件句柄为刀2 新增（Q4）。
- 文件源装载期间逐块发布（`crates/core/src/source.rs:963-971`），渐进视图的数据基础已存在。
- **读面决策（2026-11-21）**：整份即唯一读面，段只是它的分片生产单元，不复制第二份数据。文件源的页读取直接读 `Payload`，不依赖 `LiveIndex`。
- **缓存缺口（2026-11-21 补）**：`CacheStore::load` 只返回 `Payload`，缓存命中不经过 `parse_source`、从不建段；若页读取依赖段，第二次打开会拿到空索引。按“整份即读面”天然修复。

### 2.2 文本与显示

- sidecar 缓存不含文本，"命中仍须打开源文件取文本"已写进主规范（`openspec/specs/logcore/source-acquire/spec.md` 的 sidecar 缓存复用）。
- 文本不常驻：装载时 `fs::read` 整份、解析后字节即丢弃（`crates/core/src/source.rs:819`、`:963`、`:990`）；源句柄状态里只有路径与 `offs/lens/moff`，文本句柄按 Q4 另存。
- C# 现状：整文件 mmap 取文本（`logcat-viewer/Services/LogDocument.cs:81-83`、`:151-152`）；时间列 = 行首时间戳原文，由 `HeaderBytes` 从 `offs..moff` 切片并按两种日期形态判定（`logcat-viewer/Services/LogDocument.cs:281-301`）；Message 列 = `moff..len`；Tag 走字典；级别名在 UI 侧映射（`logcat-viewer/Controls/LogListView.cs:475-508`）。
- 列声明 6 列 `time/level/pid/tid/tag/message`，行号列由宿主自加（`crates/core/src/descriptor.rs:85-92`）；"行/列读取不在本阶段导出面"（`crates/core/src/descriptor.rs:177` 注释，D20）。
- **列序不一致**：UI 列序是 `Time/PID/TID/Lv/Tag/Message`（`logcat-viewer/Controls/LogListView.cs:12`），声明序是 `time/level/pid/tid/tag/message`；规范要求按列 id 渲染，现状按位置渲染。归 UI 接入 change 修，刀2 绑定层按 id 暴露取值。

### 2.3 cabi 与 C# 绑定

- 现有 15 个导出全是获取面（plan/commit/feed/stop/close/cancel/poll_events/get_progress/get_descriptor/startup_maintain/shutdown/free_handle/free_string/take_last_error/test_panic）（`crates/cabi/src/lib.rs:73-294`）。
- `catch_unwind` 只包导出函数（`crates/cabi/src/lib.rs:38`）；`test_panic` 只覆盖同步边界（`:293`）；loader / consume / 落盘线程均无防护，C4 未落。
- C# 绑定三文件约 800 行（`LogcoreNative` / `LogcoreSource` / `LogcoreContracts`），**产品代码零引用**，只有 `tests/logcat.Tests/LogcoreBindingTests.cs` 在用。
- C# 虚拟列表接缝已存在：`RetrieveVirtualItem → MakeItem/MakeFields`，按行点取带 LRU 缓存（`logcat-viewer/Controls/LogListView.cs:402-473`）；旧整表入口 `SetRows(int[])` 保留（`:107`）。

### 2.4 采集源缺口（新发现，刀2 必补）

- 消化线程内用 `Indexer`（`crates/core/src/parse.rs:412-433`）逐行 `add_line`，只把行数写进 `shared.rows`（`crates/core/src/source.rs:1161-1163`）；段从未发布到 `LiveIndex`，`Indexer` 也没有 `store_result`，线程结束时连同 payload 一起丢弃（`crates/core/src/source.rs:1113-1184`）。
- 后果：采集在途与 stop 之后都没有可读的列/文本面；`rowCount`/`docSize`/事件齐全，但任何分页读取都会得到空表。刀2 若只做"文件源分页"，接入后的实时采集视图仍不可用。
- 需要二选一：消化线程按批 `freeze + publish`（与文件源同一条发布路径），或 stop 后从采集文件重建索引。前者才有"采集中实时翻看"。
- 采集文件以写方式创建（`crates/core/src/source.rs:1265`）；Rust std 在 Windows 上默认共享标志允许并发读（对照 `crates/core/src/locks.rs:23` 显式 `share_mode(0)` 的用途）。**猜测**：常驻只读句柄与写线程同文件并发可成立，采集读取刀用 IT 钉死。

### 2.5 发布顺序窗口（新发现，刀2 必处理）

- `publish` 先存行数、后存段列表（`crates/core/src/segment.rs:219-223`）；读侧若先读 `rows` 再取快照，可能看到"行数 N+1、段只覆盖到 N"的瞬时组合。
- 影响范围：文件页读取改读整份后不受此顺序影响；风险留给将来的采集/前瞻段快照读方。
- 处理选项：甲 读侧先取快照、以快照行数为准；乙 发布顺序改为先段后行数；丙 读侧重试。已定**乙**随刀2 落地，成本为零；甲作为读侧原则保留。

### 2.6 实测数字

- 真实日志样本（`testdata/m_test.log` 前 500 行）：平均 60.7 B/行；同数据按 `{ord, docRow, level, pid, tid, tag, text}` 序列化 JSON 的体积 ≈ raw 的 **2.41×**。
- 合成 200 行页（含较长正文）：raw 22,490 B → JSON 40,491 B（1.80×）。
- 200 行页的 raw 量级约 12–40 KB（随正文长度；切片笔记"≈40 KB"是长消息口径）。
- **已实测（2026-11-21，C# 绑定直连 dll，`mode=Off`，样本在网络共享盘）**：176 MiB / 3,063,982 行样本。plan ≈ 63–172 ms、commit 受理 <1 ms、plan→done ≈ 24.6–43.1 s（共享盘传输与解析占主导）。首个 200 行页：release dll 3.3 ms、debug dll 7.8 ms；稳态 200 行页（随机起点 300 次）：release P50 ≈ 0.68 ms、P95 ≈ 1.2 ms，debug P50 ≈ 1.6 ms；同页 raw ≈ 12.4 KB。P/Invoke + 编码开销（空页）P50 ≈ 0.5 µs，单行页 ≈ 4.4 µs（release）。dll 体积：release ≈ 0.78 MiB、debug ≈ 1.45 MiB。
- **未实测**：二进制 vs JSON 的端到端页耗时对比（JSON 仅有体积比）；千万行 `set_result` 40 MB 单次拷贝（`set_result` 已移出本期）；采集文件并发读写（留采集读取刀）。

## 3. 待定项

每项写清：为什么需要澄清 / 可选方案 / 当前偏向。

### Q1 页寻址（已定：乙，2026-11-20）

- **结论**：只按结果集序号 `ord`（可见行序，0 起）寻址；空结果集 = 恒等映射全行。`docRow` 不作为寻址参数。
- **理由**：与刀3 的导航面同形（`open` 后按结果集序 `page`），刀2 的 `set_result(ids)` 直接喂出同一语义；MCP 的 `context` 用 `resolve + page` 组合表达（邻近语义 2026-11-15 定甲）。丙案在刀3 后调用方没有 `ids` 表，换算不出文档行号；甲案两套编号容易传错。
- **开放点（design 内定）**：`ord` 越界、结果集为空、页跨越采集追加边界的行为，要与刀3 一次定死。

### Q2 返回编码（已定：甲，2026-11-20）

- **结论**：页读取走打包二进制（定长行头 + 变长文本区，一次分配一次拷贝）；JSON 只留给描述/事件/字典等冷路径。
- **理由**：页是滚动热路径、文本占大头；实测 JSON ≥1.8× raw（真实样本 2.41×），且 Rust 转义 + C# 解析都是额外开销。
- **开放点（design 内定）**：精确布局（行式还是列式分段）、字节序、文本按 UTF-8 原样（解析侧已是 lossy，页侧不再二次判定）、缓冲分配方（偏向 core 分配 + `free` 导出，与现有 `free_string` 约定一致）。

### Q3 页行载荷（已定：丙，2026-11-20）

- **结论**：每行给**原文 + `moff` + 时间戳偏移区间**（如 `timeStart/timeLen`，从原文切片，不重复拷贝文本）。宿主做两刀 memcpy 级切片：`time = raw[timeRange]`、`message = raw[moff..]`。
- **理由**：时间戳切法（方括号前缀、两种日期形态、小数秒不定长）是唯一非平凡的规则，归 Rust 一份；C# 与 MCP 都只做偏移切片，不再各自实现；原文保留，复制原始文本 / MCP 原样输出可直接用；相比甲不强制 Node 重写规则，相比乙不丢原文。
- **开放点（design 内定）**：`moff = 0`（未识别行）时 time 区间为空、time 列为空，保持现状语义；长格式时间区间从 `[ ` 之后起算。

### Q4 文本来源（已定：装载时常驻只读句柄，2026-11-21）

- **结论**：装载时打开只读句柄（共享读/写/删除）并保留，页读取按偏移复用；不做每页重开，不做常驻 mmap。
- **理由**：每页重开按路径重新找文件，源文件轮转/替换后会把旧索引配到新文件文本，且新文件长度够时不报错；常驻句柄绑定装载时的文件对象，轮转/替换后仍读原内容，截断按 `IO_ERROR` 报错。这是正确性选择，不是性能选择。
- **边界**：copytruncate 式原地截断重写不受句柄保护（同一文件对象被改写），即使长度恢复也可能读到新内容，契约声明为不保证；本地路径统一走 `long_path_for`（`crates/core/src/request.rs:298`）；采集在途只读已落盘范围（`offs + lens` 有界、`docSize` 原子更新）。
- **待验证**：句柄按偏移读取 + 拷贝的页耗时仍用刀2 apply 探针出数；打开策略已定，不再由探针决定。

### Q5 tag / 异常类型 / 包名：页内字符串表 vs 全局字典导出

- **为什么**：页行带 tag，崩溃摘要需要异常类型与包名；采集追加会让字典增长，全局导出要同步增量。
- **方案**：甲 页内字符串表（页内去重、自包含）；乙 全局字典一次导出 + 版本/增量；丙 每行内联字符串。
- **偏向**：**甲**。临时接口自包含、无同步状态；刀3 的 exact 匹配在 core 内部用全局 id，不受此影响。
- **开放点**：折叠方式（大小写、trim）沿用哪一侧——解析侧 tag 已是原文，UI 只展示。

### Q6 临时 `set_result` 的形状与生命周期

- **为什么**：C16 定甲（刀2 引入、刀3 删除）；千万级 `int[]` 跨界成本由刀2 承担。
- **方案**：甲 整表替换一次推入（`int32` 数组，空数组 = 全行）；乙 增量 append/remove；丙 分块推入。
- **偏向**：**甲**。临时接口最简；core 拷贝后宿主可立即释放源数组；调用仅在已装载 / 采集运行 / 已停止状态合法（写进 D23 矩阵）。
- **开放点**：重复调用 = 替换；越界行号报 `INVALID_ARG` 还是忽略；40 MB 单次拷贝是否需要上限。
- **当前切片**：刀2 最小切片不做 `set_result`，本节方案留到过滤读取刀。

### Q7 页与在途追加的并发语义

- **为什么**：采集逐批发布；宿主滚动条与页请求交错。
- **方案**：甲 页内一致（一次快照锁定 `[start, end)`），页间按最新 `rowCount`；乙 页请求允许跨发布点混合新旧段。
- **偏向**：**甲**，并修正 §2.5 的发布顺序。文件源整份在 `Loaded` 后不可变，页请求取 `Payload` 的 `Arc` 即可；采集将来需要“段 + 字典 + 崩溃 + 行数”的统一快照，留到采集读取刀。追加只影响尾部，已有行号前缀稳定（与过滤笔记"结果集升序、只追加"同一结论）。

### Q8 崩溃摘要：随页返回还是延后

- **为什么**：现有 UI 的 Message 列对崩溃头显示"类型 · 包名 · 时间"摘要，依赖 `crashes` 与 exc/pkg 字典。
- **方案**：甲 页行带 `crashKind/excId/pkgId` + 页内异常/包名表；乙 页只给原文，摘要由宿主另取崩溃表；丙 延后到 UI 接入 change。
- **偏向**：**甲**。页是唯一读取面，缺摘要则 UI 接入时还得补导出；崩溃表小、成本低。
- **开放点**：崩溃面板（`CrashSummaryForm`）是否复用页接口，还是另设崩溃事件导出——建议随 UI 接入定。

### Q9 panic poisoning 与错误码

- **为什么**：C4 定"线程级 `catch_unwind` + poisoned 位 + panic 事件 + 句柄作废码"；现在只有导出层 guard。
- **方案**：甲 句柄 poisoned 原子位 + 所有导出前置检查 + panic 事件 + 专用码；乙 复用 `INTERNAL`。
- **偏向**：**甲**，专用码占状态类空位 **8**。旧笔记"错误码统一负数枚举"与已落地的 D15 正码表矛盾，建议正式收敛为"非负分段，权威表 = `crates/core/src/error.rs` / `LogcoreErrorCodes`"。
- **开放点**：落盘后台线程 panic 是否也 poison（C4 口径为包含异步落盘）；poison 后 `close` 是否仍合法（建议幂等成功）、`free_handle` 是否仍只认已关闭（建议是）。
- **当前切片**：刀2 最小切片不做 poisoning，本节方案留到后续刀。

### Q10 宿主读面与过滤结果模型（已定：整份，2026-11-21）

- **读面**：整份 = 段按序拼接 + tag/异常/包名三字典 + 崩溃表 + 行数，以不可变快照整体替换；段是整份的分片，不是第二份数据（不存两份，避免每行 +30 字节、272 万行约 +82 MB）。UI/MCP 只从整份取数。
- **文件源**：整份就是 `Payload`；缓存命中与首次解析同形，页读取天然可用（修复 §2.1 的缓存缺口）。
- **采集源**：需“段 + 字典 + 崩溃 + 行数”的统一快照，留到采集读取刀；本刀不建。
- **过滤结果**：结果 buffer 存文档行号（`u32`），只追加、不重排；无过滤时用恒等视图，不复制。页的 `ord` = 结果 buffer 下标；无过滤时 = 整份行号。
- **差分**：取快照 S 的同一时刻记 `watermark = S.rows`，扫 S 建结果；新快照只处理 `[watermark, rows)`。表达式变更才重建，不做增量差分。
- **崩溃回溯**：崩溃头有 3 行前瞻、可跨段，已处理行可能被后来补上的崩溃记录“回溯打标”；流式且谓词涉及崩溃时结果不是纯追加，需重建或延后。文件源装载完成后崩溃表已终态，无此问题。
- **文本谓词**：整份不含正文，消息正则/关键字过滤需解析时求值或回读文件，刀3 定。

## 4. 刀2 范围（已收口，2026-11-21）

- **core**：整份读面的页组装（8 列 + 字典/崩溃 + 时间区间）、打包二进制布局、装载时保留只读句柄并按偏移取文本；`LiveIndex::publish` 先段后行数；采集消化线程逐批 `freeze + publish`（内部地基，不开放采集读取）。
- **cabi**：`rowCount` / `page` / 页缓冲释放导出。
- **C#**：P/Invoke 分页绑定 + 绑定层 IT（不接 UI）。
- **测试**：Rust UT + IT（布局固定字面量、跨块、缓存命中、边界、截断、字符串表、句柄生命周期、轮转/替换、空页形状、状态矩阵）+ C# 绑定 IT + P/Invoke 页耗时探针。
- **文档**：`docs/TESTING.md` 按现行口径更新 Rust 条数与覆盖率；探索笔记随 change 归档。
- **后续刀**：`set_result`（Q6）、过滤/结果 buffer（Q10）、poisoning（Q9）、采集读取（统一快照）、UI 接入、MCP、旧引擎删除、DLT。

## 5. 未实测清单（显式标猜测）

| 项 | 现状 | 何时出数 |
| --- | --- | --- |
| P/Invoke 单次页调用 + 拷贝 | 空页 P50 ≈ 0.5 µs、单行页 ≈ 4.4 µs（release） | 已出（7.1 探针） |
| 常驻句柄按偏移读取的页耗时 | 200 行页 P50 ≈ 0.68 ms / P95 ≈ 1.2 ms；首开→done ≈ 24.6–43.1 s（共享盘） | 已出（7.1 探针） |
| 二进制 vs JSON 的端到端页耗时 | 只有体积比（2.41× / 1.80×） | 本期不测（编码已定二进制，无对比需求） |
| 10M 行 `set_result` 40 MB 拷贝 | 未测 | 顺延到过滤读取刀 |
| dll 体积 | release ≈ 0.78 MiB / debug ≈ 1.45 MiB | 已出 |
| 采集文件并发读写 | Rust std 默认共享标志推断可读 | 采集读取刀 IT |

## 6. 范围外但需要收口的旧账

- C1（Rust 正式定案 / 退出路径）：刀1 已交付归档，建议按"已定"收口，退出路径只留历史说明，不再作为待拍项。
- 两个 sidecar 既有缺口（Reload 后从不重新落盘且装载来源标记不重置；读侧无 payload 校验、字典 id 不 bounds-check）已明确不在本 change 内，留后续 change 决定。
- 刀2 已按最小切片 propose（change `rust-core-paging-read`）：**Q1–Q3 已定**（ord 寻址 / 打包二进制 / 原文+偏移），**Q4 已定常驻只读句柄**（装载时打开、页读取复用），**Q10 已定整份读面**；`set_result` / poisoning 移出本期，留待后续刀。
