# Design

## Context

- 上一刀 `rust-core-result-set` 已落地 `ResultSet { Identity { rows: u32 } | Rows(Arc<[u32]>) }` 与 `resolve`（`crates/core/src/result.rs`），页装配已改为「`ord` → `docRow` → 取数」（`crates/core/src/page.rs`）；`Rows` 分支至今没有生产者。对外 `page(start)` 的 `start` 已定为真实文档行号。
- 整份 `Payload`（`crates/core/src/sidecar.rs`）持有结构化列：`lvls`（`u8`，0 = 未知，1–7 = V/D/I/W/E/F/A）、`pids` / `tids`（`i32`，未知 = -1）、`minutes`（`u8`，时间戳分钟 0–59，未知 = 255）、`tag_ids`（`i32`，无 tag = -1）与 tag 字典；**不含正文**；正文位置由 `offs` / `lens` / `moff` 描述，消息为 `[offs + moff, offs + lens)`。
- 源句柄在装载后常驻 `Arc<File>`（`crates/core/src/source.rs` 的 `Inner::source_file`）；`read_page` 在锁外经 `identity::read_exact_at` 逐行回读文本。装载后文件被轮转/替换时，该句柄仍指向装载内容。
- C# 现行过滤（`logcat-viewer/Services/FilterEngine.cs`）的可见语义是移植参照：条件之间 AND；预筛顺序 level → pid → tid → minute → marked → tag → message；忽略大小写 = ASCII 行字节折叠、含非 ASCII 行整行 Unicode 小写化；空词、排除语义见 `openspec/specs/old/message-filter/spec.md`。本 change 不引用其实现形态（并行块扫等），只对齐行为。
- 约束：只动 `crates/core`，不扩 cabi、C#、LPAG、sidecar；不新增依赖（`serde` / `serde_json` 已在）。

## Goals / Non-Goals

**Goals:**

- 在 core 内建立永久的过滤条件模型（含 JSON 形态）与扫描入口，产出 `ResultSet`，让 `Rows` 分支获得第一个真实生产者。
- 判定口径与 `logcore/filter` 规格逐条对应，测试用固定样本与手写字面量期望钉死。
- 正文只在必要时回读，且回读方式与分页同源（常驻句柄 + 装载内容）。

**Non-Goals:**

- 不引入查询句柄、queryId、状态机、异步、进度、取消；不扩 `Op` 状态矩阵。
- 不做正则、时间区间、标记、导出、流式统计；不做采集源；不做并行与性能目标。
- 不把过滤结果接入 `page`/`resolve` 的对外入口（函数级组合可用，句柄级待下一刀）。

## Decisions

### D1：结果集两态复用，不新增第三态

- 无任何有效条件（按 D6 判定） → `ResultSet::identity(payload.row_count() as u32)`，零分配；因空词恒真的正文条件不在“未启用”之列。
- 行数超过 `u32::MAX` 时与 `read_page` 同口径 `debug_assert`；本代不新增错误码。
- 有命中 → `ResultSet::from_rows(rows, doc_row_count)`，`Vec<u32>`（升序文档行号）转 `Arc<[u32]>`。
- **为什么**：1a 已把两态与 `resolve`/`page` 定案；过滤只是给 `Rows` 供数。新增「可追加」第三态没有生产者（采集过滤不在本刀），属假想需求。
- **备选**：每次物化 `0..rows`（无过滤也分配，违背零复制）；查询 buffer 自行定义（与 `ResultSet` 重复，下一代还得合并）。

### D2：条件模型用扁平 JSON + 嵌套类别，作为未来 FFI 的永久形态

```json
{
  "levels": [3, 4],
  "pids":  { "values": [100], "exclude": false },
  "tids":  { "values": [200], "exclude": false },
  "minutes": [0, 30],
  "tags": { "values": ["Activity"], "match": "substring", "op": "or",   "case": false, "exclude": false },
  "msg":  { "values": ["started"],  "mode": "substring", "op": "and",  "case": false, "exclude": false }
}
```

- 字段 `camelCase`，`deny_unknown_fields`；缺省值：`match = "substring"`、`op`（tag）= `"or"`、`op`（msg）= `"and"`、`case = false`、`exclude = false`、`mode = "substring"`。
- 枚举字面量：`tags.match` 允许 `substring` / `exact`；`tags.op` / `msg.op` 允许 `and` / `or`；`msg.mode` 仅 `substring`。未知值（含 `regex`）一律 `INVALID_ARG` + 对应 `params.field`，不静默降级。
- 枚举与范围在解析后用 Rust 侧校验，给出 `INVALID_ARG` + `params.field`（`levels` / `minutes` / `pids.values` / `tids.values` / `tags.values` / `tags.match` / `tags.op` / `msg.mode` / `msg.op`）；JSON 类型或形状错误（含已知字段类型不符）统一 `INVALID_ARG` + `params.field = "filter"`。
- 取值域：`levels` 仅 1–7（0 = 未知级别，不可作为过滤值）；`pids.values` / `tids.values` 必须 ≥ 0（-1 = 未知，不可选）；`tags.values` 每项非空；`minutes` 0–59。
- `levels` / `minutes` 以有符号整数（`i32`）反序列化后再校验范围，确保 0、8、300 等越界值归因到 `levels` / `minutes`，而不是被 serde 当成类型错误归到 `filter`。
- **为什么现在定 JSON**：过滤入口最终经 C API 收 JSON；先在 core 定 schema，下一刀 cabi 只做透传，不会出现第二套字段名。若只做 Rust 结构体，cabi 刀就要重新定义映射，属返工。
- **备选**：直接复用 C# `FilterSpec` 的扁平字段（`pidExclude` 等）——字段与类别绑定，MCP 的 `match`/`mode` 无处安放；自由字符串 `op` 不校验——错误延后到扫描期。

### D3：两阶段扫描——结构化预筛在前，正文回读在后

1. **结构化阶段**：按列数组判定级别、pid、tid、分钟、tag；候选行保持文档升序。
2. **正文阶段**：仅当 `msg` 类别启用时执行，只回读通过结构化预筛的候选行；无正文条件时零回读。条件值全为空词时判定为恒真，可不回读正文，但仍按启用处理排除。按需回读后判定子串并可选排除。

- 判定顺序固定为 level → pid → tid → minute → tag → msg（廉价在前，与现有引擎一致），便于用例逐层定位失败。
- 单线程、按文档顺序；**为什么**：本刀只验正确性，并行会引入分块/合并顺序问题，且性能目标未定。候选顺序天然升序，无需后排序。
- 与分页组合：分页起点为真实文档行号，按宿主意图分两种——条件变化后的**锚点定位**把当前锚点交给 `resolve` 求 `ord`（取最近、平局取前、越界夹到首末），再从该 `ord` 取页；**顺序翻页**只向后定位第一条 `docRow ≥ start` 的结果行，没有则空页、不回退。二者不可互换：锚点定位允许回退到最近结果，顺序翻页不允许。
- **备选**：正文先扫（对不满足元数据的行白读 IO）；先并行再合并（此阶段无收益证据，复杂化结果顺序保证）。

### D4：正文按块顺序回读，不逐行 pread、不整份读、不 mmap

- 候选行的 `offs` 升序；维护一个可复用缓冲（默认 256 KiB）与当前覆盖区间：行区间落在缓冲内直接切片，否则从该行起点起读一块。块长取 `min(max(默认块长, 行长度), doc_end - 起点)`，`doc_end = max(offs[i] + lens[i])` 为装载内容末尾；块读只到 `doc_end`，避免在末行/小文件上向 EOF 之外读而误报。
- 正文区间为 `[offs + moff, offs + lens)`；`lens` 不含行终止符（`\n` 及紧邻其前的 `\r`），`moff = 0` 的行整行视为正文；空正文直接按空切片判定，不回读。
- 源显示路径由调用方以只读字符串传入，仅用于 `IO_ERROR` 的参数，不参与判定。
- 回读失败（`doc_end` 之前的短读、截断/不可读）→ `IO_ERROR`，`params` 复用 `source.rs` 的 `io_error` 键名（`path` / `osError`），整次扫描失败、**不产出部分结果集**；不改变句柄状态（本刀无句柄入口，函数直接返回错误）。
- **为什么不用逐行 pread**：272 万行会产生 272 万次系统调用；按块读把系统调用降到「文件大小 / 块长」量级。
- **为什么不用整份读**：正文可能上百 MB，与「不复制正文」的目标冲突；缓冲复用保持峰值平坦。
- **为什么不用 mmap**：与装载后文件轮转、删除、替换、临时文件原子替换的既有策略冲突；常驻只读句柄已提供「装载内容」一致性。
- **为什么不在解析期求值**：过滤表达式在装载后才给出，且可反复变更；解析期求值会强制「换条件 = 重解析」。

### D5：大小写等价按「needle 预折叠 + 行两条路径」

- 编译期把条件值折叠：区分大小写存原始字节；忽略大小写按 Unicode 小写化后存字节。
- 忽略大小写时逐行：以**折叠后的 needle** 判断——若其为纯 ASCII，正文切片纯 ASCII 时走字节级忽略大小写子串，正文含非 ASCII 时按 UTF-8 lossy 解码、Unicode 小写化、重新编码后做字节子串；若折叠后的 needle 含非 ASCII，则纯 ASCII 正文直接不命中，非 ASCII 正文走 Unicode 路径。注意判断依据不是原文：U+212A（开尔文符号）折叠为 ASCII `k`，仍应命中纯 ASCII 正文。
- **为什么**：这是规格要求的可见语义（`ß`/`ẞ` 等价、`ı`≠`i`、重音不等价），也是现有 UI 行为；把子串编译成正则会让 `ß`/`İ` 等语义漂移，并丢掉纯 ASCII 快路径。
- **非法 UTF-8**：lossy 替换字符参与比较，与 C# `GetString` 的替换行为对齐；测试钉死。

### D6：条件归一与空值

- `levels` 为空或恰为 `{1..7}` → 级别条件未启用（**级别 0 的行因此不会被「全选级别」误杀**）。
- `minutes` 为空 → 分钟条件未启用；`pids.values` / `tids.values` / `tags.values` / `msg.values` 为空 → 对应类别未启用；六类全未启用 → 恒等视图。未启用只按取值集合为空（级别另含全 1–7）判定；正文条件即使因空词而恒真，也不归一为未启用。
- 正文空词沿用既有语义：`or` 立即成立、`and` 跳过；全为空词的 `and` 等价于空真，全部记录命中（与 C# `MatchAny` 一致）。因此 `msg.values = [""]` + `exclude = true` 的结果为空集；若正文条件是唯一有效限制且 `exclude = false`，可用恒等视图表示全行结果，与其他有效条件共存时仍按类别相交。空正文按空切片参与：非空词不命中、空词命中。tag 值不得为空：`values` 为空数组视为该类别未启用；数组内含空串时解析期拒绝（`INVALID_ARG` + `tags.values`），避免「子串空值命中任意有 tag 行」这类隐式语义。
- **为什么**：与 C# 的 `IsEmpty()` 归一与用户可见行为一致；「全选级别」必须与未启用等价，否则未知级别行会凭空消失。

### D7：模块形态——纯函数 + 可测的块长

```text
filter::FilterExpr                  // serde 模型 + 校验
filter::CompiledFilter              // 归一后的判定器（类别开关、集合、折叠后的值）
filter::scan(&Payload, &File, display: &str, &CompiledFilter) -> CoreResult<ResultSet>
filter::scan_with_chunk(&Payload, &File, display: &str, &CompiledFilter, chunk: usize)  // 仅测试用，默认走 256 KiB
```

- `scan` 只借用 `Payload` 与 `File`，不持有句柄、不加锁；调用方（将来的查询入口）负责取快照与状态门禁。
- 复用现有错误：把 `source.rs` 的私用 `io_error` 上移到 `error.rs`（`pub(crate)`），筛选路径与分页路径共用同一错误形状。
- 不建 `Op::Filter`、不动 `state.rs`：本刀没有句柄入口，状态矩阵没有可执行的语义；提前加会在下一刀被查询状态机替换。
- **为什么**：与 1a「先立纯结构、再接入口」同一模式；`scan` 是下一刀查询句柄唯一需要包的一层。

### D8：tag 条件在字典层求值，行判定只做 id 归属

- 对 tag 字典一次性求出「命中的 tag id 集合」：子串匹配逐字典项做包含判断；整串匹配逐字典项做相等；忽略大小写时，子串与整串都对字典项与条件值先做 Unicode 小写化折叠。`and` 与 `or` 只在字典项上组合；`and` + 整串的多值天然只命中「同时等于多个值」——字典项折叠后至多等于一个值，故折叠后互不相同的值时集合为空。
- 行判定：`tag_ids[row]` 属于命中集合即命中；`-1`（无 tag）永不属于，启用时不命中、排除时保留。
- **为什么**：tag 字典远小于行数，字符串判断从「每行」降到「每字典项」；同时把规格里的「整串 + 全部命中 = 空集」自然表达出来，无需特判。

### D9：结构化条件用定宽小集合

- 级别：`bool[8]`；分钟：`bool[60]`（索引前先判 255 = 无时间戳，哨兵不进入表）；pid/tid：`HashSet<i32>`。逐行只看一次数组/集合查询。
- **为什么**：级别与分钟取值域固定且小，位图/布尔数组比哈希更便宜；pid/tid 取值稀疏，哈希合适。避免每行线性扫条件列表。

### D10：测试形态

- 固定样本以源码内字面量构造（`testdata/` 只放少量手工小 log），期望是**手写字面量行号**，禁止用当前实现输出回填。
- 分块边界用 `scan_with_chunk(..., 小值)` 钉死：正文跨块、候选稀疏（块间跳读）、单行大于块长、末行/小文件大块长四种；同一条件用默认块长与极小测试块长各跑一次，结果集必须全等。末行/小文件用例防止把 `doc_end` 之外的 EOF 短读误判为错误。
- 正文空值：空正文+空词命中、空正文+非空词不命中、空词/全空词 + 排除为空集；另用 U+212A 样本钉死非 ASCII 原文折叠成 ASCII 的快路径。
- 结果集与分页的组合用现有 `page_rows_for` + `read_exact_at` 验证：锚点定位用 `resolve` 求 `ord` 再取页，顺序翻页用「第一条 `docRow ≥ start`」求 `ord` 再取页；两种意图分别用测试钉死，不能互相替代。过滤结果按真实行起页、页内文本与源一致。
- 错误路径用临时文件截断/改名验证；文件轮转、删除、替换在 Windows 句柄语义下仍读装载内容。

## Risks / Trade-offs

- **`Rows` 仍无生产消费者（直到查询/FFI 刀）** → 保持函数级 API 与固定期望测试，下一刀只包接口；`scan` 不掺查询生命周期。
- **单线程扫描比 C# 当前并行块扫慢** → 本刀不以性能为验收；索引/分块结构已够用，性能基线等接入 UI 后再测，届时另立优化刀（可并行化候选区间，不改契约）。
- **非 UTF-8 行的 lossy 小写化与 C# 有极小差异风险** → 用 `ß/ẞ`、`ı`、非法字节样本钉死；错误直接落在规格场景上。
- **`Rows` 内存 4 B/命中行（272 万行全命中约 11 MB）** → 可接受；结果集需要可寻址，位图虽然更省但无法直接支撑 `resolve`/分页，且与 1a 定案不一致。
- **JSON schema 提前定型** → 用规格场景 + 固定 JSON 用例锁住；本代拒绝未列字段，后续加字段属于显式契约变更（可被评审看见）。

## Migration Plan

无持久化格式、无 FFI、无 C# 行为变化：回滚即回退本次提交；`Payload`、LPAG、`.lcs`/`.lcm` 均不迁移。

## Open Questions

- 并行扫描与性能目标：等真实界面接入后有基线再定，不改变本 change 的契约与任务。
- 时间区间条件与标记条件的字段形态：在各自刀次（MCP 需求明确时）定义，本代显式拒绝未知字段。
- 取值边界按严格拒绝落地：级别 0、负进程号/线程号、空 tag 值均不可作为过滤值；若后续 MCP 需要选择「未知元数据」的行，另开 change 定义显式字段，不复用哨兵值。