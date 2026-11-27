# Rust core 正则档：方言边界与匹配语义

- 状态：**已实施待验收（R1–R7 已拍，P1–P14 已实测）**
- 日期：2026-11-27
- 关联提交：无（纯探索）
- 验收结论：不适用
- 实测载体：`.tmp-test/regexprobe`、`.tmp-test/regexsize`（git-ignored 临时工程，`regex` 解析到 1.13.2 / `regex-syntax` 0.8.12，工具链 1.99.1，Release + LTO + strip）

## 1. 结论先行

- **"C# / Node / Rust 三种正则写法是否相同"是伪问题**：正则字符串自始至终只被 Rust `regex` 编译执行一次，C# 与 Node 只承载文本、只接收命中/未命中。方言只有一套。
- 真正会咬人的是四层，且互不相同：**语法能力集**（环视、反向引用不支持）、**Unicode 默认**（`\w` `\d` `\b` 在 Rust/.NET 是 Unicode，在 Node 是 ASCII）、**字符串转义承载**（UI 原文 / JSON 双反斜杠 / C# 字面量 / JS 字面量正则需剥斜杠）、**大小写折叠表**。
- 本项目**没有历史 .NET 用户正则包袱**：UI 侧 `Regex` 只用于切词与内部解析，用户从不写正则 → 方言可自由选定，无迁移成本。
- 本档只交付**谓词**（pattern → 该行是否命中）。捕获组抽取（`log_trace_field` 类需求）是行变换而非行选择，另立 capability，不复用本接口、不在本档预留字段。
- 非 UTF-8 正文、空正文行、不可信 pattern 三处是本次探索的真卡点，结论见 R1 / R3 / R6。

## 2. 前提事实

| 事实 | 位置 |
| --- | --- |
| Rust 侧尚无 regex 依赖（本档起需新增） | `crates/core/Cargo.toml` |
| 子串档的非法 UTF-8 归一原语已存在 | `crates/core/src/filter.rs:422`（`String::from_utf8_lossy(body).to_lowercase()`） |
| 已有测试钉住"裸 `0xFF` 脏行被 `` 命中" | `crates/core/src/filter.rs:1463` `filter_msg_lossy_invalid_utf8` |
| 空词逻辑：`or` 见空即真、`and` 全空才真、否则跳过空值 | `crates/core/src/filter.rs:329`、`:356` |
| `values: []` 归一为条件未启用；`values: [""]` **不**归一 | `crates/core/src/filter.rs:262` |
| 正则模式当前显式拒绝（槽位已冻结在契约里） | `crates/core/src/filter.rs:942` |
| 主规范已钉"本代以正则提交 MUST 被拒绝" | `openspec/specs/logcore/filter/spec.md`「正则模式被拒」场景 |
| 全系统非法 UTF-8 口径统一为替换字符 | `crates/core/src/parse.rs:475`；C# 侧 `Encoding.UTF8.GetString` 默认 replacement fallback |
| `encoding` 仅 `Utf8`、编码转换未做 → GBK 日志按原字节装载，脏行是中文场景常态 | 源获取契约 D14；C7 未实施 |

## 3. 方言差异对照（regex 1.13 官方语法表核对，本仓库声明 `regex = "1.13"` / `regex-syntax = "0.8"`，解析到 1.13.2 / 0.8.12）

| 写法 | Rust regex | .NET Regex | Node.js RegExp |
| --- | --- | --- | --- |
| `(?=x)` `(?!x)` `(?<=x)` `(?<!x)` 环视 | **不支持** | 支持 | 支持 |
| `\1` 反向引用 | **不支持**（默认语法错） | 支持 | 支持 |
| `(?>x)` 原子组 | **不支持**（`unrecognized flag`） | 支持 | 不支持 |
| `x*+` 独占量词 | **无该构造**：语法层即解析为叠加量词 `(x*)+` 并接受（实测 P12） | 不支持 | 不支持 / ES2024 起支持 |
| `(?i)` 内联 flag，可中途切换 | 支持 | 支持 | **不支持**（仅 `/x/i` 后置） |
| `(?x)` verbose、`(?R)` CRLF | 支持 | 选项形式 / 不支持 | 不支持 |
| `[a-z--b]` 类集合运算 `&& -- ~~` | 支持 | 不支持 | 仅 `v` flag 且写法不同 |
| `\w \d \s \b` | Unicode（默认） | Unicode | **ASCII** |
| `\p{...}` 属性类 | 默认支持 | 支持 | 需 `u`/`v` flag |

要点：缺项（环视、反向引用）恰是"排除含 X 的行"这类日志检索高频写法，LLM 生成 pattern 时最常踩；多项（集合运算、`(?x)`）只造成"这份 pattern 换引擎跑不了"，对本项目无害。

## 4. 已定决策

### R1 非 UTF-8 正文：lossy 归一后交给 `Regex`

采 `String::from_utf8_lossy` + `Regex::is_match`，**不用 `bytes::Regex`**。

- `from_utf8_lossy` 返回 `Cow<str>`：正文合法时为 `Borrowed`，**零分配**；仅脏行分配。故"先 `from_utf8` 试探、失败才 lossy"不是第三方案，它就是该函数内部行为，手写属重复实现。
- 否掉 `bytes::Regex` 的理由：非法字节在 Unicode 模式（默认开）下**根本不是可匹配字符**，`\x{FFFD}` 也命中不了裸 `0xFF` —— 会造成"屏幕上看得见、正则搜不着"，直接违反 `filter.rs:1463` 已钉的同类口径。
- 归一化接缝与 C7 编码转换（GBK→UTF-8）同点位：转码落地时只改"喂给匹配器之前"这一处。
- 实现约束：**每行归一化一次、N 条 pattern 复用**，不得在 pattern 循环内部反复 lossy。
- **P1 实测（否案已证伪）**：`bytes::Regex` 的 `unicode` 默认为**开**，非法字节不是合法码点 —— 对 `[x, 0xFF, y]`，`bytes::Regex` 的 `x.y` **不命中**、`(?s).*` 只吃 1 字节，而 lossy 后 `x\u{FFFD}y` 让 `x.y` 与 `(?s).*` 都成立；`\x{FFFD}` 与字面 U+FFFD 在 `bytes::Regex` 上对裸 `0xFF` **均不命中**。即丙方案会让脏行在正则档整体不可搜，与 `filter.rs:1463` 已钉口径冲突，**排除**。
- **P2 实测（成本不构成问题）**：约 80 B 真人行长 × 200k 行，仅 lossy = **97 ns/行**（合法 UTF-8 走 `Cow::Borrowed`、零分配），仅 `is_ascii()` = 33 ns/行，脏行 lossy 需分配、约 14 ms/50k 次。lossy 一次的成本**低于**现有子串档 CI 路径（双侧 `to_lowercase` = 243 ns/行），故正则档不必另设 ASCII 快速通道。

### R2 纯字面量短路（丁，判据已实测收紧）

pattern 解析后的 `Hir` 若整棵为 `Concat(Literal*)`（实测 `regex_syntax::hir::literal::Extractor` + `HirKind` 可判），该条件**不启动正则引擎**，直接复用子串档 `needle_matches`。

- 实测决定短路范围：**`(?i)` 一旦出现在 pattern 里，Hir 就变成 `Concat([Class({'T'..='T','t'..='t'}), …])`，不再是纯字面量**。故短路**只在区分大小写时触发**，而区分大小写的字面量匹配两档逐位同形（都是字节包含），**不存在折叠表分歧**。原判据"是否需限定纯 ASCII"随 P4 实测作废——不需要额外限定，靠 Hir 形态即自洽。
- `tim\.eout` 判为纯字面量（`Literal("tim.eout")`）→ 短路；`^abc$`、`a|b`、`tim.*eout`、`\d+` 均不判为纯字面量 → 走引擎。**`^abc$` 不短路是对的**：锚点语义与子串包含不等价。
- 收益：用户写的"正则"里很大比例是字面量（含转义标点），这部分不付 NFA 代价，且与子串档结果一致（含 U+FFFD 规则）。实测每行成本差：80 B 行 × 200k 行，单 pattern `is_match` = 158 ns/行，子串档区分大小写 = 110 ns/行（差约 48 ns/行、272 万行 ≈ 130 ms）。
- **短路判定落在整条 `msg` 条件级**：全部 `values` 都是纯字面量才短路，任一是真 pattern 则整条走正则路径。
- 判据成本可忽略：`regex-syntax` 是 `regex` 的既有传递依赖，不新增依赖树。

### R3 空正文行 × 零宽 pattern：以引擎结论为准（甲）

`moff == lens` 的空正文行，在正则档按**空文本参与匹配**处理：`.*`、`\s*`、`a*`、`^$` 在其上命中。

- 依据：正则的定义即"对一段文本求值，空串是合法文本"，PCRE / .NET / JS / Rust 四台引擎一致；采守卫等于造一条所有正则用户都不预期的私规。
- 保住表达力：`^$` 可查"正文为空的行"。加守卫则该查询永久无法表达，且无替代入口（空 pattern 是恒真、非"仅空正文"）。
- 与 R2 零冲突：能匹配空串的 pattern 必含元字符，必不触发字面量短路。
- **连带账**：已归档规范「正文多值与排除」中"条件值为非空词时，空正文视为未命中"须限定作用域为**子串模式**，并新增正则档需求。属 MODIFIED，非纯 ADDED。

### R4 空 pattern：不特殊处理

`""` 在 regex 里合法且处处匹配，与子串档 `always_true` 逻辑逐条同形（`or` 含空即真、`and` 全空才真、混填时空值不否决）。规范补场景钉住该等价性即可，无需新决策。`values: []` 与 `values: [""]` 的既有分野（后者不归一为"未启用"）继续成立——一旦归一，`exclude: true` 将错留全部行。

### R5 多 pattern：`RegexSet` 单遍 + 只用存在性

- `op` 的 `and` / `or` 均可由一次 `RegexSet::matches` 得出（`matched(i)` / `matched_any()`），故**正则档不存在子串档的"每词一轮全量扫描"缺口**；旧笔记 §1 列的唯一真缺口在本档不需要单独治理。
- `RegexSet` 只报告哪些 pattern 命中、不给位置，与谓词档需求完全对齐；空 pattern 恒真行为天然一致。
- **硬约束**：只允许 `is_match` 级别的存在性判定。`find_iter` / `captures_iter` 的最坏复杂度是 `O(m·n²)`（官方明说无法规避），而 `is_match` / `find` 是 `O(m·n)`。本档禁迭代 API —— 它与"不做捕获抽取"是同一条边界。

### R6 不可信 pattern 的资源边界

pattern 来自 MCP（LLM 生成）与将来 UI 输入，属不可信输入；而 dll 运行在宿主进程内、无进程隔离，畸形 pattern 的资源代价直接落在用户进程上。

- **长度上限**：对 pattern 字符数设硬常量，超限即拒。取值依据（P5/成本实测）：2000 分支 pattern 编译 6.9 ms、10k 字符字面量 3.3 ms，编译侧不构成风险；上限主要用来封住"条数 × 每条尺寸"的乘积面，取 **2000 字符/条**。
- **收紧 `size_limit` = 1 MiB**（默认 10 MiB 实测**挡不住**关键形态）：`(\w+\s?){200}` 在默认下编译通过，对 2 KB 正文单行匹配耗时 **7.0 ms**（其余同类 pattern 都在 µs 级）——按 272 万行折算即小时级，这是本档唯一实测到的真实失控形态。1 MiB 直接拒收它，同时保留 `timeout`、`(?i)(a|…|j)+`、`(a|b|c){50}`、2000 字符字面量、`(x|y)×500`、`(a+)+b`、`([a-z]+)*$`、`(x|x)+y`、10 层嵌套 group。
- `nest_limit` 沿用解析层默认（1000）：实测 200 层嵌套 group 编译 0.6 ms、匹配 14.6 µs，不构成威胁；收紧到 16 反而会把 `(a|b){100}{100}` 这类"合法但无意义"的写法一并挡在门外，收益已由 `size_limit` 承担，**不调整**。
- **错误码**：语法错、超长、编译过大合用**一个码**（`REGEX_INVALID` 空段追加），用 `params.reason: "syntax" | "too_long" | "too_big"` 区分。符合源获取契约 D15/B1"params 只放语义键与数值、无文案"，宿主各自本地化；不给错误码表增加三条几乎无人区分的条目。
- **无匹配超时**（沿用 2026-11-15 拍板）：线性时间界 + `size_limit` 承担防拖死职责；本档不复活超时。
- **残留风险（如实记）**：线性界保证的是"单条 pattern 对单行 O(行宽)"，但 `RegexSet` 的每行常数开销实测约单 pattern 的 3 倍（503 ns vs 158 ns / 80 B 行），条数上限若放开到数百，全文档耗时会到秒级——属可接受（同步返回、非 UI 线程），但需与 §7 的条数上限一并定。

### R7 两档大小写折叠表不一致：实测唯一差异点

正则档 `(?i)` 用 UTS#18 **simple** case folding，子串档用 `char::to_lowercase` **全**折叠。逐对实测（needle/hay 共 13 组）：

| 组合 | 正则 `(?i)` | 子串档（`to_lowercase`） | 一致 |
| --- | --- | --- | --- |
| `i` vs `İ`(U+0130) | 不命中 | **命中** | **否** |
| `İ` vs `i` | 不命中 | 不命中 | 是 |
| `ss` vs `ß`(U+00DF)、`ß` vs `ss` | 不命中 | 不命中 | 是 |
| `ẞ`(U+1E9E) vs `ß` | 命中 | 命中 | 是 |
| `ſs`(U+017F+`s`) vs `ß` | 不命中 | 不命中 | 是 |
| `k` vs `K`(U+212A KELVIN)、`K` vs `K`(U+212A) | 命中 | 命中 | 是 |
| `I` vs `ı`(U+0131)、`ı` vs `I` | 不命中 | 不命中 | 是 |
| `İstanbul` vs `istanbul` | 不命中 | 不命中 | 是 |

- **先前挂在 R6/D5 的猜测已澄清**：U+212A 在正则 `(?i)` 下折叠为 `k`、与子串档一致（与早期"字节快筛按 ASCII 表可能漏"的担心无关，两档都走 Unicode 折叠）。真正的差异只剩 **U+0130 一类**（全折叠会多出组合字符 U+0307，从而让 `i` 命中 `İ`）。
- 该差异**只在同一查询混用两档时才可见**：`msg.mode=substring` 的 `case:false` 与 `msg.mode=regex` 的 `(?i)`/`case:false` 对土耳其语正文可能给出不同行集。
- 处理结论：不入规范，本文只留事实（§7「折叠差异」条）。

## 5. 关于 feature 裁剪的一条官方保证
> enabling or disabling any of the Unicode features can only add or subtract from the total set of valid regular expressions; it will never modify the match semantics.

即裁剪 `unicode-*` feature **不会改变已合法 pattern 的命中结论**，只会让某些写法变成编译失败。实测印证：关掉 `unicode-case` 后 `(?i)TIMEOUT` **直接编译失败**（不是退化成 ASCII 折叠），关掉 `unicode-perl` 后 `\w+` 对中文正文**编译失败**——所以"裁feature = 静默少结果"的担心不成立，风险形态是"用户写法变非法"。`perf-literal`（aho-corasick + memchr 字面量预筛）官方称收益可达数个数量级，**不可关闭**，依赖树省不掉。

## 6. 探针结果（已实测，非文档措辞）

| 编号 | 结论 | 实测值 |
| --- | --- | --- |
| P1 | `bytes::Regex` 默认 Unicode 模式**开**，非法字节不可匹配 → 丙案否死 | `[x,0xFF,y]`：`x.y` 不命中、`(?s).*` 只吃 1 字节；`\x{FFFD}` 与字面 U+FFFD 均不命中 |
| P2 | lossy 成本可接受，**不需**额外 ASCII 快速通道 | 80 B 行 × 200k：仅 lossy = 97 ns/行、仅 `is_ascii()` = 33 ns/行；合法 UTF-8 返回 `Cow::Borrowed`（零分配），脏行才分配 |
| P3 | `size_limit` 收紧取值 = **1 MiB** | 默认 10 MiB 时 `(\w+\s?){200}` **编译通过**（正是该防的形态）；1 MiB 与 640 KiB 拒之，同时 `timeout`、`(?i)(a\|…\|j)+`、`(a\|b\|c){50}`、2000 字符纯字面量、`(x\|y)×500` 全部仍通过 |
| P4 | 短路判据不需 ASCII 限定（`(?i)` 使 Hir 不再是纯字面量）；两档折叠差异只剩 U+0130 | `(?i)timeout` → `Concat([Class,…])`；`tim\.eout` → `Literal("tim.eout")`；详见 R7 表 |
| P5 | 引入 regex 的产物增量（LTO+strip，含 std 基线 112 KiB） | 仅 `perf` = 1154 KiB；`+unicode-case` = 1243.5 KiB；`+unicode-perl` = 1294 KiB；**default（全开）= 1529 KiB**。即引擎地板 ≈ +1.0 MiB，全量 Unicode 相对"case+perl"只多 **235 KiB** |

补充成本事实（同一探针）：

- 编译耗时：2000 分支 pattern = 6.9 ms；10k 字符纯字面量 = 3.3 ms；200 层嵌套 group = 0.6 ms；2000 条 pattern 的 `RegexSet` 构造 = 27.8 ms。pattern 侧不是瓶颈。
- 匹配吞吐：`Regex` 对 100 KB 正文单遍 = 23–52 µs，与分支数（1 → 2000）基本无关；`RegexSet` 8 → 2000 条 = 50 µs → 753 µs（163 MB 全文档折算 86 ms → 1.2 s）。
- **每行固定开销才是主成本**（80 B 行 × 200k）：单 pattern 158 ns/行、`RegexSet` 4 条 = 503 ns/行、4 分支合并 alternation = 193 ns/行、子串档区分大小写 = 110 ns/行、子串档 CI 双侧折叠 = 243 ns/行。折算 272 万行：正则单条件 ≈ 0.44 s，`RegexSet` 4 条 ≈ 1.4 s。
- `RegexSet` 能力面实测：支持 `(?i)`；含捕获组可编译但**只回报"哪条命中"、不给区间**——与"只判存在性"的需求边界吻合（`matches()` 返回 bitset，位置不可得）。
- 零宽 pattern 对空正文：`.*` `a*` `\s*` `^$` `^` `$` `""` 全部**命中**（区间长度 0），`\b` **不命中**。R3 的甲按此成立。

## 6a. apply 期补测（任务 2.2 / 2.3 / 2.5 / 6.1 / 7.4，探针为 git-ignored 临时 crate，regex 1.13.2 / regex-syntax 0.8.12）

| 编号 | 结论 | 实测值 |
| --- | --- | --- |
| P6 | `RegexSetBuilder::size_limit` 按**整组产物累计**，相同 pattern 不共享额度 | `(\w+\s?){16}` 单条最小可接受额度 812 688 B；4 / 8 / 16 条 = 3 250 336 / 6 498 928 / 12 995 856 B，恰为 4.00× / 8.00× / 16.00×。混批低于线性（引擎对公共部分去重）：D7 保留清单 16 条整组只需 268 676 B，`(a\|b\|c){50}` 组/单条 = 8.33×、`[A-Za-z_][A-Za-z0-9_]*` = 10.04× |
| P7 | 整组闸非空转，据此定兜底 **4 MiB** | `(\w+\s?){12}` × 16 条：单条 595 KiB（过逐条 1 MiB 闸）、整组 9 519 KiB（被 4 MiB 拒）；同 pattern 取 4 条整组 3 174 KiB 通过 |
| P8 | 内联大小写开关与外层 `case` **双向互不抹除**（走 `RegexSetBuilder::case_insensitive`） | 外层区分 + `(?i)timeout` 对 `TIMEOUT` **命中**（不带 `(?i)` 未命中）；外层缺省忽略 + `(?-i)timeout` 只命中 `timeout`，对 `TIMEOUT`/`Timeout` **未命中**；裸 `(?-i)` 与 `(?i)(?-i)` 同结果 → 内联按作用域覆盖外层置位 |
| P9 | `(?-u)` 边角：词类降 ASCII 可用，属性类与越出 ASCII 的写法**编译期即被拒** | `(?-u)\w` 可编译、对中文正文不命中；`(?-u)\p{Lu}` / `(?-u)\p{Greek}` → `Unicode not allowed here`；`(?-u:\W)` / `(?-u:.)` / `(?-u)[a-z&&[^b]]` → `pattern can match invalid UTF-8`（str 通道要求只匹配合法 UTF-8）；`(?-u)[a-z--b]` 可编译。`Regex` 与 `RegexSet` 两通道结论一致 |
| P10 | 缓存收益量级：合成口径占全量扫描 1.2%，**真实文档口径 0.37% → 缓存不做** | 16 条满载 `RegexSet` 构造 2.85–3.00 ms（冷热无差）、存活堆 293.4 KiB；典型 4 条 set 存活 65.8 KiB；单条 `timeout` 2.9 KiB、2000 字符字面量 101.7 KiB。20 万行 × 16 条（45 B 合成短行）扫描 256 ms = 1281 ns/行 |
| P11 | 方法学：`size_limit` 只能用**阈值二分**测，不能用分配流量代理 | 单条 `\w+` 累计分配 760 KiB 而存活仅 60 KiB，且限额计的是自动机规模——按累计字节取「1× 单条」额度时 16 条整组仍通过，会误判成逐条口径 |
| P12 | 该方言**不含独占量词**：`x*+` 在语法层就是叠加量词，不是被拒构造 | `a*+` 可编译，Hir = `Repetition{min:1, sub: Repetition{min:0, Literal("a")}}` 即 `(a*)+`；`a**` 同为两层叠加。因内层可空匹配，整条对任意正文恒命中（`aaa`/`xyz`/`aab` 三行全中）。对照：`(?>ab)` 原子组 → `unrecognized flag` 被拒，`a(?=b)` → `look-around ... is not supported` |
| P13 | 真实文档两档对照（任务 7.4）：16 条档正则 ≈ 子串，条数上限可留 | 3 063 982 行 / 184 MB 网络共享样本、Release：正则档 1 / 4 / 16 条 = 1.18 / 2.63 / **5.07 s**（386 / 857 / 1655 ns/行），子串档 = 0.64 / 1.49 / **5.31 s**；16 条编译 18.79 ms。正则档边际 ≈ 0.26 s/条（子串 ≈ 0.31 s/条）→ `RegexSet` 单遍只省"重复取正文"，不省"每行按条数做功" |
| P14 | 真实 dll 增量高于 P5 的 exe 外推，且"只裁 `regex`"是空操作 | Release 926 720 → 2 543 616 B（+1.54 MiB）、debug 1 757 184 → 5 065 728 B（+3.16 MiB）；只把 `regex` 裁到 `perf`+`unicode-case`+`unicode-perl` 而 `regex-syntax` 保持默认 → 仅省 103 KiB 且 52 条正则用例全过（feature 统一把 Unicode 表重新打开，等于没裁到数据）；`regex-syntax` 再裁到 `["std"]`（该 crate 无 `perf`）→ 累计省 343 KiB，但 `\p{Lu}` 编译失败、2 条用例挂 |

## 7. 决策收口（本 change 定稿口径）

- **Unicode 能力面 = 默认全开**：地板在引擎本体（`perf` 单档即 1154 KiB / std 基线 112 KiB），裁剪省下的不足 235 KiB，代价却是 `\p{...}` 编译失败乃至 `\w`、`(?i)` 不可用，故不裁。
- **真实 dll 增量已复测**（任务 2.2，正则路径落地后）：Release 926 720 → 2 543 616 B（+1.54 MiB）、debug 1 757 184 → 5 065 728 B（+3.16 MiB）。评审接受该增量、阈值上调到 1.6 MiB。**原 P5 的 exe 外推偏低**，且探针没暴露 feature 统一这一层：真实工程里 `regex-syntax` 是直接依赖，只裁 `regex` 的 feature 实际只省 103 KiB（数据表被 `regex-syntax` 的默认 feature 重新打开），必须两侧同裁才见效——详见 change 的 design D1。
- **折叠差异不入规范**：两档唯一分歧在 U+0130 一类（R7 实测），只在本文留事实，spec 不写"正则档用 simple folding、子串档用全折叠"的差异句。
- **条数与长度上限 = 16 条 / 2000 字符**（取区间上沿）：`RegexSet` 4 条 503 ns/行、16 条真实文档 1655 ns/行（任务 7.4，3 063 982 行样本），16 条全量扫描 5.07 s 与同条数子串档 5.31 s 持平，故上限不下调。
- **`op: and` / `or` 都走 `RegexSet` 单遍**：一次 `matches` 的 bitset 同时给出每条命中位，两者只差在取位方式；「合并 alternation」不作实现路径——它受单条额度约束，会让同一批 pattern 的拒绝面随 `op` 变化（见 D4/D8）。每行成本仍随条数上升，不等同于"条数免费"。
- **编译缓存不做**（任务 2.5 + 7.4）：16 条编译 18.79 ms，占真实全量扫描 5.07 s 的 0.37%，只在"同组条件反复开启而不重扫"时省这一档；相对每源一套 LRU、原文序列键与预算闸的复杂度不划算，故 D3a 定稿为"不做"，spec 不留"实现 MAY 复用"措辞。
- **方言承诺形式**：spec 写能力集 + 不支持清单，版本锁定由 `Cargo.lock` 承担（27 → 31 包，regex 1.13.2 / regex-syntax 0.8.12）。apply 期按实测纠正一处：该方言**不含独占量词**，`x*+` 是叠加量词而非被拒构造（P12），原探索清单把它与环视并列是错的。
- **宿主侧语法校验不开口**：本档只定"宿主 MUST NOT 以其他引擎判定 pattern 合法性"的负向条款；UI 若将来要输入框实时提示，须另立无句柄的 `validate_pattern` 类导出才能同引擎判定，本 change 不做。
- **`tags.match` 不开放 regex**（枚举仍 `substring | exact`）、**时间区间与标记条件仍拒绝**：均沿用既有拒绝面，不随本 change 扩。
- **采集源与命中位置留作后续**：本档只覆盖装载完成的文件源（采集源至今无统一快照读面）；谓词档不返回"哪条 pattern 命中 / 命中区间"，MCP 要高亮须另立接口。
