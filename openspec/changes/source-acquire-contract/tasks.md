# Tasks：source-acquire-contract

- [ ] 0.1 **worktree 标识（防断链）**：本次 apply 在分支 `wt/source-acquire-contract` 的独立 git worktree 中实施（路径以 `git worktree list` 为准，禁写绝对路径）；「wt 合入目标分支」任务见 11.2，目标分支 = `master`
- [ ] 0.2 确认 cargo 工具链可用（`cargo --version`），记录 rustc 版本进本任务完成备注；不可用即停下报告，不擅自装工具链

## 1. Rust crate 骨架与 FFI 基座

- [ ] 1.1 新建顶层 `logcore/` crate（lib + cdylib 双目标，`Cargo.toml` 配置见 design D1/D8），`cargo build` 产出 dll 且 `cargo test` 空跑通过
- [ ] 1.2 定义错误码数值表与返回状态枚举（两宿主共用 design D15 的表：0–7 状态类、10–15 失败类；`BUSY` 为保留码、不因第二个源触发），错误码常量与 `params` 键集测试锁定不漂移（用例名 `error_code_table_values_are_stable`、`error_params_schema_is_stable`）
- [ ] 1.3 实现 C ABI 导出面骨架：`plan` / `commit` / `feed` / `stop` / `close` / `poll_events` / `get_progress` / `cancel` / `free_*`，全部包 `catch_unwind`（panic 转错误码），句柄为不透明指针；plan 返回句柄与 design D14 的 plan JSON（`route` / `estimatedSize`），commit 复用、cancel 两阶段有效，close 只释放源资源、句柄保留至 `free_handle`；封送往返测试（用例名 `ffi_string_roundtrip`、`ffi_panic_maps_to_error_code`、`plan_handle_reused_by_commit`、`close_keeps_handle_until_free`、`poll_after_close_returns_closed`）

## 2. 文件链判定（UT 主体，cargo test）

- [ ] 2.1 实现代理路由三态判定（九格：模式 × 直接打开结果 × 嗅探结果；非法模式回退显式关闭）：用例 `proxy_decision_nine_cell_truth_table`、`unknown_mode_falls_back_to_off`
- [ ] 2.2 实现文本嗅探（8 KB 窗口、控制字符白名单排除 09/0A/0C/0D、7F 计入、占比严格大于 10%、空文件判文本、UTF-16 判非文本；窗口与阈值可经快照注入）：用例 `probe_window_boundary_and_truncation`、`probe_control_char_whitelist`、`probe_strictly_greater_than_threshold`、`probe_empty_file_is_text`、`probe_utf16_is_binary`
- [ ] 2.3 实现 plan 无副作用语义（不写盘、不建索引、不改状态；estimatedSize 三来源且采集未知不参与大小判定；路径原样保留、不规范化不解析链接）：用例 `plan_has_no_side_effects`、`plan_estimated_size_sources`、`path_display_verbatim`
- [ ] 2.4 实现 identity 四要素计算（size、mtime、首尾各 64 KB 指纹）与「空 identity + 未命中」合法组合：用例 `identity_four_elements_stable`、`identity_empty_is_legal_combination`
- [ ] 2.5 实现源描述组装（字段名与取值枚举按 design D14 的线格式；三轴正交、columns 按稳定 id、displayPath/sourcePath 分离、identity 无值为整体 null、loadMode/appendMode/leaseKind/timeSemantics 取值约束）：用例 `descriptor_axes_orthogonal`、`descriptor_columns_stable_ids`、`descriptor_display_and_source_path_split`、`descriptor_json_matches_schema`

## 3. 代理 spool、commit 与缓存

- [ ] 3.1 core 实现代理管道客户端协议（连接、拉起超时、spool 接收、三态出口与失败分类、判定环「读魔数失败回退代理再判」；暂存目录按 D7 全局序号命名并独占创建）：用例 `proxy_route_fallback_judgment_loop`、`proxy_launch_timeout_classified`、`proxy_spool_receives_bytes`、`plan_cancel_releases_proxy`（plan 成功后取消：连接释放、无持久产物）、`spool_dir_unique_on_collision`（IT 侧与 C# 既有 fake 对端联测在 §6）
- [ ] 3.2 实现 commit 装载与直读索引（本地直读逐行逐列解析，行数 / 每列值 / 顺序可读回；超长绝对路径按 D14 补前缀打开、显示路径不变）：用例 `commit_local_direct_reads_known_content`、`commit_cancellation_releases_resources`、`format_encoding_fixed_after_load_start`、`long_path_open_succeeds`
- [ ] 3.3 实现多源并存与句柄独立性（每句柄独立状态机与事件队列、多文件源与采集源可同时存在、关闭或释放一个句柄不影响其他、core 不设数量上限且不自行回收闲置源）；关闭顺序不变量（停扫描 → 释放源资源（文件句柄、租约/临时目录）→ 句柄保留至 free）：用例 `multiple_sources_coexist`、`close_one_source_does_not_affect_others`、`core_does_not_auto_close_idle_sources`、`close_order_fixed_sequence`
- [ ] 3.4 实现 sidecar 缓存复用（键派生沿用既有规则、字节格式冻结现有版本、plan 只读探测、commit 开始复核身份、命中跳过解析、损坏/版本/身份不符静默降级、临时文件 + 原子替换写入、只服务本地直读源、容量淘汰保留在用条目）；先以现 C# 实现生成小样本键向量与 sidecar 字节入库 `logcore/testdata/` 作 golden（只加不改）：用例 `sidecar_key_matches_golden_vectors`、`sidecar_bytes_match_golden_dump`、`sidecar_hit_skips_parse`、`sidecar_identity_mismatch_misses`、`sidecar_corrupt_degrades_silently`、`sidecar_version_mismatch_misses`、`sidecar_atomic_replace`、`sidecar_only_local_direct`、`sidecar_eviction_keeps_in_use`、`sidecar_plan_probe_writes_nothing`
- [ ] 3.5 实现启动维护入口 `sidecar_maintain`（无句柄、不依赖活动源：结构校验、删除损坏条目与残留临时文件、按容量回收并返回统计；幂等、目录不存在视为空、失败返回部分状态不抛错）：用例 `sidecar_maintain_cleans_corrupt_and_temp`、`sidecar_maintain_is_idempotent`、`sidecar_maintain_missing_dir_is_empty`、`sidecar_maintain_failure_is_partial`

## 4. 采集推入（A11 / A14）

- [ ] 4.1 实现采集会话 commit：跳过嗅探与代理、core 在快照 tempDir 下按 D7 命名（时间戳 + 全局序号、独占创建防覆盖）创建采集文件（UTF-8 无 BOM、CRLF）、描述符返回 sourcePath / Retained / leaseId / 身份为空：用例 `device_commit_skips_probe_and_proxy`、`device_capture_file_created_by_core`、`capture_file_unique_on_collision`
- [ ] 4.2 实现 `feed` 异步入队（行数组封送、单批上限 65536、空批合法、入队即返）与无上限 MPSC 队列：用例 `feed_enqueues_without_blocking`、`feed_empty_batch_legal`、`feed_over_limit_rejected`
- [ ] 4.3 实现消化线程（解析 → 索引追加 → append 落盘 → 事件入队，按入队顺序处理；appended 与提交批次一一对应、仅行数增长时发）：用例 `consume_thread_processes_in_order`、`appended_matches_committed_batches`、`appended_not_emitted_on_zero_growth`
- [ ] 4.4 实现 stop / close 排空收尾（stop 排空 + flush + 发 stopped、之后 feed 返回 STOPPED；close 同步排空后返回并释放源资源（句柄保留至 free）、只消化已入队行、排空进度可轮询、不发终态事件；落盘失败清队列 + 发 error + 后续 feed 返回 FAILED、已落盘行保留）：用例 `stop_drains_then_emits_stopped`、`feed_after_stop_returns_stopped`、`close_drains_with_pollable_progress`、`close_drains_only_enqueued_rows`、`close_emits_no_terminal_event`、`stop_and_close_are_idempotent`、`stop_on_file_source_invalid_state`、`disk_failure_clears_queue_and_keeps_committed_rows`

## 5. 事件通道

- [ ] 5.1 实现轮询事件队列与原子进度（stage 按 design D16 五值定义与路径顺序推进、命中缓存路径不进入 Indexing、同一阶段 value 单调且切换归零、总量未知为 null；channelChanged 时机、warning 累加、终态按源类型分派且同源唯一、迟轮询不丢不合并；错误事件带 D15 的码与 params，含 `directOpenError` 附注）：用例 `events_stage_progress_monotonic`、`stage_transitions_follow_path`、`stage_hit_path_skips_indexing`、`progress_total_null_when_unknown`、`events_channel_changed_timing`、`events_terminal_per_source_kind`、`events_survive_delayed_poll`、`events_error_params_match_schema`

## 6. C# 绑定层与 IT

- [ ] 6.1 新增 C# FFI 绑定层（P/Invoke 封送：请求 JSON、按 design D14 的字段名与取值反序列化描述符、feed 行数组、错误码映射、`sidecar_maintain` 请求与统计结果、句柄与字符串释放配对、close 与 free 分离、缺失 dll 抛明确错误），绑定层为 `logcat-viewer/` 下新增文件、不改既有链路任何文件、不动 csproj / slnx；绑定层 UT：用例 `BindingMarshalingRoundtripTests`、`BindingErrorCodeMappingTests`、`BindingSidecarMaintainTests`、`BindingMissingLibraryThrowsClearError`
- [ ] 6.2 C# IT（xUnit 经 FFI 打真实 core）：本地直读、代理 spool（复用既有 fake 对端，验证跨语言复用推断）、identity 二次打开一致、推入批次读回（行数/顺序/增量一致、sourcePath 文件可复制且编码换行正确）、失败路径（拉起超时/直接打开失败，断言 D15 码值与 `params` 键集）、plan/commit 取消、多源并存（关闭一个不影响其他）、退出排空（停止取流并推完残余后 close，返回即全量落盘且不再发终态事件；排空 value 等于提交行数、total 等于发起时队列行数快照）；用例名 `SourceAcquireLocalDirectIT`、`SourceAcquireProxySpoolIT`、`SourceAcquireIdentityIT`、`SourceAcquireFeedIT`、`SourceAcquireFailureIT`、`SourceAcquireCancellationIT`、`SourceAcquireConfigSnapshotIT`、`SourceAcquireExitDrainIT`、`SourceAcquireCloseConcurrencyIT`（关闭期间另一线程轮询进度、close 返回后轮询安全返回 CLOSED，再 free_handle）、`SourceAcquireMultiSourceIT`、`SourceAcquirePathFormIT`（本地构造超长路径与等价短路径，断言显示路径原样、实际打开成功；不依赖网络）、`SourceAcquireSidecarIT`（二次打开命中、身份变化重解析、损坏条目降级、版本不符、只缓存本地直读、关闭不删条目）、`SourceAcquireSidecarStartupIT`（启动维护清理损坏与残留、幂等、目录不存在、目录不可写时部分失败且获取仍成功）
- [ ] 6.3 采集吞吐压测用例（固定速率推入 10 万行量级，断言积压收敛、无丢行）：用例 `SourceAcquireFeedThroughputIT`；超预期即如实记录并回报，不擅自改 A14 口径；压测吞吐数字作为退出等待默认值的推导依据（D12）

## 7. 验证收口与文档

- [ ] 7.1 Rust 侧全量 `cargo test` 出口径数字；C# 侧 `dotnet test` 全量出口径数字（相对 docs/TESTING.md 基线无新增失败）；Rust 覆盖率工具链（cargo llvm-cov）可用性结论记入 TESTING.md 草稿段
- [ ] 7.2 `docs/BUILDING.md` 增补 Rust 构建小节（cargo build 产物、dll 探测目录、缺失时行为）；`docs/TESTING.md` 新增 Rust 段（UT/IT 两层口径、覆盖率口径独立声明）并同步 C# 用例数与覆盖率口径；两份 README 的测试数字与徽章按 TESTING.md 联动同批更新（功能内容等打 tag 再动）
- [ ] 7.3 `openspec validate --strict source-acquire-contract` 通过；逐条核对 specs delta 场景 ↔ UT/IT 用例覆盖对照表（写入本 change 目录 `coverage-map.md`）

## 8. 合入与收尾

- [ ] 8.1 tasks 全部关闭后自检：变更仅含新增文件 + 文档更新（BUILDING / TESTING / 两份 README 的数字与徽章）+ openspec 产物，未触碰既有 C# 链路（A13，csproj / slnx 未改），`git status` 干净度核对
- [ ] 8.2 **wt 合入目标分支**：将 `wt/source-acquire-contract` 合入 `master`（归档前置条件；用户「可以归档 source-acquire-contract」即一并授权本次合入、归档 git mv 与 worktree 清除）
