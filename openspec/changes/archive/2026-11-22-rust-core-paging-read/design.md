# Design

## Context

- 刀1 已归档：`crates/core` 有段化列存（`Segment` 8 列 + `Store` 字典/崩溃表）、`LiveIndex`（`ArcSwap<Vec<Arc<Segment>>>` + 行数原子）、整份 `Payload`（常驻句柄）与 sidecar 读写；`crates/cabi` 只有获取面导出。
- 文本不常驻：文件装载时整份读入、解析后丢弃；装载完成后保留只读文件句柄，句柄另存路径与 `offs/lens/moff`；sidecar 不含文本。
- C# 旧线用 mmap 自读文本（`LogDocument`），绑定层已有获取面但产品代码未引用。
- 探索结论（`openspec/notes/2026-11-20-RUST-CORE-PAGING-CONTRACT.md`）：Q1 乙（ord 寻址）、Q2 甲（打包二进制）、Q3 丙（原文 + `moff` + 时间戳偏移）、Q4 已定常驻只读句柄（装载时打开、页读取复用；每页重开被否决）。
- 两处地基缺口：`LiveIndex::publish` 先存行数后存段，读侧可见"行数多、段少"的窗口；采集消化线程从不发布段，采集在途没有任何可读索引。
- 验收口径：Rust UT + IT；`cargo test` 串行（进程级代理登记表）。

## Goals / Non-Goals

**Goals:**

- 文件源装载完成后，按 ord 分页返回原文、列值、时间戳区间、tag 与崩溃摘要字段。
- 页文本与装载索引来自同一份装载内容；源路径轮转/替换后不混入新文件内容。
- 页数据用带版本号的打包二进制一次跨 FFI；布局由双端固定字面量测试钉死。
- 修 `publish` 顺序；采集消化线程每批冻结并发布段（仍不开放采集读取）。
- C# 绑定层扩到分页并通过 IT；不接 UI。

**Non-Goals:**

- `set_result(ids)`、过滤与正则、标记与 `.lcm`、导出（刀3）。
- 线程级 panic poisoning 与句柄作废码（刀2 后续块）。
- 渐进装载中的页读取；采集源分页读取；UI 接入与切流。
- 不改 sidecar 字节格式、缓存行为、获取面导出与既有错误码值。

## Decisions

### D1 页寻址与结果集语义

`page(start, count)` 的 `start` 是结果集序号（Q1 乙），本 change 结果集恒为文档全部行，因此序号与文档行号相等。后续 `set_result` / `open(filter)` 只替换"结果集表"，页接口与二进制布局不变；这样刀3 不需要重写读取面。页内序号用 u32 承载，本代支持的行数上限为 i32::MAX（2,147,483,647），与 sidecar v1 行数字段、崩溃表行号的范围一致；超出上限的源不在支持范围，不新增装载期检查（整份装载本身受内存约束）。

备选：只按文档行号（刀3 后宿主无法把可见行序换算成文档行号，否决）；双寻址（两套编号容易传错，否决）。

### D2 页二进制布局 v1

小端、定长行记录 + 页内字符串表 + 文本区；版本字段允许以后加列而不破坏旧宿主。

```
头部（36 字节）：
  magic       4   "LPAG"
  version     4   u32 = 1
  rows        4   u32（本页行数）
  startOrd    4   u32（本页首行序号）
  tagCount    4   u32
  excCount    4   u32
  pkgCount    4   u32
  textBytes   8   u64

行记录（rows × 52 字节）：
  ord         4   u32
  level       1   u8（原始级别码，0 = 未知）
  flags       1   u8（bit0 = 崩溃行）
  crashKind   1   u8（0 Java / 1 Native / 2 ANR / 3 Tombstone）
  pad         1
  pid         4   i32（-1 = 未知）
  tid         4   i32（-1 = 未知）
  moff        4   u32（消息起始偏移，0 = 整行）
  timeStart   4   u32
  timeLen     4   u32（0 = 无时间戳）
  textOff     8   u64（原文在文本区内的偏移）
  textLen     4   u32
  tagRef      4   u32（0xFFFFFFFF = 空）
  excRef      4   u32（0xFFFFFFFF = 空）
  pkgRef      4   u32（0xFFFFFFFF = 空）

字符串表：tag / exc / pkg 各自为若干条 [len u32 + UTF-8 字节]
文本区：各行原文按行序拼接（不含行尾 \r\n）
```

字符串表只收录本页用到的值（页内去重、自包含），宿主无需另行获取字典；全局字典快照随整份走，页内表不做跨页共享。行记录中的 `pad` 与 `flags` 保留位 MUST 写 0；读侧 MUST 忽略未知位与保留字节，为 version 演进留余地。页缓冲由 core 用 `Vec<u8>` 分配、通过 `logcore_free_page(ptr, len)` 释放；C# 绑定在调用返回后立刻解析成托管数组并释放。

空页也使用同一头部：`rows = 0`、`startOrd = 0`，字符串表与文本区为空，不省略头部。

### D3 文本切片归 core 定义、宿主执行

每行返回原文 + `moff` + `timeStart/timeLen`（Q3 丙）。`timeStart/timeLen` 只由行首时间戳语法决定，与 pid/tid/级别/tag 是否识别、`moff` 是否为 0 无关：无年/带年 threadtime 取到小数秒末位，方括号 long 跳过 `[ ` 前缀后同样止于小数秒末位（无小数秒时止于秒末位）；行首不匹配这些形态时 `timeLen = 0`。宿主只做偏移切片：`time = raw[timeStart..timeStart+timeLen]`、`message = raw[moff..]`（`moff = 0` 取整行），不实现时间戳识别。宿主必须先按字节切片、再按 UTF-8 解码，避免正文含多字节时字符下标错位。

本契约不迁就旧 C# `HeaderBytes` 的展示口径（新 UI 与 MCP 均按本契约重写）；区间在页组装时对已取回原文现场计算，不新增段列、不改 sidecar。

备选：宿主自行识别时间戳（Node/MCP 需重写规则，否决）；core 预切两个字符串（丢原文、复制文本，否决）；索引时把区间存进段（每行 +8 字节常驻，并迫使 sidecar 升版，否决）。

### D4 文本读取：装载时保留只读句柄 + 按偏移读

装载时经 `long_path_for` 打开 `sourcePath`（共享读、写、删除），以 `Arc<File>` 存入 `Inner` 并在进入 `Loaded` 前保留；页请求复用该句柄，用 `seek_read` / `read_at` 按 `offs` 读 `lens`，不改文件游标，页间互不干扰。句柄绑定的是装载时的文件对象：源路径被改名轮转、删除或替换后，页仍读原内容；源文件被截断导致长度不足时整页返回 `IO_ERROR`（`path` / `osError`），不返回部分页。

身份采集、整份装载读取、sidecar 落盘的 `after` 身份与页读取必须使用同一根句柄：打开后从该句柄取 metadata、首尾指纹并读取整份，不再按路径二次打开。缓存命中路径不重新解析，保留同一句柄前复核身份（size + mtime + 首尾指纹），不一致按未命中重新装载。句柄打开或装载读取失败按装载失败（`Failed`），不进入 `Loaded`。copytruncate 式原地截断重写不受句柄保护：同一文件对象被改写后，即使长度恢复也可能读到新内容，此边界在契约中声明为不保证。

备选：每页重开（轮转后可能按路径读到新文件，无法兑现"不混入"；页前头指纹校验又抓不住等长中间改写，否决）；mmap 常驻（阻塞改名/删除，与临时产物清理冲突，否决）。

### D5 页快照、状态与并发（整份即读面）

- 文件源的**整份**就是句柄里的 `Arc<Payload>`（8 列 + 三字典 + 崩溃表），装载完成（`Loaded`）后不再变化；页读取以它为唯一读面，不读 `LiveIndex` 的段列表。段只是整份的分片生产单元，不复制第二份数据。
- 页读取与行数读取仅在 `Loaded` 状态合法；`PlanReady`/`Committing`/`Running`/`Stopping`/`Stopped`/`Failed` 返回 `INVALID_STATE`，`Closed` 返回 `CLOSED`。
- `Loaded` 的前置条件包含源句柄已就位：装载读取、身份采集与页读取共用同一 `Arc<File>`；打开或装载读取失败按装载失败处理。
- 一次页请求在 `inner` 锁内取 `payload` 的 `Arc`（行数取 `payload.row_count()`）与源句柄的 `Arc<File>`，释放锁后按偏移读文本；页内各行与跨页均来自装载时的同一文件对象，轮转/替换不会混入新文件。
- **缓存命中天然可读**：`CacheStore::load` 直接给出整份，与首次解析产物同形；页读取不依赖 `parse_source` 产生的段，两条装载路径的读面一致。
- **关闭顺序**：先释放源句柄，再清理由句柄持有的临时产物（代理落盘文件等），避免 Windows 上占用阻止删除。
- **修正 `LiveIndex::publish` 顺序**：先 `segments.store`、后 `rows.store`。本期文件页读取不走段，此修正面向采集/前瞻的段快照读方，仍随本 change 落地。
- 段 + 字典 + 崩溃表的统一快照抽象（供采集读取）不在本 change 建立，留到采集读取 change；本期不引入第二种布局。
- 本能力不承诺同一句柄的并发调用：宿主串行使用 `rowCount` 与 `page`；锁内取快照、锁外读文本只为缩短锁持有，不构成并发安全承诺。

### D6 采集批发布（内部地基）

`Indexer` 增加 `freeze()` 与 `frozen()`；消化线程每成功处理一批后 `freeze` 并调用 `shared.live.publish(frozen().to_vec())`，行数仍由既有原子更新。跨批崩溃前瞻窗口保留在 `Store.pending`，不受 `freeze` 影响。本 change 不开放采集源页读取（`Running` 仍返回 `INVALID_STATE`），发布仅为下一刀铺路。

代价：每批克隆一次段指针列表，长采集下为 O(批次数²) 个 `Arc` 克隆；单批 feed 上限 65536 行，粗估可忽略，任务中带多批次测试并在探针里记录。

### D7 cabi 导出面

新增三个导出，均套既有 `guard`（同步 panic 转 `INTERNAL`）：

- `logcore_row_count(handle, out_rows: *mut u64) -> i32`
- `logcore_page(handle, start: i64, count: i64, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32`
- `logcore_free_page(ptr: *mut u8, len: usize)`

`rowCount` / `page` 的状态门禁走 `state.rs` 新增的 `Op::RowCount` / `Op::Page`（仅 `Loaded`），不另写状态判断。

参数校验：`out_rows` / `out_ptr` / `out_len` 为 null → `INVALID_ARG` 且 `params.field = "out"`；`count < 0` 或 `count > MAX_PAGE_ROWS`（4096）→ `INVALID_ARG` 且 `params.field = "count"`；`start < 0` → `INVALID_ARG` 且 `params.field = "start"`；`count = 0` 返回空页。`start >= rowCount` 返回空页。状态检查先于参数检查：非法状态一律 `INVALID_STATE`，避免宿主用参数探状态；参数检查只在状态合法后进行。

页区间按 `end = start.saturating_add(count).min(rowCount)` 计算，避免 `start` 接近 `i64::MAX` 时溢出。失败路径必须把 `*out_ptr` 写为 null、`*out_len` 写为 0（`out` 指针本身为 null 的调用无法写，除外），宿主在错误分支不得读取或释放 out 值。

空页（`count = 0` 或 `start >= rowCount`）仍返回 36 字节头部：`rows = 0`、`startOrd = 0`，字符串表与文本区为空；缓冲照常经 `logcore_free_page` 释放。

页缓冲的 `Vec` 以精确长度分配，释放时按 `len = capacity` 重建，避免容量与长度不一致导致未定义行为。

### D8 C# 绑定层

`LogcoreNative.Exports` 增加 `LogcorePage`、`LogcoreRowCount`、`LogcoreFreePage` 三个委托与绑定；`LogcoreContracts.cs` 增加 `PageRow`（含崩溃摘要子类型）；`LogcoreSource` 增加 `RowCount` / `GetPage(long start, int count)`，把原生缓冲解析为 `PageRow[]`（含 `Ord / Level / Pid / Tid / Tag / Time / Message / Raw / Crash`）。`Time` / `Message` 先按字节区间切片再 UTF-8 解码，`Raw` 保留原始字节；解析成功后立即释放原生缓冲，解析抛异常也必须经 `try/finally` 释放。空页返回空数组，不抛异常。`LogcoreErrorCodes` 增加 `MaxPageRows = 4096`，并用跨端一致性测试（读 core 常量，口径同 `BindingFeedLimitConstantTests`）锁定。绑定层不引入 UI 依赖；新增 IT 类标 `[Collection("Logcore")]`；IT 用 `crates/core/testdata/` 的小样本，断言列值、时间切片、崩溃字段、末页短页与空页。

### D9 验收与文档

- Rust UT：时间戳区间（threadtime / 带年 threadtime / long / 无时间戳 / 时间戳可识别但元数据未知）、二进制布局固定字面量（头部 + 单行记录逐字节）、崩溃摘要字段（Java 崩溃头带类别/异常类型/包名、异常类型缺失为空、普通行不带）、字符串表去重、空页头部形状、`Op::RowCount` / `Op::Page` 状态矩阵、`publish` 顺序不变量。
- Rust IT：整份装载与分块装载后取页结果一致且与源文本逐字节一致、末页短页、越界空页、截断文件报错且状态不变、装载读取与页读取同一句柄（装载期间替换仍一致、打开失败转 Failed）、源文件改名轮转或替换为更长文件后仍读装载内容、缓存命中后取页与首次一致、采集 Running 期间段数增长。
- C# IT：绑定层解析布局 v1、错误码映射、页释放后无二次释放、空页返回空数组、`RowCount` 装载前拒绝、缓存命中后取页一致、源文件改名后仍读装载内容。
- 探针：200 行页的端到端耗时（句柄按偏移读取 + 拷贝），不设通过/失败阈值，只记录基线数字回填探索笔记的未实测清单；性能预算留到刀3 过滤路径有对照后再定。
- `docs/TESTING.md` 更新 Rust 侧条数与覆盖率口径（新增导出与绑定层带来分母变化）。

## Risks / Trade-offs

- **布局提前冻结** → 版本字段 + 双端固定字面量测试；以后加列走 version 2，不移除旧字段。
- **常驻只读句柄** → 句柄随 `Inner` 生命周期；关闭须先放句柄再清临时产物；共享删除让改名轮转不被阻塞。
- **页读取与文件外部改写竞态** → 轮转/替换由句柄绑定原文件对象兜住；截断按长度不足整页失败；copytruncate 原地重写不保证，写入契约。
- **采集批发布 O(批次数²)** → 多批次测试 + 探针记录；若成为瓶颈，后续改为追加友好结构，不改本 change 契约。
- **装载中读取被拒** → 本 change 明确为非法状态；刀2 后续块若要渐进读，需 MODIFY 本能力并补字典快照。
- **C# 绑定无产品调用** → 只有 IT 覆盖，回归面有限；这是刻意的（UI 接入独立 change）。

## Migration Plan

1. 修 `publish` 顺序与采集批发布，跑现有 Rust 全量确认无回归。
2. 实现时间戳区间、源句柄生命周期与页组装，UT 红→绿。
3. 落二进制布局与 cabi 导出，固定字面量 UT 钉死。
4. 扩 C# 绑定与 IT。
5. 跑探针记录数字，更新 `docs/TESTING.md`。
6. 回滚策略：纯新增与两处内部修正，按提交回退；无数据格式迁移。

## Open Questions

无。