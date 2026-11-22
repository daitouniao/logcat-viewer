# Tasks

## 1. 准备与基线

- [x] 1.1 在独立 worktree 中实施：分支 `wt/rust-core-result-set`（路径以 `git worktree list` 定位，任务内不写本机绝对路径）；确认 worktree 内 `cargo test --workspace -- --test-threads=1` 可运行且基线全绿，记录条数与 `docs/TESTING.md` 现有 Rust 记录一致；确认 `cargo llvm-cov` 可调用（供 5.2，缺则按 `docs/TESTING.md` §6 安装）
- [ ] 1.2 记录 C# 侧基线：`cargo build` 后运行 `dotnet test tests/logcat.Tests/logcat.Tests.csproj`，确认与当前记录无新增失败（本 change 不改 C#，此基线用于回归对比）

## 2. 结果集模型

- [x] 2.1 新增 `crates/core/src/result.rs` 并在 `lib.rs` 挂载：`ResultSet { Identity { rows: u32 }, Rows(Arc<[u32]>) }`、`len() -> usize`、`doc_row(ord: usize) -> u32`（宽度口径见 design D1）；构造用 `debug_assert` 守「严格升序、元素小于文档行数」；UT `result_identity_maps_ord_to_ord`、`result_rows_maps_ord_to_doc_row`、`result_rows_len_matches_rows` 通过
- [x] 2.2 实现 `resolve(doc_row) -> Option<u32>`，两态分支：`Identity` 算术钳位（空 → `None`，否则 `doc_row.min(rows - 1)`，O(1) 零分配）；`Rows` 二分（命中返回该 `ord`；不命中取最近、平局取前；小于首项取首、大于末项取末；空表 → `None`）。UT `resolve_identity_hit`、`resolve_identity_clamps_last`、`resolve_identity_empty_returns_none`、`resolve_rows_hit_exact`、`resolve_rows_between_prefers_nearest`、`resolve_rows_tie_prefers_previous`、`resolve_rows_before_first_returns_first`、`resolve_rows_after_last_returns_last`、`resolve_rows_empty_returns_none` 通过

## 3. 页装配改为 ord → docRow

- [x] 3.1 拆分页装配：新增按文档行号取数的内核 + 低层原语 `page_rows_for(&ResultSet, &Payload, ord_start, count)`（`ord_start` 为内核坐标；对外 `page` 的 `start` 仍为文档行号，本刀恒等视图原样透传）；`PageRow.ord` 改名为 `doc_row`；恒等路径复用同一内核。删除原 `page_rows`，其 4 条 UT 改为恒等口径保留、用例数不变：`page_rows_from_whole_snapshot_contiguous` → `page_rows_for_identity_contiguous`、`page_start_beyond_rows_empty` → `page_rows_for_start_beyond_empty`、`page_tail_short_page` → `page_rows_for_tail_short_page`、`page_crash_fields_from_whole_snapshot` → `page_rows_for_identity_crash_fields`
- [x] 3.2 非恒等结果集取页：用结果集 `[0, 2, 5]` 验证行内容按 `doc_row` 取数；UT `page_rows_for_result_uses_doc_row_for_text`（原文、moff、时间区间、pid/tid/tag）与 `page_rows_for_result_crash_fields_follow_doc_row`（结果集跨过崩溃行时摘要仍绑定正确行）通过
- [x] 3.3 LPAG 编码语义切换：行记录首字段写 `doc_row`，页头第 4 字段写首行 `ord`（保持“首行序号”语义），布局字节数不变；更新逐字节断言 `page_binary_header_fixed`、`page_binary_row_record_fixed`，新增非恒等页断言 `page_binary_header_start_ord`（`Rows [2,5]` 起取页，页头第 4 字段 = 1、首条行记录首字段 = 2），并保持 `page_binary_string_tables_dedup`、`page_binary_text_blob_concatenated` 通过

## 4. 源句柄接线

- [x] 4.1 `SourceHandle::read_page` 改走 `ResultSet::Identity` + 新页装配，文本偏移按 `doc_row` 回查 `offs/lens`；构造时 `debug_assert!(payload.row_count() <= u32::MAX as usize)` 后收窄为 `u32`；既有源侧页测试原样通过：`page_text_matches_source_bytes`、`page_text_truncated_source_errors`、`page_partial_read_fails_whole_page`、`page_after_rename_reads_loaded_content`、`page_after_longer_replacement_reads_loaded_content`、`page_after_cache_hit_matches_parse`、`page_after_progressive_load_matches`、`page_spans_chunk_boundary`
- [x] 4.2 确认对外语义未变：`row_count`、状态矩阵、descriptor 行数均未改；`state_matrix_matches_d23`、`page_rejected_before_loaded`、`page_closed_after_close`、`page_capture_running_rejected`、`cabi_row_count_matches_descriptor`、`cabi_page_beyond_rows_returns_empty` 通过；C# 绑定 IT `Page_returns_slices_and_crash_fields`、`Page_tail_returns_short_page`、`Page_after_cache_hit_matches_full_parse` 原样通过

## 5. 全量验证、文档与合入

- [x] 5.1 在 worktree 跑全量：`cargo test --workspace -- --test-threads=1` + `dotnet test tests/logcat.Tests/logcat.Tests.csproj` + `bash scripts/run-tests-en-us.sh`（Release + en-US，与 CI 同口径）；确认受影响用例全过、全量无新增失败并记录条数
- [x] 5.2 在 worktree 跑 `cargo llvm-cov -- --test-threads=1`，按实测同批更新 `docs/TESTING.md` 的 Rust 用例数与覆盖率（行/区域/函数及分母，保留 cabi 测试代理剔除口径）；核对 README 测试数字联动口径（C# 侧未变则不动）
- [ ] 5.3 将 `wt/rust-core-result-set` 合入目标分支 `master`（`git merge`），在 master 上重跑 5.1 的全部命令并确认结果一致

## Workflow follow-up

- 用户验收通过后，按 AGENTS.md §2.3 指示归档 `rust-core-result-set`，并同批清除该 worktree。
- 本 change 未新增探索笔记；归档时不涉及笔记迁移。