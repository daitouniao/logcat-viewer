# specs delta ↔ 测试覆盖对照表

> 口径：UT = `cargo test --manifest-path logcore/Cargo.toml -- --test-threads=1`（Rust 单元测试）；
> IT = `dotnet test` 中 `logcore` 集合的 `SourceAcquire*IT`（xUnit 经 C ABI 打真实 core）。
> 名称均省略前缀：`source::tests::` / `descriptor::tests::` 等模块路径保留在 UT 列，IT 列写类名.方法。
> 标注「UT-only」的场景由 Rust 侧确定性覆盖（如注入落盘失败、慢消费，C# 侧无注入口），未记为已由 IT 验证。
> 标注「D20」的断言属按列读回，本轮不做，留待下一阶段。

## 1. 两段式获取流程

| 场景 | UT | IT |
| --- | --- | --- |
| plan 只判定不装载 | `plan_has_no_side_effects` | `SourceAcquireSidecarIT.Second_open_hits_cache_and_plan_probe_is_read_only`（plan 不建缓存目录） |
| 计划句柄贯穿两阶段 | `ffi::plan_handle_reused_by_commit` | `SourceAcquireCommitAsyncIT.Commit_returns_immediately_and_descriptor_converges_after_done` |
| plan 之后取消 | `plan_cancel_releases_proxy` | `SourceAcquireCancellationIT.Plan_then_cancel_and_plan_then_close_release_proxy_alike` |
| 采集会话跳过探测与代理 | `device_commit_skips_probe_and_proxy` | `SourceAcquireFeedIT`、`SourceAcquireEventsIT.Device_events_carry_incremental_and_cumulative_rows` |

## 2. 提交受理与装载完成

| 场景 | UT | IT |
| --- | --- | --- |
| 提交在装载完成前返回 | `ffi::commit_returns_before_load_completes` | `SourceAcquireCommitAsyncIT.Commit_returns_immediately_and_descriptor_converges_after_done` |
| 完成经事件告知 | `events_done_marks_completion` | `SourceAcquireEventsIT.File_load_emits_no_appended_and_done_is_terminal` |
| 运行期失败只经事件 | `commit_runtime_failure_emitted_as_event` | `SourceAcquireFailureIT.Post_accept_direct_open_failure_is_event_only` |
| 受理前错误同步返回 | `ffi::plan_failure_returns_error_without_event` | `SourceAcquireFailureIT.Launch_timeout_is_synchronous_code_10_without_events` |
| 采集受理即运行 | `device_capture_file_created_by_core` | `SourceAcquireFeedIT.Feed_lands_lines_in_order_and_rejects_over_limit_batch` |

## 3. 受理前发现计划结果不可用

| 场景 | UT | IT |
| --- | --- | --- |
| 计划连接失效后提交 | `commit_precheck_lost_connection_releases_proxy` | `SourceAcquireStateMatrixIT.Lost_plan_connection_fails_commit_and_keeps_plan_ready`（码 11，句柄仍 PlanReady） |
| 重新计划恢复 | 同左 | 同左（失效后再次 plan 可正常提交，由该用例前半段的新 plan 覆盖） |

## 4. 入口判定归 core

| 场景 | UT | IT |
| --- | --- | --- |
| 宿主不做探测 | `route::tests::proxy_decision_nine_cell_truth_table` | `SourceAcquireConfigSnapshotIT`（请求原样透传）、`SourceAcquirePathFormIT` |
| 网络共享路径原样显示 | `path_display_verbatim` | UT-only（IT 用本地超长路径覆盖同一「原样」断言，不依赖网络） |
| 相对路径被拒 | `request::tests::request_relative_path_rejected` | `SourceAcquireConfigSnapshotIT.Missing_required_and_relative_paths_report_field` |
| 超长路径可打开且显示不变 | `long_path_open_succeeds` | `SourceAcquirePathFormIT.Long_display_path_is_kept_verbatim_and_opens` |

## 5. 代理路由三态判定

| 场景 | UT | IT |
| --- | --- | --- |
| 自动模式直接打开成功且为文本 | `route::tests::proxy_decision_nine_cell_truth_table` | `SourceAcquireLocalDirectIT.Descriptor_matches_known_content` |
| 自动模式嗅探判非文本 | `proxy_route_fallback_judgment_loop` | `SourceAcquireFailureIT.Direct_sniff_failure_falls_back_to_proxy_without_events` |
| 非法模式值回退 | `request::tests::mode_values_fall_back_to_off`、`route::tests::unknown_mode_falls_back_to_off` | UT-only（绑定层只构造合法取值） |

## 6. 代理实例复用与结束

| 场景 | UT | IT |
| --- | --- | --- |
| 多个源共用同一代理实例 | `proxy::tests::proxy_instance_reused_per_pipe` | `SourceAcquireProxyConcurrencyIT.Two_sources_share_external_proxy_and_shutdown_keeps_it` |
| 拉起去重 | `proxy::tests::proxy_launch_single_flight` | `SourceAcquireProxyConcurrencyIT.Owned_single_instance_spawns_once_and_survives_first_close` |
| 关闭一个源不结束代理 | `proxy_survives_until_last_user_close` | 同「拉起去重」IT |
| 最后使用者关闭后结束 | `proxy_survives_until_last_user_close` | 同「拉起去重」IT（全部关闭后 `Shutdown` 收尾） |
| 不同进程各自拉起互不干扰 | `proxy::tests::proxy_spawn_uses_derived_pipe_name`（派生名含 pid） | UT-only（同一 testhost 内无法起第二进程；名称派生即隔离依据） |
| 外部代理可跨进程共用 | `proxy::tests::proxy_base_name_external_not_owned` | `SourceAcquireProxySpoolIT.External_proxy_on_base_name_is_reused_and_spools` |
| 收尾中止活动连接可重试 | `proxy::tests::proxy_shutdown_interrupts_active_spool` | `SourceAcquireProxyConcurrencyIT.Shutdown_interrupts_active_spool_and_next_acquisition_respawns` |
| 退出收尾只结束自己拉起的代理 | `proxy::tests::proxy_shutdown_owned_only` | `SourceAcquireProxyConcurrencyIT.Two_sources_share_external_proxy_and_shutdown_keeps_it` |

## 7. 失败分类

| 场景 | UT | IT |
| --- | --- | --- |
| 代理拉起超时 | `proxy::tests::proxy_launch_timeout_classified` | `SourceAcquireFailureIT.Launch_timeout_is_synchronous_code_10_without_events` |
| 代理无法启动 | `proxy::tests::proxy_launch_failure_classified` | `SourceAcquireFailureIT.Missing_launch_file_reports_proxy_path_and_os_error` |
| plan 失败不经事件 | `events_not_published_for_plan_failure` | `SourceAcquireFailureIT.Launch_timeout_is_synchronous_code_10_without_events` |
| 直接打开失败转代理 | `proxy_route_fallback_judgment_loop` | `SourceAcquireFailureIT.Direct_sniff_failure_falls_back_to_proxy_without_events` |
| 受理后直开失败不再回退 | `commit_direct_open_failure_no_fallback` | `SourceAcquireFailureIT.Post_accept_direct_open_failure_is_event_only` |
| 运行中断线 | `commit_runtime_failure_emitted_as_event` | `SourceAcquireFailureIT.Mid_spool_disconnect_is_reported_only_as_event` |

## 8. 文本嗅探规则

| 场景 | UT | IT |
| --- | --- | --- |
| 空文件判文本 | `probe::tests::probe_empty_file_is_text` | UT-only |
| 控制字符占比恰在阈值上 | `probe::tests::probe_strictly_greater_than_threshold` | UT-only |

## 9. 三轴源描述

| 场景 | UT | IT |
| --- | --- | --- |
| 三轴正交 | `descriptor::tests::descriptor_axes_orthogonal` | `SourceAcquireLocalDirectIT`、`SourceAcquireProxySpoolIT` |
| 文件源身份受理即可读 | `descriptor::tests::descriptor_identity_readable_at_accept`、`descriptor_available_while_committing` | `SourceAcquireCommitAsyncIT.Commit_returns_immediately_and_descriptor_converges_after_done` |
| 计划后文件被改写 | `sidecar::tests::sidecar_recheck_after_plan_change` | `SourceAcquireSidecarIT.Second_open_hits_cache_and_plan_probe_is_read_only`（追加行后转 Progressive） |
| 显示路径不外泄暂存路径 | `descriptor::tests::descriptor_display_and_source_path_split` | `SourceAcquireProxySpoolIT.External_proxy_on_base_name_is_reused_and_spools`（displayPath 原路径 / sourcePath 暂存） |
| 采集源身份为空属合法 | `descriptor::tests::descriptor_identity_null_for_proxy`、`identity_null_for_proxy_spool` | `SourceAcquireEventsIT`（采集描述无 identity） |
| 代理暂存源身份为空 | `identity_null_for_proxy_spool` | `SourceAcquireProxySpoolIT` |
| 采集源无显示路径 | `descriptor::tests::descriptor_display_path_null_for_device` | `SourceAcquireFeedIT`（描述来自采集源） |
| 失败态描述仍可读 | `commit_failure_keeps_descriptor_snapshot`、`capture_commit_failure_keeps_descriptor_snapshot` | `SourceAcquireFailureIT.Post_accept_direct_open_failure_is_event_only`（失败后读描述 Channel 不变） |

## 10. 列声明下发

| 场景 | UT | IT |
| --- | --- | --- |
| 按声明渲染表头 | `descriptor::tests::descriptor_columns_stable_ids`、`descriptor_json_matches_schema` | **下一阶段（D20）**：本轮 IT 只断言描述可读与行数，不按列读回 |

## 11. 配置只经快照传入

| 场景 | UT | IT |
| --- | --- | --- |
| core 不读配置文件 | `request::tests::request_defaults_applied` | `SourceAcquireConfigSnapshotIT` |
| 缺必填字段报字段名 | `request::tests::request_required_fields_rejected_with_field` | `SourceAcquireConfigSnapshotIT.Missing_required_and_relative_paths_report_field` |
| 缺省值生效 | `request_defaults_applied` | `SourceAcquireConfigSnapshotIT.Defaults_apply_and_unknown_fields_are_ignored` |
| 未知字段被忽略 | `request::tests::request_unknown_fields_ignored` | 同「缺省值生效」IT |
| 采集请求不要求文件字段 | `request::tests::request_device_requires_only_temp_dir` | `BindingMarshalingRoundtripTests.DeviceRequest_requires_only_temp_dir`、`SourceAcquireFeedThroughputIT` |

## 12. sidecar 缓存复用

| 场景 | UT | IT |
| --- | --- | --- |
| 二次打开命中缓存 | `source::tests::sidecar_hit_skips_parse` | `SourceAcquireSidecarIT.Second_open_hits_cache_and_plan_probe_is_read_only` |
| 身份变化降级解析 | `sidecar::tests::sidecar_identity_mismatch_misses` | 同「二次打开」IT（追加行后 Progressive 21 行） |
| 损坏条目静默降级 | `sidecar::tests::sidecar_corrupt_degrades_silently` | 同「二次打开」IT（条目写坏后仍完整解析） |
| 版本不符视为未命中 | `sidecar::tests::sidecar_version_mismatch_misses` | `SourceAcquireSidecarIT.Version_mismatch_degrades_to_full_parse` |
| 只缓存本地直读源 | `sidecar_only_local_direct` | `SourceAcquireProxySpoolIT.External_proxy_on_base_name_is_reused_and_spools`（代理源缓存目录为空） |
| 容量回收保留在用条目 | `sidecar::tests::sidecar_eviction_keeps_in_use` | UT-only |
| 关闭不删缓存 | `sidecar::tests::sidecar_in_use_released_on_close` | `SourceAcquireSidecarIT`（关闭后条目仍在） |

## 13. 在用条目口径

| 场景 | UT | IT |
| --- | --- | --- |
| 跨进程回收只致重新解析 | `maintain::tests::maintain_evicts_in_use_of_other_owner` | UT-only |
| 在用登记随源关闭注销 | `sidecar::tests::sidecar_in_use_released_on_close` | `SourceAcquireSidecarIT`（关闭后条目保留但可回收） |

## 14. 临时产物与锁标记

| 场景 | UT | IT |
| --- | --- | --- |
| 产物与标记同时存在 | `capture_lock_marker_held_while_open`、`spool_lock_marker_held_while_open` | `SourceAcquireStopDrainIT`（stop 后文件与 `.lock` 均在） |
| 正常收尾不留标记 | `close_deletes_capture_file_and_marker` | `SourceAcquireCloseDiscardIT.Close_discards_queue_deletes_artifacts_and_locks_out_reads` |
| 停止保留的产物仍被占用 | `stop_keeps_capture_file_and_marker` | `SourceAcquireStopDrainIT` |
| 异常退出后产物可判无主 | `maintain::tests::maintain_cleans_orphan_temp_artifacts` | `SourceAcquireStartupMaintainIT.Maintain_cleans_cache_and_temp_without_touching_unmatched` |

## 15. 启动维护流程

| 场景 | UT | IT |
| --- | --- | --- |
| 启动维护清理损坏与残留 | `maintain_cleans_cache_corrupt_and_temp_writes` | `SourceAcquireStartupMaintainIT.Maintain_cleans_cache_and_temp_without_touching_unmatched` |
| 清理无主临时产物 | `maintain_cleans_orphan_temp_artifacts` | 同上 |
| 跳过仍被占用的产物 | `maintain_skips_artifacts_with_held_marker` | `SourceAcquireStartupMaintainIT.Held_marker_skips_artifact_and_missing_dirs_are_empty` |
| 启动维护幂等 | `maintain_is_idempotent` | `SourceAcquireStartupMaintainIT.Maintain_cleans_cache_and_temp_without_touching_unmatched`（二次调用零删除） |
| 目录不存在视为空 | `maintain_missing_dirs_is_empty` | `SourceAcquireStartupMaintainIT.Held_marker_...`（缺失目录返回 Ok） |
| 维护失败不阻断获取 | `maintain_failure_is_partial` | UT-only（独占占用触发 Partial；获取侧不受影响的断言在 UT） |

## 16. 采集推入语义

| 场景 | UT | IT |
| --- | --- | --- |
| 推入批次按序可读 | `consume_thread_processes_in_order` | `SourceAcquireFeedIT`（逐行内容与顺序） |
| 推入后宿主可立即复用缓冲 | `ffi::tests::feed_copies_lines_before_return` | `SourceAcquireFeedIT`（feed 后原地覆写缓冲，落盘仍为原内容） |
| 入队即返不阻塞 | `feed_enqueues_without_blocking` | `SourceAcquireFeedThroughputIT`（10×1 万行推入后收敛） |
| 空批次无事件 | `feed_empty_batch_legal` | UT-only |
| 超限批次整批被拒 | `feed_over_limit_rejected_with_field`、`feed_over_limit_enqueues_nothing` | `SourceAcquireFeedIT`（`field=lines`、行数不变、无 appended） |
| 采集文件由 core 落盘 | `device_capture_file_created_by_core` | `SourceAcquireFeedIT` |

## 17. 采集收尾语义

| 场景 | UT | IT |
| --- | --- | --- |
| 停止后排空并保留文件 | `stop_drains_then_emits_stopped` | `SourceAcquireStopDrainIT.Stop_drains_to_completion_and_is_idempotent` |
| 停止后推入被拒 | `feed_after_stop_returns_stopped` | 同上（stop 后补推返回 STOPPED） |
| 排空期间推入被拒 | `feed_during_stopping_returns_stopped` | 同上（排空循环内推入返回 STOPPED） |
| 排空期间关闭中止排空 | `close_during_stopping_aborts_drain`、`close_during_stopping_emits_no_stopped` | `SourceAcquireCloseDiscardIT.Close_during_drain_aborts_and_blocked_stop_returns_closed` |
| 宿主先停取流再收尾 | `stop_and_close_are_idempotent` | `SourceAcquireStopDrainIT` + `SourceAcquireCloseDiscardIT.Close_is_best_effort_when_capture_file_is_held` |
| 关闭直接丢弃未消化队列 | `close_discards_queue_without_draining` | `SourceAcquireCloseDiscardIT.Close_discards_queue_deletes_artifacts_and_locks_out_reads` |
| 停止排空进度可查 | `stop_drain_progress_pollable` | `SourceAcquireStopDrainIT`（stage=Draining、Total=Value） |
| 关闭后推入被拒 | `state_close_from_plan_ready_equals_cancel` 等关闭语义 UT | `SourceAcquireCloseDiscardIT`（close 后 feed 返回 CLOSED） |
| 关闭后采集文件删除 | `close_deletes_capture_file_and_marker` | 同上（文件与 `.lock` 均不存在） |
| 落盘失败不静默丢数据 | `disk_failure_clears_queue_and_keeps_committed_rows` | UT-only（注入写失败） |
| 排空期间落盘失败 | `disk_failure_during_drain_is_terminal_error` | UT-only（注入写失败） |

## 18. 关闭时产物删除尽力而为

| 场景 | UT | IT |
| --- | --- | --- |
| 产物被外部占用时关闭仍完成 | `close_best_effort_when_capture_file_held` | `SourceAcquireCloseDiscardIT.Close_is_best_effort_when_capture_file_is_held` |
| 无外部占用时关闭不留产物 | `close_deletes_capture_file_and_marker` | `SourceAcquireCloseDiscardIT.Close_discards_queue_deletes_artifacts_and_locks_out_reads` |

## 19. 排空进度口径

| 场景 | UT | IT |
| --- | --- | --- |
| 排空进度从零起算 | `stop_drain_progress_pollable` | `SourceAcquireStopDrainIT` |
| 累计行数不走排空进度 | `appended_carries_delta_and_total` | `SourceAcquireStopDrainIT`（appended.totalRows 与描述 rowCount 对账）、`SourceAcquireEventsIT` |

## 20. 事件通道为轮询制

| 场景 | UT | IT |
| --- | --- | --- |
| 轮询延迟不丢事件 | `events_survive_delayed_poll` | `SourceAcquireEventsIT.File_load_emits_no_appended_and_done_is_terminal` |
| 关闭作废未取走事件 | `close_discards_unpolled_events` | `SourceAcquireCloseDiscardIT`（close 后 poll 返回 CLOSED） |
| 无新事件返回空结果 | `events_survive_delayed_poll` | `SourceAcquireEventsIT.Device_events_carry_incremental_and_cumulative_rows`（空轮询合法） |
| 进度单调 | `events::tests::events_stage_progress_monotonic` | UT-only |
| 文件源装载不发增量事件 | `events_no_appended_for_file_load` | `SourceAcquireEventsIT.File_load_...`、`SourceAcquireCommitAsyncIT.Commit_returns_immediately_...` |
| 渠道在计划返回时确定 | `events_no_channel_change_after_accept` | `SourceAcquireCommitAsyncIT`（受理与 done 后 Channel 均为 Local） |
| 无总量时如实表达 | `events::tests::progress_total_null_when_unknown` | UT-only |
| 终态唯一 | `events_terminal_per_source_kind` | `SourceAcquireEventsIT`（done/stopped 各一） |
| 关闭不发终态事件 | `close_emits_no_terminal_event` | `SourceAcquireCloseDiscardIT` |

## 21. 采集只属图形界面

| 场景 | UT | IT |
| --- | --- | --- |
| 第二宿主不接采集 | —（宿主契约，非 core 行为） | 文档约束；本轮无第二宿主，未以用例断言 |

## 22. 取消与不变量

| 场景 | UT | IT |
| --- | --- | --- |
| 提交阶段取消释放资源 | `commit_cancellation_releases_resources` | `SourceAcquireCommitAsyncIT.Cancel_before_load_completes_turns_closed` |
| 计划阶段直接关闭 | `plan_close_releases_proxy`、`state_close_from_plan_ready_equals_cancel` | `SourceAcquireCancellationIT` |
| 格式编码运行期不变 | `format_encoding_fixed_after_load_start` | UT-only |
| 文件源上停止被拒 | `stop_on_file_source_invalid_state` | UT-only |
| 停止与关闭幂等 | `stop_and_close_are_idempotent` | `SourceAcquireStopDrainIT`（重复 stop） |
| 失败态上停止返回失败 | `state_matrix_matches_d23` | UT-only |
| 关闭后句柄仍可安全访问 | `ffi::close_keeps_handle_until_free` | `SourceAcquireCloseConcurrencyIT` |
| 关闭期间并发轮询安全 | `close_during_stopping_aborts_drain`（并发收尾） | `SourceAcquireCloseConcurrencyIT.Progress_polling_during_close_is_safe_and_then_closed` |

## 23. 句柄状态与操作合法性

| 场景 | UT | IT |
| --- | --- | --- |
| commit 重复调用被拒 | `state::tests::state_commit_twice_invalid` | `SourceAcquireStateMatrixIT.Plan_commit_and_free_guards_follow_d23` |
| 装载完成前取消中止装载 | `state_cancel_during_commit_closes_handle` | `SourceAcquireCommitAsyncIT.Cancel_before_load_completes_turns_closed` |
| 装载完成后取消被拒 | `state_cancel_after_load_invalid` | `SourceAcquireStateMatrixIT` |
| 计划就绪时读描述被拒 | `state_descriptor_before_commit_invalid` | `SourceAcquireStateMatrixIT` |
| plan 阶段轮询与读进度 | `state_poll_before_commit_is_empty`、`state_progress_before_commit_is_initial` | `SourceAcquireStateMatrixIT` |
| 未关闭即释放被拒 | `ffi::state_free_before_close_invalid`、`state_free_during_stopping_invalid` | `SourceAcquireStateMatrixIT`（free 被拒且句柄仍可用，随后 close/free 成功） |

## 24. 多活动源并存

| 场景 | UT | IT |
| --- | --- | --- |
| 多源并存 | `multiple_sources_coexist` | `SourceAcquireMultiSourceIT.Closing_one_source_does_not_affect_other` |
| 关闭一个源不影响其他 | `close_one_source_does_not_affect_others` | 同上 |
| core 不自行回收闲置源 | `core_does_not_auto_close_idle_sources` | UT-only |
| 宿主回收保留最后源 | `close_one_source_does_not_affect_others`（关闭顺序覆盖） | `SourceAcquireProxyConcurrencyIT`（最后使用者关闭才结束实例） |

---

## 未记为本轮已验证的项

- **按列读回**（需求「列声明下发」）：design D20 明确留待下一阶段；本轮仅验证列元数据与描述字段，未做按列取值的等价断言。
- **Rust 覆盖率**：`cargo-llvm-cov` 未安装，本轮未采集；Rust 覆盖率口径独立于 C# 的 94.26%。
- **环境性失败**：全量 C# 用例中 5 条 `SingleInstance*` 在「本机已有 logcat.exe 运行」时失败（互斥量被占用），与本 change 无关；关停实例后可全过。