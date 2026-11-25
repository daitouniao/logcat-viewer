# Tasks

## 1. 前置与 worktree

- [ ] 1.1 在独立 git worktree 中实施本 change：worktree 标识 = 分支 `wt/rust-core-msg-regex`（路径以 `git worktree list` 定位，禁写本机绝对路径），目标分支 `master`；验证：`git worktree list` 列出该分支、主工作树 `git status` 保持干净
- [ ] 1.2 收口三项评审要点并按结论实施：D1 feature 面（偏向默认全开）、D10 折叠差异（偏向不入 spec）、D7 条数上限（偏向 16）；验证：结论以文字回写 `design.md` 对应决策，改判时先改设计再编码

## 2. 依赖与体积基线

- [ ] 2.1 `crates/core/Cargo.toml` 增加 `regex = "1.11"` 与 `regex-syntax = "0.8"`（默认 feature，D1）；验证：`cargo build` 成功，`Cargo.lock` 由 27 包增至 31 包（新增 `aho-corasick` / `memchr` / `regex-automata` / `regex-syntax`）
- [ ] 2.2 实测带依赖重编后的 `logcore.dll` 增量，并与 D1 备选乙（`perf` + `unicode-case` + `unicode-perl`）对比；验证：给出 debug 与 Release 两口径的字节数，数值记入本任务备注，结论决定是否触发体积告警

## 3. 错误码 17（两表对齐）

- [ ] 3.1 `crates/core/src/error.rs` 新增 `REGEX_INVALID = 17`、`code_name` 分支、`param_keys = ["reason"]`；验证：`error_code_table_values_are_stable` 通过——原断言 `code_name(17) == "UNKNOWN"` 改为 `"REGEX_INVALID"`，段间留空断言顺延到 18；`error_params_schema_is_stable` 扩一条 `(17, ["reason"])`
- [ ] 3.2 C# 镜像 `LogcoreErrorCodes` 补 `RegexInvalid = 17` 与 `Name(17) => "REGEX_INVALID"`；验证：`BindingErrorCodeMappingTests.Code_names_match_d15_table` 扩断言后 `dotnet test --filter Code_names_match_d15_table` 通过

## 4. 正则档编译与校验（开启查询期，D3/D8）

- [ ] 4.1 `validate()` 放行 `msg.mode = "regex"`（仍拒 `"word"` 等未知取值），`CompiledMsg` 增加正则分支与 `always_true` 同形规则；验证：新增 `filter_expr_accepts_regex_mode`、`filter_expr_regex_mode_defaults`，改写 `filter_expr_rejects_regex_mode` 为 `filter_expr_rejects_unknown_mode` 保持红转绿
- [ ] 4.2 按 D8 次序实现四因校验：条数 > 16 → `too_many`、单条字符数（按 `chars()`）> 2000 → `too_long`、`regex_syntax::parse` 失败 → `syntax`、`size_limit = 1 MiB` 构造失败 → `too_size`，`params` 带 `reason` 与可归因时的 `index`；验证：`regex_reject_reason_syntax`、`regex_reject_reason_too_long`、`regex_reject_reason_too_many`、`regex_reject_reason_too_size`、`regex_reject_bounded_pattern_no_arg_error`
- [ ] 4.3 常见形态不被上限误伤（D7 实测清单）；验证：`regex_accepts_common_forms`（含 20 分支合并正则、2000 字符纯字面量、逐字符量词串联、10 层嵌套分组、`(x|x)+y`）
- [ ] 4.4 字面量短路判据落地（D5：Hir 整棵为 `Concat(Literal*)` 时整条复用 `needle_matches`，`(?i)` 使 Hir 变 `Class` 因而只在区分大小写触发）；验证：`regex_pure_literal_short_circuit_hits`、`regex_escaped_dot_equals_substring_result`、`regex_anchor_not_short_circuited`、`regex_mixed_values_skip_short_circuit`

- [ ] 4.5 开启查询面同步（`logcore/query` 的 MODIFIED）：正则非法在 open 同步拒绝、句柄状态不变，合法正则正常建查询；验证：新增 `query_open_invalid_regex_reports_regex_invalid`、`query_open_valid_regex_builds_result`，既有 `query_open_invalid_filter_reports_field` 与 `query_open_empty_filter_is_identity` 不回归

## 5. 扫描与正文语义（D2）

- [ ] 5.1 正则档按 D2 每行归一化一次、该行全部 pattern 复用（lossy 置于 pattern 循环外，代码走查确认），并保持 `scan_inner` 正文接缝不变；验证：`regex_dirty_row_matches_replacement_char`、`regex_dirty_row_spanned_by_wildcard`、`regex_multi_pattern_equals_single_pattern_union`
- [ ] 5.2 空正文与空 pattern（R3/R4）；验证：`regex_empty_body_anchor_matches`、`regex_empty_body_star_matches`、`regex_empty_body_word_boundary_misses`、`regex_empty_pattern_always_true_skips_read`、`regex_empty_pattern_exclude_yields_empty`
- [ ] 5.3 大小写等价（缺省折叠、区分大小写精确、pattern 内联开关不被外层抹掉、`ß`/`ẞ`）；验证：`regex_case_insensitive_default_hits`、`regex_case_sensitive_misses`、`regex_inline_flag_overrides_outer_case`、`regex_sharp_s_fold_equivalent`
- [ ] 5.4 `op` 与 `exclude` 组合：`RegexSet` 位集与「合并 alternation」「逐条 `is_match`」两种对照档给出同一行集，`and`/`or`/排除/顺序无关；验证：`regex_or_equals_merged_alternation`、`regex_and_requires_all_patterns`、`regex_exclude_keeps_misses`、`regex_value_order_independent`
- [ ] 5.5 正则档沿用既有扫描不变量：块长无关、重复执行一致、只读、不回退分页；验证：`regex_chunk_size_invariant`、`regex_repeat_yields_same_result`、`regex_does_not_mutate_snapshot`
- [ ] 5.6 正则档与子串档互不回归（同一文档、同批条件分别以两档提交）；验证：`regex_mode_does_not_change_substring_result`、`substring_default_mode_unchanged`

## 6. 方言边界（spec「正则方言边界」）

- [ ] 6.1 环视、反向引用、原子组、独占量词按 `syntax` 拒绝，不静默降级；验证：`regex_rejects_lookahead`、`regex_rejects_lookbehind`、`regex_rejects_backreference`、`regex_rejects_atomic_group`、`regex_rejects_possessive`、`regex_rejects_unclosed_group`
- [ ] 6.2 Unicode 语义可用（`\w` 对中文正文、`\p{...}` 属性类、字符类集合运算）；验证：`regex_unicode_word_class_on_chinese`、`regex_property_class_uppercase_letter`、`regex_class_set_operation`

## 7. 全量验证与文档联动

- [ ] 7.1 Rust 全量按串行口径复跑 `cargo test --workspace -- --test-threads=1`；验证：基线 425/425（core 392 + cabi 33）无新增失败，报告「串行口径 N 条」与新增条数明细
- [ ] 7.2 C# 全量 `dotnet test`；验证：基线 923/923 无新增失败（含 3.2 的断言扩充）
- [ ] 7.3 更新 `docs/TESTING.md` §6 的 Rust 数字与 README×2 的徽章/当前版本行（同一批，README 两份必须同步）；验证：文档数字与 7.1/7.2 实测一致
- [ ] 7.4 在 176 MB / 272 万行 SMB 测试文件上实测正则档全量扫描耗时（单条、4 条、16 条三档）并与子串档对照；验证：数值记入本任务备注，16 条上限下的耗时落在可接受口径内（否则回到 D7 调整条数上限并同步 spec）

## 8. 收口

- [ ] 8.1 wt 合入目标分支：`git merge wt/rust-core-msg-regex` 合入 **`master`**；验证：`git log master` 出现该合并、`git status` 干净；worktree 清除随归档执行（本任务只记合入完成）

## Workflow follow-up

- 用户明确「验收通过」后追问是否归档；仅在用户明示「可以归档 rust-core-msg-regex」时执行 `openspec archive`。
- 归档同批：把探索笔记 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` 以 `git mv` 移入 `openspec/notes/archive/`（加归档日期前缀），并 `git worktree remove` 清除 `wt/rust-core-msg-regex`。
- 归档门禁：8.1 已关闭且 `wt/rust-core-msg-regex` 已合入 `master`；tasks 存在未关闭项时不得归档。
- 本 change 不含发版动作（版本号 5 处同步与打 tag 需另行当轮授权）。
