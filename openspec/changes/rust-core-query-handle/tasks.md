# Tasks

## 1. 准备与基线

- [ ] 1.1 在独立 worktree 中实施：分支 `wt/rust-core-query-handle`（路径以 `git worktree list` 定位，任务内不写本机绝对路径）；确认 worktree 内 `cargo build` + `cargo test --workspace -- --test-threads=1` 基线全绿且条数与 `docs/TESTING.md` 现有 Rust 记录一致（373 条：core 347 + cabi 26），并确认 `cargo llvm-cov` 可调用
- [ ] 1.2 记录 C# 基线：`dotnet test tests/logcat.Tests/logcat.Tests.csproj`，确认与当前记录一致（915 条、无失败）

## 2. core 查询句柄

- [ ] 2.1 在 `crates/core/src/state.rs` 新增 `Op::OpenQuery`：仅 `Loaded` 放行，`Closed` 返回 `CLOSED`，其余 `INVALID_STATE`；同步更新 `state_matrix_matches_d23` 的逐格表与操作名；UT `state_open_query_only_loaded` 通过
- [ ] 2.2 新增 `crates/core/src/query.rs` 并在 `lib.rs` 挂载：`QueryHandle` 持有 `Arc<Payload>` / `Arc<File>` / 显示路径 / `ResultSet`；`SourceHandle` 增加取装载快照的 crate 内方法（状态门禁走 `Op::OpenQuery`），开启时用 `filter::scan` 产出结果集；UT `query_open_empty_filter_is_identity`、`query_open_filtered_row_count`、`query_open_invalid_filter_reports_field`、`query_open_requires_loaded`、`query_open_after_close_returns_closed`、`query_two_queries_independent` 通过
- [ ] 2.3 结果集增加两个定位原语：向后取第一条文档行号不小于起点的结果序号；向前取文档行号严格小于终点的结果条数（恒等分支不物化数组）：UT `result_forward_ord_rows`、`result_forward_ord_identity`、`result_forward_ord_past_end_none`、`result_count_before_rows`、`result_count_before_identity`、`result_count_before_zero_none` 通过

## 3. core 读取：行数、定位、分页

- [ ] 3.1 实现 `QueryHandle::row_count` 与 `resolve`：对外返回最近结果行的**文档行号**（内部 `resolve` 求序号再映射），空结果集返回无定位，负锚点取首条；UT `query_resolve_exact_hit`、`query_resolve_nearest_tie_previous`、`query_resolve_clamps_ends`、`query_resolve_negative_clamps_first`、`query_resolve_empty_none` 通过
- [ ] 3.2 把 `SourceHandle::read_page` 的「取行 + 回读原文 + 编码」抽成共用函数（入参含 `&ResultSet`）；源分页传恒等结果集，行为与字节输出不变；UT（既有分页用例全部保持通过）
- [ ] 3.3 实现 `QueryHandle::page(start, count)`：按真实文档行号向后起页，复用 3.2 的共用函数与 `MAX_PAGE_ROWS`；负起点、负条数、超上限按规定归因，零条与越界返回空页；UT `query_page_starts_at_real_doc_row`、`query_page_forward_skips_non_matching`、`query_page_forward_no_repeat`、`query_page_between_results_starts_next`、`query_page_past_end_returns_empty`、`query_page_zero_count_returns_empty`、`query_page_rejects_negative_start`、`query_page_rejects_zero_count_with_negative_start`、`query_page_rejects_negative_count`、`query_page_rejects_count_over_max`、`query_page_text_matches_source`、`query_page_entries_carry_doc_row` 通过
- [ ] 3.4 实现 `QueryHandle::page_backward(end, count)`：取终点之前最近 `count` 条结果行、页内升序、不足返回全部、无结果空页；复用 3.2 的共用函数；UT `query_page_backward_returns_tail`、`query_page_backward_contiguous_no_repeat`、`query_page_backward_before_first_empty`、`query_page_backward_short_returns_all`、`query_page_backward_zero_count_returns_empty`、`query_page_backward_rejects_negative_end`、`query_page_backward_rejects_negative_count`、`query_page_backward_text_matches_source` 通过
- [ ] 3.5 生命周期与一致性：源关闭后查询仍可读装载内容；截断返回 `IO_ERROR`（带 `path` / `osError`、无部分页、查询仍可定位）；轮转/替换后仍读开启时内容；空文档查询为空；查询不改源分页；UT `query_survives_source_close`、`query_open_empty_document`、`query_page_truncated_returns_io_error`、`query_page_after_rename_reads_loaded_content`、`query_page_after_replacement_reads_loaded_content`、`query_does_not_change_source_paging`、`query_repeat_open_same_rows` 通过

## 4. cabi 导出

- [ ] 4.1 新增导出 `logcore_open_query` / `logcore_query_row_count` / `logcore_query_resolve` / `logcore_query_page` / `logcore_query_page_backward` / `logcore_free_query`：全部包 `guard`；失败路径先清空输出（指针 null、长度 0、行号 -1）；查询页缓冲复用 `logcore_free_page`；过滤错误透传 `params.field`；UT `cabi_open_query_rejects_null_out`、`cabi_open_query_invalid_filter_field`、`cabi_query_page_roundtrip`、`cabi_query_page_backward_roundtrip`、`cabi_query_resolve_no_result_is_minus_one`、`cabi_query_page_clears_out_on_error`、`cabi_free_query_null_safe` 通过
- [ ] 4.2 C# 绑定层扩展：`LogcoreNative` 增加六个委托与绑定；新增查询包装（持有查询指针与导出表，提供行数、定位、双向取页与释放，取页复用既有解析）；`LogcoreSource` 增加开启查询入口；`LogcorePageRow.Ord` 更名 `DocRow` 并同步四处 IT 断言
- [ ] 4.3 C# 集成测试（真实 dll，`[Collection("Logcore")]`）：`Query_returns_filtered_rows`、`Query_page_starts_at_real_doc_row`、`Query_page_backward_returns_previous_rows`、`Query_jump_to_tail_renders_tail_rows`、`Query_resolve_returns_nearest_doc_row`、`Query_survives_source_close`、`Query_invalid_filter_reports_field`、`Query_page_tail_and_empty` 通过

## 5. 文档与覆盖率

- [ ] 5.1 跑 `cargo llvm-cov -- --test-threads=1`，按实测同批更新 `docs/TESTING.md` 的 Rust 用例数与覆盖率（保留 core / cabi 分列口径）；C# 用例数按实测同步
- [ ] 5.2 核对 README 是否出现 Rust/C# 数字，如有按实测联动；无则不动

## 6. 全量验证与合入

- [ ] 6.1 在 worktree 跑全量：`cargo build` + `cargo test --workspace -- --test-threads=1` + `dotnet test tests/logcat.Tests/logcat.Tests.csproj` + `bash scripts/run-tests-en-us.sh`（Release + en-US，与 CI 同口径）；确认受影响用例全过、全量无新增失败并记录条数
- [ ] 6.2 将 `wt/rust-core-query-handle` 合入目标分支 `master`（`git merge`），在 master 上重跑 6.1 的全部命令并确认结果一致
- [ ] 6.3 `openspec validate rust-core-query-handle --strict` 通过；回读四件产物与实现，列出需求与用例覆盖对照
  - 查询开启与结果集 → `query_open_empty_filter_is_identity`、`query_open_filtered_row_count`、`query_open_invalid_filter_reports_field`、`query_open_requires_loaded`、`query_open_after_close_returns_closed`、`query_open_empty_document`、`query_two_queries_independent`
  - 结果分页 → `query_page_starts_at_real_doc_row`、`_forward_skips_non_matching`、`_forward_no_repeat`、`_between_results_starts_next`、`_past_end_returns_empty`、`_zero_count_returns_empty`、`_rejects_negative_start`、`_rejects_zero_count_with_negative_start`、`_rejects_negative_count`、`_rejects_count_over_max`、`_text_matches_source`、`_entries_carry_doc_row`
  - 向上翻页与尾部定位 → `query_page_backward_returns_tail`、`_contiguous_no_repeat`、`_before_first_empty`、`_short_returns_all`、`_zero_count_returns_empty`、`_rejects_negative_end`、`_rejects_negative_count`、`_text_matches_source`、`cabi_query_page_backward_roundtrip`
  - 导航定位 → `query_resolve_exact_hit`、`_nearest_tie_previous`、`_clamps_ends`、`_negative_clamps_first`、`_empty_none`
  - 查询生命周期与所有权 → `query_survives_source_close`、`cabi_free_query_null_safe`
  - 读取一致性与错误 → `query_page_truncated_returns_io_error`、`query_page_after_rename_reads_loaded_content`、`query_page_after_replacement_reads_loaded_content`、`query_does_not_change_source_paging`
  - 串行调用口径 → `query_two_queries_independent`、`cabi_query_page_roundtrip`

## Workflow follow-up

- 用户验收通过后，按 AGENTS.md §2.3 指示归档 `rust-core-query-handle`，并同批清除该 worktree。
- 本 change 未新增探索笔记；归档时不涉及笔记迁移。