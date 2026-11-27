# Proposal

## Why

子串档 `msg.values` 至今无条数与长度上限：`CompiledMsg::new_substring` 对每个取值按行做一轮全量正文扫描，k 个词即 O(k·N)。正则档已在 `rust-core-msg-regex` 收口 16 条 / 2000 字符 + `REGEX_INVALID`(17)，但子串档是同一乘积面上更大的一档——同样的条数与长度无任何闸口，一次调用即可用上千个子串词打满扫描。这是该 change 归档时在 `openspec/notes/BACKLOG-MCP-AND-FORMATS.md` D4 显式登记的遗留敞口（"子串档上限宜与 MCP 侧调用约束同批治理"），也是当前 Rust core 过滤链上唯一"已知、有实测依据、不夹带返工"的最小缺口。补齐后过滤契约的规模面闭合，MCP 侧文案只需讲一套上限。

## What Changes

- `msg` 子串模式（缺省）新增两道规模闸：单条取值字符数 ≤ 2000、同一正文条件取值条数 ≤ 16；取值对齐正则档（16 / 2000）属实现选择，契约层面两档各自独立钉数。
- 新增拒绝码 `SUBSTR_INVALID`(18)，`reason` 取 `too_many` / `too_long`，能归因时附 `index`；在开启查询时同步拒绝，不产出结果集、不改源句柄状态。
- 只治 message 子串档：`tags.values` 走字典预计算（命中集合成本与文档行数无关）、`pids`/`tids` 为整数集合（O(1) 求值），无乘积扫描敞口，本卡不加其上限。
- 不动既有语义：空串 / 空词 / `always_true` / `exclude` 求值逻辑与结论完全不变；正则档（17）路径不串档。
- C# 两处错误码镜像表各加一条常量与映射，并扩 `LogcoreBindingTests` 钉值断言。

## Capabilities

### New Capabilities

无。子串上限是正文条件规模面的补全，与既有正文契约同源，另立 capability 会把同一 `msg` 字段的两套规模闸拆成两处互相引用。

### Modified Capabilities

- `logcore/filter`（`openspec/specs/logcore/filter/spec.md`）：
  - ADDED「子串条件的规模上限」（16 条 / 2000 字符，显式 `substring` 与缺省同等适用）
  - ADDED「超限子串条件被拒绝」（`SUBSTR_INVALID` = 18，`reason` = `too_many` / `too_long` + 可归因 `index`）
- `logcore/query`（`openspec/specs/logcore/query/spec.md`）：MODIFIED「查询开启与结果集」——把原句对错误码的逐一举例收敛为总则（"按该过滤契约定义的对应错误口径拒绝，具体码与原因为过滤契约给出"），以后新增拒绝码不再回改本条；新增「超限子串不产生查询」场景。

分类归属不变：该契约由 Rust core 执行，已居 `logcore`，不涉及跨分类迁移。

## Impact

- **代码**：`crates/core/src/error.rs`（新增 18 与 `param_keys`，修正既有断言 `code_name(18) == "UNKNOWN"` → `"SUBSTR_INVALID"`）、`crates/core/src/filter.rs`（`new_substring` 入口两道闸 + `SUBSTR_MAX_VALUES` / `SUBSTR_MAX_CHARS` 常量）、`crates/core/src/query.rs`（开启查询对子串超限的错误口径测试）、`crates/core/src/filter_substr_tests.rs`（新增，照 `source.rs` 的 `#[path]` 先例由 `filter.rs` 挂载本卡测试）。
- **C# 侧**：仅两处码名镜像表补一条常量与映射（`logcat-viewer/Services/Logcore/LogcoreContracts.cs` 与 `logcat-next/Next.Interop/LogcoreErrorCodes.cs`，两文件头注释均声明"与 core 共用、不得漂移"）；`tests/logcat.Tests/LogcoreBindingTests.cs` 扩 `Name(18)` 断言。不改 FFI 签名、不改 UI 交互（过滤条件已是 JSON 字符串通道，子串上限对宿主透明）。
- **依赖与产物**：无新增 crate 依赖，`Cargo.lock` 不变，`logcore.dll` 体积无显著变化（仅校验分支）。
- **文档**：`docs/TESTING.md` §6 的 Rust 数字（基线串行 478：core 445 + cabi 33；C# 923）按联动同批更新；README×2 只挂 C# 口径，本卡 C# 用例数不变故不动。
- **cabi**：不引用错误码名常量（错误经 `CoreError::to_json` 透传，已核实零码表引用），本卡无 cabi 改动与新增测试。
- **非目标**：`tags.values` 条数上限、导出/上下文接口、采集源过滤、编码转换（C7）、poisoning（C4）、UI 输入框、MCP 侧调用尺寸约束（D5/D9，另卡）。
- **与在途 change 的关系**：`mcp-node-shell` / `avalonia-*` 均不触碰 `filter.rs` 校验路径与错误码表，本卡与它们无文件重叠。
- **验收依据**：本卡无 UI 面，以「Rust 全量（基线 478 + 新增）与 C# 全量（923）无新增失败」为验收材料，不做 GUI 手工验收。
