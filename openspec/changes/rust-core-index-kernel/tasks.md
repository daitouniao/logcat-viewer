# Tasks

## 0. 工作树与基线

- [ ] 0.1 在独立 worktree 中实施本 change：分支 `wt/rust-core-index-kernel`，工作树路径以 `git worktree list` 定位；验证：`git worktree list` 可见该分支条目
- [ ] 0.2 记录改动前基线：串行跑 Rust 测试与 `dotnet test`（含 FFI 集成测试），把条数写入本任务备注；验证：两组数字可复现

## 1. Workspace 拆分（保持产物名与路径）

- [ ] 1.1 仓库根建 workspace（成员 `crates/core`、`crates/cabi`、`crates/cli`），用 `git mv` 迁移源码；验证：`cargo build` 全 workspace 成功
- [ ] 1.2 仓库根 `.cargo/config.toml` 固定 `build.target-dir = "logcore/target"`，`crates/cabi` 包名与产物名保持 `logcore`；验证：构建后 `logcore/target/debug/logcore.dll` 仍存在
- [ ] 1.3 更新 `.github/workflows/ci.yml` 构建入口与 `docs/BUILDING.md` 路径段落；验证：按文档命令本地重建成功
- [ ] 1.4 现有 Rust 测试（含代理与 FFI 相关用例）串行全过，条数与 0.2 基线一致；验证：`cargo test -- --test-threads=1` 输出
- [ ] 1.5 C# 全量测试通过（含 36 条 FFI 集成测试），证明 dll 装载路径未破坏；验证：`dotnet test` 输出数字
- [ ] 1.6 仓库根新增 `rust-toolchain.toml` 固定工具链 1.99.1；验证：`rustup show active-toolchain` 显示 1.99.1，CI 与本地相同
- [ ] 1.7 根 `Cargo.toml` 的版本号与 `Directory.Build.props` 对齐；验证：两处版本号一致

## 2. 解析差额：方括号 long 格式

- [ ] 2.1 先写按基线必红的 UT `parse_long_format_variants`：覆盖可选年、`,` 小数、无 tid、`]` 后无空格；验证：测试失败且失败点符合预期
- [ ] 2.2 实现 long 行解析（`[` 快速路径 + 回退正则），六列齐备；验证：`parse_long_format_variants` 转绿
- [ ] 2.3 在 `crates/core/testdata/` 增加小样本 long 日志并补 IT，列值写固定字面量；验证：IT 全过，行数不变

## 3. 解析差额：时间戳字段校验

- [ ] 3.1 先写 UT `timestamp_field_range_rejects`（越界行整行未知元数据、行不丢）与 `timestamp_range_valid_calendar_invalid_keep`（`02-31`、秒 60、年 `0000` 保持解析）；验证：前者红、后者绿
- [ ] 3.2 实现字段范围校验；验证：3.1 两测全绿，且不误伤闰秒分钟归属

## 4. 崩溃打标

- [ ] 4.1 先写 UT `crash_extractor_java`、`crash_extractor_native`、`crash_extractor_anr`、`crash_extractor_tombstone`、`crash_binds_to_parent_row`、`crash_extract_independent_of_timestamp`；验证：全部红
- [ ] 4.2 实现字节快筛 + 3 行前瞻 + 异常类型/包名两字典；验证：4.1 全绿
- [ ] 4.3 补 IT：小样本崩溃行号与字典顺序写固定字面量；验证：IT 全过

## 5. 分块渐进与段化列存

- [ ] 5.1 引入 `arc-swap`，实现段化列存与原子追加发布；验证：`cargo build` 与现有测试全过
- [ ] 5.2 先写 IT `chunked_equals_whole` 与 UT `chunk_boundary_alignment`，`chunked_equals_whole` 断言 8 列逐位、tag 字典顺序、崩溃行号三组全等；验证：按当前实现红
- [ ] 5.3 实现首块 4 MiB、稳态 32 MiB 分块装载与跨块崩溃前瞻；验证：5.2 转绿
- [ ] 5.4 内置确定性样本生成器（固定种子、显式字节），运行时产出 40 MiB 大样本与按格式（threadtime、方括号 long 等）各 5 MiB 的样本到 git-ignored 临时目录；验证：生成后 IT 覆盖块边界崩溃且仓库无大文件入库

## 6. sidecar 写侧

- [ ] 6.1 写侧改逐列 `BufWriter`，临时名追加进程内唯一后缀；验证：UT `encode_stream_roundtrip` 与高版本条目不覆盖用例全过
- [ ] 6.2 落盘移装载完成后后台线程、完成事件先发；验证：IT `done_before_sidecar_flush` 全过
- [ ] 6.3 写失败注入：验证 IT `sidecar_write_failure_silent`（装载成功、无错误事件）

## 7. sidecar 读侧（乙：头部探测 + 装载期解码）

- [ ] 7.1 实现定长头部探测（魔数、版本、行数、段长、总长、身份）并在通过时登记在用；验证：UT `header_probe_accepts_valid`、`header_probe_rejects_*` 全过
- [ ] 7.2 plan 只读头部、`cache_hit` 改存头部信息；验证：IT `plan_header_hit_skips_decode`（列数据损坏但头部完好的条目在 plan 仍判命中）
- [ ] 7.3 commit 流式解码（不 `fs::read` 整份），解码失败降级完整解析并把装载模式更新为分块渐进一次；验证：IT `corrupt_payload_degrades_to_full_parse` 断言结果完整、无错误事件、模式已更新，且 UT `decode_rejects_bad_magic`、`decode_rejects_seg_len_mismatch`、`decode_rejects_total_mismatch`、`decode_rejects_dict_id_oob` 全过
- [ ] 7.4 回归身份复核路径；验证：IT `identity_change_after_plan_degrades` 全过
- [ ] 7.5 核对 `openspec/notes/RUST-CORE-REWRITE-SLICING.md` §5.4 第 4 条与 design D6 的乙口径一致（笔记已定稿，不再改写）；验证：无差异即关闭

## 8. 集成验收

- [ ] 8.1 串行跑全量 Rust 测试并记录条数，`dotnet test` 全过（含 FFI 集成）；验证：两组数字写入任务备注，无新增失败
- [ ] 8.2 将 `wt/rust-core-index-kernel` 合入 `master`；验证：`git log --oneline master` 含该分支提交，任务全部勾选

## Workflow follow-up

- 用户验收通过并明确指示后，归档本 change（关联探索笔记同批归档，worktree 同批清除）。
