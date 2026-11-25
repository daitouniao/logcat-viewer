# Design

## Context

行为契约见 `specs/logcore/filter/spec.md`（本 change 的 delta），动机与范围见 `proposal.md`。本文只记「怎么实现」与取舍理由。

实测依据来自探索笔记 `openspec/notes/RUST-CORE-REGEX-DIALECT.md`（P1–P5，探针工程为 git-ignored 临时 crate，`regex` 解析到 1.13.2 / `regex-syntax` 0.8.12，工具链 1.99.1，Release + LTO + strip）。关键数值在本文相关决策处自包含复述，不依赖笔记存活。

当前实现状态（`crates/core/src/filter.rs`）：

- `MsgFilter { values, mode, op, case, exclude }` 中 `mode` 已存在但 `validate()` 只放行 `"substring"`（`:159`），`"regex"` 走 `invalid_arg("msg.mode")`。
- `CompiledMsg { needles: Vec<Vec<u8>>, op, case, exclude, always_true }` 是纯字节包含模型；`compile()` 在 `msg.values` 非空时构造它（`:261`）。
- `scan_inner` 的正文判定处已给出「每行取正文一次」的接缝（`:499`）：`body_range` → `reader.read` → `msg.matches(body)`；`len == 0` 时直接以空切片求值，不回读。
- 非法 UTF-8 归一原语已在 `contains_folded`（`String::from_utf8_lossy(body).to_lowercase()`，`:420`）内，且 `filter_msg_lossy_invalid_utf8` 已钉住脏行可搜的口径。
- `core` 现有依赖只有 `arc-swap` / `serde` / `serde_json`；`Cargo.lock` 27 包。
- 错误码表在 `crates/core/src/error.rs`，0–7 为状态类、10–16 为失败类，段间留空；**既有测试断言 `code_name(17) == "UNKNOWN"`**，新增 17 时该断言必须同步改。
- FFI 侧过滤条件已是 JSON 字符串通道（`logcore_open_query`），正则档不需要新导出、不需要改 C ABI。

## Goals / Non-Goals

**Goals:**

- 正则档与子串档共用同一条扫描循环、同一份正文取法、同一套 `op`/`exclude` 结论，只在「该行是否命中」这一步分叉。
- 每行的 UTF-8 归一化只做一次，被该行全部 pattern 复用。
- 全部拒绝面在 `compile()`（即开启查询）时同步完成，扫描期不再产生错误。
- 引入依赖带来的产物体积与每行成本可量化、可复核。

**Non-Goals（设计层面，范围类非目标见 proposal）:**

- 不改 `scan_inner` 的分块读取、`ChunkReader`、`ResultSet` 形态与分页协作。
- 不引入匹配超时、后台线程、取消位——线性时间界 + 编译规模上限承担防拖死职责（沿用 2026-11-15 拍板）。
- 不做跨查询的 pattern 编译缓存（同一句柄内重复开启查询会重编，属可接受成本，见 D9 编译耗时实测）。
- 不动 `tags.match` 枚举（仍 `substring | exact`）与时间区间/标记的拒绝面。

## Decisions

### D1 依赖：`regex` 取默认 feature，同时显式声明 `regex-syntax`

```toml
regex = "1.11"          # 实际解析到 1.13.2，与 Cargo.lock 一致
regex-syntax = "0.8"    # 字面量短路判据需要直接 use，传递依赖不可直接 import
```

- 为什么不 `default-features = false` 裁剪 Unicode：实测（同一探针 LTO+strip，std 基线 112 KiB）`perf` 单独 = 1154 KiB、`+unicode-case` = 1243.5 KiB、`+unicode-case+unicode-perl` = 1294 KiB、全开 = 1529 KiB。地板在引擎本体（≈ +1.0 MiB），裁剪最多省 235 KiB。
- 裁剪的失效形态是**编译失败**而非静默少结果：关 `unicode-case` 后 `(?i)TIMEOUT` 直接编译失败，关 `unicode-perl` 后 `\w+` 对中文正文编译失败。官方保证裁剪只增减合法 pattern 集合、不改匹配语义。
- 因此默认全开（甲）：`perf-literal`（aho-corasick + memchr 字面量预筛）本就不可裁，省下的字节换不回方言能力。`Cargo.lock` 27 → 31 包（新增 `aho-corasick` / `memchr` / `regex-automata` / `regex-syntax`）。
- 备选（乙：`perf` + `unicode-case` + `unicode-perl`）保留为可回退开关，代价是 `\p{...}` 变成"方言里没有这个写法"。**列为评审要点，未拍。**
- `regex-syntax` 必须显式声明：D5 的短路判据要 `use regex_syntax::...`，而它作为 `regex` 的传递依赖在 Cargo 里不可直接 import（不声明则编译期报"找不到 crate"）。它不是新增依赖树条目，只是把既有版本提到直接依赖。

### D2 归一化接缝：在正则分支内做一次 lossy，不新增第二套正文取法

正则分支把 `matches(&[u8])` 换成对 `Cow<str>` 求值：

```rust
// CompiledMsg 的正则档
let text = String::from_utf8_lossy(body);          // 合法 UTF-8 ⇒ Borrowed，零分配
set.matches(&*text).matched_any()                  // 或 matched(i) 逐位取
```

- 不用 `bytes::Regex` 直喂 `&[u8]`：实测 `bytes::Regex` 的 `unicode` 默认为**开**，非法字节不是合法码点——对 `[x, 0xFF, y]`，`x.y` 不命中、`(?s).*` 只吃 1 字节、`\x{FFFD}` 与字面 U+FFFD 均不命中。结果是"屏幕上看得见、正则搜不着"，直接违反 `filter_msg_lossy_invalid_utf8` 已钉口径。
- 不另设 ASCII 快速通道：实测 80 B 真人行长 × 200k 行，仅 lossy = 97 ns/行、仅 `is_ascii()` = 33 ns/行；lossy 一次低于子串档 CI 路径的双侧 `to_lowercase`（243 ns/行）。加门反而多一次遍历。
- 「先 `from_utf8` 试探、失败才 lossy」不是第三方案——那就是 `from_utf8_lossy` 的内部行为，手写属重复实现。
- 接缝位置：`scan_inner:499` 已有的 `msg.matches(body)` 调用点保持不变，分叉在 `CompiledMsg` 内部（子串档保留 `needles`，正则档保留编译好的 matcher）。`len == 0` 的快速路径同样保留——正则档对空切片求值即空文本求值，正是契约要求的语义。

### D3 编译时机：全部放在 `compile()`，扫描期零错误

`FilterExpr::compile()` 内完成长度/条数校验 → 逐条 parse + Hir 短路判定 → 构造 matcher；任一失败即返回 `REGEX_INVALID`。`scan_inner` 只消费已编译结果，不新增错误分支。

理由：开启查询是同步返回点（`query::open`），把拒绝收在此处即满足"不产出部分结果集、不改变源句柄状态"；若推迟到扫描期，就得为运行期失败设计第二套上报通道，与既有 `IO_ERROR` 通道混读。

### D4 多 pattern 执行档：`RegexSet` 单遍 + 只用存在性

- `op` 的 `and`/`or` 都能由一次 `RegexSet::matches` 的 bitset 得出，因此正则档不存在子串档的「每词一轮全量扫描」缺口。
- 硬约束：**只允许存在性判定**。`is_match`/`find` 是 `O(m·n)`，`find_iter`/`captures_iter` 最坏 `O(m·n²)`（官方明说无法规避）。本档禁迭代与捕获 API——这与"不做抽取、不回传命中位置"是同一条边界（spec「正则模式参与正文过滤」）。
- 备选「合并 alternation」（`(?i)(a|b|c)`）实测每行 193 ns，低于 `RegexSet` 4 条的 503 ns/行（单条 `Regex` 为 158 ns）；但合并档只给整体命中，`and` 需要逐条位信息，故只能在纯 `or` 下退化为一条 `Regex` 划算。**实现选择留 apply 内定**：`op = or` 可用合并档或逐条短路，`op = and` 用 `RegexSet`，行为不变（结果集必须逐字节相同，由测试钉住）。
- 每行固定开销折算：`RegexSet` 4 条 = 503 ns/行 → 272 万行 ≈ 1.4 s；条数上限取 16（D7）后最坏约 5 s 量级，属同步返回、非 UI 线程可接受范围。

### D5 纯字面量短路

pattern 解析后的 `Hir` 若整棵为 `Concat(Literal*)`，该条不走正则引擎，直接复用子串档的 `needle_matches`（字节包含）。

- 判据实测自洽，**不需要额外的"是否纯 ASCII"限定**：`(?i)` 一旦出现，Hir 就变成 `Concat([Class({'T'..='T','t'..='t'}), …])`，不再是纯字面量。因此短路只在**区分大小写**（`case: true`）时可能触发，而区分大小写的字面量匹配两档逐位同形（都是字节包含），不存在折叠表分歧——这正是 spec「字面量型正则与子串档同结论」的实现依据。
- 实测形态：`timeout` → `Literal("timeout")`（短路）；`tim\.eout` → `Literal("tim.eout")`（短路）；`^abc$` → `Concat([Look, Literal, Look])`（**不短路，正确**：锚点语义与子串包含不等价）；`a|b` → `Class`（不短路）；`tim.*eout`、`\d+`（不短路）。
- 短路在**整条 `msg` 条件级**判定：全部取值都是纯字面量才整条短路；任一条是真 pattern 则整条走正则路径。混合档内不逐条分叉，避免同一行内两套正文取法交错。
- 收益实测：80 B 行 × 200k 行，单 pattern `is_match` = 158 ns/行、子串档区分大小写 = 110 ns/行（差 48 ns/行，272 万行 ≈ 130 ms）。用户写的"正则"里很大比例是字面量（含转义标点）。

### D6 错误码 17 与参数形状

```rust
pub const REGEX_INVALID: i32 = 17;              // param_keys = ["reason"]
// reason: "syntax" | "too_long" | "too_many" | "too_size"
// index:  能归因到具体某条时附带（从 0 起的序号，可选键）
```

- 单一码 + `reason` 分因，符合 D15/B1「params 只放语义键与数值、无文案」；不给错误码表加三条宿主几乎无法区分的条目。
- 与探索笔记的三值 `reason` 相比**增加 `too_many`**：条数超限是可独立归因的用户可修问题，与"编译规模过大"在排障动作上不同（前者删条目、后者改写 pattern）。
- 必须同步改：`error.rs` 的 `code_name`、`param_keys`，以及既有测试 `error_code_table_values_are_stable` 中 `assert_eq!(code_name(17), "UNKNOWN")`（否则全量必红）。
- C# 镜像 `LogcoreErrorCodes` 补 `RegexInvalid = 17` 与 `Name(17) => "REGEX_INVALID"`，并在 `BindingErrorCodeMappingTests.Code_names_match_d15_table` 扩一条断言；不新增 FFI IT（filter JSON 对宿主是透传字符串，正则行为已在 core 层钉死）。

### D7 常量取值

| 常量 | 取值 | 依据 |
| --- | --- | --- |
| 单条 pattern 字符上限 | 2000 | 按 `chars()` 计（非字节）。编译侧不构成风险：2000 分支 pattern 编译 6.9 ms、10k 字符字面量 3.3 ms；上限主要封「条数 × 每条尺寸」的乘积面 |
| 正则条数上限 | 16 | 条数直接乘每行成本（`RegexSet` 4 条 = 503 ns/行，2000 条 = 753 µs/100 KB）；旧线 `BLOCK_SCAN_MAX_KEYS = 8` 是兜底不是契约，取区间上沿 |
| `size_limit` | 1 MiB（1_048_576） | 默认 10 MiB **挡不住**关键形态：`(\w+\s?){200}` 在默认下编译通过、对 2 KB 正文单行耗时 7.0 ms（其余同类都在 µs 级）→ 272 万行即小时级。1 MiB 拒它，同时保留 `timeout`、`(?i)(a\|…\|j)+`、`(a\|b\|c){50}`、2000 字符字面量、`(x\|y)×500`、`(a+)+b`、`([a-z]+)*$`、`(x\|x)+y`、10 层嵌套 group |
| `nest_limit` | 沿用默认 1000 | 200 层嵌套 group 编译 0.6 ms、匹配 14.6 µs，不构成威胁；收紧到 16 会把 `(a\|b){100}{100}` 这类"合法但无意义"写法一并挡掉，收益已由 `size_limit` 承担 |
| 匹配超时 | 不设 | 线性时间界 + `size_limit` 承担防拖死（2026-11-15 拍板），本档不复活超时 |

条数与长度上限进 spec（调用方需知道），`size_limit` 数值只留本文（引擎实现口径）。

### D8 校验与编译次序（决定 `reason` 归因）

1. `mode` 取值合法性（既有 `INVALID_ARG`）；
2. 条数 > 16 → `too_many`；任一条字符数 > 2000 → `too_long`（廉价、先于编译）；
3. 逐条 `regex_syntax::parse` → 失败即 `syntax`；
4. 短路判定（纯字面量 ⇒ 该条不进引擎）；
5. 需要进引擎的条目以 `size_limit = 1 MiB` 构造 → 失败即 `too_size`。

同一条 pattern 只会给一个 `reason`（按此序首次命中者），`index` 取该条序号。

### D9 正则方言边界（写进 spec 的能力集，本文留对照）

pattern 只被 Rust core 编译执行一次，C#（UI）与 Node（MCP）只承载文本、只接收命中/未命中——方言只有一套，"三种语言写法是否相同"是伪问题。真正咬人的是四层：语法能力集、Unicode 默认、字符串转义承载、大小写折叠表。

| 写法 | 本档（Rust regex 1.13） | .NET Regex | Node.js RegExp |
| --- | --- | --- | --- |
| `(?=x)` `(?!x)` `(?<=x)` `(?<!x)` 环视 | **不支持** | 支持 | 支持 |
| `\1` 反向引用 | **不支持** | 支持 | 支持 |
| `(?>x)` 原子组 / `x*+` 独占量词 | **不支持** | 支持 / 不支持 | 不支持 / ES2024 起 |
| `(?i)` 内联 flag，可中途切换 | 支持 | 支持 | 不支持（仅 `/x/i` 后置） |
| `(?x)` verbose、`(?R)` CRLF | 支持 | 选项形式 / 不支持 | 不支持 |
| `[a-z--b]` 类集合运算 `&& -- ~~` | 支持 | 不支持 | 仅 `v` flag 且写法不同 |
| `\w \d \s \b` | Unicode | Unicode | **ASCII** |
| `\p{...}` 属性类 | 支持 | 支持 | 需 `u`/`v` flag |

- 缺项（环视、反向引用）恰是"排除含 X 的行"这类高频写法，LLM 生成 pattern 最常踩 → spec 明确列为拒绝并给 `syntax` 因。
- 字符串承载：MCP 侧 JSON 双反斜杠、C# 字面量、JS 字面量正则需剥斜杠——属宿主侧问题，core 只接受最终字符串，MUST NOT 由 core 猜宿主转义。宿主侧不做实时校验；若将来 UI 要输入框提示，须另立无句柄的 `validate_pattern` 类导出才能同引擎判定（本档不开）。
- 承诺形式：spec 写能力集 + 不支持清单，版本锁定由 `Cargo.lock` 承担，本文留全量对照表。regex 升级时须重跑对照表（探索笔记已冻结，不再更新，新探索另生成笔记）。

### D10 大小写折叠表：两档唯一差异点，记录不修

正则档 `(?i)` 用 UTS#18 **simple** case folding，子串档用 `char::to_lowercase` **全**折叠。逐对实测（13 组 needle/hay）后唯一分歧是 **U+0130（`İ`）**一类：全折叠会多出组合字符 U+0307，使子串档下 `i` 命中 `İ`、正则档不命中。`ß`/`ss`、`ẞ`/`ß`、`ſs`/`ß`、`k`/U+212A（KELVIN）、`ı`/`I`、`İstanbul`/`istanbul` 两档一致。

- 处理：**只在本文与探索笔记留事实，spec 不写该差异**（该差异只在同一查询混用两档时可见，且纯 ASCII 正文不受影响——spec 用「MUST NOT 出现在纯 ASCII 正文上」封住可观察面）。备选是在 spec 加一句折叠表出处，或把子串档改成 simple folding（会动已归档规范与现有测试，代价最大）。**列为评审要点，未拍。**
- 由此 D5 的短路只在区分大小写触发，天然避开折叠分歧。

### D11 与既有测试/文档的连带

- 改写 `filter_expr_rejects_regex_mode`：`"regex"` 不再被拒，改为断言被接受；`filter_expr_rejects_unknown_mode`（`"word"`）保留。
- `docs/TESTING.md` §6 的 Rust 数字（基线 Rust UT 425：core 392 + cabi 33；C# 923）与 README 徽章按联动同批更新。
- 探索笔记 `RUST-CORE-REGEX-DIALECT.md` 随本 change 归档同批移入 `openspec/notes/archive/`（归档时执行，非本 change 编码内容）。

### D12 查询开启面连带（查漏所得）

`logcore/query` 的「查询开启与结果集」把"过滤表达式非法"一律钉为**参数错误**。正则非法改走 `REGEX_INVALID`（17）后该句不再自洽，故本 change 同时 MODIFIED 该条：改为"按过滤契约的对应错误口径拒绝"，并补「非法正则不产生查询」场景。`openspec/specs/old/message-filter` 未出现正则口径，无需连带；`tags.match` 与时间区间/标记的拒绝面保持不变。

### D13 delta 形态：原「无效条件被拒绝」走 REMOVED + ADDED，而非 MODIFIED

openspec 对 MODIFIED 有防丢场景门禁——MODIFIED 整块替换需求，若少了主规范已有的场景即判 ERROR，且 REMOVED 与 ADDED 同名单也会判冲突。本 change 需要删掉该条里的「正则模式被拒」场景，因此整条 REMOVED（附 Reason/Migration）并以「参数类无效条件被拒绝」重建，其余 8 个场景逐条原样保留。归档后主规范只有新名单的条目，无重复内容。

## Risks / Trade-offs

- **[体积] `logcore.dll` 上浮约 1.0–1.3 MiB（现有 debug dll 1.72 MiB → 约 1.6 倍）** → 探针口径是独立 exe，apply 阶段带真实依赖重编后复核增量；若增量超预期，回退路径是 D1 备选乙（省 235 KiB，代价 `\p{...}` 变非法），而非关闭 `perf`。
- **[耗时] `RegexSet` 每行常数约为单条 `Regex` 的 3 倍，16 条上限下全文档可至秒级** → 同步返回、非 UI 线程；条数上限与每行成本一并由 D7 承担；`op = or` 允许退回合并档/逐条短路（行为不变，测试钉等价）。
- **[方言] LLM 生成的 pattern 可能带环视/反向引用，直接得到 `syntax` 拒绝** → 这是有意选择（拒绝优于静默降级）；宿主侧把"该方言不支持环视与反向引用"写进 MCP 工具说明，属于 MCP change 的文案工作，不在本 change。
- **[一致性] 两档折叠表在 U+0130 上分歧，混用可能出现不同行集** → D10 记录并限定作用域；不改动子串档（改动会破坏已归档规范与既有测试）。
- **[不可信输入] pattern 来自 MCP，dll 无进程隔离** → 线性时间界 + 长度/条数/`size_limit` 三重上限；实测唯一确认的失控形态 `(\w+\s?){200}` 在 1 MiB 下被拒。
- **[回归] 新增依赖触碰 `Cargo.lock` 与全量构建** → 全量 Rust UT 必须按串行口径复跑（基线 425/425），并声明口径数字；C# 侧仅需 `dotnet test` 全量无新增失败（基线 923）。

## Migration Plan

无数据迁移、无格式版本变更：正则槽位 `msg.mode` 早已在契约与结构中冻结，本 change 只是放行取值并补齐语义。回滚 = 恢复 `validate()` 对 `"regex"` 的拒绝（回到当前口径），已发布索引与缓存不受影响；依赖与 `Cargo.lock` 回退随 revert 一并处理。宿主侧无需改动即可继续只用子串档。

## Open Questions

- 真实 `logcore.dll` 在带依赖重编后的体积增量（apply 首步实测，不改变方案，只决定是否需要讨论 D1 备选乙）。
- `op = or` 的最终实现档（合并 alternation / 逐条短路 / `RegexSet`）——三档行为已由 spec 钉等价，属实现内择一。
- 采集源何时开放正则：本档只覆盖装载完成的文件源，采集源仍无统一快照读面（沿用既有缺口）。
- 命中位置/命中词回传是否需要：需另立接口，与"不做抽取"边界相邻，本档不预留字段。
