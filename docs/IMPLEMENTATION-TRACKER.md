# 设计落地与缺陷修复跟踪表

> 目的：跟踪 `docs/design/`（已评审定稿的设计）与 `docs/bugfixes/`（修复报告）的落地状态，保证文档与代码不脱节。
> 维护规则见文末。最近核对：**2026-10-15**（测试基线 **519 用例全过** = 493 基线 + 6 条 X-A8 + 20 条 ADB root 检测重构；X-A8 变异测试通过：LogDocument 24 变异点 20 KILLED / 4 SURVIVED 均为 UT-AUDIT 既有记录项；行覆盖 98.85% / 分支 87.79% 为 2026-10-14 口径，口径 Services+Models）。

## 状态标记

| 标记 | 含义                                 |
| ---- | ------------------------------------ |
| ✅    | 已落地（代码 + 测试齐备）            |
| 🔶    | 部分落地（分期设计，部分阶段未开工） |
| ⏳    | 待实施（方案已确认，未动工）         |
| 🖐    | 已实施，待手工验收                   |

## 一、docs/design/ 设计文档

| 设计文档                                                          | 状态                                                    | 落地提交                                                                                    | 代码锚点（符号名）                                                                                                                                                                                                                                             | 核对结论                                                                                                                                              |
| ----------------------------------------------------------------- | ------------------------------------------------------- | ------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| [FILTER-REFACTOR-PLAN.md](design/FILTER-REFACTOR-PLAN.md)         | ✅ 已执行完毕                                            | d1a4ffd / 6dc2e79 / dc4d908                                                                 | `FilterEngine`（无 `CompileRegex`/`_reCache`）、`FilterSpec`（无 `TagRegex`/`MsgRegex`）、`frmMain.ApplyTermBoxStyle`、op 兜底统一 `?? "or"`                                                                                                                   | 2026-10-14 全仓 grep 无正则残留；README 的 LogParser 格式回退（regex fallback）按坑 2 保留 ✅                                                          |
| [TOOLBAR-FAVORITES-DESIGN.md](design/TOOLBAR-FAVORITES-DESIGN.md) | ✅ 已落地                                                | 622824d、1e235bb（§4.1 2026-10-12 修订：下拉不按输入过滤）                                  | `FavoritesStore.ContainsTagFilter/ContainsMsgFilter/ToggleTagFilter/ToggleMsgFilter`；`frmMain` 的 `_btnFavTagMenu/_btnFavTagToggle/_btnFavMsgMenu/_btnFavMsgToggle`（★ `Overflow.Never`）、`ShowFilterFavMenu`、`RefreshFavStars`、「清空收藏」YesNo 二次确认 | 2026-10-14 核对通过；用例在 `FavoritesStoreTests`                                                                                                     |
| [RECENT-FILES-DESIGN.md](design/RECENT-FILES-DESIGN.md)           | ✅ 已落地                                                | 6f2983d                                                                                     | `FavoritesStore.RecentFiles/MaxRecentFiles/AddRecentFile`；`frmMain` 的 `_mruMenu/RebuildRecentMenu/IsNetworkPath`；`StartIndex` 成功路径调用 `AddRecentFile`                                                                                                  | 2026-10-14 核对通过；4 条 `AddRecentFile` 单测在位；`LocTable.MainUi` 含「最近打开 → Recent Files」。**文档头部原写「待评审」已滞后，本次核对已订正** |
| [CRASH-SUMMARY-DESIGN.md](design/CRASH-SUMMARY-DESIGN.md)         | 🔶 阶段⓪①②③ 已落地；阶段④ 未开工（符合「后续增强」定位） | c574592（⓪ i18n 底座=语言切换残留修复）、58fbd8a（①②③ F-01/F-02/F-05）、7bc7f17（测试补齐） | `CrashEvent`/`CrashExtractor`（`ContainsCrashKeyword`/`ExtractCrashAt`，绑定索引阶段）/`CrashAggregator`；`LogListView` ☠ 摘要行；`CrashSummaryForm`（非模态 + VirtualMode + 「无崩溃」空态 + 点击定位）；frmMain「崩溃汇总…」菜单；`LocTable.CrashUi`         | 2026-10-14 核对通过；用例在 `CrashTests.cs`。阶段④（按类型过滤 / 指纹分组 / 时间轴联动）待 ROADMAP 排期，非欠账                                       |

> **备注（2026-10-15）**：ADB root 检测重构（`RootMode` 三态）已随 `2589f47` 落地，但其设计文档 `ADB-ROOT-DETECTION.md` **仍在 `docs/private/design/`**（未按「评审通过后移档」规则进 `docs/design/`）。代码锚点：`Models/DeviceInfo.cs`（`RootMode` None/Su/Adbd + `RootDetectEntry` + `DeviceInfo.IsRoot`）、`AdbManager`（`DetectRootAsync`/`TryProbeAsync` 8s 超时/`ClassifyRootOutput`/`ShouldRedetect`/`BuildRootShellCmd`/`RedetectRootAsync`）、`DeviceFilePane.ShellCmd`、`frmMain.AdbRefreshManualAsync`、`DeviceOpsDialog.MaybeOfferRootRetry`；20 条单测在 `ModelTests`。探测链路待真机手工验收；**是否移档 `docs/design/` 待确认**，移档后并入上表。

## 二、docs/bugfixes/ 修复报告

| 修复报告                                                        | 状态     | 提交                                             | 代码锚点 / 现状核实                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          | 剩余动作                                                                     |
| --------------------------------------------------------------- | -------- | ------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| [FILEBROWSER-BLANK-LIST.md](bugfixes/FILEBROWSER-BLANK-LIST.md) | ✅ 已验收 | 36014aa（fix）、314aa44（报告）                  | `FilePane.CreateColumns` 三处 `Columns.Add(Loc.BindCol(...))`（共 12 列）已在位                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              | 无——2026-10-14 手工验收 3 条（两栏显示 / 列头切语言刷新 / 列宽回填）全部通过 |
| [EMPTY-LINE-STACK-LOSS.md](bugfixes/EMPTY-LINE-STACK-LOSS.md)   | ✅ 已验收 | 57ffae8（fix）/ 8ea3bf5（test）/ ee9a082（docs） | 2026-10-15 实施：① 修法 A——`LogDocument.ScanRange` 空行分支同时重置 `prevHead`/`joined`（注释含横幅分支语义相反的勿「统一」警示）；② 批界配套 fix-5——`ScanPart.EndPrevHead` + `_prevHeadAtEnd` 缓存替代 `PrevRowJoinable()` 反推（该方法已删除）；③ fix-6——`ScanPart.EndJoined` + `_joinedAtEnd` + `ScanRange` 增 `joinedDoc` 参，`DoBuild`/`Append` 收尾刷新缓存（赋值在任何早退分支之前）。新增 6 条用例（含「整批空行后批末状态仍刷新」），回退态验证 4 红 1 判别绿；**变异测试通过**（`scripts/mutation-test.py LogDocument`：24 变异点 20 KILLED / 4 SURVIVED 均为 UT-AUDIT 既有记录项，X-A8 相关 4 个新变异全 KILLED） | 无——2026-10-15 手工验收通过（§8 判据），X-A8 闭环                            |

## 三、当前待办（按优先级）

1. **ADB-ROOT-DETECTION 探测链路**——已编码（2589f47）且 519 用例全过；待真机手工验收（Adbd/Su/None 判定、授权框不再重弹、Q6 重试入口）。
2. CRASH-SUMMARY 阶段④——按 ROADMAP P1 排期，非欠账。

## 四、维护规则

- 设计文档评审通过移入 `docs/design/` 时，在第一节登记一行；落地后在同行动态更新状态与提交号。
- bugfix 报告状态变更（⏳ → 🖐 → ✅）时，同步更新第二节与第三节待办。
- 每次核对更新头部「最近核对」日期与测试基线数；核对方式 = 按代码锚点符号名 grep + 跑测试基线，不凭记忆。
- 状态以代码为准，不以文档头部自述为准（本次核对即发现 RECENT-FILES-DESIGN 头部「待评审」滞后于实际落地）。
