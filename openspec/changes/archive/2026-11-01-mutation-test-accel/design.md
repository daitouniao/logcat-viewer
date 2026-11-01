# Design：mutation-test-accel

## Context

现状与实测数据见探索笔记 `openspec/notes/MUTATION-TEST-ACCEL.md`（耗时拆分、子集探针、决策记录）。要点：105 个变异点串行执行，每轮 = CLI/编译 ~5s + 全量测试 14.7s；子集运行实测 1.5~2s；历史 KILLED 率 ~97%、每个变异通常只被 1~3 条用例抓住。改动物：入库脚本 `scripts/mutation-test.py`、git-ignored 的反射 runner（`.tmp-test/tstrun`）、`docs/TESTING.md`、`docs/UT-AUDIT.md`。

第一轮决策已由用户确认（映射混合式 / tier-1 含消费方 / 允许改 runner / 双口径 / 走 change / 不跑探针 2）。第二轮 5 项已确认按偏向定稿（2026-10-31）：fail-fast 环境变量开关、默认两级 + `--full` 回退、KILLED 只留首败、一致性校验轮纳入 tasks、文档分工。

## Goals / Non-Goals

**Goals:**

- 全程 35 分钟 → ~15 分钟，不牺牲判定正确性（见下）。
- runner 日常用法默认行为不变（不加开关时与现在逐字节等价）。
- 映射表漂移可自愈：映射失效时安全降级为全量，不产生错误结论。

**Non-Goals:**

- 不做多 worker 并行（方案 C）。
- 不改测试用例本身、不碰产品代码。
- 不追求 tstrun 口径与 dotnet test 口径的统一（既有差异由 TESTING.md 声明承载）。

## Decisions

### D1 两级调度放在脚本侧，runner 只加 fail-fast 开关

调度逻辑（先子集、失败即 KILLED、全过回退全量）全部在 `mutation-test.py` 实现。runner 只新增一个「首个失败即退出」能力，由环境变量 `TSTRUN_FAIL_FAST=1` 启用。

理由：脚本已有解析 `passed: X failed: Y` 汇总行的契约，两级调度只改变「跑哪些用例」，不改输出格式；runner 改动越小，TESTING.md §3.1 模板同步与 756 交叉验证的回归面越小。备选「runner 内置两级调度」被否：runner 无法感知变异点与文件的对应关系，且 git-ignored 重建即丢。

汇总行格式保持不变（只提前退出）：`passed: X failed: Y skipped: Z`，脚本解析逻辑零改动。

### D2 fail-fast 用环境变量而非命令行参数

runner 现有 `args[0]` 语义是类名过滤子串（TESTING.md §3.1），再占一个位置会产生歧义（过滤串本身可能含任意字符）；环境变量与现有用法完全正交。默认未设置时行为与现状逐字节等价——保护日常 `dotnet run` 用法与 756 口径交叉验证（已确认）。

### D3 runner 过滤参数支持逗号分隔多子串

现状 `args[0]` 只支持单子串（`type.Name.Contains`）。tier-1 子集往往不是单子串能覆盖的（如 LogDocument 相关 = `LogDocumentTests` + `LogDocumentZeroCopyTests` + `FilterEngineTests` + `CrashAggregatorTests`）。扩展为：过滤串含 `,` 时按逗号拆分、任一子串命中即入选。

理由：一次进程启动跑齐整个子集（~2s），比每类一次进程（每个再付 ~1s CLI 启动）快且省；向后兼容（不含逗号时语义不变）。该扩展同样回写 TESTING.md §3.1 模板。

### D4 映射 = 约定推导 + 显式覆盖表（第一轮决策 #1/#2 的落地）

- **推导**：被变异文件 `Services/X.cs` → 测试程序集中类名匹配 `X`（前缀式 Contains，与 runner 过滤同规则），自动命中 `<X>Tests` / `<X>*Tests`。
- **覆盖表**：脚本内显式 dict，承担两件事——① 消费方类补挂（如 `LogDocument.cs` 补挂 Filter/Crash 相关类）；② 推导失效时的手工修正。
- **安全降级**：推导结果为空且覆盖表无条目 → 该变异直接全量（打 `[FULL]` 标记），绝不跑空子集。

理由：约定推导零维护、覆盖大多数单文件场景；显式表复用脚本既有维护模式（TEXT_MUTATIONS/LINE_MUTATIONS 同类结构，漂移问题的处理先例是「行号 + 内容双校验 + 安全失败」，D4 沿用同一哲学）。

### D5 默认两级模式，`--full` 开关回退（已确认）

`python scripts/mutation-test.py` 默认走两级；`--full` 强制每变异全量（= 现行为，留作一致性校验与争议仲裁口径）。汇总输出各 tier 占比（SUBSET-HIT / FALLBACK / FULL），用于校准 D4 覆盖表与验证预算假设（「90% KILLED 子集内命中」未实测，落地后以实际占比校准）。

### D6 KILLED 只保留首个失败详情（已确认）

fail-fast 下子集输出只有首个失败用例。KILLED 判定只需 `failed >= 1`；需要完整杀手名单时对单条变异用 `--full` 复跑（全量不 fail-fast，输出全量 FAILURES）。与既有 UT-AUDIT「抓住它的用例」记录方式兼容。

### D7 超时分级

子集轮 600s、全量轮维持 1800s（现有值）。变异导致死循环时子集轮更快超时，缩短坏变异的占用时间；阈值仍远大于正常耗时（子集 ~2s / 全量 ~20s），不会误杀。

## Risks / Trade-offs

- [映射漂移（消费方新增/测试类改名）→ 该变异回退全量，变慢但不失真] → 汇总占比暴露 FALLBACK 数量；覆盖表与 LINE_MUTATIONS 行号同批核对（既有先例：改 FilterEngine 后核对行号）。
- [假 SURVIVED] → 结构上排除：SURVIVED 判定只出自全量轮；子集全过不判存活。一致性校验轮（tasks）做双口径对照兜底。
- [runner 是 git-ignored，重建丢失 fail-fast/逗号过滤改动] → TESTING.md §3.1 模板同步更新（tasks 内显式任务）+ 改后 756 交叉验证（既有必做动作）。
- [子集口径的 KILLED 与全量口径的杀手数不同（fail-fast 只见首个）] → 接受，口径声明写进 UT-AUDIT（第一轮决策 #4）。
- [沙箱/安全软件导致的耗时波动] → 预算按本机实测，脚本输出每轮耗时与占比，波动可观测；不设硬性性能验收线，只要求「显著快于 --full 同批对照」。
- [首次上线映射表覆盖不全 → 前几轮 FALLBACK 偏多、提速低于预期] → 属可观测的降级而非错误；按占比补覆盖表即可。
- [worktree × runner 指向：脚本 REPO 取脚本所在目录（worktree），而 runner 的 ProjectReference 硬编码指向主工作树 → apply 期在 worktree 验证脚本行为时，变异改 worktree 源、编译主树代码 → 全部假 SURVIVED（脚本 docstring 明文警告的坑）] → apply 期核对指向，验证前临时改指本 worktree 的测试工程、验完还原（tasks 0.1 显式步骤）。

## Open Questions

- 覆盖表初值的消费方清单（哪些文件挂哪些类）在 apply 期按测试类清单（2026-10-31 已枚举，60+ 类）落定，属实施细节不阻塞评审。
