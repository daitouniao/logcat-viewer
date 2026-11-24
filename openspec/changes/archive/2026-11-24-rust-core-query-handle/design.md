# Design

## Context

- 过滤内核已落地：`crates/core/src/filter.rs` 的 `FilterExpr::from_json` / `compile` / `scan(&Payload, &File, display, &CompiledFilter) -> ResultSet` 是纯函数；空条件返回恒等视图，不复制行号。
- 结果集两态已定：`crates/core/src/result.rs` 的 `ResultSet { Identity { rows } | Rows(Arc<[u32]>) }`，提供 `len` / `doc_row` / `resolve`（命中、最近、平局取前、越界首末、空结果 `None`）。
- 分页已定：`crates/core/src/page.rs` 的 `page_rows_for` 按结果集序号取连续一段，`encode_page` 产出布局 v1；行记录首字段是文档行号，页头第 4 字段是首行结果集序号。`SourceHandle::read_page` 在锁内取 `Arc<Payload>` / `Arc<File>`，锁外回读文本并编码。
- 源句柄：`crates/core/src/source.rs` 的 `Inner` 在装载后持有 `payload` / `source_file`，`close` 释放资源并尽力删除临时产物；状态矩阵在 `crates/core/src/state.rs`。
- cabi：`crates/cabi/src/lib.rs` 每个导出体包 `catch_unwind`，失败经 `logcore_take_last_error` 取走；页缓冲由 `logcore_free_page(ptr, len)` 释放。
- C# 绑定：`Services/Logcore/LogcoreNative.cs`（导出表）、`LogcoreSource.cs`（页解析）、`LogcoreContracts.cs`（`LogcorePageRow.Ord`）。IT 位于 `tests/logcat.Tests/Logcore*ITs.cs`，要求先 `cargo build`。

## Goals / Non-Goals

**Goals:**

- 在 core 内建立查询句柄：开启时完成扫描并持有不可变快照，之后只做读取。
- 把查询接到既有分页编码与文本回读，不产生第二套页格式。
- 通过 cabi 暴露最小接口，C# 绑定可独立验证；对外坐标只用真实文档行号。
- 明确生命周期：查询与源句柄解耦，关闭源不使已开查询失效。

**Non-Goals:**

- 不做异步开启、进度、取消、查询状态机。
- 不做正则、标记、导出、流式统计、采集源。
- 不接 UI、不接 MCP、不删 C# 旧引擎。
- 不做并行扫描与性能验收。

## Decisions

### D1：查询是独立句柄，持有装载快照的 `Arc`

```text
query::QueryHandle {
    payload: Arc<Payload>,
    file: Arc<File>,
    display: String,
    result: ResultSet,
}
```

- 开启时从源句柄取一次 `(Arc<Payload>, Arc<File>, display)`，扫描后与结果集一起存入查询；此后查询不加锁、不可变。
- 源句柄关闭不影响已开查询：查询继续读装载内容（与分页的「装载内容一致性」同口径）。
- **为什么**：FFI 所有权最简单——查询不借用源句柄，不存在跨句柄失效、弱引用或关闭顺序检查；`Arc` 零复制共享索引，只在结果集上有 4 B/命中行的增量。
- **代价**：几乎没有。Rust 标准库打开文件默认带删除共享，本机探针实测：源关闭时删除临时产物成功，已打开句柄仍可读被删除文件，目录也能删除——查询继续读装载内容，不留残留。若删除失败（外部占用等既有尽力而为场景），残留交 `startup_maintain`。
- **备选**：查询作为源的子对象、源关闭即失效——需要查询登记表、失效广播与 close 顺序约束，复杂度明显更高，收益只是尽早删除临时文件。

### D2：对外只认真实文档行号，不外泄结果集序号

- `query_page(start_doc_row, count)` 只做**顺序翻页**语义：向后定位第一条文档行号 `>= start_doc_row` 的结果行，没有则空页，不向前回退。
- `query_page_backward(end_doc_row, count)` 做**向上翻页 / 跳到尾部**语义：取文档行号严格小于 `end_doc_row` 的最近 `count` 条结果行，页内升序；不足则返回全部；没有则空页。宿主用「当前页首行文档行号」作终点向上翻，用「文档行数」作终点跳到尾部。
- `query_resolve(doc_row)` 返回**最近结果行的文档行号**，不返回结果集序号；空结果集返回无定位；负锚点按小于首条处理，取首条。
- 起点、终点与条数的参数校验先于空页判定：起点/终点为负时即使条数为零也返回参数错误（与源分页的校验顺序一致）。
- 锚点定位由宿主组合完成：`resolve(锚点)` 得到最近结果行的文档行号，再以该值 `page`，即「从最近结果行起页」；两种意图不可互换，与过滤规格一致。
- 内部实现仍是 `ResultSet::resolve` → `ord` → `doc_row`，`ord` 只用于调用 `page_rows_for`。
- **为什么**：用户已定「对外参数保持真实坐标，内部序号不外泄」。若把 `ord` 暴露给 UI，过滤条件变化后宿主会让滚动位置指向错误行；文档行号是唯一跨查询稳定的坐标。
- **备选**：`resolve` 返回 `ord`，另加按 `ord` 起页的入口——把内部表示变成公共契约，换过滤条件后宿主必须自行换算。

### D3：开启查询同步执行扫描

- `open_query` 在调用线程内完成过滤（含按块回读正文），成功才返回查询；失败不产查询、不改变源状态。
- 不提供进度、取消与后台任务；宿主决定在哪个线程调用（UI 可自行放到工作线程）。
- **为什么**：本刀的目标是把纯函数接上生命周期，不是查询调度；扫描耗时等 UI 接入后有基线再决定是否异步化，提前做会引入查询状态机与取消语义。
- **备选**：异步开启 + 查询状态（Pending/Ready/Failed）——当前无消费者，且会把状态矩阵扩大一倍。

### D4：复用分页编码与文本回读，不新增页格式

- 把 `SourceHandle::read_page` 中的「按结果集区间取行 + 回读原文 + 编码」抽成共用函数（入参含 `&ResultSet`、起始序号、条数、`Arc<Payload>`、`Arc<File>`、显示路径）；源分页传恒等结果集，查询分页传自身结果集。向后翻页先用结果集算出「终点之前的最后 `count` 条」的起始序号，再走同一函数；两种翻页只有「求起始序号」不同。
- 查询页与分页页同为布局 v1：行记录首字段是文档行号，页头第 4 字段是首行结果集序号；空页只含头部。
- C# `LogcorePageRow.Ord` 更名为 `DocRow`，解析字节不变；产品代码零引用，IT 中四处断言随更名调整。
- **为什么**：页格式、空页与释放口径已由分页规格钉死；复制一份会让两条路径漂移。更名消除「identity 下 Ord 恰好等于文档行号」造成的误解，为查询页做准备。
- **备选**：查询页新增带 `ord` 的字段——宿主不需要，且与 D2 冲突。

### D5：状态矩阵只加一格，查询自身无状态机

- 新增 `Op::OpenQuery`：仅源状态 `Loaded` 放行；`Closed` 返回 `CLOSED`；其余返回 `INVALID_STATE`。
- 查询句柄只有「有效 / 已释放」两态：释放后调用属宿主违约（与既有句柄同口径），`free_query` 对空指针安全。
- 采集源不进入 `Loaded`，因此本刀自然拒绝采集源查询，无需额外分支。
- **为什么**：查询不可变，没有 `Loading`/`Failed` 之类中间态；加状态机是无消费者的假想需求。

### D6：错误口径复用现有码表，不新增错误码

- 过滤表达式非法：`INVALID_ARG`，`params.field` 沿用过滤内核的归因（`filter` / `levels` / `minutes` / `tags.values` / `msg.mode` 等）。
- 起始/终点/条数非法：`INVALID_ARG`，字段 `start` / `end` / `count`；条数上限复用 `MAX_PAGE_ROWS`；校验先于空页判定（负起点 + 零条数仍报起点错）。
- 句柄为空：`INVALID_ARG`，字段 `handle` / `query`；输出指针为空：`INVALID_ARG`，字段 `out`。
- 文本回读失败：`IO_ERROR`，参数 `path` / `osError`；不产出部分页、不改变查询与源状态。
- 无定位不是错误：返回成功并把输出文档行号写为 `-1`（文档行号非负，哨兵无歧义）。
- **为什么**：C# `LogcoreErrorCodes` 已与 Rust 同表并钉死「码值一经发布不改」；查询失败都能归入既有类。

### D7：cabi 面最小且对称

```text
logcore_open_query(source_handle, filter_json, out_query) -> i32
logcore_query_row_count(query, out_rows) -> i32
logcore_query_resolve(query, doc_row, out_doc_row) -> i32   // 无定位时 OK + -1
logcore_query_page(query, start, count, out_ptr, out_len) -> i32
logcore_query_page_backward(query, end, count, out_ptr, out_len) -> i32
logcore_free_query(query) -> i32
```

- 失败路径先清空输出（指针置 null、长度置 0、行号置 -1），与分页导出同口径。
- 查询页缓冲复用 `logcore_free_page`，不新增释放入口。
- **为什么**：六个符号覆盖「开、读、定位、双向取页、释放」；不把查询做成源句柄上的 `queryId`，避免在源句柄里维护查询表。

### D8：C# 只扩绑定层与 IT

- `LogcoreNative` 增加六个委托与导出绑定；新增查询包装（持有原生查询指针与 `Exports` 引用，页解析复用现有函数）。
- `LogcoreSource` 增加开启查询的方法；`LogcorePageRow.Ord` → `DocRow`。
- IT 用真实 dll 走 C ABI，覆盖：过滤结果、按文档行起页、向上翻页与尾部、定位、源关闭后读取、非法条件归因、页尾与空页。
- **为什么**：UI 切流会同时改动显示与交互，混在本刀会让评审看不清接口是否正确；绑定 + IT 已能证明接口可用。

### D9：无查询数量上限，资源账公开

- 同一源可开任意多个查询，各自持有 `Arc<Payload>`（共享）与结果集（4 B/命中行）。
- 同一查询上的读取由宿主串行（与分页同口径）；不同查询之间不加锁。
- **为什么**：D17 已定多源并存、core 不设上限；查询复用同一原则，回收责任在宿主。

## Risks / Trade-offs

- **同步开启可能阻塞调用线程**（272 万行 + 正文回读）→ 规格明确开启是同步的，宿主自行选择线程；性能基线等 UI 接入后另立优化刀。
- **源关闭后临时产物已取消链接** → 探究实测：删除成功、句柄仍可读，查询继续读装载内容；删除失败（外部占用）时残留交 `startup_maintain`，与既有尽力而为口径一致。
- **中段按结果序号精确跳转仍无入口** → 本代只提供文档行号坐标；若新 UI 的滚动条需要按索引精确跳转，另立 change 增加序号入口（加法，不改既有函数）。
- **多查询内存** → 结果集 4 B/命中行；宿主负责释放，测试覆盖重复开启与释放。
- **页头第 4 字段的「首行序号」在查询页不等于文档行号** → 规格明确行坐标为文档行号，C# 只读行记录字段；页头值仅供内核/调试。
- **`DocRow` 更名触及现有 IT** → 仅四处断言，编译期即可发现遗漏，字节不变。

## Migration Plan

无持久化格式与 FFI 既有符号变化：新增导出、C# 绑定扩展；回滚即回退本次提交。LPAG、`.lcs`、`.lcm` 不迁移，C# UI 行为不变。

## Open Questions

- 查询开启是否需要进度/取消：等 UI 切流后有实测基线再定。
- 滚动条按结果序号精确跳转：本代只提供文档行号坐标（双向取页已覆盖向上滚动与跳到尾部）；若新 UI 需要按索引直接跳转，另立 change 增加序号入口。
- 正则、`markedOnly` 与 `.lcm`、导出如何接入查询：各自刀次在过滤条件与查询选项上扩展，不重做本 change 的句柄形态。
- 采集源统一快照：另立 change；本代的 `OpenQuery` 状态门禁已天然排除采集源。