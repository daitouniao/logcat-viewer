# Rust core 正则档：方言边界与匹配语义

- 状态：**已定稿（R1–R6 已拍，P1–P5 已实测；卡点 C 与两个常量待拍）**
- 日期：2026-11-25
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

## 3. 方言差异对照（regex 1.13 官方语法表核对，本仓库 `regex = "1.11"` 解析到 1.13.2）

| 写法 | Rust regex | .NET Regex | Node.js RegExp |
| --- | --- | --- | --- |
| `(?=x)` `(?!x)` `(?<=x)` `(?<!x)` 环视 | **不支持** | 支持 | 支持 |
| `\1` 反向引用 | **不支持**（默认语法错） | 支持 | 支持 |
| `(?>x)` 原子组 / `x*+` 独占量词 | **不支持** | 支持 / 不支持 | 不支持 / ES2024 起支持 |
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

- **长度上限**：对 pattern 字符数设硬常量，超限即拒。取值依据（P5/成本实测）：2000 分支 pattern 编译 6.9 ms、10k 字符字面量 3.3 ms，编译侧不构成风险；上限主要用来封住"条数 × 每条尺寸"的乘积面，倾向 **2000 字符/条**。
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
- 处理倾向与是否升为规范条款，见 §7「折叠差异」条（**未拍**）。

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

## 7. 待拍板（探针已给出数值，本文不下结论）

- **卡点 C：Unicode 能力面到哪一档**（P5 已量，含 std 基线 112 KiB、LTO+strip）：
  | 档 | 特性 | 产物 | 能力得失 |
  | --- | --- | --- | --- |
  | 甲（=regex 默认） | `perf` + 全 `unicode` | 1529 KiB | 全量方言，`\p{Greek}` `\p{Lu}` 合法 |
  | 乙 | `perf` + `unicode-case` + `unicode-perl` | 1294 KiB | 丢属性类：`\p{...}` **编译失败**；`\w \d \s \b` 与 `(?i)` 保持 |
  | 丙 | 仅 `perf` + `unicode-case` | 1243.5 KiB | 再丢 `\w`（中文正文上**编译失败**）——对本项目不可用 |
  | 丁 | 仅 `perf` | 1154 KiB | 连 `(?i)` 都编译失败——不可用 |
  当前偏向：**甲**。理由：乙/丁省下的 235 KiB 换不掉任何真实收益（地板在引擎、不在 Unicode 表），却把"合法写法"缩出一档——LLM 写 `\p{Lu}` `\p{Greek}` 是常见写法，届时用户拿到的是"方言里没有这个"而不是"没命中"，比方言窄更难解释。**代价待验**：P5 测的是探针 exe，真实 `logcore.dll` 的增量要在 apply 阶段带依赖重编才算数（现有 debug dll = 1.72 MiB，若 +1.0 MiB 量级即约 1.6 倍）。
- **折叠差异是否写进规范**：R7 实测两档只在 U+0130 一类上分歧（`i` 命中 `İ` 与否）。选项——甲 只在本笔记留事实、规范不提（倾向）；乙 在 spec 加一句"正则档大小写等价按 Unicode simple case folding，与子串档全折叠在少数字符上不同"；丙 把子串档也改成 simple folding（会动已归档规范与现有测试，代价最大，不倾向）。
- **pattern 条数上限取值**：`msg.values` 在正则档可放几条。倾向复用子串档既有阀值口径（旧线 `BLOCK_SCAN_MAX_KEYS = 8` 是兜底、非契约），新常量建议 **8–16 条**；条数直接乘每行成本（`RegexSet` 4 条实测 503 ns/行）。
- **`op: and` 的实现选择**（属实现细节、留 design）：`RegexSet` 单遍可同时给出每条命中位，`and`/`or` 共用一遍；但实测每行成本约单条 `Regex` 的 3 倍，`or` 是否退回"逐条 `is_match` + 短路"由 design 定，行为不变。
- **方言承诺形式**：写"能力集 = 所锁定 regex/regex-syntax 版本行为"还是写显式白名单 + 黑名单（防升级静默改方言）。倾向：规范写能力集 + 明确不支持清单，版本锁定由 `Cargo.lock` 承担，design 记全量对照表。
- **宿主侧语法校验**：本档定"宿主不得以其他引擎判定正则合法性"为负向条款；若 UI 将来要输入框实时提示，需另立无句柄的校验导出（`validate_pattern` 类）才能同引擎判定。
- **`tag` 是否开放 regex**：`tags.match` 枚举已冻结为 `substring | exact`，倾向不扩。
- **时间区间条件**：规范现列为 MUST 拒绝（与标记同批），是否随 MCP 需求开。
- **采集源可否开查询**：采集源至今无统一快照读面，本档只覆盖文件源。
- **命中位置的可查询性**：谓词档不返回"哪条 pattern 命中 / 命中区间"，MCP 若需高亮命中词，须另立接口（与"不做抽取"边界相邻）。
- **既有缺口连带**：`Cargo.lock` 目前 27 个包，引入 `regex` 会新增 `aho-corasick` / `memchr` / `regex-automata` / `regex-syntax` 四条（实测解析到 regex 1.13.2，非笔记原先写的 1.11.1；方言措辞按 1.13 校准）。
