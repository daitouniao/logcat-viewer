# Tasks

## 1. 准备与基线

- [ ] 1.1 在独立 worktree 中实施：分支 `wt/rust-core-filter-kernel`（路径以 `git worktree list` 定位，任务内不写本机绝对路径）；确认 worktree 内 `cargo test --workspace -- --test-threads=1` 基线全绿且条数与 `docs/TESTING.md` 现有 Rust 记录一致（285 条），并确认 `cargo llvm-cov` 可调用
- [ ] 1.2 记录 C# 侧基线：`cargo build` 后运行 `dotnet test tests/logcat.Tests/logcat.Tests.csproj`，确认与当前记录无新增失败（本 change 不改 C#，此基线用于回归对比）

## 2. 条件模型与校验

- [ ] 2.1 新增 `crates/core/src/filter.rs` 并在 `lib.rs` 挂载：`FilterExpr` 按 design D2 的 JSON schema（camelCase、缺省值、枚举字面量、`deny_unknown_fields`）反序列化，提供 `from_json` 与「归一后是否为空」；JSON 类型或形状错误统一 `INVALID_ARG` + `params.field = "filter"`；UT `filter_expr_defaults_are_empty`、`filter_expr_parses_full_expression`、`filter_expr_parses_tag_exact_match`、`filter_expr_default_match_op_mode`、`filter_expr_rejects_unknown_field`、`filter_expr_rejects_type_mismatch`、`filter_expr_rejects_unsupported_category` 通过
- [ ] 2.2 解析后校验枚举与范围（级别 1–7、分钟 0–59、进程号/线程号 ≥ 0、tag 值非空），返回 `INVALID_ARG` + `params.field`；越界值至少覆盖 0、8、300（级别）与 60、300（分钟），确认归因到 `levels` / `minutes` 而非 `filter`：UT `filter_expr_rejects_unknown_op`、`filter_expr_rejects_unknown_tag_match`、`filter_expr_rejects_unknown_mode`、`filter_expr_rejects_minute_out_of_range`、`filter_expr_rejects_regex_mode`、`filter_expr_rejects_level_out_of_range`、`filter_expr_rejects_negative_pid_tid`、`filter_expr_rejects_empty_tag_value` 通过
- [ ] 2.3 实现 `CompiledFilter` 归一（design D6/D8/D9）：级别空或 `{1..7}` 视为未启用、分钟 0–59 布尔表、pid/tid 集合、tag 字典命中 id 集合（子串/整串、and/or、忽略大小写折叠）。UT `compile_levels_all_equivalent_to_disabled`、`compile_empty_pid_tid_disabled`、`compile_tag_exact_and_distinct_values_empty`、`compile_tag_exact_and_case_fold_equivalent_hits`、`compile_tag_substring_case_fold`、`compile_tag_exact_case_fold` 通过

## 3. 结构化扫描与结果集

- [ ] 3.1 实现 `scan` 的结构化阶段（level → pid → tid → minute），候选保持文档升序；无任何有效条件返回恒等视图。UT `filter_empty_returns_identity`、`filter_level_all_levels_keeps_unknown_level_row`、`filter_level_set_any`、`filter_pid_exclude_keeps_unknown_pid`、`filter_tid_exclude_keeps_unknown_tid`、`filter_minute_skips_missing_timestamp`、`filter_conditions_are_anded` 通过
- [ ] 3.2 接入 tag 行级判定与排除：`tag_ids = -1` 在启用时不命中、排除时保留。UT `filter_tag_substring_or`、`filter_tag_substring_and`、`filter_tag_exact_or`、`filter_tag_exact_all_values_empty`、`filter_tag_exact_and_case_fold_equivalent_hits`、`filter_tag_exclude_keeps_missing_tag`、`filter_tag_exclude_removes_hit`、`filter_tag_case_insensitive`、`filter_tag_exact_case_fold` 通过
- [ ] 3.3 产出 `ResultSet`：命中用 `from_rows`（严格升序、去重、元素小于文档行数）；文档行数超过 `u32::MAX` 时与 `read_page` 同口径 `debug_assert`；无命中为空，空行与横幅行不入集。UT `filter_result_rows_ascending_unique`、`filter_result_contains_only_records`、`filter_no_hit_returns_empty_rows` 通过

## 4. 正文回读与匹配

- [ ] 4.1 实现按块顺序回读（design D4）：默认 256 KiB 缓冲、候选稀疏时跳读、行大于块长时按行长读；块长与 `doc_end = max(offs + lens)` 夹取，避免末行越界读；新增仅测试用的块长入口；`scan` 接收源显示路径供错误参数使用；把 `source.rs` 的 `io_error` 上移到 `error.rs`（`pub(crate)`）供两条路径共用；正文条件未启用时不回读正文。UT `filter_text_spans_chunk_boundary`、`filter_text_sparse_candidates_skip_gaps`、`filter_text_row_larger_than_chunk`、`filter_text_last_row_with_default_chunk`、`filter_text_reads_message_slice_only`、`filter_chunk_size_does_not_change_result`、`filter_structured_only_skips_text_read` 通过
- [ ] 4.2 正文作用域与空正文：从消息起始偏移到行尾，行尾不含 `\r` / `\n`，行头不参与；`moff = 0` 的行整行视为正文；空正文按长度为 0 的正文参与，空词命中、非空词不命中。UT `filter_msg_scope_ignores_header`、`filter_msg_empty_body_with_empty_needle`、`filter_msg_empty_body_with_nonempty_needle`、`filter_msg_empty_needle_and_mode_skips`、`filter_msg_continuation_matches_itself` 通过
- [ ] 4.3 大小写等价（design D5）：区分大小写按原始字节；忽略大小写按折叠后的 needle 选路径，ASCII 字节折叠、非 ASCII 行 Unicode 小写化、非法 UTF-8 走替换字符。UT `filter_msg_case_sensitive`、`filter_msg_case_insensitive_ascii`、`filter_msg_eszett_equivalent`、`filter_msg_kelvin_sign_folds_to_ascii`、`filter_msg_turkish_dotless_i_not_equal`、`filter_msg_accent_not_equal`、`filter_msg_non_ascii_fold`、`filter_msg_lossy_invalid_utf8` 通过
- [ ] 4.4 正文多值与排除（design D6）：or/and、空词、排除保留未命中与空正文；空词条件不归一为未启用，全空词/任一空词 + 排除结果为空集。UT `filter_msg_or`、`filter_msg_and`、`filter_msg_empty_needle_or_hits_all`、`filter_msg_and_all_empty_hits_all`、`filter_msg_empty_needle_or_exclude_returns_empty`、`filter_msg_all_empty_and_exclude_returns_empty`、`filter_msg_exclude_keeps_nonmatch`、`filter_msg_exclude_keeps_empty_body` 通过

## 5. 错误、一致性与集成

- [ ] 5.1 回读失败与装载内容一致性：截断返回 `IO_ERROR`（带源路径与 OS 错误、无部分结果集）；改名轮转、同路径替换、删除后仍按装载内容判定。UT `filter_text_truncated_returns_io_error`、`filter_text_error_carries_path_and_os_error`、`filter_after_rename_reads_loaded_content`、`filter_after_replacement_reads_loaded_content`、`filter_after_deletion_reads_loaded_content` 通过
- [ ] 5.2 与既有分页/导航组合：锚点定位用 `resolve` 求 `ord`（命中/平局取前/越界首末/空结果），顺序翻页用「第一条 `docRow ≥ start`」求 `ord`（超过末条结果行返回空页）；过滤结果按真实文档行起页、顺序翻页不重复、空结果集空页、页内文本与源一致。UT `filter_result_page_anchor_exact_hit`、`filter_result_page_anchor_nearest`、`filter_result_page_anchor_past_end_takes_last`、`filter_result_page_empty_result_empty`、`filter_result_page_forward_no_repeat`、`filter_result_page_forward_past_end_empty`、`filter_result_page_tail_short`、`filter_result_page_text_matches_source`、`filter_result_resolve_exact_hit`、`filter_result_resolve_nearest_tie_previous`、`filter_result_resolve_clamps_ends`、`filter_result_resolve_empty_none` 通过
- [ ] 5.3 只读性：同一条件重复执行结果一致，过滤前后对同一 `Payload`/`File` 的分页读取字节不变、`Payload` 不被修改。UT `filter_repeat_returns_same_rows`、`filter_does_not_mutate_payload`、`filter_page_bytes_unchanged_after_filter` 通过
- [ ] 5.4 跑 `cargo llvm-cov -- --test-threads=1`，按实测同批更新 `docs/TESTING.md` 的 Rust 用例数与覆盖率（保留 cabi 测试代理剔除口径）；核对 README 如有 Rust 数字则联动，C# 侧未变不动

## 6. 全量验证与合入

- [ ] 6.1 在 worktree 跑全量：`cargo build` + `cargo test --workspace -- --test-threads=1` + `dotnet test tests/logcat.Tests/logcat.Tests.csproj` + `bash scripts/run-tests-en-us.sh`（Release + en-US，与 CI 同口径）；确认受影响用例全过、全量无新增失败并记录条数
- [ ] 6.2 将 `wt/rust-core-filter-kernel` 合入目标分支 `master`（`git merge`），在 master 上重跑 6.1 的全部命令并确认结果一致
- [ ] 6.3 `openspec validate rust-core-filter-kernel --strict` 通过；回读四件产物与实现无未覆盖的需求场景，列出覆盖对照

## Workflow follow-up

- 用户验收通过后，按 AGENTS.md §2.3 指示归档 `rust-core-filter-kernel`，并同批清除该 worktree。
- 本 change 未新增探索笔记；归档时不涉及笔记迁移。