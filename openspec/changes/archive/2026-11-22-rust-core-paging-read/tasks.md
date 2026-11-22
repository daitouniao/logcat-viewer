# Tasks

## 1. 准备与基线

- [x] 1.1 在仓库根创建并进入独立 worktree：分支 `wt/rust-core-paging-read`（路径以 `git worktree list` 定位，产物不写本机绝对路径），核对 `Cargo.toml` 工作区成员与 `.cargo/config.toml` 的 `target-dir` 在 worktree 内可用
- [x] 1.2 在 worktree 跑基线：`cargo test --workspace` 与 `dotnet test tests/logcat.Tests/logcat.Tests.csproj`（先 `cargo build`，C# IT 依赖 `logcore/target/debug/logcore.dll`），把条数与 `docs/TESTING.md` 现有记录对齐，确认无新增失败

## 2. 索引发布地基

- [x] 2.1 修 `crates/core/src/segment.rs` 的 `LiveIndex::publish`：先 `segments.store`、后 `rows.store`；加 UT `publish_never_reports_rows_beyond_visible_segments`（发布线程反复发布不同长度的段列表，读线程断言 `rows <= 快照行数之和`），`cargo test -p logcore_core publish` 通过（本期文件页读取不依赖段，此修正面向采集/前瞻的段快照读方）
- [x] 2.2 给 `crates/core/src/parse.rs` 的 `Indexer` 加 `freeze()` / `frozen()`（转发 `Store`），改 `crates/core/src/source.rs` 的 `consume` 每批处理成功后 `freeze` 并 `shared.live.publish(frozen().to_vec())`；加 UT `consumer_publishes_segments_per_batch`（多批 feed 后段数与行数随批增长、行序连续），既有 `consumer_*` 与采集 IT 全部通过

## 3. 时间戳区间

- [x] 3.1 在 `crates/core/src/parse.rs` 新增 `pub fn time_range(line: &[u8]) -> (u32, u32)`（无分配、只扫行首）：无年/带年 threadtime 取到小数秒末位，方括号 long 跳过 `[ ` 前缀后同样止于小数秒末位（无小数秒时止于秒末位），其余返回 `(0, 0)`；UT `time_range_threadtime`、`time_range_year_threadtime`、`time_range_long_prefix`、`time_range_missing_timestamp_empty` 通过
- [x] 3.2 用 `crates/core/testdata/crash_sample.txt` 与 `long_sample.txt` 覆盖真实形态，UT `time_range_independent_of_metadata`（时间戳合法但 pid/tid/级别/tag 未知仍给区间、消息取整行）与 `time_range_sample_files`（1~9 位小数秒、带年份、方括号前缀）通过；`cargo test -p logcore_core parse` 通过

## 4. core 页组装

- [x] 4.1 新增 `crates/core/src/page.rs`：从 `inner.payload`（整份快照）的 8 列与字典/崩溃表收集行，产出页行中间结构；UT `page_rows_from_whole_snapshot_contiguous`、`page_start_beyond_rows_empty`、`page_tail_short_page`、`page_crash_fields_from_whole_snapshot`（Java 崩溃头带类别/异常类型/包名、异常类型缺失为空、普通行不带崩溃字段）通过
- [x] 4.2 实现布局 v1 编码（`LPAG` 头、52 字节行记录、tag/exc/pkg 页内字符串表、文本区，小端）；UT `page_binary_header_fixed`、`page_binary_row_record_fixed`（逐字节字面量）、`page_binary_string_tables_dedup`、`page_binary_text_blob_concatenated` 通过
- [x] 4.3 实现源句柄与装载同源：`Inner` 增加 `Arc<File>`；`load_local` 打开一次，身份采集（含 sidecar 的 `after`）与整份读取均复用该句柄（`Identity` 增加基于 `&File` 的入口或等价实现），缓存命中保留已有打开；`load_proxy` 在 spool 完成后打开 spool 文件并用于格式探针与解析；三条路径在 `store_result` 后、`Loaded` 前写入 `Inner`；打开或读取失败转 `Failed`；`cleanup_resources` 先取走句柄再删临时产物；UT/IT `source_handle_present_after_loaded`、`load_uses_single_source_handle`、`load_source_replaced_during_load_still_consistent`、`load_source_open_failure_fails`、`source_handle_released_before_temp_cleanup` 通过
- [x] 4.4 实现文本读取：页请求复用源句柄，用 `seek_read` / `read_at` 按 `offs` 读 `lens`，长度或区间不符整页 `IO_ERROR`（带 `path` / `osError`）；UT/IT `page_text_matches_source_bytes`、`page_text_truncated_source_errors`、`page_partial_read_fails_whole_page`、`page_after_rename_reads_loaded_content`、`page_after_longer_replacement_reads_loaded_content`、`page_after_cache_hit_matches_parse`（命中缓存后取页与首次解析一致）、`page_after_progressive_load_matches`（一次到位与分块渐进装载后取页一致）、`page_spans_chunk_boundary`（单页行跨块边界仍连续）通过
- [x] 4.5 接入状态门禁：`state.rs` 的 `Op` 增加 `RowCount` / `Page`，矩阵仅 `Loaded` 放行（`Closed` → `CLOSED`，其余 → `INVALID_STATE`），`state_matrix_matches_d23` 表扩到 11 行；core 的行数/页入口走矩阵，再做单页上限、负序号、条数超限校验；UT `row_count_rejected_before_loaded`、`page_rejected_before_loaded`、`page_closed_after_close`、`page_capture_running_rejected`、`page_rejects_count_over_max` 通过

## 5. cabi 导出

- [x] 5.1 在 `crates/cabi/src/lib.rs` 新增 `logcore_row_count`、`logcore_page`、`logcore_free_page`（均套 `guard`；页缓冲精确分配、按 `len` 重建释放；out 指针为 null 报 `INVALID_ARG`）；UT `cabi_row_count_matches_descriptor`、`cabi_row_count_before_loaded_invalid`、`cabi_row_count_null_out_invalid`、`cabi_page_beyond_rows_returns_empty`、`cabi_page_zero_count_returns_header_only`、`cabi_page_count_over_max_field`、`cabi_page_negative_start_field`、`cabi_page_negative_count_field`、`cabi_page_invalid_state_beats_param_error`（非法状态先于参数校验）、`cabi_page_null_out_invalid`、`cabi_page_error_clears_out`、`cabi_page_start_near_i64_max_returns_empty`、`cabi_free_page_null_safe` 通过（`cargo test -p logcore`）

## 6. C# 绑定层

- [x] 6.1 `LogcoreNative.Exports` 增加三个委托与绑定；`LogcoreContracts.cs` 增加 `PageRow`（含崩溃摘要子类型）；`LogcoreSource` 增加 `RowCount` 与 `GetPage(long start, int count)`，解析布局 v1 为 `PageRow[]`，解析后经 `try/finally` 立即释放原生缓冲；`LogcoreErrorCodes` 增加 `MaxPageRows = 4096` 并加跨端一致性测试（读 core 常量，仿 `BindingFeedLimitConstantTests`）；新增 IT 类标 `[Collection("Logcore")]`；IT `Page_returns_slices_and_crash_fields`、`Page_tail_returns_short_page`、`Page_rows_have_unknown_sentinels`、`Page_empty_page_returns_empty_array` 通过
- [x] 6.2 错误与缓存路径 IT：`RowCount_before_loaded_reports_invalid_state`、`Page_rejects_count_over_max`、`Page_before_loaded_reports_invalid_state`、`Page_after_close_reports_closed`、`Page_after_truncation_reports_io_error_and_state_unchanged`、`Page_after_cache_hit_matches_full_parse`（同文件打开两次，第二次命中缓存后取页内容与首次一致）、`Page_after_source_renamed_still_returns_loaded_content`；重复取页 `Page_repeated_requests_stable`（连续多页无崩溃、无二次释放）；`dotnet test --filter "FullyQualifiedName~Page_|FullyQualifiedName~RowCount_"` 通过

## 7. 性能探针与文档

- [x] 7.1 在 `.tmp-test/` 下加页读取探针（git-ignored），对 176MB / 272 万行样本测 200 行页的端到端耗时与首次打开成本（常驻句柄已按正确性定案，探针不评估打开策略），不设通过阈值、只记录基线；样本不可达时用确定性脚本生成本地等量样本（生成器与期望值可入库，样本本身不入库）；把数字回填 `openspec/notes/2026-11-20-RUST-CORE-PAGING-CONTRACT.md` 的未实测清单
- [x] 7.2 更新 `docs/TESTING.md`：Rust 侧用例条数、C# 绑定 IT 条数与覆盖率分母变化；核对 README 测试数字联动口径（如需）

## 8. 收口与合入

- [x] 8.1 全量验证：`cargo test --workspace` + `dotnet test` + `bash scripts/run-tests-en-us.sh`（CI 区域口径），确认受影响类全过、全量无新增失败，记录条数
- [x] 8.2 将 `wt/rust-core-paging-read` 合入目标分支 `master`（`git merge`），在 master 上重跑 8.1 的全部命令并确认结果一致

## Workflow follow-up

- 用户验收通过后，按 AGENTS.md §2.3 指示归档本 change，并同批清除该 worktree。
- 归档时把 `openspec/notes/2026-11-20-RUST-CORE-PAGING-CONTRACT.md` 移入 `openspec/notes/archive/`。