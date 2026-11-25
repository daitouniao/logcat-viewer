# Proposal

## Why

MCP 侧的日志分析需要模式检索（`ANR in com\..*`、"排除含 GC 的行"这类），子串档表达不了；而 core 当前对 `msg.mode = "regex"` 显式拒绝，主规范还钉着"本代以正则提交 MUST 被拒绝"，等于把检索面完全留给宿主各自实现——宿主是 Node（MCP）与 C#（UI），与 core 的正则方言不同源，一旦各写各的就会出现"同一份条件、两种行集"。方言边界与非 UTF-8 / 空正文 / 不可信 pattern 三处卡点已在 `openspec/notes/RUST-CORE-REGEX-DIALECT.md` 以探针实测收口（P1–P5），可以落到契约。

## What Changes

- `msg.mode` 接受 `"regex"`（缺省与 `"substring"` 继续走子串档，行为与结论完全不变）；正则档只对记录正文求值，匹配作用域、`op`（全部/任一）、`exclude`、结果集形态与分页协作沿用既有需求。
- 正则档的正文归一化沿用既有口径：非法 UTF-8 按替换字符参与匹配（与子串档 `filter_msg_lossy_invalid_utf8` 已钉结论同源）；每行归一化一次、该行的多条 pattern 复用。
- 大小写：`case: false`（缺省）时正则档按引擎的大小写折叠求值，`case: true` 时按原始码点精确求值；pattern 内自行写的内联 flag 按引擎行为生效。
- 空正文行在正则档按**空文本参与求值**：`.*`、`^$`、`\s*`、`a*` 命中，`\b` 不命中。**BREAKING（仅相对既有文字）**：原「正文多值与排除」中"条件值为非空词时空正文视为未命中"须限定作用域为子串模式，属 MODIFIED 而非纯 ADDED。
- 新增拒绝面 `REGEX_INVALID`（17）：语法错、超长、编译规模过大在 open 时同步拒绝，`reason` 取 `syntax` / `too_long` / `too_size` 三值区分，不产出结果集、不静默降级。
- 不可信 pattern 的资源边界：单条 pattern 字符数上限、条数上限、编译规模上限（收紧引擎默认值）。
- 移除既有拒绝：「无效条件被拒绝」中"以正则模式提交 MUST 被拒绝"场景作废，该条整条 REMOVED 后由「参数类无效条件被拒绝」逐条承接（openspec 不允许 MODIFIED 丢场景）；`"word"` 等未知 mode 仍按 `INVALID_ARG` 拒绝。
- 新增 Rust 依赖 `regex`（连带 `regex-syntax` / `regex-automata` / `aho-corasick` / `memchr`），`Cargo.lock` 27 → 31 包，dll 体积增约 1.0–1.3 MiB（探针实测口径，真实增量在 apply 阶段带依赖重编复核）。

## Capabilities

### New Capabilities

无。本 change 不新建 capability：正则档是正文条件的一种匹配方式，与既有正文契约同源同边界，另立 capability 会把"同一份 `msg` 字段的两套语义"拆成两处互相引用。

### Modified Capabilities

- `logcore/filter`（`openspec/specs/logcore/filter/spec.md`）：
  - ADDED 正则档参与过滤（mode 合法化 + 正文归一 + 空正文/空 pattern + 大小写 + 方言边界 + 字面量同结论 + 规模上限 + `REGEX_INVALID` = 17）
  - ADDED 「参数类无效条件被拒绝」承接原「无效条件被拒绝」的全部参数类口径
  - REMOVED 「无效条件被拒绝」——其中"以正则模式提交 MUST 被拒绝"场景作废；openspec 的 MODIFIED 禁止丢场景，故整条作废重建（内容除该场景外逐条保留，「正则模式被拒」→「未知正文匹配方式被拒」）
  - MODIFIED 「正文多值与排除」：空正文条款限定为子串模式
  - MODIFIED 「正文大小写等价」：作用域标注为子串档，避免与正则档折叠表混读
- `logcore/query`（`openspec/specs/logcore/query/spec.md`）：MODIFIED 「查询开启与结果集」——原文把"过滤表达式非法"一律钉为参数错误，正则非法改用专用码后该句不再自洽，改为按过滤契约的对应错误口径拒绝，并补一条「非法正则不产生查询」场景。

分类归属不变：该契约由 Rust core 执行，已居 `logcore`，不涉及跨分类迁移。

## Impact

- **代码**：`crates/core/Cargo.toml`（加依赖）、`crates/core/src/filter.rs`（`msg.mode` 校验、`CompiledMsg` 的正则分支、每行归一化接缝）、`crates/core/src/error.rs`（新增 17 与 `param_keys`，并修正既有断言 `code_name(17) == "UNKNOWN"`）、`crates/core/src/query.rs`（开启查询对正则非法的错误口径测试）。
- **C# 侧**：仅 `logcat-viewer/Services/Logcore/LogcoreContracts.cs` 的码名表补一条常量与映射；不改 FFI 签名、不改 UI 交互（过滤条件已是 JSON 字符串通道，正则档对宿主透明）。
- **依赖与产物**：`Cargo.lock` 新增 4 包；`logcore.dll` 体积上浮（口径见上）。
- **文档**：`docs/TESTING.md` §6 的 Rust 数字（基线 Rust UT 425：core 392 + cabi 33；C# 923）与 README 徽章按联动同批更新。
- **非目标**：tag 条件开放正则、捕获组抽取与命中位置回传（属行变换，另立 capability）、导出与上下文接口的正则参数、采集源正则、编码转换（C7）、UI 输入框与 `validate_pattern` 类无句柄校验导出、稳定版发版。
- **与在途 change 的关系**：`mcp-node-shell` 明确"不接入日志数据能力、不定义业务工具面"，`avalonia-app-storage` 只动宿主持久化，`avalonia-ui-shell` 明确不改 `crates/` 与 C ABI——三者与本 change 无文件与契约重叠。
