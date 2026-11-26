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
- 不做**跨源句柄**的 pattern 编译缓存（缓存只在单个源句柄内生效，口径见 D3a）；不引入进程级全局缓存表。
- 不动 `tags.match` 枚举（仍 `substring | exact`）与时间区间/标记的拒绝面。

## Decisions

### D1 依赖：`regex` 取默认 feature，同时显式声明 `regex-syntax`

```toml
regex = "1.13"          # 方言承诺与上限实测均按 1.13.2 校准，下限不得退到 1.11/1.12
regex-syntax = "0.8"    # 字面量短路判据需要直接 use，传递依赖不可直接 import
```

- 为什么不 `default-features = false` 裁剪 Unicode：实测（同一探针 LTO+strip，std 基线 112 KiB）`perf` 单独 = 1154 KiB、`+unicode-case` = 1243.5 KiB、`+unicode-case+unicode-perl` = 1294 KiB、全开 = 1529 KiB。地板在引擎本体（≈ +1.0 MiB），裁剪最多省 235 KiB。
- 裁剪的失效形态是**编译失败**而非静默少结果：关 `unicode-case` 后 `(?i)TIMEOUT` 直接编译失败，关 `unicode-perl` 后 `\w+` 对中文正文编译失败。官方保证裁剪只增减合法 pattern 集合、不改匹配语义。
- 因此默认全开（**已定，2026-11-25 评审确认**）：`perf-literal`（aho-corasick + memchr 字面量预筛）本就不可裁，省下的字节换不回方言能力。裁剪档（`perf` + `unicode-case` + `unicode-perl`，实测 1294 KiB，省 235 KiB）只是**回退候选**，不是自动开关：任务 2.2 实测真实 dll 增量超过 1.3 MiB 时**停下报告、等重新拍板**，不得自行启用——裁剪会把 `\p{...}` 从"可用"变成"编译失败"，属用户可见能力面变更，须走评审并同步 spec 与 MCP 工具说明。`Cargo.lock` 27 → 31 包（新增 `aho-corasick` / `memchr` / `regex-automata` / `regex-syntax`）。
- `regex-syntax` 必须显式声明：D5 的短路判据要 `use regex_syntax::...`，而它作为 `regex` 的传递依赖在 Cargo 里不可直接 import（不声明则编译期报"找不到 crate"）。它不是新增依赖树条目，只是把既有版本提到直接依赖。
- 版本下限取 `1.13` 而非原先的 `1.11`：spec 的方言能力集、P3 的规模上限与 D7 保留清单都按 1.13.2 校准；下限提到实测次版本，避免 `Cargo.lock` 删除重装时解析到 1.11/1.12 而让"所锁定版本行为"的承诺落空。仍用 caret 允许后续 1.x 补丁升级，与仓库既有依赖风格一致。

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
- 接缝位置：`scan_inner:499` 已有的 `msg.matches(body)` 调用点保持不变，分叉在 `CompiledMsg` 内部（子串档保留 `needles`，正则档按 D5a 保留 `needles` + `set` + `routes`）。`len == 0` 的快速路径同样保留——正则档对空切片求值即空文本求值，正是契约要求的语义。

### D3 编译时机：全部放在 `compile()`，扫描期零错误

`FilterExpr::compile()` 内完成长度/条数校验 → 逐条 parse + Hir 短路判定 → 构造 matcher；任一失败即返回 `REGEX_INVALID`。`scan_inner` 只消费已编译结果，不新增错误分支。

理由：开启查询是同步返回点（`query::open`），把拒绝收在此处即满足"不产出部分结果集、不改变源句柄状态"；若推迟到扫描期，就得为运行期失败设计第二套上报通道，与既有 `IO_ERROR` 通道混读。

### D3a 编译产物缓存：每源 LRU，8 条 + 8 MiB 保守预算（已定）

MCP 会话会反复用同一组 pattern 开启查询，故允许缓存正则编译产物（评审确认）。口径：

1. **归属 = 每个源句柄**，挂在 `SourceHandle` 既有的 `inner: Arc<Mutex<Inner>>` 上。core 当前没有会话对象，进程级全局静态缓存会新引入跨线程与生命周期契约；`Inner` 已有串行化边界，复用即可，不新增同步原语。
2. **缓存对象 = 编译后的正则 matcher**，不缓存整个 `CompiledFilter`（后者本就 `Clone`，其余字段编译很便宜）。键为正文条件的 pattern 序列与 `case` 取值；`op` 与 `exclude` 不参与 matcher 编译，不进键。**键取入参原文序列，不取哈希**：`RegexSet` 不实现 `Hash`/`Eq`，也拿不到它自己的 pattern 列表，键只能建在入参侧；用哈希会把「同一组条件必然同一结果」这条可观察不变量降成概率正确，而最坏 32 KB 文本的一次比较相对全文档扫描是噪声级。
3. **淘汰 = LRU**，不用 LFU：LFU 会把历史热门条目长期锁死、新热门进不来，还需老化机制；会话内重复用同一组条件，LRU 已足够。
4. **容量 = 条数与预算双闸**：最多 8 条，且每条按其配置的 `size_limit` 预留额度、总额 8 MiB。预算是保守预留而非实测堆占用（`RegexSet` 不暴露产物尺寸），因此能给出确定的内存上界。条目放不进预算时**照常执行本次查询，只是不入缓存**——缓存是纯优化，不得改变行为或新增拒绝理由。
5. 任务 2.5 的实测（16 条典型 pattern 的编译耗时、产物内存、重复开启命中收益）用于校验第 4 条取值；若实测显示收益可忽略，按无返工原则砍掉缓存而不是缩小预算。

### D4 多 pattern 执行档：`RegexSet` 单遍 + 只用存在性

- `op` 的 `and`/`or` 都能由一次 `RegexSet::matches` 的 bitset 得出，因此正则档不存在子串档的「每词一轮全量扫描」缺口。
- 硬约束：**只允许存在性判定**。`is_match`/`find` 是 `O(m·n)`，`find_iter`/`captures_iter` 最坏 `O(m·n²)`（官方明说无法规避）。本档禁迭代与捕获 API——这与"不做抽取、不回传命中位置"是同一条边界（spec「正则模式参与正文过滤」）。
- 备选「合并 alternation」（`(?i)(a|b|c)`）**不采用（已定）**：实测每行 193 ns 虽低于 `RegexSet` 4 条的 503 ns/行，但合并档是把全部条目拼成**一条**正则去编译，受的是「单条 1 MiB」约束而非整组约束——会出现同一批 pattern 在 `op = or` 下因超限被拒、在 `op = and` 下却通过。拒绝面属可观察行为，不能由 `op` 左右，故正则档统一走 `RegexSet` 单路径，`and`/`or` 只差在取位方式。若日后实测证明 `RegexSet` 的每行常数不可接受，改动时必须连同超限口径一并重新拍定。
- 每行固定开销折算：`RegexSet` 4 条 = 503 ns/行 → 272 万行 ≈ 1.4 s（实测）。16 条的量级**属按条数线性外推的推算，未实测**，以任务 7.4 的数值为准；换算时注意 7.4 的离线兜底样本是 40 MiB（约 272 万行的 1/4.5），不要把两口径混读。若实测显著劣于推算，回到 D7 重议条数上限。

### D5 纯字面量短路

pattern 解析后的 `Hir` 若整棵为 `Concat(Literal*)`，该条不走正则引擎，直接复用子串档的 `needle_matches`（字节包含）。

- 判据实测自洽，**不需要额外的"是否纯 ASCII"限定**：`(?i)` 一旦出现，Hir 就变成 `Concat([Class({'T'..='T','t'..='t'}), …])`，不再是纯字面量。因此短路只在**区分大小写**（`case: true`）时可能触发，而区分大小写的字面量匹配两档逐位同形（都是字节包含），不存在折叠表分歧——这正是 spec「字面量型正则与子串档同结论」的实现依据。
- 实测形态：`timeout` → `Literal("timeout")`（短路）；`tim\.eout` → `Literal("tim.eout")`（短路）；`^abc$` → `Concat([Look, Literal, Look])`（**不短路，正确**：锚点语义与子串包含不等价）；`a|b` → `Class`（不短路）；`tim.*eout`、`\d+`（不短路）。
- 短路按**逐条**判定：每条 pattern 独立过 Hir 判据，纯字面量的条目复用 `needle_matches`，其余条目进 `RegexSet`（结构见 D5a）。混合批次不整条放弃短路——同一行只取一次正文，两类判定都在这份文本上做，不存在两套正文取法交错。
- 收益实测：80 B 行 × 200k 行，单 pattern `is_match` = 158 ns/行、子串档区分大小写 = 110 ns/行（差 48 ns/行，272 万行 ≈ 130 ms）。用户写的"正则"里很大比例是字面量（含转义标点）。

### D5a `CompiledMsg` 正则档形态：同结构并存 + 逐条路由

```rust
// 正则档新增字段，与子串档共存于同一 struct（不拆成两个类型）
needles: Vec<Vec<u8>>,   // 该批中判为纯字面量的条目正文
set: Option<RegexSet>,   // 只由非字面量条目构成；全为字面量时为 None
routes: Vec<Route>,      // Literal(usize) | Set(usize)，保持提交顺序
```

1. 一条 `msg` 条件的判定 = 按 `routes` 逐条求值后按 `op` 聚合；`exclude` 仍作用于整条结论，不随路由变化。
2. 字面量条目在**该行同一份 lossy 文本**上求值（对 `text.as_bytes()` 做字节包含），与正则条目共用同一次归一化——这正是 spec「同一行的归一化结论 MUST 被该行全部 pattern 复用」的实现约束。MUST NOT 为字面量条目另开一份原始字节正文取法，否则同一行内出现两种正文口径。
3. 全字面量批次**不构造** `RegexSet`（空集会被判为永不命中，把结果集误压成空），该形态与子串档同结果集由测试钉住。
4. 连带口径：字面量条目不进整组 `RegexSet`，因此不受 D15 第二道闸约束（D8 末句已写明「短路命中的条目不会因规模被拒」）；单条仍受条数 16 与 2000 字符限制，二者在路由之前完成校验。
5. 缓存（D3a）的键仍是**整条正文条件的原文序列 + `case`**，不因路由拆分而变；缓存对象是 `routes` + `set` 这一组编译产物。
6. 每行代价：`routes` 是一次线性遍历加逐条分支，相对 `RegexSet` 单遍判定为噪声级；收益是绝大多数含字面量的批次少编译、少占整组规模额度。

### D6 错误码 17 与参数形状

```rust
pub const REGEX_INVALID: i32 = 17;              // param_keys = ["reason"]
// reason: "syntax" | "too_long" | "too_many" | "too_size"
// index:  能归因到具体某条时附带（从 0 起的序号，可选键）
```

- 单一码 + `reason` 分因，符合 D15/B1「params 只放语义键与数值、无文案」；不给错误码表加三条宿主几乎无法区分的条目。`reason` 取值为 `syntax` / `too_long` / `too_many` / `too_size` 四值（**已定**，探索笔记原列三值，评审确认把条数超限单列：它比"改写 pattern"与"删条目"是两种不同的修复动作）。
- `index` 为**附注键**，不进 `param_keys`：照 `DIRECT_OPEN_ERROR_KEY` 的先例处理——`param_keys(17) = ["reason"]`，能归因到具体某条时另以 `CoreError::with("index", ...)` 附带。宿主按 `reason` 定文案、按 `index`（若存在）定位第几条，缺失时按整体拒绝处理。
- 必须同步改：`error.rs` 的 `code_name`、`param_keys`，以及既有测试 `error_code_table_values_are_stable` 中 `assert_eq!(code_name(17), "UNKNOWN")`（否则全量必红）。
- C# 镜像（**已定**）：`LogcoreErrorCodes` 补 `RegexInvalid = 17` 与 `Name(17) => "REGEX_INVALID"`，并在 `BindingErrorCodeMappingTests.Code_names_match_d15_table` 扩一条断言；**不新增经 FFI 的 IT**——filter JSON 对宿主是透传字符串，正则行为已在 core 层钉死，跨层重复断言不增加覆盖。

### D7 常量取值

| 常量 | 取值 | 依据 |
| --- | --- | --- |
| 单条 pattern 字符上限 | 2000（**已定**） | 按 `chars()` 计（非字节）。编译侧不构成风险：2000 分支 pattern 编译 6.9 ms、10k 字符字面量 3.3 ms；上限主要封「条数 × 每条尺寸」的乘积面 |
| 正则条数上限 | 16（**已定**） | 条数直接乘每行成本（`RegexSet` 4 条 = 503 ns/行，2000 条 = 753 µs/100 KB）；旧线 `BLOCK_SCAN_MAX_KEYS = 8` 是兜底不是契约，取区间上沿 |
| `size_limit` | 1 MiB（1_048_576） | 默认 10 MiB **挡不住**关键形态：`(\w+\s?){200}` 在默认下编译通过、对 2 KB 正文单行耗时 7.0 ms（其余同类都在 µs 级）→ 272 万行即小时级。1 MiB 拒它，同时保留 `timeout`、`(?i)(a\|…\|j)+`、`(a\|b\|c){50}`、2000 字符字面量、`(x\|y)×500`、`(a+)+b`、`([a-z]+)*$`、`(x\|x)+y`、10 层嵌套 group |
| `nest_limit` | 沿用默认 1000 | 200 层嵌套 group 编译 0.6 ms、匹配 14.6 µs，不构成威胁；收紧到 16 会把 `(a\|b){100}{100}` 这类"合法但无意义"写法一并挡掉，收益已由 `size_limit` 承担 |
| `dfa_size_limit` / `dfa_state_limit` 等 | 沿用默认 | 这些默认值本就面向不可信输入；`RegexSet` 的执行策略由引擎选择，本档不替它选策略，也不在无实测依据时复制一遍默认。任务 2.3 记录 16 条满载的构造结果与峰值内存，作为默认值够用的证据 |
| 匹配超时 | 不设 | 线性时间界 + `size_limit` 承担防拖死（2026-11-15 拍板），本档不复活超时 |

条数与长度上限进 spec（调用方需知道），`size_limit` 数值只留本文（引擎实现口径）。

- **上限不导出、宿主侧也不镜像常量（已定）**：`cabi` 现在不导出任何 limit 常量，C# 侧只镜像错误码（`logcat-viewer/Services/Logcore/LogcoreContracts.cs` 的错误码段）。为一个常量开新 ABI 面要连带管 C ABI、版本与镜像测试，比抄数字贵得多；反过来在 C# 镜像一份 `16 / 2000` 也没有调用方（GUI 无正则入口），属为假想消费者加代码。数字的唯一权威源是 spec：Node 侧 MCP 工具文案由 `mcp-server` 卡抄 spec（素材见 D9a），本卡不新增导出、不新增 C# 常量。

- 上限只约束正则档（**已定**）：子串档的 `msg.values` 今天既无条数也无长度上限（`validate()` 只拦 `tags.values` 空串），且子串档是「每词一轮全量扫描」，敞口比正则档更大。本 change 不动它，避免把已归档的「条件模型与组合」一起改；缺口已记入 `openspec/notes/BACKLOG-MCP-AND-FORMATS.md` D4，与 MCP 侧调用约束一并处理。
- 规模上限是两道闸（口径见 D15）：逐条 `RegexBuilder::size_limit = 1 MiB` 预检 + 整组 `RegexSetBuilder::size_limit` 兜底。整组计数口径与兜底数值由任务 2.3 探针确定，不套用逐条数值。

### D8 校验与编译次序（决定 `reason` 归因）

1. `mode` 取值合法性（既有 `INVALID_ARG`）；
2. 条数 > 16 → `too_many`；任一条字符数 > 2000 → `too_long`（廉价、先于编译）；
3. 逐条 `regex_syntax::parse` → 失败即 `syntax`；
4. 短路判定（纯字面量 ⇒ 该条不进引擎）；
5. 逐条以 `RegexBuilder::size_limit = 1 MiB` 预检 → 失败即 `too_size` 并带该条 `index`；再以整组上限构造 `RegexSet` → 失败即 `too_size` 且**不带** `index`（见 D15）。

同一条 pattern 只会给一个 `reason`（按此序首次命中者），`index` 取该条序号；只有整组规模超限这一道闸无法归因到单条，此时 `reason` 仍为 `too_size` 但不带 `index`。短路命中的纯字面量条目不进第 5 步，因此也不会因规模被拒。

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
- 内联 `(?u)` / `(?-u)`：**允许，按引擎既有语义处理（已定）**。拒绝一个语法合法且语义明确的写法需要理由，本档没有；且 `(?-u)` 的 ASCII 语义是 crate 文档明写的（`(?-u:\w)` 等取 ASCII 定义），它会直接改变「`\w` 命中中文正文」这条已钉行为，所以必须被测试钉住而不是留给升级后静默变味。两处不在本档承诺：`(?-u:\W)` 一类取非补集时会延伸到非 ASCII 字节（crate 文档同段说明），本档 haystack 是 lossy 后的 `&str`，其边界行为由任务 2.3 探针记录、不进 spec；属性类与集合运算在 `(?-u)` 下是"可用 / 被拒"哪种，同样待 2.3 实测后才决定是否为它单列场景。
- 字符串承载：MCP 侧 JSON 双反斜杠、C# 字面量、JS 字面量正则需剥斜杠——属宿主侧问题，core 只接受最终字符串，MUST NOT 由 core 猜宿主转义。宿主侧不做实时校验；若将来 UI 要输入框提示，须另立无句柄的 `validate_pattern` 类导出才能同引擎判定（本档不开）。
- 承诺形式：spec 写能力集 + 不支持清单，版本锁定由 `Cargo.lock` 承担，本文留全量对照表。regex 升级时须重跑对照表（探索笔记已冻结，不再更新，新探索另生成笔记）。

**D9a MCP 工具说明的可粘贴文案**（评审确认由本卡产出，`mcp-server` 卡直接取用，不再重新考古）：core 只回错误码，不会向 AI 解释"为什么被拒"，故工具说明必须前置这段规则，否则 AI 会反复生成被拒的 pattern。

```text
Pattern syntax: Rust `regex` crate (RE2-style). No lookaround, no backreferences,
no atomic groups or possessive quantifiers. Supported: inline flags (?i)(?x)(?R),
Unicode \w \d \s \b, \p{...} property classes, character-class set operations
(&& -- ~~). Case-insensitive by default; `case: true` makes it exact, and an inline
(?i)/(?-i) inside the pattern still applies. Limits: at most 16 patterns per message
condition, at most 2000 characters each. Invalid or over-budget patterns are rejected
with REGEX_INVALID and a reason (syntax / too_long / too_many / too_size) — they are
never silently downgraded to substring matching. `(?-u)` is accepted and switches the
Perl classes to ASCII-only semantics, so `\w` stops matching CJK text — leave Unicode
on unless you mean it.
```

### D10 大小写折叠表：两档唯一差异点，记录不修

正则档 `(?i)` 用 UTS#18 **simple** case folding，子串档用 `char::to_lowercase` **全**折叠。逐对实测（13 组 needle/hay）后唯一分歧是 **U+0130（`İ`）**一类：全折叠会多出组合字符 U+0307，使子串档下 `i` 命中 `İ`、正则档不命中。`ß`/`ss`、`ẞ`/`ß`、`ſs`/`ß`、`k`/U+212A（KELVIN）、`ı`/`I`、`İstanbul`/`istanbul` 两档一致。

- 处理（**已定**）：**只在本文与探索笔记留事实，spec 不写该差异**——该差异只在同一查询混用两档时可见，且纯 ASCII 正文不受影响，spec 已用「MUST NOT 出现在纯 ASCII 正文上」封住可观察面。曾考虑的两个替代（spec 加折叠表出处 / 子串档改 simple folding）均不采纳，后者会动已归档规范与既有测试。
- 由此 D5 的短路只在区分大小写触发，天然避开折叠分歧。

### D11 与既有测试/文档的连带

- 改写 `filter_expr_rejects_regex_mode`：`"regex"` 不再被拒，改为断言被接受；`filter_expr_rejects_unknown_mode`（`"word"`）保留。
- **测试落点（已定）**：本卡新增用例约 40 条，不写进 `filter.rs`（已 1821 行、88 个 `#[test]`），改按 `source.rs` 末尾 `#[path = "source_tests.rs"]` 的既有先例拆出 `filter_regex_tests.rs`；实现代码仍留 `filter.rs`——拆实现为目录模块要把一批 `pub(crate)` 可见性重排，属纯搬家、白扩 diff 面。
- **验收面（已定）**：GUI 无正则入口、`crates/cli` 只有 90 行调试壳，本卡不做 GUI 手测，验收材料 = 全量测试口径数字 + 四项实测数值（见 proposal 验收依据）。
- **耗时实测不作测试用例（已定）**：任务 7.4 的三档耗时是一次性测量，数值只进 tasks 备注与 D4，不写成 `#[test]`、不引入 `#[ignore]`（仓库现无该先例，而 40 MiB 样本测试已在默认串行测试集内，再加性能用例会直接拉长 CI）。耗时随 `regex` 升级的漂移属「重跑方言对照表」时的检查项，靠人工复测承担，不做 CI 守卫。
- `docs/TESTING.md` §6 的 Rust 数字（基线 Rust UT 425：core 392 + cabi 33；C# 923）与 README 徽章按联动同批更新。
- 探索笔记 `RUST-CORE-REGEX-DIALECT.md` 随本 change 归档同批移入 `openspec/notes/archive/`（归档时执行，非本 change 编码内容）。

### D12 查询开启面连带（查漏所得）

`logcore/query` 的「查询开启与结果集」把"过滤表达式非法"一律钉为**参数错误**。正则非法改走 `REGEX_INVALID`（17）后该句不再自洽，故本 change 同时 MODIFIED 该条：改为"按过滤契约的对应错误口径拒绝"，并补「非法正则不产生查询」场景。`openspec/specs/old/message-filter` 未出现正则口径，无需连带；`tags.match` 与时间区间/标记的拒绝面保持不变。

### D13 delta 形态：原「无效条件被拒绝」走 REMOVED + ADDED，而非 MODIFIED

openspec 对 MODIFIED 有防丢场景门禁——MODIFIED 整块替换需求，若少了主规范已有的场景即判 ERROR，且 REMOVED 与 ADDED 同名单也会判冲突。本 change 需要删掉该条里的「正则模式被拒」场景，因此整条 REMOVED（附 Reason/Migration）并以「参数类无效条件被拒绝」重建（**改名已定**），其余 8 个场景逐条原样保留。归档后主规范只有新名单的条目，无重复内容；改名带来的连带是 `openspec/specs/old/message-filter` 与文档中不存在对该条目名的引用，故无需回改其他文件。

### D15 规模闸口：逐条预检 + 整组兜底（已定）

源码事实（本仓库锁定版本 `regex` 1.13.2，`src/builders.rs`）：`RegexSet` 与 `RegexBuilder` 同集具备 `size_limit` / `dfa_size_limit` / `nest_limit` / `case_insensitive` / `multi_line` / `ignore_whitespace`；但 `RegexSetBuilder` 只有 `new(patterns)` 一条批量入口、**没有 `add()`**，无法逐条设限，且其 `size_limit` 实现只是把同一个值转发给内部引擎 builder、文档措辞沿用单条口径的 "a single regex"（示例只给一条 `\w`）。因此「多条时按整组产物计还是逐条计」无法由文档推定，由任务 2.3 探针实测。

口径：

1. **逐条** `RegexBuilder::size_limit = 1 MiB` 预检——只为拒收与 `index` 归因，编译产物不进匹配路径。保留探索笔记 P3 的实测结论（1 MiB 拒 `(\w+\s?){200}`，D7 保留清单全过）。
2. **整组** `RegexSetBuilder::size_limit` 兜底——防「条数 × 每条尺寸」的乘积面。数值按 2.3 探针回填（起始候选 4 MiB），验收线是 D7 保留清单在 16 条满载下全部通过。
3. 两道闸都只在开启查询期发生，不在扫描热路径；多一趟逐条编译可接受。
4. 整组超限复用 `reason = too_size`，不新增第五个取值，且**不附 `index`**（归因不到单条）——已由 spec「归因能定位到单条时才附带序号」钉住。
5. 不采「仅整组一道闸」：整组产物天然大于任一条，逐条口径的 1 MiB 直接套用会误拒 16 条常见写法，且 `index` 归因失效。不采「不引入 `RegexSet`、逐条 `Regex::is_match`」：实测单条 158 ns/行、16 条最坏约 2.5 µs/行，272 万行折算约 6.9 s，劣于 `RegexSet` 单遍 503 ns/行。

### D16 内联大小写开关与外层 `case` 对称生效（口径已定，行为待实测）

spec 现有正向场景（外层区分大小写 + pattern 内声明忽略）与本批新增的反向场景（外层缺省忽略 + pattern 内 `(?-i)` 要求区分）都是可观察行为，**双向一并钉进规范**，不给宿主留各自猜测的空间；测试对称两条（任务 5.3）。

**如实标注**：反向场景的依据只是 `case_insensitive` 等价于在 pattern 起始处置位、内联开关可覆盖这一方言常识，本仓库探索笔记**没有该项实测记录**。任务 2.3 的同一探针须双向各测一次；若实测与场景不符，以实测为准回改本条与 spec 场景（不得反过来放宽实现迁就文档），并把结论写回 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` 后再归档。

### D17 `params` 只带 `reason`（+ 可归因时的附注 `index`），不带 `field`（已定）

17 码下 `field` 恒为 `"msg.values"`，属冗余信息；`index` 已足够定位到具体某条。宿主复用 `INVALID_ARG` 定位通道的做法只适用于参数类拒绝，不扩展到 17 码。`param_keys` 保持 `["reason"]`（任务 3.1）。

## Risks / Trade-offs

- **[体积] `logcore.dll` 上浮约 1.0–1.3 MiB（现有 debug dll 1.72 MiB → 约 1.6 倍）** → 探针口径是独立 exe，apply 阶段带真实依赖重编后复核增量；增量超 1.3 MiB 时**停下报告、等重新拍板**，候选退路是 D1 的裁剪档（省 235 KiB，代价 `\p{...}` 变非法），不自动启用（见 D1）。
- **[耗时] `RegexSet` 每行常数约为单条 `Regex` 的 3 倍，16 条上限下全文档可至秒级（推算值，见 D4）** → 同步返回、非 UI 线程；条数上限与每行成本一并由 D7 承担，数值由任务 7.4 一次性实测；执行路径唯一（D4 已定 `RegexSet` 单遍 + D5a 逐条路由），合并 alternation 与逐条 `is_match` 只作测试内对照档，不作为实现回退开关。
- **[方言] LLM 生成的 pattern 可能带环视/反向引用，直接得到 `syntax` 拒绝** → 这是有意选择（拒绝优于静默降级）；宿主侧把"该方言不支持环视与反向引用"写进 MCP 工具说明，属于 MCP change 的文案工作，不在本 change。
- **[一致性] 两档折叠表在 U+0130 上分歧，混用可能出现不同行集** → D10 记录并限定作用域；不改动子串档（改动会破坏已归档规范与既有测试）。
- **[不可信输入] pattern 来自 MCP，dll 无进程隔离** → 线性时间界 + 长度/条数/`size_limit` 三重上限；实测唯一确认的失控形态 `(\w+\s?){200}` 在 1 MiB 下被拒。
- **[回归] 新增依赖触碰 `Cargo.lock` 与全量构建** → 全量 Rust UT 必须按串行口径复跑（基线 425/425），并声明口径数字；C# 侧仅需 `dotnet test` 全量无新增失败（基线 923）。

## Migration Plan

无数据迁移、无格式版本变更：正则槽位 `msg.mode` 早已在契约与结构中冻结，本 change 只是放行取值并补齐语义。回滚 = 恢复 `validate()` 对 `"regex"` 的拒绝（回到当前口径），已发布索引与缓存不受影响；依赖与 `Cargo.lock` 回退随 revert 一并处理。宿主侧无需改动即可继续只用子串档。

## Open Questions

- 真实 `logcore.dll` 在带依赖重编后的体积增量（apply 首步实测，不改变方案，只决定是否触发 D1 的裁剪回退）。
- 整组 `RegexSetBuilder::size_limit` 的计数口径与兜底数值（D15 已定「两道闸」，数值待任务 2.3 探针回填）。
- pattern 内 `(?-i)` 在外层缺省忽略大小写下是否真的生效（D16 已定口径，行为待任务 2.3 双向实测；不符则回改 spec 场景）。
- 编译缓存（D3a）的 8 条 / 8 MiB 取值是否需要按任务 2.5 实测下调（口径已定，只调数值；实测若显示收益可忽略则整体砍掉）。
- 采集源何时开放正则：本档只覆盖装载完成的文件源，采集源仍无统一快照读面（沿用既有缺口）。
- 命中位置/命中词回传是否需要：需另立接口，与"不做抽取"边界相邻，本档不预留字段。
