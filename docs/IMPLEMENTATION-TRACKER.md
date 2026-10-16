# 设计落地与缺陷修复跟踪表

> 目的：跟踪 `docs/design/`（已评审定稿的设计）与 bugfix 报告的落地状态，保证文档与代码不脱节。报告验收后归档至 `docs/archive/bug/`，本表只作状态说明。
> 维护规则见文末。最近核对：**2026-10-16**（测试基线 **519 用例全过** = 493 基线 + 6 条 X-A8 + 20 条 ADB root 检测重构；X-A8 变异测试通过：LogDocument 24 变异点 20 KILLED / 4 SURVIVED 均为 UT-AUDIT 既有记录项；行覆盖 98.85% / 分支 87.79% 为 2026-10-14 口径，口径 Services+Models）。

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
| [ADB-ROOT-DETECTION.md](design/ADB-ROOT-DETECTION.md)                   | ✅ 已编码；🖐 探测链路待真机手工验收                              | 2589f47                                                                                     | `Models/DeviceInfo.cs`（`RootMode` None/Su/Adbd + `RootDetectEntry` + `DeviceInfo.IsRoot`）、`AdbManager`（`DetectRootAsync`/`TryProbeAsync` 8s 超时/`ClassifyRootOutput`/`ShouldRedetect`/`BuildRootShellCmd`/`RedetectRootAsync`）、`DeviceFilePane.ShellCmd`、`frmMain.AdbRefreshManualAsync`、`DeviceOpsDialog.MaybeOfferRootRetry`                                                                                                                                                                                                                                             | 2026-10-16 自 `docs/private/design/` 移档入库（de4005e）；20 条单测在 `ModelTests`；真机验收（Adbd/Su/None 判定、授权框不再重弹、Q6 重试入口）待做                                                                                                       |
| [CRASH-SUMMARY-DESIGN.md](design/CRASH-SUMMARY-DESIGN.md)         | 🔶 阶段⓪①②③ 已落地；阶段④ 未开工（符合「后续增强」定位） | c574592（⓪ i18n 底座=语言切换残留修复）、58fbd8a（①②③ F-01/F-02/F-05）、7bc7f17（测试补齐） | `CrashEvent`/`CrashExtractor`（`ContainsCrashKeyword`/`ExtractCrashAt`，绑定索引阶段）/`CrashAggregator`；`LogListView` ☠ 摘要行；`CrashSummaryForm`（非模态 + VirtualMode + 「无崩溃」空态 + 点击定位）；frmMain「崩溃汇总…」菜单；`LocTable.CrashUi`         | 2026-10-14 核对通过；用例在 `CrashTests.cs`。阶段④（按类型过滤 / 指纹分组 / 时间轴联动）待 ROADMAP 排期，非欠账                                       |

> **备注（2026-10-16）**：ADB-ROOT-DETECTION.md 已于 2026-10-16 移档 `docs/design/` 并入库（de4005e），见上表 ADB-ROOT-DETECTION 行。

## 二、bugfix 修复报告（已全部归档，2026-10-16）

两份报告均已验收并归档至 `docs/archive/bug/`（归档日期前缀 `2026-10-16-`），此处只作状态说明，不写链接：

- **EMPTY-LINE-STACK-LOSS**（空行导致崩溃堆栈行在打开文件时丢失，X-A8）——已验收（2026-10-15）；修复 57ffae8、测试补充 8ea3bf5、文档 ee9a082；变异测试通过（LogDocument 24 变异点 20 KILLED / 4 SURVIVED 均为 UT-AUDIT 既有记录项）。
- **FILEBROWSER-BLANK-LIST**（文件浏览两栏列表全部空白——列头未挂进 ListView）——已验收（2026-10-14）；修复 36014aa、报告 314aa44。

新报告按 bug 修复最短流程产生于 `docs/bugfixes/`，验收并经用户指示归档后移入 `docs/archive/bug/`，本节省查。

## 三、当前待办（按优先级）

1. **ADB-ROOT-DETECTION 探测链路**——已编码（2589f47）且 519 用例全过；待真机手工验收（Adbd/Su/None 判定、授权框不再重弹、Q6 重试入口）。
2. CRASH-SUMMARY 阶段④——按 ROADMAP P1 排期，非欠账。
3. **文档合规整改**——已验收并归档（2026-10-16，关联提交 da97abb）：整改报告（清理 25 处本机绝对路径、私有记忆引用、README 结构清单与分区表订正、TESTING/UT-AUDIT/BUILDING 口径联动）已按 §3.2 移入 `docs/archive/bug/`，此处只作状态说明、不写链接。**测试未验证**（当轮 shell 进程创建失败，两条测试路径都起不来）。

## 四、维护规则

- 设计文档评审通过移入 `docs/design/` 时，在第一节登记一行；落地后在同行动态更新状态与提交号。
- bugfix 报告状态变更（⏳ → 🖐 → ✅）时，同步更新第二节与第三节待办。
- 每次核对更新头部「最近核对」日期与测试基线数；核对方式 = 按代码锚点符号名 grep + 跑测试基线，不凭记忆。
- 状态以代码为准，不以文档头部自述为准（本次核对即发现 RECENT-FILES-DESIGN 头部「待评审」滞后于实际落地）。
