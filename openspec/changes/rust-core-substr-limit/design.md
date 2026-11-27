# Design

- 状态：待评审
- 日期：2026-11-27
- 关联提交：（编码后补）
- 验收结论：（用户验收后补）

## Context

子串档的正文匹配在 `CompiledMsg::matches`（`crates/core/src/filter.rs`）里对每行正文按 `op` 遍历 `needles` 逐个求值，k 个取值 = 每行 k 轮全量正文扫描，整体 O(k·N)。正则归档时已实测同条数子串档在约 306 万行 / 184 MB 真实样本上 1 / 4 / 16 条 ≈ 0.64 / 1.49 / 5.31 秒，边际约 0.3 秒/条（实测取自正则档 `rust-core-msg-regex` 的归档记录）。正则侧据此收了 16 条 / 2000 字符两道闸，子串侧当时有意未动（"不动已归档的条件模型与组合"）。本卡补上这一侧。

## Decisions

### D1 上限取值：16 条 / 2000 字符，与正则档同值

子串档取 `SUBSTR_MAX_VALUES = 16`、`SUBSTR_MAX_CHARS = 2000`，数值直接对齐正则档的 `REGEX_MAX_VALUES` / `REGEX_MAX_CHARS`。

- 依据：子串档 16 条全量扫描 ≈ 5.3 秒已到"单次调用明显但可接受"边界，与正则档同量级；取同值后两档在 MCP 文案与 spec 只记一套数。
- 单条 2000 字符：logcat 单行正文通常 < 4 KB，needle 比正文还长必然不命中；2000 远超有意义关键词，纯防塞超长串放大 JSON 入参。
- 影响：常量在实现口径固定，不因源文件或调用方而变（与正则一致）。
- 判定基准：单条长度按**原始取值**的 `chars().count()` 计（与正则档 `value.chars().count()` 同口径），`to_lowercase` 折叠不改变判定基准。

### D2 治理范围：仅 message 子串档

只给 `msg` 的子串取值加规模闸，`tags` / `pids` / `tids` 不动。

- 依据：`tags` 命中在 `CompiledTag::hit_ids` 只对 tag 字典预计算一次（成本 = |dict| × |values|，与文档行数无关）；`pids` / `tids` 编译为 `HashSet<i32>`，每行 O(1)。三者均无 O(k·N) 乘积扫描敞口。
- 被排除：给 `tags.values` 同批加条数上限——一致性更全，但 tag 实际无全量扫描成本，属为假想问题扩面，偏离最小切片。

### D3 错误码：`SUBSTR_INVALID` = 18，形态对齐 17

`error.rs` 新增 `pub const SUBSTR_INVALID: i32 = 18;`，`code_name(18) => "SUBSTR_INVALID"`，`param_keys(18) = ["reason"]`；`reason` 取 `too_many` / `too_long` 两值（子串无语法、无编译规模，故不含 `syntax` / `too_size`）。

- `index` 附注键与正则档同形：新增 `pub const SUBSTR_ERROR_INDEX_KEY: &str = "index";`（值与 `REGEX_ERROR_INDEX_KEY` 同为 `"index"`，独立命名保持两档解耦）。
- 拒绝形态（专用码而非 `INVALID_ARG`）经评审取「对齐正则档」：调用方需区分"减词"与"截短"两种修正动作，专用码 + `reason` 比复用 `INVALID_ARG` + `field` 更可寻址；18 段现成（0–7 状态类、10–17 失败类，18 顺延）。

### D4 校验落点：`new_substring` 入口，编译期同步

两道闸置于 `CompiledMsg::new_substring`（`filter.rs`）开头，与 `new_regex` 同位；`new_substring` 由 `compile` → `compile_msg` 在 `query::open` 路径调用，故超限在开启查询时同步返回 `SUBSTR_INVALID`，与正则档同一拒绝时机。

- 不改 `new_substring` 内既有的 `needles` 折叠、`has_empty` / `all_empty` / `always_true` 逻辑；闸只在入参 `values` 的条数与单条 `chars().count()` 上判定，先于折叠构造。
- `exclude` 与规模闸正交：超限在求值前拒绝，`exclude=true` 的超限条件同样被拒。

### D5 检查顺序与归因

先条数、后逐条长度，命中即返：

1. `values.len() > SUBSTR_MAX_VALUES` → `reason=too_many`，**不带** `index`（整组拒绝无法归因单条）。
2. 逐条 `value.chars().count() > SUBSTR_MAX_CHARS` → 第一条超限即 `reason=too_long` + `index=该下标`（下标按 0 起，与正则档 `enumerate` 同；第二条超限即 `index=1`）。

与正则档归因口径一致：可归因到单条才附 `index`，整组类（此处为条数）不附。

### D6 red-first：既有必红断言

`crates/core/src/error.rs` 的 `error_code_table_values_are_stable` 现有断言 `code_name(18) == "UNKNOWN"`（段间留空校验）——新增 18 后此断言必失败，正是本卡 red-first 的现成落点：先改该断言与 `error_params_schema_is_stable` 加 `(18, ["reason"])`，实现码前它必红。子串上限的拒绝用例（17 条 / 2001 字符）先写按正确语义必失败的测试，再落 `new_substring` 闸。

- 期望值口径：边界（恰 16 条 / 恰 2000 字符通过、17 条 / 2001 字符拒绝）为契约字面量，直接断言，**禁止拿 Rust 现状输出回填**。
- 真·必红仅 Rust 一处（`error.rs` 的 `code_name(18) == "UNKNOWN"`）；C# 侧无因 18 必红项——`LogcoreBindingTests` 的 `Code_names_match_d15_table` 为抽查式（仅点 0/4/6/10/16/17，无遍历/计数断言），本卡对其为对称增补 `Name(18)`，非修红。

### D7 测试落点

- 新建 `crates/core/src/filter_substr_tests.rs`，照 `filter.rs` 末尾以 `#[path = "../../filter_substr_tests.rs"] mod substr_tests;` 挂进 `filter::tests`（路径解析与 `filter_regex_tests.rs` 先例一致），复用其 `expr` / `fixture` / `lines_data` / `rows` 等私有辅助；`filter.rs` 自身不新增 `#[test]`。
- 辅助归属已核实：`expr` / `rows` / `lines_data` / `fixture` 定义在 `filter::tests` 父模块（非 `regex_tests` 子模块），`regex_tests` 即经 `#[path]` 同构挂进该父模块，故 `substr_tests` 以 `use super::*` 可直接复用，无跨模块断链。
- 拆分前后 `cargo test --workspace -- --test-threads=1` 用例总数应一致（除本卡净新增），验证挂载正确。
- `query::open` 超限口径用例写在 `crates/core/src/query.rs` 的 `mod tests` 内（`Fixture` / `open` / `assert_err` 为该模块私有夹具，跨模块复用需另造，就近落点，与 regex §4.5 同例）。

### D8 C# 两处码表同步

`logcat-viewer/Services/Logcore/LogcoreContracts.cs` 与 `logcat-next/Next.Interop/LogcoreErrorCodes.cs` 各加 `SubstrInvalid = 18` 常量与 `Name(18) => "SUBSTR_INVALID"` 映射；`tests/logcat.Tests/LogcoreBindingTests.cs` 在既有 `Name(17)` 断言旁对称加 `Name(18) == "SUBSTR_INVALID"`。走查 GUI 错误兜底按码名渲染为 `SUBSTR_INVALID（18）`，GUI 无产生子串超限的入口（过滤 JSON 由宿主拼），故不扩 `Loc` 文案。

### D9 cabi 无改动

`crates/cabi` 经 grep 确认不硬编码错误码名常量、不引用 `code_name`/`param_keys`/码值，错误经 `CoreError::to_json` 透传 `code` 与 `params`，新增 18 自然随 JSON 出栈；本卡不新增 cabi 导出、不新增 cabi 测试，亦无 cabi 侧因 18 必红的断言（已两轮核实 cabi 零码表引用）。

## Risks / Trade-offs

- 与正则档取同值意味着未来若一档回调，两档需一并评审；用独立常量名（`SUBSTR_*` vs `REGEX_*`）保留分调能力，当前取同值。
- `SUBSTR_INVALID` 与 `INVALID_ARG` 职责边界：结构性非法（未知 mode/op、负 pid、空 tag 值）仍走参数错误；仅"规模超限"走 18。spec 两条需求与「参数类无效条件被拒绝」互不重叠，不产生双码拒绝。

## Open Questions

无。四项数值口径（16 / 2000 / 仅 message / 专用码 18）已在评审拍定。
