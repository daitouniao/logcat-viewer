# Tasks

## 0. 工作树与基线

- [x] 0.1 在独立 worktree 中实施本 change：分支 `wt/rust-core-index-kernel`，工作树路径以 `git worktree list` 定位；验证：`git worktree list` 可见该分支条目
- [x] 0.2 记录改动前基线：串行跑 Rust 测试与 `dotnet test`（含 FFI 集成测试），把条数写入本任务备注；验证：两组数字可复现
  - 基线备注（2026-11-20，worktree）：Rust 串行 **187 passed / 0 failed**；`dotnet test` **896/902**。失败 6 条 = 5 条 `SingleInstanceTests`/`SingleInstanceNameTests`（本机 `logcat.exe` 运行占用 `Local\logcat-viewer-single-instance` 互斥量）+ 1 条 `SourceAcquireFailureIT.Direct_sniff_failure_falls_back_to_proxy_without_events`（master 既有失败：`reject-unsupported-format` 归档后 Auto 模式非文本改为拒绝，该 C# IT 未随之更新）。同日在 master 主树复跑 `dotnet test` 结果相同（896/902，同 6 条），基线可复现。

## 1. Workspace 拆分（保持产物名与路径）

- [x] 1.1 仓库根建 workspace（成员 `crates/core`、`crates/cabi`、`crates/cli`），用 `git mv` 迁移源码；验证：`cargo build` 全 workspace 成功
- [x] 1.2 仓库根 `.cargo/config.toml` 固定 `build.target-dir = "logcore/target"`，`crates/cabi` 包名与产物名保持 `logcore`；验证：构建后 `logcore/target/debug/logcore.dll` 仍存在
  - 实测：`cargo build` 后 `logcore/target/debug/logcore.dll` 与 `logcore-test-proxy.exe` 均在；仓库根与 `crates/core` 子目录运行均落到同一 target，无根级 `target/`。
- [x] 1.3 更新 `.github/workflows/ci.yml` 构建入口与 `docs/BUILDING.md` 路径段落；验证：按文档命令本地重建成功
- [x] 1.4 现有 Rust 测试（含代理与 FFI 相关用例）串行全过，条数与 0.2 基线一致；验证：`cargo test -- --test-threads=1` 输出
  - 实测：`cargo test -- --test-threads=1` = core 174 + cabi 13（+ cli 0）= **187 passed / 0 failed**，与基线 187 一致。
- [x] 1.5 C# 全量测试通过（含 36 条 FFI 集成测试），证明 dll 装载路径未破坏；验证：`dotnet test` 输出数字
  - 实测：**896/902**，失败 6 条与 0.2 基线完全相同（5 条单实例互斥量占用 + 1 条 reject change 遗留过期 IT），无新增失败；36 条 FFI 集成测试全部通过。另修正 `LogcoreBindingTests` 中硬编码的 `logcore/src/source.rs` 路径为 `crates/core/src/source.rs`。
- [x] 1.6 仓库根新增 `rust-toolchain.toml` 固定工具链 1.99.1；验证：`rustup show active-toolchain` 显示 1.99.1，CI 与本地相同
- [x] 1.7 根 `Cargo.toml` 的版本号与 `Directory.Build.props` 对齐；验证：两处版本号一致
  - 实测：`[workspace.package] version = "0.1.9-pre1"`，三箱成员均 `version.workspace = true`；`cargo metadata` 显示三者均为 `0.1.9-pre1`，与 `<Version>0.1.9-pre1</Version>` 一致。

## 2. 解析差额：方括号 long 格式

- [x] 2.1 先写按基线必红的 UT `parse_long_format_variants`：覆盖可选年、`,` 小数、无 tid、`]` 后无空格；验证：测试失败且失败点符合预期
  - 实测：红，失败点 `minute` 255≠0（`[` 前缀当前走未知元数据），并补 `parse_long_format_variants_after_bracket_no_space`（`[` 后无空格）。
- [x] 2.2 实现 long 行解析（`[` 快速路径 + 回退正则），六列齐备；验证：`parse_long_format_variants` 转绿
  - 实现备注：回退路径按 C# `ReLong` 语法手写（未引入 regex 依赖，Impact 未列该依赖）；`[` 行不匹配语法时整行按未知元数据收。
- [x] 2.3 在 `crates/core/testdata/` 增加小样本 long 日志并补 IT，列值写固定字面量；验证：IT 全过，行数不变
  - 实测：新增 `testdata/long_sample.txt`（3 行）与 IT `long_format_sample_columns`（全管线装载后解码 sidecar，断言 8 列与消息文本），行数 3 不变。

## 3. 解析差额：时间戳字段校验

- [x] 3.1 先写 UT `timestamp_field_range_rejects`（越界行整行未知元数据、行不丢）与 `timestamp_range_valid_calendar_invalid_keep`（`02-31`、秒 60、年 `0000` 保持解析）；验证：前者红、后者绿
  - 实测（实现前）：`cargo test -p logcore_core --lib timestamp` = rejects 红（month 13 仍解析出元数据）/ keep 绿。
- [x] 3.2 实现字段范围校验；验证：3.1 两测全绿，且不误伤闰秒分钟归属
  - 实测：两测全绿；`cargo test -- --test-threads=1` = core 179 + cabi 13 = **192 passed / 0 failed**（含既有用例无回归）。

## 4. 崩溃打标

- [x] 4.1 先写 UT `crash_extractor_java`、`crash_extractor_native`、`crash_extractor_anr`、`crash_extractor_tombstone`、`crash_binds_to_parent_row`、`crash_extract_independent_of_timestamp`；验证：全部红
  - 实测（实现前）：`cargo test -p logcore_core --lib crash` = 0 passed / **6 failed**（提取器为桩、索引未产崩溃事件）。
- [x] 4.2 实现字节快筛 + 3 行前瞻 + 异常类型/包名两字典；验证：4.1 全绿
  - 实测：6 测全绿；`cargo test -- --test-threads=1` = core 185 + cabi 13 = **198 passed / 0 failed**。
- [x] 4.3 补 IT：小样本崩溃行号与字典顺序写固定字面量；验证：IT 全过
  - 实测：`crates/core/testdata/crash_sample.txt`（9 行，含空行）+ IT `crash_sample_rows_and_dictionaries`：崩溃行 [1,4,5,6,8]、kind [Java,Native,ANR,Tombstone,Native]、exc 字典 ["java.lang.RuntimeException","SIGSEGV","SIGABRT"]、pkg 字典 ["com.example.app","com.example.anr"] 均为固写字面量。
  - 顺带修正两处发现的缺口：① `sidecar::decode` 的 tag id 校验把合法值 -1（未知 tag 行）判为越界（`-1 as usize` 回绕），改为接受 -1（与 C# 基线一致）；② 装载路径 `parse_with_progress` 未在 EOF 收尾崩溃前瞻，补 `finish_pending()`，否则末尾崩溃头漏标。

## 5. 分块渐进与段化列存

- [x] 5.1 引入 `arc-swap`，实现段化列存与原子追加发布；验证：`cargo build` 与现有测试全过
  - 实测：新增 `crates/core/src/segment.rs`（`Segment` 8 列 + `Store` 段装配/字典/崩溃窗口 + `LiveIndex` = `ArcSwap<Vec<Arc<Segment>>>`）；`Indexer` 收编为 `Store` 薄壳；装载结果同时产出整份 `Payload` 与段列表并发布。`cargo build` 通过；`cargo test -- --test-threads=1` = core 186 + cabi 13 = **199 passed / 0 failed**。
- [x] 5.2 先写 IT `chunked_equals_whole` 与 UT `chunk_boundary_alignment`，`chunked_equals_whole` 断言 8 列逐位、tag 字典顺序、崩溃行号三组全等；验证：按当前实现红
  - 实测（实现前）：UT 走 `parse_chunked` 桩 panic；IT 分块装载 `segment_count == 1`（未实现分块）→ 0 passed / **2 failed**。
- [x] 5.3 实现首块 4 MiB、稳态 32 MiB 分块装载与跨块崩溃前瞻；验证：5.2 转绿
  - 实测：`segment::chunk_ranges`（首块/稳态尺寸、换行对齐）+ `parse_chunked`/`feed_range`；装载路径每块 `freeze` 后发布 LiveIndex，取消检查与进度按块/1 MiB 分段；崩溃窗口跨段保留。5.2 两条转绿（`chunk_boundary_alignment`、`chunked_equals_whole`）。`cargo test -- --test-threads=1` = core 188 + cabi 13 = **201 passed / 0 failed**。
- [x] 5.4 内置确定性样本生成器（固定种子、显式字节），运行时产出 40 MiB 大样本与按格式（threadtime、方括号 long 等）各 5 MiB 的样本到 git-ignored 临时目录；验证：生成后 IT 覆盖块边界崩溃且仓库无大文件入库
  - 实测：新增 `crates/core/src/sample_gen.rs`（LCG 固定种子、显式行字节；大样本每 1000 行插 4 行崩溃块，Java/Native/ANR/Tombstone 轮换），运行时写临时目录、不入库。IT `generated_sample_chunk_boundaries`：40 MiB 样本 `parse_chunked(4096,4096)` 与 `parse_bytes` 全等、崩溃数等于生成器预期、存在崩溃窗口被段边界切开；三种格式 5 MiB 样本均切多段且行数 > 0。顺带发现 testdata 两个 `.log` 小样本被 `*.log` 忽略（克隆后 IT 会缺样本），改名 `crash_sample.txt` / `long_sample.txt`。`cargo test --workspace -- --test-threads=1` = core 189 + cabi 13 = **202 passed / 0 failed**。

## 6. sidecar 写侧

- [x] 6.1 写侧改逐列 `BufWriter`，临时名追加进程内唯一后缀；验证：UT `encode_stream_roundtrip` 与高版本条目不覆盖用例全过
  - 实测：`sidecar::encode` 改为 `write_to`（流式）薄封装，`save` 用 `BufWriter` + 逐列分块写（8K 元素缓冲）、临时名后缀改为进程内原子计数；UT `encode_stream_roundtrip`（流式写与整份编码逐字节一致 + 解码回原 payload）与 `high_version_entry_not_overwritten`（高版本条目拒绝写入且原样保留）全过。`cargo test --workspace -- --test-threads=1` = core 191 + cabi 13 = **204 passed / 0 failed**。
- [x] 6.2 落盘移装载完成后后台线程、完成事件先发；验证：IT `done_before_sidecar_flush` 全过
  - 实测：`load_local` 不再同步写缓存，只产 `PendingSave`（`Arc<Payload>`，`Inner.payload` 同步改为 `Arc`）；`run_loader` 在 `Done` 事件后 `spawn_pending_save`，线程内落盘、失败忽略。IT `done_before_sidecar_flush`（测试钩子把后台落盘延迟 800ms：进入 Loaded 时事件已含 done 且 `.lcs` 不存在，随后后台补齐）。连带修正三处依赖“装载同步写完缓存”的既有用例改用 `wait_lcs` 轮询（`sidecar_hit_skips_parse`、`stage_hit_path_skips_indexing`、`identity_change_after_plan_degrades`；后者原先会在断言失败时持装载门 panic，Drop 加入口线程而死锁，属测试路径问题）。
- [x] 6.3 写失败注入：验证 IT `sidecar_write_failure_silent`（装载成功、无错误事件）
  - 实测：IT 把缓存目录路径占成普通文件（`create_dir_all` 必失败），装载仍到 Loaded、事件以 done 收尾且无 error、缓存路径原状。`cargo test --workspace -- --test-threads=1` = core 193 + cabi 13 = **206 passed / 0 failed**。

## 7. sidecar 读侧（乙：头部探测 + 装载期解码）

- [x] 7.1 实现定长头部探测（魔数、版本、行数、段长、总长、身份）并在通过时登记在用；验证：UT `header_probe_accepts_valid`、`header_probe_rejects_*` 全过
  - 实测：`HEADER_SIZE` = 124（魔数/版本/行数/宽度/8 段长/total/身份）与 `parse_fixed_header` 共用解析；`probe_header(bytes, file_len, identity)` 校验魔数/版本/行数/段长形状/total≥头部+列长/total==文件长度/身份；`CacheStore::probe_header` 只读 124 字节并登记在用。UT：`header_probe_accepts_valid`、`_rejects_bad_magic/_version/_negative_rows/_seg_len_mismatch/_total_mismatch/_identity_mismatch`、`header_probe_reads_prefix_only`（列数据全损坏仍命中、`in_use_len==1`）。
- [x] 7.2 plan 只读头部、`cache_hit` 改存头部信息；验证：IT `plan_header_hit_skips_decode`（列数据损坏但头部完好的条目在 plan 仍判命中）
  - 实测：`Inner.cache_hit` 类型 `Option<Header>`；plan 走 `probe_header`；commit 在身份复核通过后用 `CacheStore::load` 解码，失败即落入完整解析。IT `plan_header_hit_skips_decode`：把 `.lcs` 头部之后全部改成 0xff，plan 仍报 `loadMode=OneShot`，放行后降级重解析、行数正确。
- [x] 7.3 commit 流式解码（不 `fs::read` 整份），解码失败降级完整解析并把装载模式更新为分块渐进一次；验证：IT `corrupt_payload_degrades_to_full_parse` 断言结果完整、无错误事件、模式已更新，且 UT `decode_rejects_bad_magic`、`decode_rejects_seg_len_mismatch`、`decode_rejects_total_mismatch`、`decode_rejects_dict_id_oob` 全过
  - 实测：`decode` 改为 `decode_reader(&mut &bytes[..])` 薄封装，新增 `decode_file` 用 `BufReader` 读头部→字典→崩溃表→逐列；`CacheStore::load` 走 `decode_file`；原“整份解码”的 `CacheStore::probe` 删除，相关 UT 改用 `probe_header`。降级时只把 `loadMode` 置一次 `Progressive`。UT 4 条与 IT `corrupt_payload_degrades_to_full_parse`（mode=Progressive、rowCount=2、有 Indexing、无 error、以 done 收尾）全过。
- [x] 7.4 回归身份复核路径；验证：IT `identity_change_after_plan_degrades` 全过
  - 实测：原 `identity_rechecked_at_commit_updates_once` 更名为任务指定名；覆盖 plan 命中→文件追加→commit 身份不一致→`Progressive` + `Indexing` + 新行数，且该用例前补 `wait_lcs` 以适配后台落盘。
- [x] 7.5 核对 `openspec/notes/RUST-CORE-REWRITE-SLICING.md` §5.4 第 4 条与 design D6 的乙口径一致（笔记已定稿，不再改写）；验证：无差异即关闭
  - 实测：笔记 §5.4 第 4 条=“plan 只读定长头部（魔数/版本/行数/段长/总长/身份），解码移到装载开始、身份复核后，失败静默降级并把 `loadMode` 改为分块渐进（仅一次）”；与 D6 乙及实现（`probe_header` / `decode_file` / 一次 `Progressive`）一致，无差异；笔记未改写。`cargo test --workspace -- --test-threads=1` = core 207 + cabi 13 = **220 passed / 0 failed**。

## 8. 集成验收

- [x] 8.1 串行跑全量 Rust 测试并记录条数，`dotnet test` 全过（含 FFI 集成）；验证：两组数字写入任务备注，无新增失败
  - 实测：Rust `cargo test --workspace -- --test-threads=1` = core 207 + cabi 13 = **220 passed / 0 failed**。`dotnet test` = **896 passed / 6 failed / 902 total**；6 条失败与 0.2 基线逐条相同（5 条 `SingleInstanceTests`/`SingleInstanceNameTests`：本机 `logcat.exe` 运行占用 `Local\logcat-viewer-single-instance` 互斥量；1 条 `SourceAcquireFailureIT.Direct_sniff_failure_falls_back_to_proxy_without_events`：`reject-unsupported-format` 归档后 Auto 模式非文本改拒绝，该 IT 未更新，master 同红），**无新增失败**；36 条 FFI 集成测试全过。同批按 §3.4 更新 `docs/TESTING.md` §6：Rust UT 条数 176 → 220（`--workspace` 口径）、用例入口命令与测试代理路径；覆盖率标注「workspace 拆分后未重采」。
- [x] 8.2 将 `wt/rust-core-index-kernel` 合入 `master`；验证：`git log --oneline master` 含该分支提交，任务全部勾选
  - 实测：`git merge --ff-only wt/rust-core-index-kernel` 快进 `b86e913..e319f0f`（46 文件），`master` 历史含该分支提交；31/31 任务全部勾选。

## Workflow follow-up

- 用户验收通过并明确指示后，归档本 change（关联探索笔记同批归档，worktree 同批清除）。
