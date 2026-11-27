# Tasks

## 1. 前置与 worktree

- [x] 1.1 在独立 git worktree 中实施本 change：worktree 标识 = 分支 `wt/rust-core-substr-limit`（路径以 `git worktree list` 定位，禁写本机绝对路径），目标分支 `master`；验证：`git worktree list` 列出该分支、主工作树 `git status` 保持干净
- [x] 1.2 复核已定口径与本文一致（16 条 / 2000 字符、仅 message 子串、`SUBSTR_INVALID` = 18、`reason` 两值 `too_many`/`too_long`、C# 两处码表 + `LogcoreBindingTests` 断言、cabi 不动）；验证：`design.md` D1–D9 无「未拍」标记，`openspec validate rust-core-substr-limit --strict` 为 0 告警

## 2. 错误码 18（两表对齐，red-first）

- [x] 2.1 `crates/core/src/error.rs` 新增 `SUBSTR_INVALID = 18`、`code_name` 分支、`param_keys = ["reason"]`，并落 `pub const SUBSTR_ERROR_INDEX_KEY: &str = "index";`；验证：`error_code_table_values_are_stable` 中原 `code_name(18) == "UNKNOWN"` 改为 `"SUBSTR_INVALID"`（先改断言、实现码前必红），段间留空断言顺延到 19；`error_params_schema_is_stable` 扩 `(18, ["reason"])` 并断言 `SUBSTR_ERROR_INDEX_KEY == "index"`；`cargo test -p logcore_core --lib error::` 通过
- [x] 2.2 C# 镜像两处补 `SubstrInvalid = 18` 与 `Name(18) => "SUBSTR_INVALID"`（`logcat-viewer/Services/Logcore/LogcoreContracts.cs`、`logcat-next/Next.Interop/LogcoreErrorCodes.cs`）；验证：`BindingErrorCodeMappingTests` 的 `Code_names_match_d15_table` 在 `Name(17)` 旁对称加 `Name(18) == "SUBSTR_INVALID"` 断言，`dotnet test --filter Code_names_match_d15_table` 通过（`TEMP`/`TMP` 指到工作区内跑）；走查 GUI 错误兜底按码名渲染为 `SUBSTR_INVALID（18）`，无按码名的 `Loc` 分支需扩

## 3. 子串规模闸（编译期同步拒绝）

- [x] 3.1 `crates/core/src/filter.rs` 增 `SUBSTR_MAX_VALUES = 16`、`SUBSTR_MAX_CHARS = 2000`；按 D5 次序在 `CompiledMsg::new_substring` 开头加闸：先条数（> 16 → `too_many`、不带 `index`），再逐条长度（`chars().count() > 2000` → 首条超限 `too_long` + `index`）；闸只判 `values` 入参，不改 `needles` 折叠 / `has_empty` / `all_empty` / `always_true`；验证：先写按正确语义必失败的拒绝用例，再落实现使其转绿
- [x] 3.2 拒绝用 `SUBSTR_INVALID`，`params` 带 `reason` 与可归因时 `index`，MUST NOT 复用 `INVALID_ARG`；验证：用例断言错误码为 18、`reason` 取值正确、条数超限不附 `index`
- [x] 3.3 常见规模不被误伤；验证：`substr_limit_boundary_accepted`（恰 16 条 / 每条恰 2000 字符通过）、`substr_limit_count_exceeded_rejects`（17 条 → `too_many`）、`substr_limit_length_exceeded_rejects`（2001 字符 → `too_long`）、`substr_limit_index_points_to_offending_entry`（第二条超长 → `index=1`）、`substr_limit_count_omits_index`、`substr_limit_rejects_even_with_exclude`（超条数且 `exclude=true` 仍拒）

## 4. 不回归与分档隔离

- [x] 4.1 子串上限不改变既有匹配结论；验证：`substr_limit_does_not_change_match`（合规规模内空词 / 全部命中 / 排除的行集与加闸前一致）、`substr_default_mode_unchanged`
- [x] 4.2 两档规模闸互不串：正则超限仍走 17、子串超限走 18；验证：`regex_over_limit_not_substr_invalid`（正则模式超 16 条/2000 字符 → `REGEX_INVALID`，非 `SUBSTR_INVALID`）
- [x] 4.3 `tags`/`pids`/`tids` 不受本卡影响（无新增上限）；验证：既有用例不回归、`tags.values` 仅空串被拒的既有口径不变

## 5. 开启查询面同步（`logcore/query` MODIFIED）

- [x] 5.1 子串超限在 open 同步拒绝、句柄状态不变；验证：在 `query.rs` 的 `mod tests` 新增 `query_open_over_limit_substr_reports_substr_invalid`、`query_open_within_substr_limit_builds_result`，既有 `query_open_invalid_regex_reports_regex_invalid` 与 `query_open_invalid_filter_reports_field` 不回归

## 6. 测试落点

- [x] 6.1 新建 `crates/core/src/filter_substr_tests.rs`，照 `filter_regex_tests.rs` 先例由 `filter.rs` 末尾 `#[path = "../../filter_substr_tests.rs"] mod substr_tests;` 挂载；§3–§4 全部新用例写在该文件、实现代码留在 `filter.rs`；验证：`filter.rs` 不新增 `#[test]`，挂载后 `filter::tests` 用例总数 = 挂载前 + 本卡净新增

## 7. 全量验证与文档联动

- [x] 7.1 Rust 全量串行口径 `cargo test --workspace -- --test-threads=1`；验证：基线 478/478（core 445 + cabi 33）无新增失败，报告串行口径 N 条与新增明细；**并行口径不可用于判定**（`proxy::tests` / `source::tests` 读到的进程全局登记表计数被并发用例的 `shutdown()` 清表，单跑或串行即过，属既有并行争用、非本卡引入）
- [x] 7.2 C# 全量 `dotnet test`；验证：基线 923/923 无新增失败（含 2.2 断言扩充）
- [x] 7.3 更新 `docs/TESTING.md` §6 的 Rust 数字（新增条数与串行口径）；验证：文档数字与 7.1/7.2 实测一致；README×2 不改（只挂 C# 口径，本卡 C# 用例数不变）

## 8. 收口

- [ ] 8.1 wt 合入目标分支：`git merge wt/rust-core-substr-limit` 合入 **`master`**；验证：`git log master` 出现该合并、`git status` 干净；worktree 清除随归档执行（本任务只记合入完成）
- [ ] 8.2 交付验收材料（本卡无 UI 面，不做 GUI 手测）：汇总 7.1/7.2 口径数字，交用户验收；验证：AI 不自行判定「验收通过」，等用户明示

## Workflow follow-up

- 本 change 目录为未跟踪文件，需用户当轮授权后才 `git add` + commit；归档那批默认要求已在 `master` 上。
- 用户明确「验收通过」后追问是否归档；仅在用户明示「可以归档 rust-core-substr-limit」时执行 `openspec archive`。
- 归档同批：`git worktree remove` 清除 `wt/rust-core-substr-limit`（本卡无关联探索笔记，不移动 notes）。
- 归档门禁：8.1 已关闭且 `wt/rust-core-substr-limit` 已合入 `master`；tasks 存在未关闭项时不得归档。
- 本 change 不含发版动作（版本号 5 处同步与打 tag 需另行当轮授权）。
