# 状态栏统计塌缩取证结案（V0.1.9-pre3 不复现）

状态：已归档（取证终止）
日期：2026-11-10
关联提交：`2ca1631`（探针实现）、`c7a7bcb`（合入 `V0.1.9`）、`d29e013`（tag `V0.1.9-pre3`）、`2d73ba9`（空白根因另修）
验收结论：维护者确认 `V0.1.9-pre3` 上统计塌缩与列表空白均不复现，两个取证 change 结案归档

> 本笔记只记录已核对的事实与维护者决议。**不含任何原因推测**：症状为何不再复现、四候选机制中哪一条成立，均无观测数据支撑，故不作判断。

## 1. 已核对事实

| 事实 | 依据 |
| --- | --- |
| `[StatBar]` 诊断流与延迟快照已实现并合入 `V0.1.9` 分支 | `2ca1631`、`c7a7bcb`；分支上存在 `logcat-viewer/Services/StatBarProbe.cs` 与 `tests/logcat.Tests/StatBarProbeTests.cs` |
| 预发布取证版已发出 | tag `V0.1.9-pre3` 指向 `d29e013`，`V0.1.9` 与 `origin/V0.1.9` 均含该提交 |
| 复现机器上症状未复现 | 维护者 2026-11-10 当轮确认 |
| 未回收到复现机器的启动日志 | 症状未出现，无「复现一次 + 滚动一次」的样本可发回 |
| D7 判读表未适用，四种候选机制均未定性 | 无日志帧，无法对照 `w` / `len` / `Avail` / `Place` 三帧 |
| 列表空白根因由另一条路径修复 | `2d73ba9 fix(view): 零命中装载后视图未绑定文档导致整屏空白`，落在 `V0.1.9` 分支，不含诊断插桩代码 |
| `add-load-diag-logging` 的诊断插桩从未进入 `master` 或 `V0.1.9` | 两个分支的树中均无 `LogDiag.cs` / `ViewDiag.cs`；改动只以未提交形式留在该 change 的 worktree |

## 2. 结案口径

- 两轮取证 change 的使命是「拿到日志、定性机制」；症状不复现即无取证对象，继续挂账没有依据，故结案而非继续等复现。
- **机制未定性**不是「已修复」的结论。状态栏统计区的行为契约仍不写入 `openspec/specs/`；若日后复发，按本轮 design 的 D7 判读表重新取证，另起新探索笔记。
- `statbar-collapse-probe` 的探针代码按原设计长期保留在 `V0.1.9` 分支（design D6、tasks 5.1 备注），随后续 `merge(release)` 回流 `master`；不在本次归档中删除。
- `add-load-diag-logging` 的诊断代码经维护者决定**不落地**：worktree 内未提交改动随归档丢弃，`specs/load-diagnostics/spec.md` 不并入主规范，只随 change 冻结为历史档案。

## 3. 归档动作（2026-11-10）

- `statbar-collapse-probe`、`add-load-diag-logging` 两个 change 移入 `openspec/changes/archive/`，tasks 全部关闭。
- 关联探索笔记 `2026-11-03-STATUSBAR-STAT-COLLAPSE-RELAPSE.md` 与本笔记同批移入 `openspec/notes/archive/`，此后冻结不再维护。
- 两个 change 专属的 git worktree 清除；`V0.1.9` 预发布分支与其 worktree 保留（稳定版发布后才收尾）。

## 4. 关联

- `openspec/changes/archive/2026-11-10-statbar-collapse-probe/design.md`：D7 判读表与四候选机制的原始定义（裸路径文字提及，归档后不写链接）。
- `openspec/notes/archive/2026-10-28-STATUSBAR-STAT-COLLAPSE.md`、`2026-11-03-STATUSBAR-STAT-COLLAPSE-RELAPSE.md`：前两轮修复与复发记录，均已冻结。
