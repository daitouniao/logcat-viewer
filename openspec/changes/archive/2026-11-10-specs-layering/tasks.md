# Tasks

> **worktree 标识**：**不使用 worktree**（维护者 2026-11-10 定，见 `design.md` D-8）。工作分支 = `master`，改动直接在 `master` 上落；`AGENTS.md` §2.3 的「worktree 标识」与「wt 合入目标分支」两项对本 change 不适用（无分支可合入），归档门禁的合入条件自动满足。

## 1. 准备

- [x] 1.1 确认在 `master` 上工作且无历史 worktree 残留；验证：`git branch --show-current` 输出 `master`，`git worktree list` 中**不存在** `wt/specs-layering`
- [x] 1.2 处置主工作树的在途改动并复测基线；验证：`git status --porcelain` 只剩本 change 目录（`openspec/changes/specs-layering/`）未跟踪，维护者自己的在途修改已提交或明确要求忽略；再跑 `openspec list --specs --json`，输出 **13 条无前缀 id** 且 `requirementCount` 与本任务内联期望值逐项一致——adb-capture 4 / adb-server 3 / android-toolbox 6 / app-shutdown 3 / chunked-file-load 8 / device-file-transfer 8 / file-browser 3 / fileproxy 27 / message-filter 7 / settings-storage 2 / sidecar-index-cache 7 / time-column 3 / ui-language 1（合计 **82**）。**若实测与 82 不符**：停下报告，按实测值更新本文件 1.2 与 5.1 的期望值后再继续（说明期间有人改过 specs，基线已漂移）

## 2. 迁移 13 条 capability（内容零改动）

- [x] 2.1 建立 `openspec/specs/old/` 并逐条 `git mv openspec/specs/<capability> openspec/specs/old/<capability>`，覆盖 13 个目录（adb-capture、adb-server、android-toolbox、app-shutdown、chunked-file-load、device-file-transfer、file-browser、fileproxy、message-filter、settings-storage、sidecar-index-cache、time-column、ui-language）；验证：`Get-ChildItem openspec/specs -Directory` 只剩 `old`，`Get-ChildItem openspec/specs/old -Directory` 恰好 13 个且名字集合与迁移前一致
- [x] 2.2 确认只搬路径、未改内容；验证：`git status --porcelain -- openspec/specs` 显示 13 对 rename（`R`）且无 `M`，`git diff --cached -M --stat -- openspec/specs` 输出为空。**此步之前不得编辑被移动文件的内容**（编辑会拉低相似度，让 rename 识别失败）

## 3. 规则落库（`openspec/config.yaml` + `AGENTS.md`）

- [x] 3.1 在 `openspec/config.yaml` 的 `context` 中「规范定位」段之后插入「规范分层」段，措辞逐字取 `design.md` D-2 ①；验证：`openspec instructions proposal --change specs-layering --json` 输出的 `context` 字段包含「规范分层（2026-11-10 定）」与「迁移规则『动谁迁谁』」
- [x] 3.2 在 `rules.specs` 末尾追加一条落位规则，措辞逐字取 `design.md` D-2 ②；验证：`openspec instructions specs --change specs-layering --json` 输出的 `rules` 数组含该条且共 3 条，原有两条（每条需求至少一个场景、禁止类名与文件路径）仍在
- [x] 3.3 把 `AGENTS.md` §3.1「规范类」单元格里的 `（主规范，按 capability）` 替换为 `（主规范，按实现代际分层：\`specs/<分类>/<capability>/\`，当前分类只有 \`old\`）`，行内其余文字与「更新时机」列不动；验证：`Select-String -Path AGENTS.md -Pattern "按 capability"` 零命中，`Select-String -Path AGENTS.md -Pattern "按实现代际分层"` 命中 1 行，且该行仍含 `openspec/changes/`、`openspec/notes/`、`openspec/config.yaml`、`增删活文档须同次修改本表` 四段。**注**：`AGENTS.md` 在 `.gitignore:139` 被忽略、未入库（§一.4），故该改动只存在于工作区、不产生 `git diff`

## 4. 笔记引用收敛

- [x] 4.1 按 `design.md` D-4 的表给 **4 份**笔记的旧 spec 路径补 `old/` 前缀（`2026-11-07-MESSAGE-INDEX-MECHANISM.md:6` 四个路径、`BACKLOG-APP-LIFECYCLE.md:42` 一处、`DLT-INTEGRATION.md:94` 一处、`2026-11-08-SOURCE-ACQUIRE-CONTRACT.md:44`/`:245`/`:369` 六处——第 4 份是 apply 时补入的，规划时它未被 git 跟踪所以 `git grep` 扫不到）；验证：`git grep -n "openspec/specs/" -- openspec/notes` 在**排除 `openspec/notes/archive/**`** 后，除 `2026-11-08-SOURCE-ACQUIRE-CONTRACT.md:282`（`openspec/specs/` 不带 capability 段，数字陈述，不回填）外，命中**全部**含 `specs/old/`；且 `openspec/notes/archive/**` 零改动（`git status --porcelain -- openspec/notes/archive` 为空）

## 5. 全量验收

- [x] 5.1 复核迁移结果；验证：`openspec list --specs --json` 13 条 id **全部**形如 `old/<capability>`，且 `requirementCount` 与任务 1.2 的定稿期望值逐项一致、合计仍为 82
- [x] 5.2 校验全部规范结构合法；验证：`openspec validate --specs --json` 13 条全部通过，零 findings
- [x] 5.3 校验本 change 自身合法（`skip_specs: true` 与零 delta 相容）；验证：`openspec validate specs-layering --type change --json` 通过，`openspec status --change specs-layering --json` 中 `specs` 为 `skipped`、`proposal` / `design` / `tasks` 为 `done`
- [x] 5.4 全仓旧路径扫描；验证：`git grep -nE "openspec/specs/(adb-capture|adb-server|android-toolbox|app-shutdown|chunked-file-load|device-file-transfer|file-browser|fileproxy|message-filter|settings-storage|sidecar-index-cache|time-column|ui-language)"` 在排除 `openspec/changes/archive`、`openspec/notes/archive`、`docs/archive`（三类历史快照，按 §3.2 不回填）与 `openspec/changes/specs-layering`（本 change 在描述「旧地址不再存在」，其文本必然含旧路径）之后**零命中**
- [x] 5.5 确认未越界；验证：`git status --porcelain` 全部命中都在 `openspec/` 内（`openspec/specs/` 的 13 条 rename、`openspec/config.yaml`、`openspec/notes/` 被改的笔记、本 change 目录），无任何 `.cs` / `.csproj` / `scripts/**` / `docs/**` 改动。**注**：`AGENTS.md` 未入库（`.gitignore:139`），其改动不在 `git status` 输出中，须单独以任务 3.3 的判据确认

## 6. 提交与收口

- [x] 6.1 提交本 change 的全部改动（commit `a301168`，18 文件：13 条 rename 全部 100% 相似度 + `config.yaml` + 3 份笔记 + 本 tasks；`AGENTS.md` 不入库故不在其中）（需当轮明确授权；message 用 `docs(openspec): specs 按实现代际分层，13 条迁入 old/`）；验证：`git log -1 --stat` 的文件清单与任务 5.5 扫出的范围一致，`git status --porcelain` 只剩本 change 目录
- [x] 6.2 确认合入条件；验证：本 change 不使用 worktree，`git worktree list` 无 `wt/specs-layering`、`git branch --contains` 无需检查——在 tasks 全部勾选后，`AGENTS.md` §2.2 的归档门禁（tasks 全关 + wt 已合入目标分支）对「wt 已合入」一项自动满足

## Workflow follow-up

- 归档由维护者明确指示后执行 `openspec archive specs-layering`（须满足：本 tasks 全部勾选、改动已提交在 `master`）；本 change 无 worktree，故无 `git worktree remove` 动作
- 不修改 `AGENTS.md` §2.3 的 worktree 通用约定：本次豁免只针对本 change（见 `design.md` D-8），若将来纯文档 change 频繁豁免，再单独立 change 讨论是否放宽
- 刀0 的「分页查询窗口」跨代契约落位（`logcore/` 还是另立 `contract/`）须在刀0 立项前拍板，见 `design.md` Open Questions
- 与本 change 无关的既有残留：`git worktree list` 仍有 `wt/add-load-diag-logging` 与 `wt/statbar-collapse-probe` 两个 worktree（后者所属 change 已归档），另行处理，不并入本 change
