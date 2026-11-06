# Tasks

## 1. Apply 环境与移植

- [x] 1.1 **Worktree 标识**：本 change 在分支 `V0.1.8`（预发布分支）的 worktree 中实施（worktree 路径以 `git worktree list` 定位）。分支选用于 2026-11-05 由用户指令确定（「fix-chunked-final-flush 使用分支 V0.1.8，apply 后提交到 V0.1.8」），取代原 `wt/fix-chunked-final-flush` 方案，见 design D2。
- [x] 1.2 在 `V0.1.8` worktree 中执行 `git cherry-pick 6c76576`（`5a5c7bc` 的移植版，内容逐字节一致）；确认无冲突、`frmMain.Load.cs` 的 `ApplyLoadOps` 抽取与 `finally` 强制消费两处改动就位、`tests/logcat.Tests/frmMainChunkedTailTests.cs` 就位。验证：`git show --stat HEAD` 恰为原提交两文件。（零冲突，落地提交 `fdac37d`，恰为两文件；`V0.1.8` 与 master 上 `frmMain.Load.cs` 无差异）
- [x] 1.3 记录改道前的产物处置：原 `wt/fix-chunked-final-flush` 分支与其 worktree（提交 `6c76576`）保留不删，作为内容佐证，收尾时随 3.2 一并清除（design D5）。验证：`git worktree list` 中两项共存，均非脏工作区。

## 2. 回归验证

- [x] 2.1 全量 `dotnet test`：移植的 2 条用例（`frmMainChunkedTailTests` 下「加载完成后视图行数与文档行数一致」「加载完成后状态栏总数等于文档行数」）通过。验证：`dotnet test` 全量 **793/793 全过，0 失败 0 跳过**；`--filter FullyQualifiedName~frmMainChunkedTailTests` 单独复核 **2/2**（受影响类 `LogListViewTests`、`LogDocumentTests` 含在 793 全量内）。
- [x] 2.2 全量无新增失败核对与口径报告：`V0.1.8` 为拆分前单工程结构，本地基线为 V0.1.7 的 **791/791**；本 change +2 → **793**，实测吻合，无新增失败。补跑 en-US 口径（`bash scripts/run-tests-en-us.sh`，复刻 CI 语言环境）：**793/793 全过，0 警告 0 错误**。两口径均无新增失败。
- [x] 2.3 真实窗体人工冒烟：打开一份 ≥ 32 MB 的本地日志（跨越首块 + 稳态块边界），无过滤确认列表行数与状态栏「总/命中」一致、尾块内容可见。验证：人工确认结果记入验收材料。（需用户执行，AI 不可代劳）——2026-11-06 用户确认通过（「确认ok」，随归档指令一并给出）。

## 3. 并入与收尾（均需用户当轮授权的 git 写操作）

- [x] 3.0 **合入目标分支 = `V0.1.8`**（2026-11-06 用户指令）：修复提交 `fdac37d` 直接落在目标分支 `V0.1.8` 上（2026-11-05 apply 授权内完成），无另行合并动作。验证：`git branch --contains fdac37d` 命中 `V0.1.8`。
- [x] 3.0.1 预发布 `V0.1.8-pre2` 已发布（用户 2026-11-05 指令）：版本号 5 处同步（提交 `01b58ae`）、fdd 包 `logcat-V0.1.8-pre2-win-x64-fd.zip`（1.9 MB / 28 项，单 exe，无 pdb 无 coreclr，`ProductVersion=0.1.8-pre2`）、tag `V0.1.8-pre2`（annotated）。人工冒烟（2.3）由用户在本包上执行。
- [x] 3.1 **已取消（2026-11-06 用户指令）**：原任务「回流主分支」（design D4 原案：`V0.1.8` 稳定版发布后把 `fdac37d` cherry-pick 回 master）不再执行——合入目标分支定为 `V0.1.8`，回流 master 不再属于本 change，master 缺修复的问题另行安排；design D4 已改写为否决记录。验证：无需验证（决策性关闭）。
- [x] 3.2 清理遗留分支与改道产物（原排在 3.1 回流之后；3.1 取消后已无时序依赖，可随时执行，仍属需当轮授权的 git 写操作）：删除 `wt/fix-chunked-tail-loss`（唯一提交 `5a5c7bc` 已由本 change 承接，design D3）；删除 `wt/fix-chunked-final-flush` 分支及其 worktree（design D5）。`V0.1.8` 分支与其 worktree 的收尾归预发布分支生命周期（AGENTS §2.5，随稳定版发布执行），见 Workflow follow-up，不在本 change 跟踪。验证：`git branch --list 'wt/fix-chunked*'` 为空。——2026-11-06 随归档授权执行完毕：worktree `logcat-wt-fix-chunked-final-flush` 已移除，分支 `wt/fix-chunked-final-flush`（was `6c76576`）与 `wt/fix-chunked-tail-loss`（was `5a5c7bc`）已删（`-d` 被拒后按授权改 `-D`，内容已由 `V0.1.8` 上的 `fdac37d` 承接）。

## Workflow follow-up（归档阶段，非跟踪任务）

- 验收通过后等用户明确归档指令；归档时同批 `openspec archive fix-chunked-final-flush`。
- **归档不删 worktree**：本 change 借用的 `V0.1.8` worktree 属预发布分支生命周期（AGENTS §2.5），随 `V0.1.8` 转稳定版后统一收尾，不随 change 归档删除；与 §2.3「归档同步清除 worktree」的差异已在 design D2 说明。
- master 缺修复的补救已随 2026-11-06 用户指令（取消回流、目标分支定为 `V0.1.8`）移出本 change，另行安排；此处不跟踪。
- 本 change 无探索笔记需同批归档（探索结论已内嵌于 design/proposal 事实链）。
