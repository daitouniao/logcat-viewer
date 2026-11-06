# Design

## Context

本 change 的实质是**移植一个已验收、已归档 change 的丢失提交**，而非重新设计修复。事实链（均以 git 取证）：

- 修复本体 = 提交 `5a5c7bc`（`fix(load): 分块加载收尾强制消费…`，2026-11-02），位于分支 `wt/fix-chunked-tail-loss`，该分支是**唯一**包含它的分支；主分支（master）从未合并它。`git tag --contains 5a5c7bc` 仅命中 `V0.1.7-r1-fdd`（从该分支打出）——即主分支与 `V0.1.7` 构建都缺此修复。
- 修复的父提交 `ec675c8` 在主分支历史上，且 `git diff ec675c8..HEAD -- frmMain.Load.cs` 为空：**修复基线至今主分支上该文件零漂移**，提交可干净套用。
- `git diff V0.1.7..V0.1.7-r1-fdd --stat` 恰为该提交的两个文件（`frmMain.Load.cs` +25 行级、`frmMainChunkedTailTests.cs` 新增 78 行级），进一步佐证 r1-fdd = 主线 + 修复。
- 行为契约已随归档进入主 spec（`openspec/specs/chunked-file-load/`：「已发布前缀稳定」的收尾整批上屏、「加载完成后状态栏统计反映最终值」），故本 change 无 spec delta（`.openspec.yaml` 已声明 `skip_specs: true`）。
- 修复提交自带的 2 条用例当时在测试工程上通过（口径 793/793）；基线以来测试工程有演进（`TestHelpers.cs`、`logcat.Tests.csproj` 等），移植后需全量回归确认。

提交内容摘要（详见 `git show 5a5c7bc`）：

1. `RunChunkedLoadAsync` 的 `finally`：`completed` 且 `_workerGen == gen` 时，`TakeAll()` 无条件取走缓冲全部发布操作并应用；缓冲为空且 `_doc` 已接管时补一次 `UpdateStat()`。
2. `FlushLoadOps` 的应用逻辑抽取为私有方法 `ApplyLoadOps(ops)`，节流定时器与收尾路径共用（接管判断不可旁路——≤ 首块的小文件可能从未有过成功 flush，收尾是其唯一上屏机会）。

## Goals / Non-Goals

**Goals:**

- 修复提交内容落到预发布分支 `V0.1.8` 代码树（apply 实施分支，2026-11-05 用户决策，见 D2），随带的 2 条自动化用例恢复生效。
- 全量回归通过，口径数字如实报告。
- （2026-11-06 更新）原「修复随后回流主分支」目标已由用户指令取消，见 D4：合入目标分支定为 `V0.1.8`，master 缺修复的问题另行安排。

**Non-Goals:**

- 不重写、不"改进"已验收的修复（节流窗口、块长、发布粒度、取消语义均维持 `fix-chunked-tail-loss` 定稿状态）。

## Decisions

### D1：移植方式 = cherry-pick `5a5c7bc`，而非重新实现

- **采用**：在编译 `V0.1.8` 分支的 worktree 中执行 `git cherry-pick 5a5c7bc`（实施分支见 D2）。理由：① `frmMain.Load.cs` 自修复基线零漂移（主分支与 `V0.1.8` 上该文件均与 `ec675c8` 一致），cherry-pick 零冲突、内容与已验收版本逐字节一致，不引入"重新实现"带来的二次评审与回归面；② 保留原提交信息中的诊断与测试口径记录，防断链；③ 修复已验收过，重写只增加风险不增加价值。
- **备选（否）**：照 `fix-chunked-tail-loss` 的 design 重写一遍——与已验收实现等价却要多一轮评审与测试；merge `wt/fix-chunked-tail-loss` 分支——效果同 cherry-pick，但会把分支的独立历史并入主线，不如 cherry-pick 单提交干净。

### D2：apply 实施分支 = 预发布分支 `V0.1.8`（2026-11-05 用户决策，取代原「change 专属 `wt/*` worktree」方案）

用户当轮指令：本 change 使用分支 `V0.1.8`，apply 后提交到 `V0.1.8`。

- **采用**：apply 在 `V0.1.8` 分支已有的 worktree 中实施（由 `git worktree list` 定位，不记本机路径）；cherry-pick 与测试均在该 worktree 完成，提交直接落在 `V0.1.8`。
- **合规性**：AGENTS.md §2.5 规定预发布分支「只做 bug 修改」——本 change 恰为 bug 修复，符合该约束；`V0.1.8` worktree 与主工作树隔离，仍满足 §2.3「主工作树保持干净」的实质目的。
- **备选（否）**：原方案 `wt/fix-chunked-final-flush` 专属 worktree 移植后再并入 master——该 worktree 已建成并完成移植（提交 `6c76576`），但用户要求本 change 落在预发布分支，故改道。`6c76576` 与本轮在 `V0.1.8` 上的落地提交内容逐字节一致（同为 `5a5c7bc` 的移植），可互为佐证。
- **`V0.1.8` 与主分支的结构差异不影响本次移植**：`V0.1.8` 落后主分支 4 个提交（含拆分三工程 `6765c88`），但 `git diff V0.1.8 master -- frmMain.Load.cs` 为空，修复触及的文件零漂移；测试工程在两边均为 SDK 隐式 glob，新增用例文件无需改 csproj。

### D3：遗留分支 `wt/fix-chunked-tail-loss` 在修复落地后删除

该分支唯一存在的意义就是承载那个丢失的提交；提交回流主分支后分支失去价值，留着反而制造"修复在分支上"的假象（本次事故的根因形态）。`git branch -d` 属 git 写操作，实施时请示授权。

### D4：回流主分支——原案已否决（2026-11-06 用户指令）

**当前结论**：合入目标分支定为 `V0.1.8`（2026-11-06 用户指令）；回流 master **取消**，不再属于本 change，tasks 3.1 已改为决策性关闭记录。master 缺修复的问题由维护者另行安排。

**原案留档（2026-11-05，已否决）**：apply 落 `V0.1.8` 只解决「随 0.1.8 发布」，不解决「主分支缺修复」——后者正是本次事故的根因形态（修复只在 `wt/*` 分支上）。故曾计划 `V0.1.8` 稳定版（`V0.1.8` tag）发布之后把 `fdac37d` cherry-pick 回 master，并要求回流先于删除 `V0.1.8` 分支（AGENTS §2.5 稳定版后删分支），否则回流源只剩 tag 可达。否决后该时序约束自然失效。

### D5：`wt/fix-chunked-final-flush` 分支与其 worktree 的处置

该分支/worktree 是改道前（D2 原方案）的产物，已完成同一修复的移植（`6c76576`）。改道后其唯一价值是作为内容佐证，tasks 3.2 增加「回流完成后清除该分支与 worktree」——清除属 git 写操作，实施时请示授权。当前**保留不删**，避免在多处同时删分支时混淆。

## Risks / Trade-offs

- [测试工程自基线演进，移植的 2 条用例可能不再编译/通过] → 全量 `dotnet test` 回归；若用例依赖 `TestHelpers` 既有口径漂移，按当前工程约定微调用例适配（行为断言不变），在 tasks 中如实记录。
- [cherry-pick 后提交 ID 变化，归档 change 里记录的原 ID 仍指旧提交] → 属正常现象；新提交信息沿用原修复的 `fix(load)` 前缀与描述，注明系移植落地。
- [`V0.1.7-r1-fdd` 已含相同修复，落地后主分支与该 tag 内容在 `frmMain.Load.cs` 上趋同] → 无需处理；tag 双轨规则照旧，后续发版按阶段 8 用户指令执行。

## Migration Plan

单提交移植，无数据迁移、无 API 变更。回滚 = revert 对应 cherry-pick 提交。

## Open Questions

（无——D1/D3 的取舍基于 git 取证即可定，均不改变行为契约与任务拆分。）
