# Tasks

## 1. 准备

- [x] 1.1 在 `wt/reject-unsupported-format` 分支的独立 worktree 中实施（目标分支 `master`；路径以 `git worktree list` 定位，勿写本机路径）。验证：`git worktree list` 可见该分支，worktree 内 `git status` 干净。

## 2. 格式判定模块与窗口读取

- [x] 2.1 新增格式判定模块并注册：`detect_unsupported_format(&[u8]) -> Option<&'static str>`，首部 4 字节 `44 4C 54 01` 返回 `"Dlt"`，不足 4 字节或其余内容返回 `None`。验证：UT `format_detects_dlt_magic`、`format_requires_four_magic_bytes`、`format_plain_text_is_none` 全过。
- [x] 2.2 探针模块增加 `read_window(&mut File, usize) -> io::Result<Vec<u8>>`（读满窗口或到 EOF 为止），`sample_is_text` 的判据一个字不动。验证：现有 `probe_*` UT 全过，新增 `read_window_truncates_at_eof`；spec 的「控制字符占比恰在阈值上」已由既有 `probe_strictly_greater_than_threshold`（90/10 恰 10% 判文本、11% 判非文本）覆盖，无需新增用例。

## 3. plan 期拒绝（直读可判）

- [x] 3.1 plan 直读分支改为「一次读窗口 → 魔数 → 嗅探」：非文本同步返回 `UNSUPPORTED_FORMAT`，`format` 参数取 `Dlt` / `Binary`，不建句柄；读窗口 I/O 失败保持现状（Auto 回退代理、Off 继续直读）。验证：`plan_rejects_non_text_when_auto`（由 `proxy_route_fallback_judgment_loop` 改写，原断言「嗅探判非文本转代理」必须随之删除）、`plan_rejects_non_text_in_off_mode`、`plan_rejects_dlt_magic_with_label`、`plan_rejection_creates_no_cache_entry`、`plan_empty_file_stays_local_direct` 全过。
- [x] 3.2 补回 Auto 回退的 plan 级端到端覆盖（旧用例改写后不再有任何直开失败→代理的 IT）：新增 `plan_auto_falls_back_to_proxy_on_open_error`——`exclusive_hold` 占住源文件使 `File::open` 报共享冲突（metadata 仍可读），`TestProxyServer::start_with_dir` 指向另一目录并放入同名文本文件（靠 `winserver::open_for` 的 basename 回退让代理读到），断言 `route == "ProxySpool"`。
- [x] 3.3 路由回归：文本文件在 Auto / Off 下仍为 `LocalDirect`，`On` 仍为 `ProxySpool`，非法模式值回退为显式关闭；同步更新 `route.rs` 注释——Auto 的现实触发改为「直读打开失败 / 读窗口 I/O 失败」，函数与九格真值表不动。验证：`proxy_decision_nine_cell_truth_table`、`unknown_mode_falls_back_to_off` 与路由、模式相关既有用例全过。

## 4. commit 期拒绝（暂存副本）

- [x] 4.1 `load_proxy` 在 spool 完成之后、读取副本解析之前读窗口判定（魔数先于嗅探，窗口与阈值取 request options）；非文本返回 `UNSUPPORTED_FORMAT`，经既有装载错误路径转失败态 + 错误事件，不写缓存；副本读窗口 I/O 失败放行，交给整体读取报错（与 D5 一致）。验证：IT `commit_rejects_non_text_spool`（`TestProxyServer` 服务二进制文件、`On` 模式：阶段推进到 `Spooling` 后进入失败态、无 `Indexing` 事件、事件含 14、行数 0、缓存目录无条目）、`commit_text_spool_still_loads` 全过。
- [x] 4.2 阶段与终态回归：暂存文本文件仍按 `Opening → Spooling → Indexing` 推进并以完成事件收尾。验证：`stage_transitions_follow_path` 与 spool 相关既有用例全过。

## 5. 全量验证

- [x] 5.1 全量 `cargo test` 通过并记录条数；`openspec validate reject-unsupported-format --strict` 通过。本 change C# 零改动，不跑 `dotnet test`，C# 侧由 CI 兜底。验证：两条命令输出。结果（2026-11-19，串行口径 `cargo test -- --test-threads=1`）：**187/187**（基线 176 + 新增 12 − 改写 1）；`--strict` 通过。

## 6. 合入

- [x] 6.1 `wt/reject-unsupported-format` 合入 `master`（`git merge wt/reject-unsupported-format`），合入后主工作树 `cargo test` 全量通过。验证：合并完成（fast-forward 至 `e7ad078`）且测试条数与 5.1 一致——**187/187**（串行口径；沙箱内需把 `TEMP`/`TMP` 指到工作区内，否则 `proxy_spawn_uses_derived_pipe_name` 因 `%TEMP%` 写不进去而误报）。

## Workflow follow-up

- 归档需用户明确指示，且先满足归档门禁（tasks 全部关闭、wt 已合入 `master`）。