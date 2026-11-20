# Rust Core 重写：结论与事实

> 本笔记是探索阶段的定稿版，只保留已经定下来的结论和核对过的事实；讨论过程与已被取代的方案不在这里，需要时查 git 历史。
>
> 三个沿用的说法：「刀」= 一个可单独交付、单独验收的切片（刀0…刀6）；「对拍」= 把 Rust 的输出与 C# 的输出逐位比较；「切流」= 把生产 `cache/` 的读写权从 C# 交给 Rust。
>
> 本笔记不实现代码；要开始实施，走 OpenSpec 的 propose 流程（`/opsx:propose`）。

## 0. 当前状态

- **输入链（把日志数据接进来）已经完成。** 契约部分已归档为 change `2026-11-15-source-acquire-contract`（本地直读 + 8 KB 探针 + 描述符）；实现部分已在 `logcore` 落地：代理获取与落盘（`proxy.rs`）、独占判活锁（`locks.rs`，`share_mode(0)`）、采集推流（`logcore_feed`，行数组深拷贝入队即返回，`source.rs feed`）。FFI 面 = plan / commit / feed / stop / close / cancel / poll_events / get_progress / get_descriptor / startup_maintain / shutdown（`ffi.rs`）。
- **所有带编号的问题都已定案**，没有悬而未决的编号项。编号在正文里以括注出现，便于与 git 历史、旧笔记对照。
- **下一步：刀1 propose——日志解析与索引内核。** 完成标准见 §4.3，工作地图见 §5。

## 1. 目标态与边界

Rust 拥有全部 log 文件操作与结果集：文件探测、获取（本地直读 / 代理 / 设备流）、落盘、索引、缓存、过滤、分页供数、导出、增长与重载、会话生命周期。Rust 编译成一个 C ABI 的 cdylib，C#（P/Invoke）与 Node.js MCP（koffi）共用同一个 dll。C# 只做配置与显示：不持有日志数据，不映射文件。

```
+---------------- C#：配置 + 显示 ----------------+
| 设置 / MRU / Loc / 对话框 / DPI / 字号            |
| 虚拟列表渲染：颜色、标记、行号列、截断文案         |
| 设备枚举与选择 / 工具箱 UI / 截图（GDI+）          |
| 分页页缓存 + 查询世代                            |
+------------------------+-------------------------+
                         |  C ABI（P/Invoke）
                         v
+---------------- Rust：全部 log 文件操作 ---------+
| probe（文本/编码）-> acquire（本地|代理|设备）     |
| -> spool -> 索引（8 列 + tag 去重表 + 崩溃打标）   |
| -> 持有结果集 -> 过滤（全量/增量）-> 分页供数      |
| -> sidecar 落盘与淘汰 -> 导出 -> 增长/重载策略     |
| -> 会话生命周期与租约                             |
+------------------------+-------------------------+
                         ^
                         |  同一个 dll，koffi（async）
+---------------- Node.js MCP ---------------------+
| JSON 薄壳：probe / count / window / trace_field/… |
+--------------------------------------------------+
```

这条边界有三处例外，写清楚边界才自洽：

| 例外 | 留在 C# 的内容 | 理由 |
| --- | --- | --- |
| 设备侧 | 设备枚举/连接/monitor、adb server 自愈、截图、工具箱文件面板。**采集的字节由 C# 读原始流，再推给 Rust（C2）** | `AdbManager` 在 UiCore 里且被两个 exe 共享；截图依赖 GDI+ |
| Windows 外壳 | `SaveFileDialog` 等模态框、`explorer.exe` 集成。**管道客户端、`.alive.lock` 活性锁、启动清理归 Rust（C3）**；判活走原生 API 或命名互斥 | 模态框必须由 UI 进程呈现；大文件确认走一次往返：Rust 发「需确认(size)」，C# 弹框回答 |
| 配置与文案 | 过滤参数解析（`SplitTerms` / `ParseMinutes` / `ParseInts`）、`Loc`、进度呈现、设置存储 | Rust 只回错误码 / 事件码与结构化参数 |

## 2. 基本决策

### 2.1 语言与形态

- **文件操作完全归 Rust，C# 只做配置与显示（D1）。** C2 定案后文件操作侧没有例外：spool、sidecar、临时文件全在 Rust。
- **显示走 SQL 查询窗口式分页（D2）：** 传分页参数或锚定行号，返回固定条数。C# 不映射文件、不持有结果集，也不返回长期裸指针。
- **编译成一个 dll，C# 与 Node.js MCP 共用（D3）。** CLI 保留，用于对拍和退路。
- **Node 侧用 koffi 调同一个 C ABI dll（D4）**，不引入 napi-rs 的构建矩阵。
- **panic 防护用 `catch_unwind` 逐导出函数包装（D5）。** `panic = "abort"` 不是等价替代（理由见 §3.3 与 §9.2）。
- **Rust 不要求完整复刻 C# 的能力，按最优方案做（C1）。** 不保留退回 C# 引擎的路径；切流与旧线删除执行后不可逆。对拍是验证手段，不是目标。
- **平台矩阵 v1 只做 win-x64（C12）：** CI 单矩阵；npm 包声明 `os`/`cpu` 为 win-x64；win-arm64 / linux / darwin 等出现真实需求再扩。

### 2.2 顺序与验收口径

- **GUI 是主航道，三条轨道并行推进（C20，见 §4.1）。**
- **并行只发生在 `crates/core` 层，契约层不并行（R1）。** MCP 轨不阻塞索引轨（R2）；输入链先行（R3）。
- **验收不设数字锚（R4）**，切片按「多久能上手摸一次」排序；验收以 Rust UT + IT 为准（§4.3），不以 C# 输出作判据。
- **GUI 先走 `crates/cabi` 的窄 FFI（初期只有获取面），MCP 第一版走 `crates/cli` 的 stdio（C19）；** koffi 推迟到 cabi 成熟之后，D3 / D4 的目标态不变。
- **保留免费副产品（R5）：** CLI dump（调试 / 排障）；启动日志标注「本次打开由 Rust / C# 提供」。C# 结果的 diff 仅作可选开发自查，不进验收。
- **迁移期双线并存（C8）：** logcat-viewer 不删不拆，独立存在，新线（新 UI + Rust）完整迁移功能后旧线整体删除。旧线继续可用，充当功能基准与事实上的降级（不必专门做 fallback）；引擎用例随旧线存续，新线自建用例 + 对拍；测试基线双线并存直到旧线删除（`docs/TESTING.md` 收口与 C11 同批）；删除旧线是收尾 change 的必验项。
- **UI 是新做而不是改造（C5）：** 以现有界面功能为功能基准，C# 侧直接对着 Rust 分页契约接入，等 cabi 分页面就绪后开始。

## 3. 契约与硬约束

### 3.1 分页原语（最终态）

按 SQL 的窗口查询来理解这套接口：

| SQL 窗口 | 对应原语 |
| --- | --- |
| `SELECT ... WHERE <filter>` | `open(source, filter) -> handle, total_count` |
| `LIMIT n OFFSET m` | `page(handle, offset, limit, buf, cap)` |
| keyset anchor | `resolve(handle, doc_row) -> ordinal`（不命中取前后两条、平局取前一条、越界取首末）；另有 `next_match` / `prev_match` / `context(row, before, after)`（「附近」取全部原始行，窗口内谓词由 MCP 本地过滤） |
| 取完整大字段 | `full_text(doc_row, buf, cap)` |
| `COUNT(*)` 增量 | `appended(new_count)` 事件（轮询原子计数） |
| `SELECT ... INTO OUTFILE` | `export(queryId, path, overwrite)`；默认不覆盖、目标等于源拒绝、临时文件 + 改名原子写、取调用时刻的结果集长度快照 |
| 语句取消 | `cancel(handle)` + 原子标志 |

- `open(filter)` 由 Rust 执行过滤、结果集常驻 Rust——刀3 起成立；刀2 先以 `set_result(ids)` 过渡：C# 把过滤结果行号推给 dll，dll 只负责分页（C16，刀3 删除该接口）。
- **导出一次都不跨界：** Rust 直接写盘，C# 只传查询句柄或标记行号集合。

### 3.2 页缓冲与字段布局

- 调用方给缓冲区，Rust 往里填：`page(..., buf, cap) -> written / needed`。一页 200 行 × 约 200 B ≈ 40 KB。**不返回长期裸指针。**
- 页内分两段：定长数值列（doc_row / pid / tid / lvl / tag_id / msg_len / 崩溃字段）+ 变长字符串区（offset/len 索引）；tag 字典按查询下发一次，行内只带 tagId。
- 字符串一律 UTF-8；Rust 不保证缓冲区内容跨调用有效。
- 长消息：页内带截断的 message 和原始长度；`MSG_LIMIT = 4000` 的截断文案留在 C#，全文走 `full_text`。
- 崩溃摘要：Rust 回结构化 `(exc, pkg, kind, time)`；兜底提取归 Rust，`Loc.F("☠ …")` 拼字归 C#。

### 3.3 FFI 四条约定

1. **句柄生命周期：** 不透明句柄，`open` 创建、`close` 释放；关闭顺序是「停查询 → 释放句柄 → 释放内部映射」；页缓存不得跨句柄关闭存活。
2. **缓冲区约定：** 谁分配、容量语义、`written` / `needed`、截断标记。
3. **错误码：** 统一用负数枚举，C# 与 Node 共用一张表；panic 有独立错误码；句柄作废有专用错误码。
4. **panic 边界（D5）：** 每个导出函数加薄包装 + `catch_unwind`，捕获后转错误码。现代 Rust 里 unwind 逃出 `extern "C"` 会触发 abort，宿主直接死（本机实测见 §9.2）；koffi 无法替宿主捕获 Rust panic，`panic = "abort"` 又与 D3 冲突，所以必须这样防护。

**panic 之后句柄作废（C4）：** panic 隔离下沉到每一条源自该 handle 的线程——导出函数包装与后台装载/消化线程（`run_loader` / `consume` / 异步落盘）各自 `catch_unwind`；任一线程捕获到 panic 就把该 handle 标记为 poisoned、推一条 panic 事件（独立错误码），此后任何调用都返回「句柄已作废」的专用错误码，UI 提示重开。收尾 join 不再丢弃线程返回值——后台线程的 panic payload 也用于落地 poisoned 位。这里只保证「进程不死」，不保证「数据没坏」——sidecar 没有 payload 校验和；读侧只有 crash 行有 `retain` 复验，tag 字典 id 等其余读取不做 bounds-check，坏数据不会报错。原地恢复无法证明有效，abort 又违背 D5。错误码表加专用码、UI 提示重开，进刀2 design。

### 3.4 并发与 Node 的硬约束

- **页查询不得被在途的全量扫描/过滤堵住：** 读页走已发布的索引快照（arc-swap / RCU 形状），扫描在另一份上跑——对应现状分块加载的「前缀发布」语义。
- **Node：** 长扫描必须走 koffi async 或 worker_threads；MCP 会话必须长驻（索引生命周期跟进程走）。

### 3.5 Cargo 布局

```
crates/core   纯算法 + IO，无 FFI 面
crates/cabi   cdylib：extern "C" + catch_unwind 薄包装  ->  logcat_core.dll
                                                          ^          ^
                                                    C# P/Invoke   Node koffi(async)
crates/cli    同一 core 的可执行壳  ->  对拍 oracle / 调试 / MCP 退路
```

- CLI 不是可选的：对拍靠它；koffi 不可用时 Node 改拉 stdio，`core` 一行不改。
- 导出面只在 `cabi`；`core` 内不出现 `unsafe` FFI 类型。
- 契约与 DTO 放独立的 interop 工程；**进程内适配不能进 interop**（会反向引用 viewer 引擎、形成循环依赖）。interop 先只有接口 + record + 错误码表，刀2 才长出 P/Invoke。

### 3.6 不要做的事

1. 不先做 `core-extraction` 的 1a/1b 两步；目录重组要早于刀1（把现在的单箱 `logcore/` 拆成 §3.5 的三箱 workspace）。
2. 刀1 首版不上并行建索引。
3. 不顺手迁 `Loc` / DLT / 工具箱文件面板（DLT 解码归属见 `DLT-FORMAT-AND-RUST.md` K1，属外部待决项）。
4. 不返回长期裸指针；不用回调做进度或取消（进度 = 原子计数轮询，取消 = 原子标志）。
5. 不用 `panic = "abort"` 替代 `catch_unwind`。
6. 迁移期不改 sidecar 字节格式（C6）。
7. 不把过滤外迁当性能项立项（收益 ≈ 0，见刀3）。

## 4. 交付切片

### 4.1 三条轨道

```
轨A 可见收益   刀1 索引内核 --> cabi 分页（刀2） --> UI 接入（新做） --> 切流
轨B 入口归位   输入链（已完成：直读/代理 spool/采集 feed） --> cabi 获取面 --> GUI 接线
轨C MCP        等轨A 的 core --> MCP 壳（stdio） --> search / count / context --> 可选切 koffi
                                  ^
                          汇合点：cabi = 获取面 + 索引/分页面
```

- 轨A 先出可见收益；轨B 把入口归位，剩余工作是 cabi 获取面组装与 GUI 接线；轨C 等轨A 的 core 就绪后再起。
- C# 侧设备流接线（adb 读原始字节 → feed，直传见 §6.2）随新 UI 接入一起落地。
- MCP 对输入链的需求不对称（`MCP-FUNCTIONALITY.md` §5.5：MCP 只读、只接受显式绝对路径、不暴露 adb）：本地直读是 `list(path)` 入口必需；代理 spool 大概率不需要；采集不暴露给 MCP。

### 4.2 各刀的内容

**前置小 change `reject-unsupported-format`（先于刀1 立项）**——非文本输入一律拒绝，不做「解析出垃圾行」。

- **两个判定点，都只对实际读得到的副本判。** plan 期：直读打开成功（Auto / Off）时先查 4 字节魔数 `44 4C 54 01`，命中给 `format:"Dlt"`；否则按现有文本嗅探，判非文本给 `format:"Binary"`；同步返回 `UNSUPPORTED_FORMAT`（14），不建句柄。commit 期：On 与「Auto 直开失败转代理」在 spool 完成后、建文档与查缓存之前嗅探暂存副本头部，非文本发 14 错误事件、进入失败态、不发布任何行。
- **On 的 plan 期不探测原件**：On 的语义就是跳过直读，避免在不可达路径上多一次可能挂住的打开；DeviceStream 不判（喂进来的是已解码行）。
- **判定早于身份与缓存**：直读源 plan 拒在算身份/查缓存之前；暂存源本就不入缓存。历史遗留的垃圾 `.lcs` 不会复活。
- **语义边界：只判「非文本 / DLT 魔数」，不判「文本但不是 logcat」**——CSV、任意 .txt 仍按文本产出垃圾行；行级格式匹配若要做另立判据，本期不做。DLT 魔数只用于错误标签，不加 `LocalDecode` 渠道、不改列声明（DLT 解码仍为 0 工作量）。
- **判定实现口径**：读窗口 I/O 失败不当作非文本证据，路由沿用现状（Auto 转代理、Off 维持直读），暂存副本再判；暂存副本嗅探沿用 request options 的 `probeWindowBytes` / `probeThreshold`，与 plan 同口径；魔数判定独立新建 `format.rs`（`detect_unsupported_format(sample) -> Option<&'static str>`），`probe.rs` 只管文本嗅探，后续 DLT change 在 `format.rs` 长解码路由。
- **行为改写**：Auto 非文本不再「转代理」，`source_tests.rs:537` 期望由 `ProxySpool` 改写为 14；spec 的「否则转代理」改写为「否则拒绝」，显式关闭补同名场景，失败分类补 14 与参数键；新增「暂存副本非文本 → 错误事件」场景。
- **错误参数与空文件**：14 的 `format` 取 `Dlt`（魔数命中）/ `Binary` 两值，不加宽字符细分；0 字节文件按文本接受，产出空文档（沿用两侧现状）。
- **误伤与回退**：控制字符白名单不动（ESC 仍计入坏字节，colored 日志罕见、接受该误拒几率），不加绕过判定的配置项；误拒的回退 = 版本回退。
- **spec delta 形状**（`specs/logcore/source-acquire/spec.md`）：新增独立 Requirement「不支持的格式拒绝」（魔数先于嗅探、14 与 `Dlt`/`Binary` 两值、plan 同步 / 暂存副本事件两判定点、早于身份与缓存、不产出缓存条目）；「代理路由三态判定」删「否则转代理」并为显式关闭补拒绝语义、其「嗅探判非文本」场景改为同步 14；「文本嗅探规则」适用范围由「自动模式」改为「文件源」；「失败分类」只补场景不改正文。
- **Reload 不在判定范围**：只判获取入口（plan + 暂存副本）；本地直读 Reload / 追加沿用现状，文件在打开期间被替换成二进制的极端情形不覆盖。
- **文档联动**：DLT 笔记 §11 同批补「14 已有生产者（仅标签）、`LocalDecode` 与 `warnings[]` 仍未落地」；`docs/TESTING.md` 不动，Rust UT/IT 条数随刀2 按 C11 统一口径。
- **本 change 不动 C#**：桌面 UI 当前不走 Rust plan，覆盖随 cabi 接线自动生效；接线前 UI 打开 bin 仍会解析出垃圾行（已知、接受）。UI 侧口径先定死——文案「不支持的格式：文件不是文本日志。」（英文 "Unsupported format: the file is not a text log."）、被拒文件不进最近使用、拒绝发生在关旧文档之前（旧视图保留）——随接线落地。验收仅 Rust UT + IT，不设 C# 测试与 UI 手测。

**刀1 `crates/core` + `crates/cli`：索引内核**——轨A 第一步，可进 propose。现状解析雏形在单箱 `logcore`（`parse.rs` / `source.rs` / `sidecar.rs`），先拆 workspace（§3.5、§9.3）。
- 内容：`LogParser` + `LogDocument` 的扫描/装配路径（`ScanRange`、`BuildChunkedCore`、`MergePart`）+ `CrashExtractor`；产出 8 列（offs / lens / moff / min / pid / tid / lvl / tagId）、tag 去重表、崩溃打标，整份与分块两种装载，并按 sidecar v1 字节格式写出。内部结构用段化列存（§5.3）。
- 验收：Rust UT + IT（判据见 §4.3）；小样本入 `testdata/`、只加不改，大样本由生成器现造不入库（§4.3）。**必须在动 `LogDocument` 之前产出。**
- 本刀不动 C# 产品代码；Rust 把 sidecar 写到测试目录，由 Rust IT 读回验证。
- 崩溃打标绑在索引期的父行上，不能后置；「分块结果 ≡ 整份结果」本身就是一个验收项。
- 刀1 期间 sidecar 保持 v1 字节格式（沿用现有 Rust 实现，C6）；身份摘要与键派生只要求 Rust 自洽，不再要求与 C# 逐位一致。

**刀2 `crates/cabi`：dll 接管分页。**
- 内容：C ABI 导出面 + `catch_unwind` 包装 + 句柄/缓冲区/错误码契约；临时 `set_result(ids)`（C16，千万命中 = 40 MB `int[]` 一次性跨界的成本由本刀承担）；panic 注入测试覆盖同步与后台两条边界（导出函数 + `run_loader` / `consume` 各注入一次，断言宿主存活、handle 转 poisoned、`poll_events` 收到 panic 事件、后续调用返回句柄作废码）；页查询与在途扫描并发不阻塞。
- 过滤仍留在 C#（刀3 才迁）。

**刀3 过滤入 Rust。**
- 内容：`ApplyFilter` / 块扫描 / 排除 / 大小写折叠 / `FilterTail`，实现 `open(filter)` 最终契约；`FilterExpr` 含 `tagMatch`（substring / exact）与 `markedOnly`；正则也在本刀实现，方言 = Rust `regex`（线性时间，不支持反向引用和环视），多模式单遍（`aho-corasick` / `regex-automata` 预筛）只是内部候选；`BLOCK_SCAN_MAX_KEYS = 8` 在本刀重估。
- 标记集合归本刀（C17）：暴露 `mark` / `unmark` / `clear_marks` / `list_marks`；持久化走独立的 `.lcm`（与 `.lcs` 同目录同键，不参与缓存回收）；存活规则 = 忽略 mtime，用头哈希 + 逐条 `(offs, len)` 复验。标记变更**不**使查询失效，只有 `markedOnly` 选项变化时才重建结果集。
- 必验：删除 `set_result`（C16）；`to_lowercase` ≡ `ToLowerInvariant`（`ß`、土耳其 i）；SIMD ≡ 标量；增量 ≡ 全量；正则黄金集逐条对拍 + `REGEX_INVALID`；子串与正则两个引擎互不实现；移植语义四条（`FILTER-PREFILTER-AND-RUST.md` §4）——非 ASCII 回退只接管可扫词、and 的计数由多模式引擎逐词报出（不能只报「有命中」）、跨块重叠沿用 `overlap = 最长词长 - 1`、k=1/3/8/20 语料与 C# 块扫描逐位一致。
- 定位：性能收益 ≈ 0（过滤已贴带宽地板），驱动力是 MCP 语义统一与结果集归属。参数解析留在 C#。

**刀4 Node koffi + MCP 薄壳**：npm 包 + 预编译 dll + koffi async 绑定；长驻会话。工具面口径（`MCP-FUNCTIONALITY.md` vs `MCP-LOG-ANALYSIS-API.md`）未定，属该 change 的待决项。前置刀3。

**刀5 获取链收尾**：输入链主体已落地（代理客户端 / spool / 判活锁 / `startup_maintain`），本刀剩余 = 编码判定（C7，独立 change）、外壳启动清理与租约收尾（与获取契约里的 `startup_maintain` 启动维护不是一回事）、生产 `cache/` 切流治理（§5.2 的一次性清理）、进程树回收（§6.3）。

**刀6 设备取流**：已由 C2 定形为「C# 采集、push 给 Rust」，不再是独立的待拍刀。Rust 接收面（`logcore_feed`）已实现；C# 侧 adb 原始流 → feed 的接线随新 UI 接入。

**不单独切片的两项**：并行建索引（语言无关，属刀1 的内部实现选择，首版不上）；sidecar 格式保持 v1 字节不变（C6）。

### 4.3 刀1 的完成标准（C18：Rust UT + IT）

验收全部用 Rust 自身测试，不引入 C#、不设跨语言 golden、不比对 sidecar 字节。**期望值固定**：`testdata/` 放几个手工构造的小 log（按测试期望生成，把 long 格式 / 时间戳越界 / 崩溃序列等每条差额的边界都覆盖进去，只加不改），UT 与 IT 都读这些 log；断言里的期望值写成**固定字面量**，按 §5.1 的 C# 基准语义一次性定死，**禁止拿 Rust 现状输出回填**。每条差额先有「按正确语义、当前必然失败」的测试，再改实现转绿。

- **UT（模块内 `#[cfg(test)]`）**：`parse_line` 覆盖 threadtime 与方括号 long 格式、时间戳字段越界（按未知元数据收）、崩溃 3 行前瞻（Java/Native/ANR/Tombstone）；`encode`/`decode` 覆盖往返、坏 magic、版本不符、段长/总长不符、字典 id 越界；`Identity` 覆盖四要素、小文件首尾同段、mtime 稳定。
- **IT（`tests/`，读 `testdata/` 小 log）**：整份装载与分块装载结果逐位相同（8 列 + tag 字典顺序 + 崩溃行号）；sidecar 写临时目录再读回，payload 逐位相同且不触发重解析；崩溃打标绑在索引期的父行。
- **大样本与格式样本由脚本生成、不入库**：`tests/` 内置确定性生成器（固定种子、显式写字节、不依赖平台换行，只造输入、不调解析器），产出 40 MiB 大样本（崩溃序列精确落点于首块 4 MiB 与稳态 36 MiB 边界附近）和各支持格式（threadtime、方括号 long 等）的 5 MiB 输入，写入 git-ignored 临时目录；IT 用它跑「分块 ≡ 整份」、跨块崩溃与格式覆盖，过滤正确性（刀3）复用。行尾与字节布局固定，保证 `offs`/`lens` 可复现。
- **入库的只有生成器与固定期望值**：`testdata/` 只留小样本和生成器脚本，大 fixture 一律运行时生成；同种子跨机一致，固定期望字面量因此长期成立。
- 身份摘要、键派生、epoch 口径只要求 Rust 自洽；§4.3 原第②层（C# 读侧命中）与第③层（C# 旧条目清理）不再作为刀1 判据。
- 旧条目一次性清理归切流（§5.2 / 刀5），不在刀1 验（原文 §4.3 第③层与 §4.2 刀5 本就重复，此处收敛）。

## 5. 刀1 工作地图：解析与缓存要补的差额

> 三项关键解析差额（long 格式、崩溃打标、分块渐进）已在 HEAD 上复查，仍然成立（`parse.rs:5` 注释明示崩溃未做、`parse_line` 无方括号路径、全仓无 chunk/Segment 结构）；缓存侧差额尚未逐行复验，进刀1 design 时以 HEAD 再核。

### 5.1 与 C# 基准的差额

| 面 | Rust 现状 | C# 基准 | 后果 |
| --- | --- | --- | --- |
| 方括号 long 格式 | `parse_line` 无 `[` 快速路径、无回退正则（`parse.rs:94`） | 方括号行走 `ReLong` 正则兜底（`LogParser.cs:83`、`:216`） | long 行 pid/tid/level/tag 全空 |
| 时间戳校验 | 只夹分钟 0–59；月/日/时/分/秒不校验（`parse.rs:112`、`:128`） | 字段越界抛 `ParseException`，整行按未知元数据收（`LogParser.cs:269`、`LogDocument.cs:748`） | 非法时间戳两侧列值不同 |
| 崩溃打标 | **未实现**，`Payload.crashes` 恒空（`parse.rs:5`、`:280`） | 字节快筛 + 3 行前瞻 + Java/Native/ANR/Tombstone + 两字典（`LogDocument.cs:778`、`:829`） | Rust 缓存崩溃表为 0，崩溃面板空 |
| 分块渐进 | 未命中时整份 `fs::read`，解析完才 `store_result`（`source.rs:872`、`:894`） | 首块 4 MiB、稳态 32 MiB 逐块 Scan→Merge→发布（`LogDocument.cs:465-491`） | 1 GB 文件峰值 = 整份字节 + 整份列 |
| 块边界 | 无分块，故无对齐与前瞻 | 块末对齐换行，块尾多看 3 行只索引到 `indexEnd`（`LogDocument.cs:531`、`:562`） | 补分块必须一并补，否则跨块崩溃丢失/重复 |
| 缓存写时机 | loader 线程内、`Done` 之前同步写（`source.rs:881`、`:808`） | 装载完成后后台线程写，退出不等（`frmMain.Sidecar.cs:98`） | `done` 被落盘拖后 |
| 缓存编解码内存 | `encode` 建整份 `Vec<u8>`，`probe`/`decode` 整份 `fs::read`（`sidecar.rs:92`、`:370`、`:175`） | 写侧逐列 Stream、读侧直读列数组（`SidecarCache.cs:242`） | 命中路径峰值 ≈ 索引两倍 |
| 缓存键派生 | 只换 `/`→`\` 再 `to_uppercase` 做 FNV（`sidecar.rs:303`、`:313`） | `Path.GetFullPath` + `ToUpperInvariant`（`SidecarCache.cs:126`） | 少数路径算出不同条目名 → 旧条目未命中（只多一次重解析） |
| 缓存临时文件 | 条目名 + `.tmp-<pid>`（`sidecar.rs:432`） | `.tmp-<GUID>`（`SidecarCacheStore.cs:155`） | 同进程并发写同条目撞名 |
| 临时条目回收 | 4 GiB LRU，按目录全部 `.lcs` 统计 | 同 | 遗留条目按 §5.2 一次性清理，LRU 只管新条目 |
| 缓存所有权 | 仅测试目录 | 生产 `cache/` | 切流后 Rust 是唯一写者（§5.2） |

**三条差额的「今天实值」（UT 红测锚点）：**

- **long 行**：今天整行退化为未知——`minute=255 / pid=-1 / tid=-1 / level=0 / tag=None / moff=0`；正确值按 `ReLong` 六列齐（`moff` 指向 `]` 后 msg）。变体须覆盖：可选年、`,` 小数、无 tid、`]` 后无空格（`moff` 随之不同）。行不丢（unknown 行仍成记录），测试须断言「行数不变、列值不同」，只断行在会永远绿。
- **时间戳越界**：今天只夹分钟，月/日/时/秒不校验、非数字占位照过。越界行今天除「分=60」（`minute=255` 但其余列照常）外全照常解析；正确是**六列全空**（C# 整行按未知元数据收）。反向陷阱：`02-31`、秒 `60`、年份 `0000` 在基准里合法，须写成「保持解析」的绿测，防顺手加强校验而与基准分叉。
- **崩溃打标**：今天 `crashes=[]`、`exc_types`/`pkgs` 恒空；正确是每个崩溃头一行事件，`Row` 绑头行，四类 Kind（Java 0 / Native 1 / ANR 2 / Tombstone 3）。坏时间戳行仍能出崩溃事件（快筛/提取独立于 parse），不得把两者耦合。
- **实现顺序 long → 崩溃**：崩溃 3 行前瞻靠 `MsgOffset` 剥日志头；long 行未解析前 `moff=0`，整行带 `[…]` 前缀喂给提取器会让 pkg/exc 提不出，long 语料里的 Java 崩溃静默降级。

### 5.2 切流约束与遗留缓存

- **遗留 v1 缓存一次性失效：** 升级后清掉历史 v1 条目，首开全部重解析一次；此后 Rust 是唯一写者与读者，不再背路径归一化与大小写映射的历史包袱。
- **切流与解析补齐必须同一批交付：** 解析没补齐之前不得清理遗留条目——旧条目反而比 Rust 重新解析更完整。「等价」指行为语义对齐（崩溃打标、long 格式、时间戳校验等），不要求实现路径逐位复刻（C1 口径）。
- **一次性清理只执行一次**（例如 `cache/` 内放哨兵文件 + 目录扫描），不得误删清理后 Rust 新写的条目；具体形态在刀1 design 定。

三条硬约束：

1. 解析语义要先于（或同批于）缓存写者切换——残缺语义会固化进缓存，身份校验挡不住。
2. 同一 `FORMAT_VERSION` 内不得静默改义——失效闸只有身份摘要；先切后补语义 = 接受一次整目录失效。
3. 遗留条目只保留「读」一个方向——键派生不一致只造成未命中，不会张冠李戴；但解析未补齐前不得清理遗留条目。

### 5.3 渐进发布：段化列存

| 方案 | 问题 |
| --- | --- |
| 整份 Arc 换手 | 每块拷全量 payload，O(n²)，否决 |
| 单条 Vec + 读写锁 | 解析期间持写锁，页查询被堵，违反硬约束 |
| **段化列存 + 原子发布** | 每块成不可变 Segment，`ArcSwap<Vec<Arc<Segment>>>` 追加；发布 O(1)，读页按行号定位段 |

选段化的原因：一次解决三件事——渐进发布、崩溃 3 行前瞻跨段、异步落盘（后台线程共享段）。代价是写侧要按列跨段遍历，且内存表示与落盘连续列之间要做一次适配。**归刀1：分块装载在刀1 交付，内部结构随刀1 一次定死，不在切流时返工。**

### 5.4 缓存路径的四个实现坑

1. `done` 被落盘拖后：改装载完成后交后台，`done` 先发，退出不等待。
2. 整份缓冲：写侧改 `BufWriter` 逐列写，读侧逐列读进目标数组（命中是高频路径）。
3. 临时名用 pid 会撞：补进程内唯一后缀（原子计数或 GUID）。
4. plan 阶段整份读缓存（`sidecar.rs:368`）：大条目拖慢 plan，命中路径峰值约为索引两倍。**已定（乙）**：命中判定仍在 plan 完成，但只读条目定长头部（魔数 / 版本 / 行数 / 段长形状 / 总长 / 身份四要素），不读字典与列；命中条目的解码移到装载开始、身份复核通过后进行，解码失败静默降级为完整解析并把 `loadMode` 更新为分块渐进（仅一次）。「把命中判定后移到 commit」与归档获取契约「命中判定在 plan 完成」冲突，不采用。

## 6. 输入链：已完成的部分与已知坑

### 6.1 已完成的形态

- **本地直读、8 KB 探针、描述符**：契约已归档（`openspec/changes/archive/2026-11-15-source-acquire-contract/`）。
- **代理获取与 spool**（`proxy.rs`）：Rust 拉起代理、读命名管道、把字节落成 spool 文件。
- **独占判活锁**（`locks.rs`）：用 `share_mode(0)` 独占试开 `<产物>.lock` 判断持有者是否存活。Rust std 的 `OpenOptions` 恒为全共享打开、表达不了独占，所以必须显式设置共享模式。
- **采集推流**（`logcore_feed` / `source.rs feed`）：C# 把行数组交进来，Rust 深拷贝入队后立即返回；行切分在推送前完成。
- **FFI 面**：plan / commit / feed / stop / close / cancel / poll_events / get_progress / get_descriptor / startup_maintain / shutdown（`ffi.rs`）。
- 设备侧 byte 流仍由 C# 读（跑 adb、读原始字节、不解码），推给 Rust；spool 临时文件的写、读、生命周期全在 Rust（C2，D1 无例外）。

### 6.2 采集字节保真（C21）

C# 不再把字节经 UTF-8 解码成 `string` 再编码，非 UTF-8 字节原样保留。必须绕开 `ExecuteRemoteEnumerableAsync(..., Encoding.UTF8)` 的解码层（`LogcatStream.cs:89`）；行级过滤（空行、横幅）在字节层做；行尾 LF/CRLF 的差异单独处理，不混入「等价」判据。验收以设备原始流为正确性基准；与旧 C# 采集文件在非 UTF-8 语料上的差异是**预期差异**，单列，不混入「等价」。

### 6.3 已知坑

| 坑 | 现状 | 收法 |
| --- | --- | --- |
| 活性锁 | **已收**：`locks.rs` 用 `share_mode(0)` 独占试开 `<产物>.lock` 判活（获取契约 D7 的维护口径） | — |
| 进程树回收 | **未收**：`proxy.rs` 仍是裸 `child.kill()`，只杀直接子进程 | Job Object（`KILL_ON_JOB_CLOSE`）或 `taskkill /T`，进刀5 design |
| 命令行组装 | `BuildLaunchArgs` / `BuildCommandLine`（`FileProxyService.cs:49-63`）的三条规则是隐藏契约：`{pipe}` 替换时含空格要加引号；缺省追加 `--pipe <名>`；旧配置剥掉尾部模板 | 搬 Rust 必须逐字节复刻，否则旧 `settings.json` 的行为会静默改变。`proxy.rs spawn_args` 已有派生管道名的测试，逐字节对拍仍需刀5 验 |

## 7. C# 侧现状事实（HEAD 核对）

### 7.1 引擎与性能

| 事实 | 依据 |
| --- | --- |
| 引擎代码集中在四个类 | `LogParser.cs` 441 行、`LogDocument.cs` 1100 行、`FilterEngine.cs` 1133 行、`CrashExtractor.cs` 225 行 |
| 过滤已并行 + SIMD | `FilterEngine.cs:493`/`:521` `Parallel.For`；`:678` `Vector256.Create`；`:479` 硬件加速分派 |
| 块扫描有三道分派闸 | `FilterEngine.cs:23` `BLOCK_SCAN_MIN_ROWS = 2_000_000`、`:27` `BLOCK_SCAN_MAX_KEYS = 8`、`:370` 候选比阈值 |
| 建索引才是单线程 | `LogDocument.cs:609` `ScanRange` 顺序扫描；`:440`/`:487` `Task.Run` 只是单工作线程 offload |
| 主界面走分块装载 | `frmMain.Load.cs:57` `BuildChunkedAsync`；整份入口 `frmMain.cs:2262`（ADB 自动重载） |
| 崩溃打标绑在索引扫描期、标在父行 | `CrashExtractor.cs:48`/`:59` |
| 增量追加走 `Reload` | `LogDocument.cs:942`：长度比较 + 64 KB 头部比较 → unchanged / appended / rebuilt |
| 编码探测未实现（硬编码 UTF-8） | `TextProbe.cs` 只做二进制/文本嗅探 |
| 目录时间线 / 多文件合并未实现 | `BACKLOG-DIR-TIMELINE.md`；`MergePart` 是文档内的分块合并 |

### 7.2 显示侧（D2 的现状依据）

| 事实 | 依据 |
| --- | --- |
| 锚点语义已存在 | `LogListView.cs:273` `CaptureAnchor()`；`:291` `PinAnchor`、`:309` `NearestModelRow`（二分找最近存活行） |
| 控件持有全部命中行号（数组所有权，不复制） | `:107` `SetRows`、`:121` `AppendRows`；分段结构 + 段前缀和 |
| 行号换算三件套依赖本地数组 | `:344` `DocRow`、`:357` `ModelRowOf`、`:309` `NearestModelRow`、`:330` `SegLastGe` |
| `RetrieveVirtualItem` 同步取数 | `:402` 缓存未命中就在 UI 线程读 mmap 并解码 |
| `CacheVirtualItems` 钩子已挂、处理为空 | `:413`——分页预取的天然落点 |
| 行缓存键 = 模型行号，`CACHE_SIZE` 20000，超限删半 | `:61`、`:62` |
| 消息截断 `MSG_LIMIT = 4000` 是显示策略 | `:14` |
| 标记既是 UI 状态也是过滤输入 | `:32` `HashSet<int> _marked`；`FilterSpec.MarkedOnly` 参与过滤（`FilterEngine.cs:871`、`:1057`） |
| 无排序 | 列固定，分页契约 v1 不需要排序 |
| 导出读行文本 | `FilterEngine.cs:1100` `ExportRows` 读 `doc.LineBytes(row)` 写原始字节 |

### 7.3 文件操作侧（D1 的搬迁面）

| 事实 | 依据 |
| --- | --- |
| 管线编排塞在窗体里 | `frmMain.cs:1087` `LoadViaProxyAsync`（建租约→拉代理→1 GB 确认→spool→建索引→挂 doc→失败兜删） |
| ADB 取流已落本地临时文件 | `frmMain.cs:2103` 写 `<exe目录>\Temp\logcat_live_<时间戳>.log`，走 `Reload` 增长分支 |
| 文件获取已跨进程且语言无关 | `FileProxyClient.cs:39` `NamedPipeClientStream` + JSON 行/原始块帧；示例代理在 `sample/fileproxy/` |
| spool 与活性锁依赖 Windows FileShare 语义 | `FileProxyService.cs:224` `SpoolCoreAsync`（8 MB 块）、`:419-451` `.alive.lock` |
| 设备侧是 .NET 依赖且 Windows-only | `LogcatStream.cs:2` `AdvancedSharpAdbClient`、`:65` `"logcat -v threadtime"`；`AdbManager.cs:353-396` 截图用 GDI+，被两个 exe 共享 |
| sidecar 治理是纯 IO | `SidecarCacheStore.cs`：`cache/`、temp + `File.Move(overwrite)`、LRU、1 GB / 条目 4 GB / 目录上限 |
| sidecar 格式钉死、无 payload 校验、不可追加 | `SidecarCache.cs:155` `FormatVersion = 1`；变长字典在数据区前，增长即整份重写 |
| sidecar 命中是一等公民，且命中失败静默降级 | `frmMain.Sidecar.cs:33` 无条件先读；`:9-10` 失败只写启动日志、UI 无感——这就是 §4.3 硬断言存在的原因 |
| 命中后 C# 自己映射源文件（索引不含文本） | `LogDocument.cs:200` `LoadIndexed` → `:216` `OpenMap` |
| 输入链代码体量 | 管道客户端 + 帧协议 15.2 KB；拉起/轮询/杀进程树约 200 行；spool 约 200 行；租约 + 锁 + 清理约 100 行；`LogcatStream.cs` 140 行；`AdbManager` 留 C# |

## 8. 分发与 CI 实账（逐项进 tasks）

| 项 | 要做的活 |
| --- | --- |
| CI 工具链 | 加 rustup + cargo build + 产物拷贝；GitCode / GitHub 双站都要过；要考虑构建缓存与时长 |
| 发布步骤 | publish 后补 cargo 产物拷贝；sc 与 fdd 两个包各补一条 `Test-Path` 断言 |
| fdd 体积 | 现价 2.0 MB（含 1.2 MB 演示 gif）；加 2–5 MB 的 dll 后翻倍到 3 倍，需要认账 |
| `.gitignore` | 只剩 C9 一件事：预编译 dll 的分发不被 `*.dll` 误伤 |
| 版本号 | `Directory.Build.props` 是唯一来源，Cargo.toml 对齐 → 第 6 处（C10） |
| 属性传染 | `SelfContained=true` 绝不能进 csproj；dll 拷贝同理，走 publish 之后的独立步骤 |
| 覆盖率 | Rust 不进 coverlet，口径必须改（C11） |

## 9. 本机环境实测

探针工程在 `.tmp-test/`（不入库）。

### 9.1 工具链

| 项 | 值 |
| --- | --- |
| rustc / cargo | 1.99.1（stable-x86_64-pc-windows-msvc，单工具链） |
| MSVC 链接器 | VS 18 Community，rustc 经 vswhere 自动定位。**坑**：PATH 上的 `link.exe` 会命中 Git 自带的版本，不能手工改 PATH；写 CI 别把 Git bin 提前 |
| 已装 target | x86_64-pc-windows-msvc、x86_64-unknown-linux-gnu（无 aarch64） |
| .NET / Node | SDK 10.0.401；Node v26.8.1 + npm 11.19.0（koffi 宿主就绪） |
| 版本固定 | 没有 `rust-toolchain.toml`；拆 workspace 时建议补一个 |

### 9.2 端到端与 panic

- cdylib 编译、`extern "C"` 导出、C# P/Invoke、Node koffi 同步/异步全部跑通（koffi 的异步形态是 `fn.async(args, cb)`，回调签名 `(err, result)`，没有 `lib.asyncFunc`）。
- 依赖 `memmap2` 0.9 / `rayon` 1.10 / `regex` 1.11 编译通过；`Mmap::map` 可用。
- edition 2024 可用，但每个导出函数必须写 `#[unsafe(no_mangle)]`。
- **panic 实测：** 裸 `extern "C"` 内 panic → 宿主进程直接死（abort，退出码 0xC0000409），不是静默损坏；套上 `catch_unwind` 薄包装 → 宿主存活、其余导出正常。D5 由此确认必需且有效。panic 之后句柄是否损坏（C4）另有实测任务，见刀2。

### 9.3 仓库就绪度与实测缺口

- 现状：`logcore/` 单箱 workspace 已建（含 Cargo.lock 与 `testdata/`）；CI 已构建 Rust core（windows-latest 自带工具链，`cargo build --manifest-path logcore/Cargo.toml`）；FFI 面与输入链已落地。仍无 `rust-toolchain.toml`。
- 缺口：`crates/` 三箱布局（§3.5）未建，刀1 前需拆 workspace；C9 / C10 / C11 未落。
- 未实测：`to_lowercase` ≡ `ToLowerInvariant` 的口径（刀3 必验）；P/Invoke marshalling 开销与页大小的匹配（刀2 量）；dll 真实体积（推算 2–5 MB，fdd 翻倍到 3 倍待实测）。

## 10. 未验证的推断

以下是尚只有推断或估算、没有实测支撑的内容：

| 内容 | 标注 |
| --- | --- |
| 一页 200 行 ≈ 40 KB、按页拷出是微秒级，同步 `RetrieveVirtualItem` 未命中取一页可以接受 | 推断；进程内适配先量，刀2 再量 native |
| 快速拖动滚动条无可感知停顿 | 未实测；需维护者实机（2K 缩放屏）验证 |
| koffi async 足以承载 MCP 的粗粒度长任务 | API 形态已实测；承载能力未实测，退路是 CLI + stdio |
| `GetFullPathNameW` 能覆盖 `Path.GetFullPath` 的全部归一化 | 推断，需向量对拍（长路径前缀 / 设备路径行为可能不同） |
| `to_uppercase` 与 `ToUpperInvariant` 只在 `ß` 等极少数情况下发散 | 推断，需路径向量坐实（影响面 = 旧条目是否命中） |
| 段化列存后读一页的定位成本可以忽略 | 推断，cabi 分页刀实测 |
| 整份 `fs::read` 在 1 GB 文件上的峰值 ≈ 文件字节 + 列 + 编码缓冲 | 估算，未实测；用同一文件与 C# 的 mmap + 32 MiB 块对照 |

## 11. 设计阶段待落实的偏向

这些不阻塞 propose，但方向已定，design 时照着落实：

- **sidecar 是否补校验（C6）：** 不动 v1 字节格式，校验另立 change。
- **编码判定口径（C7）：** 采用 `MCP-LOG-ANALYSIS-API.md` 里已有的启发式立 spec，走独立 change（这是新功能，不是迁移项）。
- **dll 分发方式（C9）：** 不入库；CI / 本地构建后拷进 publish 目录。
- **版本号第 6 处（C10）：** 发版清单里手工同步 Cargo.toml。
- **覆盖率口径（C11）：** 口径改成「C# 侧」并声明不含 Rust；刀2 落地时同批改 `docs/TESTING.md`。
- **页字段布局（C13）：** 定长数值列 + 变长字符串区（复用 sidecar 的列式）；JSON 只用于 MCP 侧。
- **查询世代与缓存失效（C14）：** 单调世代号随页响应下发，不匹配即丢；Rust 不主动通知（无回调）。
- **句柄线程安全（C15）：** 句柄内用 arc-swap 快照，导出函数线程安全；全局串行锁会违反「页查询不被扫描堵住」的硬约束，不取。

## 12. 外部待决项与关联

本笔记没有待拍的编号项；以下两件不属本笔记编号，但会影响排期：

- **DLT 解码归属**（`DLT-FORMAT-AND-RUST.md` K1）：属 DLT change 的 design 项。排期已定——刀1–刀5 零 DLT 工作，接缝已随归档的获取契约落死，仅列 id 随下一个列契约 change 同批定死。
- **MCP 工具面口径**（`MCP-FUNCTIONALITY.md` vs `MCP-LOG-ANALYSIS-API.md`）：刀4 待决。

关联文档：

- `RUST-CORE-EVALUATION.md`：语言决策来源；其中范围、接缝推荐、内存峰值口径已被本笔记修订。
- `openspec/notes/archive/2026-11-15-SOURCE-ACQUIRE-CONTRACT.md`：已归档；输入链契约出处。
- `FILTER-PREFILTER-AND-RUST.md` §4 / §10：过滤移植语义四条与「正则随刀3 交付」的出处。
- `DLT-FORMAT-AND-RUST.md`：DLT 解码归属（其 K1）。
- `docs/TESTING.md`、`docs/BUILDING.md`：测试基线（不内联数字）与发布步骤。
