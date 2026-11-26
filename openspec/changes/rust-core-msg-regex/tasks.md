# Tasks

## 1. 前置与 worktree

- [ ] 1.1 在独立 git worktree 中实施本 change：worktree 标识 = 分支 `wt/rust-core-msg-regex`（路径以 `git worktree list` 定位，禁写本机绝对路径），目标分支 `master`；验证：`git worktree list` 列出该分支、主工作树 `git status` 保持干净
- [ ] 1.2 复核已定口径与本文一致（feature 全开、16 条 / 2000 字符、`reason` 四值、C# 只加码常量不新增 IT、折叠差异不入 spec、原条目改名承接）；验证：`design.md` D1/D6/D7/D10/D13 无「未拍」标记，且 D15–D17 已按评审结论收口或转为实测确认项

## 2. 依赖与体积基线

- [ ] 2.1 `crates/core/Cargo.toml` 增加 `regex = "1.13"` 与 `regex-syntax = "0.8"`（默认 feature，D1）；验证：`cargo build` 成功，`Cargo.lock` 由 27 包增至 31 包（新增 `aho-corasick` / `memchr` / `regex-automata` / `regex-syntax`），解析到的 `regex` 版本不低于 1.13
- [ ] 2.2 实测带依赖重编后的 `logcore.dll` 增量，并与 D1 备选乙（`perf` + `unicode-case` + `unicode-perl`）对比；验证：给出 debug 与 Release 两口径的字节数，数值记入本任务备注，结论决定是否触发体积告警

- [ ] 2.3 用临时探针（git-ignored，不落仓库）实测两项并把结论回填 `design.md`：① `RegexSetBuilder::size_limit` 按整组产物还是逐条计数，给出 16 条满载下 D7 保留清单的构造结果与峰值内存，据此定整组兜底数值（起始候选 4 MiB）；② 内联大小写开关双向实测（外层 `case:false` + pattern 内 `(?i)`、外层缺省 + pattern 内 `(?-i)`）。验证：D15 的整组数值有实测依据；D16 的两个 spec 场景与实测一致，不符则先回改 `specs/logcore/filter/spec.md` 与任务 5.3 的用例名，并把结论写回 `openspec/notes/RUST-CORE-REGEX-DIALECT.md`
- [ ] 2.4 收口口径复核：D15 两道闸、D16 双向入规范、D17 `params` 只带 `reason`（+ 可归因时的 `index`）均已定稿并回写本文与 spec；验证：`design.md` D15–D17 无「待拍」字样（只允许整组数值与 `(?-i)` 实测这两项挂「待 2.3 结果」），`openspec validate rust-core-msg-regex --strict` 仍为 0 告警
- [ ] 2.5 用临时探针测编译缓存收益：16 条典型 pattern 的编译耗时、编译产物内存、同一条件重复开启的命中收益；验证：回填 `design.md` D3a 的缓存归属、键、淘汰策略与容量，明确是否采用 LFU、是否需要字节预算，`openspec validate rust-core-msg-regex --strict` 仍为 0 告警

## 3. 错误码 17（两表对齐）

- [ ] 3.1 `crates/core/src/error.rs` 新增 `REGEX_INVALID = 17`、`code_name` 分支、`param_keys = ["reason"]`；验证：`error_code_table_values_are_stable` 通过——原断言 `code_name(17) == "UNKNOWN"` 改为 `"REGEX_INVALID"`，段间留空断言顺延到 18；`error_params_schema_is_stable` 扩一条 `(17, ["reason"])`
- [ ] 3.2 C# 镜像 `LogcoreErrorCodes` 补 `RegexInvalid = 17` 与 `Name(17) => "REGEX_INVALID"`；验证：`BindingErrorCodeMappingTests.Code_names_match_d15_table` 扩断言后 `dotnet test --filter Code_names_match_d15_table` 通过；走查 GUI 现有错误兜底能把 17 显示为可读文本（不新增 `Loc` 文案，GUI 当前无产生正则错误的入口）

## 4. 正则档编译与校验（开启查询期，D3/D8）

- [ ] 4.1 `validate()` 放行 `msg.mode = "regex"`（仍拒 `"word"` 等未知取值），`CompiledMsg` 增加正则分支与 `always_true` 同形规则；验证：新增 `filter_expr_accepts_regex_mode`、`filter_expr_regex_mode_defaults`，改写 `filter_expr_rejects_regex_mode` 为 `filter_expr_rejects_unknown_mode` 保持红转绿
- [ ] 4.2 按 D8 次序实现校验（闸口布置按 D15 拍定结果）：条数 > 16 → `too_many`、单条字符数（按 `chars()`）> 2000 → `too_long`、`regex_syntax::parse` 失败 → `syntax`、规模超限 → `too_size`，`params` 带 `reason` 与可归因时的 `index`；验证：`regex_reject_reason_syntax`、`regex_reject_reason_too_long`、`regex_reject_reason_too_many`、`regex_reject_reason_too_size`、`regex_reject_attributed_index_points_to_offending_entry`、`regex_reject_group_size_omits_index`、`regex_reject_bounded_pattern_no_arg_error`
- [ ] 4.3 常见形态不被上限误伤（D7 实测清单）；验证：`regex_accepts_common_forms`（含 20 分支合并正则、2000 字符纯字面量、逐字符量词串联、10 层嵌套分组、`(x|x)+y`）
- [ ] 4.4 字面量短路判据落地（D5：Hir 整棵为 `Concat(Literal*)` 时整条复用 `needle_matches`，`(?i)` 使 Hir 变 `Class` 因而只在区分大小写触发）；验证：`regex_pure_literal_short_circuit_hits`、`regex_escaped_dot_equals_substring_result`、`regex_anchor_not_short_circuited`、`regex_mixed_values_skip_short_circuit`

- [ ] 4.5 开启查询面同步（`logcore/query` 的 MODIFIED）：正则非法在 open 同步拒绝、句柄状态不变，合法正则正常建查询；验证：新增 `query_open_invalid_regex_reports_regex_invalid`、`query_open_valid_regex_builds_result`，既有 `query_open_invalid_filter_reports_field` 与 `query_open_empty_filter_is_identity` 不回归

## 5. 扫描与正文语义（D2）

- [ ] 5.1 正则档按 D2 每行归一化一次、该行全部 pattern 复用（lossy 置于 pattern 循环外，代码走查确认），并保持 `scan_inner` 正文接缝不变；验证：`regex_dirty_row_matches_replacement_char`、`regex_dirty_row_spanned_by_wildcard`、`regex_multi_pattern_equals_single_pattern_union`
- [ ] 5.2 空正文与空 pattern（R3/R4）；验证：`regex_empty_body_anchor_matches`、`regex_empty_body_star_matches`、`regex_empty_body_word_boundary_misses`、`regex_empty_pattern_always_true_skips_read`、`regex_empty_pattern_exclude_yields_empty`
- [ ] 5.3 大小写等价（缺省折叠、区分大小写精确、pattern 内联开关双向不被外层抹掉、`ß`/`ẞ`）；验证：`regex_case_insensitive_default_hits`、`regex_case_sensitive_misses`、`regex_inline_ignore_case_overrides_outer_case`、`regex_inline_case_sensitive_overrides_default`、`regex_sharp_s_fold_equivalent`
- [ ] 5.4 `op` 与 `exclude` 组合：`RegexSet` 位集与「合并 alternation」「逐条 `is_match`」两种对照档给出同一行集，`and`/`or`/排除/顺序无关；验证：`regex_or_equals_merged_alternation`、`regex_and_requires_all_patterns`、`regex_exclude_keeps_misses`、`regex_value_order_independent`
- [ ] 5.5 正则档沿用既有扫描不变量：块长无关、重复执行一致、只读、不回退分页；验证：`regex_chunk_size_invariant`、`regex_repeat_yields_same_result`、`regex_does_not_mutate_snapshot`
- [ ] 5.6 正则档与子串档互不回归（同一文档、同批条件分别以两档提交）；验证：`regex_mode_does_not_change_substring_result`、`substring_default_mode_unchanged`
- [ ] 5.7 编译缓存按 D3a 落地（每源、LRU、8 条 + 8 MiB 预留、`op`/`exclude` 不进键）：缓存只能省编译、不得改变行为；验证：`regex_cache_repeated_open_same_result`、`regex_cache_key_ignores_op_and_exclude`、`regex_cache_budget_full_still_executes`、`regex_cache_not_shared_across_sources`、`regex_cache_miss_matches_uncached_result`

## 6. 方言边界（spec「正则方言边界」）

- [ ] 6.1 环视、反向引用、原子组、独占量词按 `syntax` 拒绝，不静默降级；验证：`regex_rejects_lookahead`、`regex_rejects_lookbehind`、`regex_rejects_backreference`、`regex_rejects_atomic_group`、`regex_rejects_possessive`、`regex_rejects_unclosed_group`
- [ ] 6.2 Unicode 语义可用（`\w` 对中文正文、`\p{...}` 属性类、字符类集合运算）；验证：`regex_unicode_word_class_on_chinese`、`regex_property_class_uppercase_letter`、`regex_class_set_operation`

## 7. 全量验证与文档联动

- [ ] 7.1 Rust 全量按串行口径复跑 `cargo test --workspace -- --test-threads=1`；验证：基线 425/425（core 392 + cabi 33）无新增失败，报告「串行口径 N 条」与新增条数明细
- [ ] 7.2 C# 全量 `dotnet test`；验证：基线 923/923 无新增失败（含 3.2 的断言扩充）
- [ ] 7.3 更新 `docs/TESTING.md` §6 的 Rust 数字与 README×2 的徽章/当前版本行（同一批，README 两份必须同步）；验证：文档数字与 7.1/7.2 实测一致
- [ ] 7.4 实测正则档全量扫描耗时（单条、4 条、16 条三档）并与子串档对照；验证：数值记入本任务备注，16 条上限下的耗时落在可接受口径内（否则回到 D7 调整条数上限并同步 spec）。首选 176 MB / 272 万行的网络共享样本；该样本不可用时降级为 200k 行 × 约 80 B 的合成样本折算，并在备注写明用的是哪一口径，不得混读

## 8. 收口

- [ ] 8.1 wt 合入目标分支：`git merge wt/rust-core-msg-regex` 合入 **`master`**；验证：`git log master` 出现该合并、`git status` 干净；worktree 清除随归档执行（本任务只记合入完成）

## Workflow follow-up

- 本 change 目录与探索笔记 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` 目前是未跟踪文件，另有 `openspec/notes/FILTER-PREFILTER-AND-RUST.md` 的未提交改动；三者需用户当轮授权后才 `git add` + commit，归档那批默认要求它们已在 `master` 上。
- 用户明确「验收通过」后追问是否归档；仅在用户明示「可以归档 rust-core-msg-regex」时执行 `openspec archive`。
- 归档同批：把探索笔记 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` 以 `git mv` 移入 `openspec/notes/archive/`（加归档日期前缀），并 `git worktree remove` 清除 `wt/rust-core-msg-regex`。
- 归档门禁：8.1 已关闭且 `wt/rust-core-msg-regex` 已合入 `master`；tasks 存在未关闭项时不得归档。
- 本 change 不含发版动作（版本号 5 处同步与打 tag 需另行当轮授权）。
