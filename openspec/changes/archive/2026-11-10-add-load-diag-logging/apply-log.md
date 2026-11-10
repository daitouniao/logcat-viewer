# Apply Log

> 状态：已结案（取证终止，诊断代码未落地）· 日期：2026-11-10 · 归档：2026-11-10 · 分支：`wt/add-load-diag-logging`（目标分支 `master`）

## 一、实测结果

| 项 | 结果 |
| --- | --- |
| 全量测试（Release） | **862 / 862 全过，0 失败，0 跳过**（原基线 852，本 change +10） |
| 行覆盖率（Debug + coverlet） | 94.28%（2,720 / 2,885），基线 94.26%（2,677 / 2,840） |
| 分支覆盖率 | 88.56%（1,494 / 1,687），基线 88.64%（1,460 / 1,647） |
| 方法覆盖率（方法内 ≥1 行命中） | 92.77%（308 / 332），基线 92.77%（295 / 318） |
| 手工冒烟（任务 8.2） | **未执行**：本轮无 GUI 交互条件，改由用户复现覆盖；见 §四 |

新增用例 10 条：`StartupLogTests` 1、`ViewDiagTests` 4、`frmMainTests` +1、`LoadDiagLogTests` 4。

## 二、改动清单

| 文件 | 改动 |
| --- | --- |
| `logcat-uicore/Services/StartupLog.cs` | 写入入口加进程内锁（诊断日志并发写不再丢行/拼接） |
| `logcat-viewer/Services/LogDiag.cs`（新增） | 五个前缀常量 + 各类记录的拼装口径；过滤条件只记结构、不记关键词原文 |
| `logcat-viewer/Services/ViewDiag.cs`（新增） | 视图自检纯逻辑：三态判据 + 去重键 + 现场快照文本 |
| `logcat-viewer/frmMain.cs` | 打开入口记录；过滤四出口记录；统计刷新处的视图自检接线 |
| `logcat-viewer/frmMain.Load.cs` | 分块收尾记录（含「清理是否执行」）；视图接管结论（每代一次）；逐块明细 |
| `logcat-viewer/frmMain.Sidecar.cs` | 分流结论（缓存装配及其行数 / 分块编排） |
| `tests/logcat.Tests/UiThread.cs`（新增） | 把原嵌套在 `SidecarEquivalenceTests` 里的最小 UI 线程抽为共享工具 |
| `docs/TESTING.md`、双 `README` | 用例数与覆盖率数字同步（§3.3 联动） |

行为面：**零变更**。所有插入点均为「读已有局部变量 / 字段 + 追加写日志」，`await` 顺序、锁范围、赋值时机均未动（任务 7.1 逐点复核 `git diff` 通过）。

## 三、复现与取 log

**诊断构建**（worktree 内相对路径）：`logcat-viewer/bin/Release/net10.0-windows/logcat.exe`（框架依赖，需 .NET 10 桌面运行时）。

**日志位置**：`<exe 所在目录>\Log\startup.log`（追加写；关掉程序后取最完整）。

**复现脚本**（按现有已知现象设计，四步各记一次时间点）：

1. 打开第一个文件，等它加载完、并按已知复现条件完成一次过滤；
2. 拖入第二个文件（可复用上次命中的文件，让缓存装配也有样本）；
3. 出现空白后，**依次单独**试：F5 重载 → 只按 Ctrl+Enter 重新过滤 → 只拖滚动条，每步之间看一眼列表；
4. 把 `Log\startup.log` 整份回传（含空白前后两分钟即可）。

## 四、判读表（拿到日志按此对号）

| 日志特征 | 结论 |
| --- | --- |
| `[View] … bind=none`（或 `bind=other`）且 `viewRows` 与 `docRows` 都不为 0 | 行已进视图、文档绑定缺失 → 空白主因；继续看同代 `[Load] adopt=` |
| `[Load] … adopt=skipped reason=hasDoc` | 首次发布时视图已有文档，接管被跳过 |
| `[Load] … adopt=skipped reason=staleGen` | 首次发布时当前代已变（被抢占），该代永不接管 |
| `[Load] … completed=1 genMatched=0 loadingAfter=1` | 被新一代抢占；取消清理未执行、加载标志卡住 |
| `[Load] … completed=0 cleanup=1` | 装载被取消或失败，视图已整份清空 |
| `[Filter] … exit=normal hit=0` | 条件把内容滤光（不是视图问题） |
| `[Filter] … exit=loading` | 过滤被延迟到下一个发布边界（加载期行为） |
| `[Filter] … exit=staleGen` | 该次过滤被抢占取消，未上屏 |
| 最后一条 `[Chunk] … hitTotal=N` 与 `[Load] … viewRows` 不等 | 发布与落地行数不一致（丢批或重复应用） |

## 五、偏差与遗留

- **任务 8.2 未执行**：需要人工在 GUI 里跑三种装载情形；本轮以自动化接线用例（`LoadDiagLogTests`）替代了「日志字段存在且正确」的部分，剩余「按代际串读」留待用户复现时确认。
- **单实例用例曾失败 5 条**：首次全量跑时 `SingleInstance*` 5 条失败（本机有 `logcat.exe` 在跑的同一时段），随后两次全量跑均 862/862 全过，master 对照也全过——判为环境性瞬时冲突，与本 change 无关。
- **`[Chunk]` 为临时记录**：根因定案后按 tasks 的 follow-up 单独删除，常驻四类不受影响。
- **wt 未合入 → 已结案**：`V0.1.9-pre3` 上症状不复现，维护者 2026-11-10 决定诊断插桩代码不落地；分支上无本 change 的提交，任务 9.1 以「无待合入代码」关闭，worktree 内 14 个未提交文件随归档丢弃。
- **验收与后续修复**：本 change 只增加记录；空白根因已由 `V0.1.9` 分支的 `2d73ba9` 单独修复，未回收到判读日志，其余两处缺陷不再按本 change 另立修复。
