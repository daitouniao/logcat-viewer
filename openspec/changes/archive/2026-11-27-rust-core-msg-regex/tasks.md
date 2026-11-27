# Tasks

## 1. 前置与 worktree

- [x] 1.1 在独立 git worktree 中实施本 change：worktree 标识 = 分支 `wt/rust-core-msg-regex`（路径以 `git worktree list` 定位，禁写本机绝对路径），目标分支 `master`；验证：`git worktree list` 列出该分支、主工作树 `git status` 保持干净
- [x] 1.2 复核已定口径与本文一致（feature 全开、16 条 / 2000 字符、`reason` 四值、C# 只加码常量不新增 IT、折叠差异不入 spec、原条目改名承接）；验证：`design.md` D1/D6/D7/D10/D13 无「未拍」标记，且 D15–D17 已按评审结论收口或转为实测确认项

## 2. 依赖与体积基线

- [x] 2.1 `crates/core/Cargo.toml` 增加 `regex = "1.13"` 与 `regex-syntax = "0.8"`（默认 feature，D1）；验证：`cargo build` 成功，`Cargo.lock` 由 27 包增至 31 包（新增 `aho-corasick` / `memchr` / `regex-automata` / `regex-syntax`），解析到的 `regex` 版本不低于 1.13
  - 实测：`Cargo.lock` 27 → 31 包；解析结果 `regex 1.13.2` / `regex-automata 0.4.19` / `regex-syntax 0.8.12` / `aho-corasick 1.1.5` / `memchr 2.8.3`
- [x] 2.2 实测带依赖重编后的 `logcore.dll` 增量，并与 D1 裁剪档（`perf` + `unicode-case` + `unicode-perl`）对比；验证：给出 debug 与 Release 两口径的字节数、数值记入本任务备注；增量超 1.3 MiB 时**停下报告等重新拍板**，不得自行切裁剪档（见 D1）
  - 首测数值（仅加依赖、正则代码路径尚未落地）：debug 基线（`master`）1 757 184 B、本树 1 753 600 B；Release 两侧均 926 720 B，增量 0。
  - **该口径不可用于判定**：无代码引用 `regex` 时链接器按未使用段整段剔除，产物不含引擎机器码，故 0 增量不代表体积结论。本任务的重测点改到 4.1–5.7 正则求值路径落地之后，按同一命令（`cargo build` / `cargo build --release -p logcore`，`.cargo/config.toml` 的 `target-dir = logcore/target`）复取两侧数字再判 1.3 MiB 阈值；届时才决定是否需要 D1 裁剪档对比。
  - 因此本任务在 §4/§5 完成前保持未关闭，不阻塞 §2.3 / §2.5 的探针与后续编码任务。
  - **复测（正则求值路径已落地，§4/§5/§6 完成，依赖按 D1 全开）**：同一命令、同一 `target-dir`，基线取 `master`（无 regex 依赖）——debug 基线 1 757 184 B → 本树 5 065 728 B（**增量 3 308 544 B ≈ 3.16 MiB**）；Release 基线 926 720 B → 本树 2 543 616 B（**增量 1 616 896 B ≈ 1.54 MiB**）。
  - **超阈值，按本任务口径停下等重新拍板**：Release 增量 1.54 MiB > 1.3 MiB。原 P5 的探针外推（引擎地板 ≈ +1.0 MiB）偏低，实测含正则档自身代码与 `RegexSet` 自动机后到 1.54 MiB。
  - 裁剪档对比（临时改 feature 实测后**已还原为 D1 全开**，本树现状即全开）：① 只裁 `regex`（`default-features=false` + `perf`/`unicode-case`/`unicode-perl`）、`regex-syntax` 保持默认 → Release 2 438 144 B，增量 1 511 424 B ≈ 1.44 MiB，**仍超阈值**；且 52 条正则用例全过——`regex-syntax` 的默认 feature 通过 feature 统一把 Unicode 表项重新打开，故该档实际没裁掉数据，只省 103 KiB。② 再把 `regex-syntax` 裁到 `["std"]`（该 crate 无 `perf` feature，可用集见 `cargo` 报错）→ Release 2 192 384 B，增量 1 265 664 B ≈ 1.21 MiB，**唯一低于 1.3 MiB 的组合**，但 `\p{Lu}` 被拒、`regex_accepts_common_forms` 与 `regex_property_class_uppercase_letter` 共 2 条失败——直接违反 spec「正则方言边界」的「Unicode 属性类 SHALL 可用」与场景「属性类可用」及任务 6.2。
  - **收口（评审取甲）**：接受 Release 增量 1.54 MiB，D1 的阈值由 1.3 MiB 上调到 **1.6 MiB**（超出仍需停下重新拍板）；裁剪档经上述两条实测否掉，不再作为退路（理由已写进 D1）。debug 口径只作参考，不进门禁。

- [x] 2.3 用临时探针（git-ignored，不落仓库）实测三项并把结论回填 `design.md`：① `RegexSetBuilder::size_limit` 按整组产物还是逐条计数，给出 16 条满载下 D7 保留清单的构造结果与峰值内存，据此定整组兜底数值（起始候选 4 MiB）；② 内联大小写开关双向实测（外层 `case:false` + pattern 内 `(?i)`、外层缺省 + pattern 内 `(?-i)`）；③ `(?-u)` 边角——属性类与集合运算是可用还是被拒、`(?-u:\W)` 在 lossy `&str` 通道上的实际表现。验证：D15 的整组数值有实测依据；D16 的两个 spec 场景与实测一致，不符则先回改 `specs/logcore/filter/spec.md` 与任务 5.3 的用例名，并把结论写回 `openspec/notes/RUST-CORE-REGEX-DIALECT.md`；③ 若构成需要承诺的可观察行为则补 spec 场景与任务 6.3 用例，否则只记入 D9 本文
  - 备注（① 已定）：整组按**产物累计**、相同 pattern 不共享额度——`(\w+\s?){16}` 单条最小可接受额度 812 688 B，4 / 8 / 16 条恰为 4.00× / 8.00× / 16.00×；混批低于线性（D7 保留清单 16 条整组仅 268 676 B）。整组兜底定为 **4 MiB**，两条验收线均成立：不误伤 262 KiB 的保留清单满载、非空转（`(\w+\s?){12}` × 16 条整组 9 519 KiB 被拒，单条 595 KiB 过逐条闸）。回填 D15、D7 表与 notes §6a P6/P7。
  - 备注（② 已定）：双向实测走 `RegexSetBuilder::case_insensitive`，与 spec 两个场景一致，无需回改 `specs/logcore/filter/spec.md` 与任务 5.3 用例名；裸 `(?-i)` 与 `(?i)(?-i)` 同结果 → 内联按作用域覆盖。回填 D16（标题去掉「行为待实测」）、notes §6a P8。
  - 备注（③ 已定）：`(?-u)\w` 可用且对中文不命中；`(?-u)` + 属性类被拒（`Unicode not allowed here`）、`(?-u:\W)` / `(?-u:.)` / `(?-u)[a-z&&[^b]]` 被拒（`pattern can match invalid UTF-8`）、`(?-u)[a-z--b]` 可用——全部归入既有 `syntax` 拒绝，**不单列 spec 场景**，只按 D9 在任务 6.3 补一条回归用例；回填 D9、notes §6a P9。
- [x] 2.4 收口口径复核：D15 两道闸、D16 双向入规范、D17 `params` 只带 `reason`（+ 可归因时的 `index`）均已定稿并回写本文与 spec；验证：`design.md` D15–D17 无「待拍」字样（只允许整组数值与 `(?-i)` 实测这两项挂「待 2.3 结果」），`openspec validate rust-core-msg-regex --strict` 仍为 0 告警
  - 备注：D15–D17 及全文已无 `待拍 / 待定 / 待 2.3 / 待实测 / 待验` 字样（grep 为空），两项原挂「待 2.3」的口径已按实测收口（见 2.3 备注）；`openspec validate rust-core-msg-regex --strict` 通过、0 告警。
- [x] 2.5 用临时探针测编译缓存收益：16 条典型 pattern 的编译耗时、编译产物内存、同一条件重复开启的命中收益；验证：数值记入本任务备注，并校验 `design.md` D3a 的 8 条 / 8 MiB 取值是否需要下调（口径已定，只调数值；若收益可忽略则整体砍掉缓存），回填后 `openspec validate rust-core-msg-regex --strict` 仍为 0 告警
  - 备注（实测已取）：16 条满载构造 2.85–3.00 ms（冷热无差）、存活堆 293.4 KiB/条；典型 4 条 set 存活 65.8 KiB；单条 `timeout` 2.9 KiB、2000 字符字面量 101.7 KiB → 8 MiB 可容约 27 条满载，**条数闸 8 条先 binding**，满额驻留约 2.3 MiB。合成口径下省 3 ms ≈ 一次全量扫描的 1.2%。
  - **收口（评审取甲 = 砍掉缓存）**：任务 7.4 的真实样本把比值压到 **0.37%**（16 条编译 18.79 ms / 扫描 5.07 s），「取值是否下调」因此不再有意义——不是下调，是整体不做。已删 D3a 的 LRU/键/双闸口径（改为一条「不做」的决定与依据）、spec 第 7 行的「实现 MAY 复用」三分句与场景「缓存放不下时仍正常执行」、任务 5.7 的 6 条用例；design Open Questions 与 D5a 的缓存条目同步移除，`openspec validate --strict` 仍 0 告警。

## 3. 错误码 17（两表对齐）

- [x] 3.1 `crates/core/src/error.rs` 新增 `REGEX_INVALID = 17`、`code_name` 分支、`param_keys = ["reason"]`；验证：`error_code_table_values_are_stable` 通过——原断言 `code_name(17) == "UNKNOWN"` 改为 `"REGEX_INVALID"`，段间留空断言顺延到 18；`error_params_schema_is_stable` 扩一条 `(17, ["reason"])`
  - 备注：另按 D17 落 `pub const REGEX_ERROR_INDEX_KEY: &str = "index";`（与 `DIRECT_OPEN_ERROR_KEY` 同形的附注键），并在 `error_params_schema_is_stable` 断言其字面量；两测通过（`cargo test -p logcore_core --lib error::`）。
- [x] 3.2 C# 镜像 `LogcoreErrorCodes` 补 `RegexInvalid = 17` 与 `Name(17) => "REGEX_INVALID"`；验证：`BindingErrorCodeMappingTests.Code_names_match_d15_table` 扩断言后 `dotnet test --filter Code_names_match_d15_table` 通过；走查 GUI 现有错误兜底能把 17 显示为可读文本（不新增 `Loc` 文案，GUI 当前无产生正则错误的入口）
  - 备注：断言扩为 `Name(17) == "REGEX_INVALID"` + `RegexInvalid == 17`，`dotnet test logcat.slnx --filter FullyQualifiedName~Code_names_match_d15_table` 1 通过 0 失败（TEMP/TMP 指到工作区内跑）。镜像两处同步改：`logcat-viewer/Services/Logcore/LogcoreContracts.cs` 与 `logcat-next/Next.Interop/LogcoreErrorCodes.cs`（两文件头注释均声明「与 core 共用、不得漂移」）。走查结论：GUI 侧错误文案走 `LogcoreException` 的 `$"logcore 返回 {Name(code)}（{code}）"`，17 显示为 `REGEX_INVALID（17）`，无按码名的 `Loc` 分支需扩；`grep "regex"` 在 `logcat-viewer/`、`logcat-uicore/` 为空，GUI 确无正则入口。

## 4. 正则档编译与校验（开启查询期，D3/D8）

- [x] 4.1 `validate()` 放行 `msg.mode = "regex"`（仍拒 `"word"` 等未知取值），`CompiledMsg` 增加正则分支与 `always_true` 同形规则；验证：新增 `filter_expr_accepts_regex_mode`、`filter_expr_regex_mode_defaults`，改写 `filter_expr_rejects_regex_mode` 为 `filter_expr_rejects_unknown_mode` 保持红转绿
- [x] 4.2 按 D8 次序实现校验（闸口布置按 D15 拍定结果：逐条 1 MiB + 整组 4 MiB）：条数 > 16 → `too_many`、单条字符数（按 `chars()`）> 2000 → `too_long`、`regex_syntax::parse` 失败 → `syntax`、规模超限 → `too_size`，`params` 带 `reason` 与可归因时的 `index`；验证：`regex_reject_reason_syntax`、`regex_reject_reason_too_long`、`regex_reject_reason_too_many`、`regex_reject_reason_too_size`、`regex_reject_attributed_index_points_to_offending_entry`、`regex_reject_group_size_omits_index`（整组超限样本按 D15 实测取「同一 pattern `(\w+\s?){12}` 提交 16 条」：单条 595 KiB 过逐条闸、整组 9 519 KiB 被 4 MiB 闸拒）、`regex_reject_bounded_pattern_no_arg_error`
- [x] 4.3 常见形态不被上限误伤（D7 实测清单）；验证：`regex_accepts_common_forms`（含 20 分支合并正则、2000 字符纯字面量、逐字符量词串联、10 层嵌套分组、`(x|x)+y`）
- [x] 4.4 字面量短路判据与逐条路由落地（D5 + D5a：Hir 整棵为 `Concat(Literal*)` 的条目复用 `needle_matches`、其余进 `RegexSet`，`(?i)` 使 Hir 变 `Class` 因而只在区分大小写触发；全字面量批次不构造空 `RegexSet`；字面量条目在该行同一份 lossy 文本上求值）；验证：`regex_pure_literal_short_circuit_hits`、`regex_escaped_dot_equals_substring_result`、`regex_anchor_not_short_circuited`、`regex_mixed_batch_routes_per_entry`、`regex_all_literal_batch_equals_substring_mode`

- [x] 4.5 开启查询面同步（`logcore/query` 的 MODIFIED）：正则非法在 open 同步拒绝、句柄状态不变，合法正则正常建查询；验证：新增 `query_open_invalid_regex_reports_regex_invalid`、`query_open_valid_regex_builds_result`，既有 `query_open_invalid_filter_reports_field` 与 `query_open_empty_filter_is_identity` 不回归

- [x] 4.6 测试落点：新建 `crates/core/src/filter_regex_tests.rs`，照 `source.rs` 末尾 `#[path = "source_tests.rs"]` 的先例由 `filter.rs` 挂载；§4–§6 的全部新用例写在该文件，实现代码仍留在 `filter.rs`；验证：`filter.rs` 不新增 `#[test]`，拆分前后 `cargo test --workspace -- --test-threads=1` 的用例总数一致
  - §4 备注：`crates/core/src/filter.rs` 末尾的 `mod tests` 内以 `#[path = "../../filter_regex_tests.rs"] mod regex_tests;` 挂载（`#[path]` 相对父模块目录解析，故为 `../../`）；用例内 `use super::*;` 复用 `expr` / `fixture` / `lines_data` / `rows` 等私有辅助，`filter.rs` 自身不新增 `#[test]`。
  - §4 备注（落点例外）：任务 4.5 的两条用例在 `crates/core/src/query.rs` 的 `mod tests` 内——`Fixture` / `open` / `assert_err` 是该模块私有测试辅助，跨模块复用需另造一套夹具，故按就近夹具落点，不搬进 `filter_regex_tests.rs`。
  - §4 备注（实测结论）：`(\w+\s?){200}` 在真实代码路径上被逐条 1 MiB 闸拒（reason=`too_size`、index=0），`(\w+\s?){12}` × 16 条过逐条闸、被整组 4 MiB 闸拒且**不带** index——D15 两道闸与归因口径在实现内成立；`filter::tests` 由 89 → 102、`query::tests` 由 38 → 40，全绿。
  - §4 备注（D5 收口）：外层 `case` 不进 Hir，忽略大小写时纯字面量仍是 `Literal`。实现把短路限定 `case: true`（否则字面量条目走子串档全折叠，违反 spec「MUST NOT 套用本条的折叠口径」），已回写 D5 并限定其收益范围；`filter_expr_regex_mode_defaults` 同时钉住「缺省 `case` 不短路」与「全字面量批次不构造 set」。

## 5. 扫描与正文语义（D2）

- [x] 5.1 正则档按 D2 每行归一化一次、该行全部 pattern 复用（lossy 置于 pattern 循环外，代码走查确认），并保持 `scan_inner` 正文接缝不变；验证：`regex_dirty_row_matches_replacement_char`、`regex_dirty_row_spanned_by_wildcard`、`regex_multi_pattern_equals_single_pattern_union`
- [x] 5.2 空正文与空 pattern（R3/R4）；验证：`regex_empty_body_anchor_matches`、`regex_empty_body_star_matches`、`regex_empty_body_word_boundary_misses`、`regex_empty_pattern_always_true_skips_read`、`regex_empty_pattern_exclude_yields_empty`
- [x] 5.3 大小写等价（缺省折叠、区分大小写精确、pattern 内联开关双向不被外层抹掉、`ß`/`ẞ`）；验证：`regex_case_insensitive_default_hits`、`regex_case_sensitive_misses`、`regex_inline_ignore_case_overrides_outer_case`、`regex_inline_case_sensitive_overrides_default`、`regex_sharp_s_fold_equivalent`
- [x] 5.4 `op` 与 `exclude` 组合：实现只走 `RegexSet`（D4 已定，不再有合并档这条实现路径），「合并 alternation」与「逐条 `is_match`」只作为**测试内对照档**验证行集等价；验证：`regex_or_equals_merged_alternation_oracle`、`regex_or_equals_per_pattern_oracle`、`regex_and_requires_all_patterns`、`regex_exclude_keeps_misses`、`regex_value_order_independent`
- [x] 5.5 正则档沿用既有扫描不变量：块长无关、重复执行一致、只读、不回退分页；验证：`regex_chunk_size_invariant`、`regex_repeat_yields_same_result`、`regex_does_not_mutate_snapshot`
- [x] 5.6 正则档与子串档互不回归（同一文档、同批条件分别以两档提交）；验证：`regex_mode_does_not_change_substring_result`、`substring_default_mode_unchanged`
- [x] 5.7 编译缓存**不做**（评审取甲，关闭本任务而非实现它）：原口径为每源 LRU、8 条 + 8 MiB 预留、键取 pattern 原文序列 + `case`、`op`/`exclude` 不进键，对应 6 条用例（`regex_cache_repeated_open_same_result`、`regex_cache_key_ignores_op_and_exclude`、`regex_cache_key_uses_pattern_text_not_hash`、`regex_cache_budget_full_still_executes`、`regex_cache_not_shared_across_sources`、`regex_cache_miss_matches_uncached_result`）**全部取消，不写入代码**。
  - 依据：2.5 + 7.4 实测编译 16 条 18.79 ms，占真实文档全量扫描 5.07 s 的 0.37%，只在「同组条件反复开启而不重扫」时省这一档；相对要引入的每源缓存状态、原文序列键与预算闸，属为假想收益预付复杂度。
  - 连带：D3a 改写为「不做」的决定与数字，spec 删除「实现 MAY 复用已编译的匹配器」三分句与场景「缓存放不下时仍正常执行」（重复开启的行集一致性由既保留的「同一条件重复开启结果一致」场景承担），D5a 的缓存条目与 Open Questions 的缓存项一并移除。`filter.rs` 内无缓存代码，实测口径即当前实现。

## 6. 方言边界（spec「正则方言边界」）

- [x] 6.1 环视、反向引用、原子组按 `syntax` 拒绝，不静默降级；`x*+` 按量词叠加接受（实测该方言无独占量词构造，见 D9 与笔记 P12）；验证：`regex_rejects_lookahead`、`regex_rejects_lookbehind`、`regex_rejects_backreference`、`regex_rejects_atomic_group`、`regex_stacked_quantifier_is_not_possessive`、`regex_rejects_unclosed_group`
- [x] 6.2 Unicode 语义可用（`\w` 对中文正文、`\p{...}` 属性类、字符类集合运算）；验证：`regex_unicode_word_class_on_chinese`、`regex_property_class_uppercase_letter`、`regex_class_set_operation`
- [x] 6.3 内联 flag 行为可观察（`(?x)` 忽略模式内空白、`(?m)` 与 `(?mR)` 对回车换行的锚点差异、`(?-u)` 把词类降为 ASCII，见 spec 三条新场景）；另按 D9 实测钉住 `(?-u)` 下的属性类被拒（`Unicode not allowed here`）以免升级后静默变味，该条不入 spec；验证：`regex_verbose_flag_ignores_pattern_whitespace`、`regex_multiline_anchor_misses_crlf`、`regex_multiline_crlf_anchor_matches`、`regex_non_unicode_word_class_ascii_only`、`regex_non_unicode_property_class_rejected`

> **§5 / §6 实施备注**
>
> - 用例落点：§5、§6 全部新增用例在 `crates/core/src/filter_regex_tests.rs`（经 `#[path]` 挂进 `filter::tests` 的 `regex_tests` 子模块），该子模块现共 52 条。
> - 5.1 走查结论：`CompiledMsg::matches_regex` 每行只做一次 `String::from_utf8_lossy`，调用点在 `routes` 循环之外，纯字面量路由复用同一次归一结果，无二次转换；`scan_inner` 的正文接缝未动。
> - 5.2 空 pattern 的「跳过读取」只在 `case: true` 下成立（走短路路由，`always_true` 置位）；缺省忽略大小写时空 pattern 仍进整组 `RegexSet`，逐行命中，行集与短路档一致。故该用例分两段断言，与 D5 的 `case` 限定同源。
> - 5.4 对照档（合并 alternation、逐条 `is_match`）只在测试内构造，实现路径唯一为 `RegexSet`，与 D4 一致。
> - 6.1 **实测回改 spec**：原契约把「独占量词」与环视/反向引用/原子组并列为 MUST NOT，探针（笔记 P12）证伪——该方言无此构造，`a*+` 在语法层即解析为 `(a*)+` 并被接受，且因内层可空匹配而对任意正文恒命中。已同步改 spec 需求句 + 新增「量词叠加写法按叠加求值」场景、design D9 对照表与本任务验证清单，笔记 §3 对照表一并纠正。原子组 `(?>x)`（`unrecognized flag`）与环视、反向引用、未闭合分组仍按 `syntax` 拒绝，用例不变。
> - 6.3 回车换行两条场景的断言接缝是 `CompiledMsg::matches(原始字节)`：日志记录本身不含 `\n`，无法经扫描链喂入「`\r\n` + `foo` + `\r\n`」这样的多行正文，故在求值入口直接给原始字节（`(?m)^foo$` 不命中、`(?mR)^foo$` 命中），与 spec 场景的行集结论一致、只换驱动位置。
> - 6.3 另按 D9 钉住 `(?-u)\p{Lu}` 与 `(?-u:\W)` 均归 `syntax` 拒绝（不入 spec，仅防依赖升级后静默变味）。

## 7. 全量验证与文档联动

- [x] 7.1 Rust 全量按串行口径复跑 `cargo test --workspace -- --test-threads=1`；验证：基线 425/425（core 392 + cabi 33）无新增失败，报告「串行口径 N 条」与新增条数明细
  - 备注：**串行口径 478/478**（core 445 + cabi 33 + bin 目标 0），0 失败 0 跳过。新增 53 条 = `regex_tests` 子模块 52 条（§4 15 + §5 22 + §6 14 + 收口重命名 1）+ `query::tests` 2 条 − 删除 1 条（`filter_expr_rejects_regex_mode`，与既有 `filter_expr_rejects_unknown_mode` 断言冲突，见 §4 备注）。运行环境 `TEMP`/`TMP` 指向仓库内临时目录（会话沙箱限制子进程写工作区外路径）。
  - **并行口径不可用于判定**：默认并行跑时 `proxy::tests` 4 条 + `source::tests` 5 条失败（`registry_len()` / `registry_user_count()` 读到的计数被并发用例的 `shutdown()` 清表），单跑或串行即过。属既有全局登记表的并行争用，与 `docs/memory` 记录的 SingleInstance 争用同类，非本 change 引入（本卡未触碰 proxy/source 代码）。
- [x] 7.2 C# 全量 `dotnet test`；验证：基线 923/923 无新增失败（含 3.2 的断言扩充）
  - 备注：`dotnet test tests/logcat.Tests/logcat.Tests.csproj` → **923 通过 / 0 失败 / 0 跳过**，与基线同数（3.2 只扩了既有用例的断言，不新增用例）。
- [x] 7.3 更新 `docs/TESTING.md` §6 的 Rust 数字与 README×2 的徽章/当前版本行（同一批，README 两份必须同步）；验证：文档数字与 7.1/7.2 实测一致
  - 备注：§6 的串行口径改为 **478/478**（core 445 + cabi 33，2026-11-27）并补本 change 的日期条目；覆盖率同批重采（`cargo llvm-cov --workspace -- --test-threads=1`）→ 行 **92.87%**（8,896 / 9,579）、区域 93.00%、函数 91.61%，剔除 `test_proxy.rs` 后库本体 **93.19%**，库内最低 `winserver.rs` 78.21%。README×2 **不改**：两处的徽章与当前版本行只挂 C# 口径（923 / 94.26%），本卡 C# 用例数与覆盖率均未变（7.2 实测 923/923），Rust 侧数字不进 README。
- [x] 7.4 实测正则档全量扫描耗时（单条、4 条、16 条三档）并与子串档对照；验证：数值记入本任务备注，16 条上限下的耗时落在可接受口径内（否则回到 D7 调整条数上限并同步 spec）。首选 176 MB / 272 万行的网络共享样本；该样本不可用时**复用 `crates/core/src/sample_gen.rs` 的确定性样本（`BIG_BYTES = 40 MiB`，固定种子，`source_tests.rs` 已在用）折算**，不另造新样本口径；备注须写明用的是哪一口径，且 40 MiB 的数值折算到 272 万行时按约 1/4.5 的比值换算、不得混读。本任务为**一次性实测**：数值只进备注与 `design.md` D4，不写成 `#[test]`、不引入 `#[ignore]`（口径见 D11）
  - 备注（一次性实测，样本口径 = **网络共享真实文档**，非 40 MiB 离线兜底）：`\\` 共享上的 184 549 376 B / **3 063 982 行** 样本（该样本比原记录的 176 MB / 272 万行又长了一截，行数以本次实测为准），Release 探针驱动 `query::open` 全量扫描，16 条取 D7 保留清单规模的混型 pattern。
  - 装载（解析 + 索引，冷缓存）21.79 s。逐档「开启」= 编译 + 扫描，`≈扫描` 为扣除单列编译耗时后的估值：

    | 档 | 条数 | 编译 | 开启 | ≈扫描 | ns/行 | 命中行 |
    | --- | --- | --- | --- | --- | --- | --- |
    | 子串 | 1 | 0.10 ms | 0.644 s | 0.644 s | 210 | 0 |
    | 子串 | 4 | 0.01 ms | 1.490 s | 1.490 s | 486 | 15 |
    | 子串 | 16 | 0.01 ms | 5.311 s | 5.311 s | 1734 | 20 745 |
    | 正则 | 1 | 3.38 ms | 1.186 s | 1.182 s | 386 | 0 |
    | 正则 | 4 | 5.43 ms | 2.632 s | 2.627 s | 857 | 11 816 |
    | 正则 | 16 | 18.79 ms | 5.091 s | 5.072 s | 1655 | 44 201 |

  - 结论①：**16 条上限的正则档全量扫描 5.07 s，与同条数子串档 5.31 s 相当（0.95×）**，落在可接受口径内，D7 的 16 条上限无需回调。
  - 结论②：正则档**并非与条数无关**——`RegexSet` 单遍仍随条数近似线性增长（1→16 条 1.18→5.07 s，边际 ≈ 0.26 s/条；子串档边际 ≈ 0.31 s/条）。D4「正则档不存在每词一轮全量扫描缺口」只在**同一行只取一次正文**这一层成立，每行成本仍按条数上升；已按实测在 D4 补注，不外推为"条数免费"。
  - 结论③：ns/行 1655（真实行宽 ≈ 60 B）对探针合成口径 1281（45 B 短行）同量级，未见显著劣化，D4 的折算不必下调。
  - 结论④：**编译成本占全量扫描 0.37%**（16 条 18.79 ms / 5.07 s），比 P10 的合成口径（1.2%）还低一个档——缓存能省的绝对量在这里更不是瓶颈，是 D3a「保留 vs 砍掉」偏向砍掉的新增实测依据。
  - 探针落 `.tmp-test/probe74`（git-ignored，用完即弃），不写成 `#[test]`、不引入 `#[ignore]`（口径见 D11）。数值已回填 `design.md` D4。
- [x] 7.5 探索笔记收口：把 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` §7 的「待拍板」各项改写为本 change 定稿后的最终状态（含 2.2/2.3/2.5 的新实测），删除被否决选项的铺陈与「未拍」字样；验证：该节不再自称「本文不下结论」，且数值与 `design.md` D1/D7/D15 一致
  - 备注：§7 改写为「决策收口（本 change 定稿口径）」11 条，只留现行结论与被证伪的事实（`x*+` 非独占量词、只裁 `regex` 是空操作），删除评审批次的甲乙编号与「未拍」铺陈；文头状态 → 已实施待验收、日期 → 2026-11-27；§5 长度上限由「倾向」改「取」。数值对齐：dll 增量 1.54 MiB / 阈值 1.6 MiB（D1）、16 条与 2000 字符（D7）、逐条 1 MiB + 整组 4 MiB（D15）。

## 8. 收口

- [x] 8.1 wt 合入目标分支：`git merge wt/rust-core-msg-regex` 合入 **`master`**；验证：`git log master` 出现该合并、`git status` 干净；worktree 清除随归档执行（本任务只记合入完成）
  - 备注：`wt/rust-core-msg-regex`（实现提交 `305b40a`）已以 `--no-ff` 合入 `master`。合入后主树与已测 wt 树在 `crates` / `tests` / `logcat-*` / `Cargo.lock` / `docs` 上逐字节相同（`git diff` 为空），故 7.1/7.2 的 478/478 与 923/923 口径直接成立；`openspec validate --strict` 在主树复跑仍为 0 warning。
- [x] 8.2 交付验收材料（本卡无 UI 面，不做 GUI 手测）：汇总 7.1/7.2 的口径数字与 2.2/2.3/2.5/7.4 的四项实测数值，交用户验收；验证：AI 不自行判定「验收通过」，等用户明示
  - 备注：验收材料已交付（Rust 串行 478/478、C# 923/923、覆盖率行 92.87%、dll Release 增量 1.54 MiB、整组闸实测、缓存砍掉后的编译占比 0.37%、真实文档三档对照）；本卡无 UI 面。

## Workflow follow-up

- 本 change 目录与探索笔记 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` 目前是未跟踪文件，另有 `openspec/notes/FILTER-PREFILTER-AND-RUST.md` 的未提交改动；三者需用户当轮授权后才 `git add` + commit，归档那批默认要求它们已在 `master` 上。
- 用户明确「验收通过」后追问是否归档；仅在用户明示「可以归档 rust-core-msg-regex」时执行 `openspec archive`。
- 归档同批：把探索笔记 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` 以 `git mv` 移入 `openspec/notes/archive/`（加归档日期前缀），并 `git worktree remove` 清除 `wt/rust-core-msg-regex`。
- 归档门禁：8.1 已关闭且 `wt/rust-core-msg-regex` 已合入 `master`；tasks 存在未关闭项时不得归档。
- 本 change 不含发版动作（版本号 5 处同步与打 tag 需另行当轮授权）。
- 任务 2.3 / 2.5 / 7.4 的临时探针**用完即弃**：留在 git-ignored 目录、不入库、不注册为 `crates/cli` 子命令；要留下的只有实测数据（tasks 备注）与据此回填的 `design.md` 取值。
