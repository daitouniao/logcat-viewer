# 设计落地与缺陷修复跟踪表

> 目的：跟踪 `docs/design/`（已评审定稿的设计）与 bugfix 报告的落地状态，保证文档与代码不脱节。报告验收后归档至 `docs/archive/bug/`，本表只作状态说明。
> 维护规则见文末。最近核对：**2026-10-18**（测试基线 **636 用例** = 519 + 114 fileproxy + 3 D15-D17 追加；fileproxy 已归档，主规范 `openspec/specs/fileproxy/spec.md` 27 条需求；tstrun 反射 runner 口径；⚠️ 归档前全量实跑 632 过 / 4 失败，均为 `SingleInstanceTests` 在应用实例运行中被互斥体挡下的环境假红（同批用例今晨无应用运行时 633/0/0；tasks §8.4/§9.5 两条 `SpoolAsync` 本次实跑通过，详见 fileproxy 行）；覆盖率与变异测试口径未更新，沿用 2026-10-16 记录）。

## 状态标记

| 标记 | 含义                                 |
| ---- | ------------------------------------ |
| ✅    | 已落地（代码 + 测试齐备）            |
| 🔶    | 部分落地（分期设计，部分阶段未开工） |
| ⏳    | 待实施（方案已确认，未动工）         |
| 🖐    | 已实施，待手工验收                   |
| 🗄    | 已归档（移入 `docs/archive/`）       |

## 一、docs/design/ 设计文档

| 设计文档                                                | 状态                                      | 落地提交                                                                                    | 代码锚点（符号名）                                                                                                                                                                                                                                                                                                                      | 核对结论                                                                                                                                                                                    |
| ------------------------------------------------------- | ----------------------------------------- | ------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| FILTER-REFACTOR-PLAN（🗄 已归档 2026-10-16）             | 🗄 已归档；三件事已执行完毕并验收          | d1a4ffd / 6dc2e79 / dc4d908                                                                 | `FilterEngine`（无 `CompileRegex`/`_reCache`）、`FilterSpec`（无 `TagRegex`/`MsgRegex`）、`frmMain.ApplyTermBoxStyle`、op 兜底统一 `?? "or"`                                                                                                                                                                                            | 2026-10-14 全仓 grep 无正则残留；README 的 LogParser 格式回退（regex fallback）按坑 2 保留 ✅。经用户指示归档（2026-10-16），设计文档已移入 `docs/archive/design/`                           |
| TOOLBAR-FAVORITES-DESIGN（🗄 已归档 2026-10-16）         | 🗄 已归档；✅ 已落地并验收                  | 622824d、1e235bb（§4.1 2026-10-12 修订：下拉不按输入过滤）                                  | `FavoritesStore.ContainsTagFilter/ContainsMsgFilter/ToggleTagFilter/ToggleMsgFilter`；`frmMain` 的 `_btnFavTagMenu/_btnFavTagToggle/_btnFavMsgMenu/_btnFavMsgToggle`（★ `Overflow.Never`）、`ShowFilterFavMenu`、`RefreshFavStars`、「清空收藏」YesNo 二次确认                                                                          | 2026-10-14 核对通过；用例在 `FavoritesStoreTests`。经用户指示关闭并归档（2026-10-16），设计文档已移入 `docs/archive/design/`                                                                |
| [RECENT-FILES-DESIGN.md](design/RECENT-FILES-DESIGN.md) | ✅ 已落地                                  | 6f2983d                                                                                     | `FavoritesStore.RecentFiles/MaxRecentFiles/AddRecentFile`；`frmMain` 的 `_mruMenu/RebuildRecentMenu/IsNetworkPath`；`StartIndex` 成功路径调用 `AddRecentFile`                                                                                                                                                                           | 2026-10-14 核对通过；4 条 `AddRecentFile` 单测在位；`LocTable.MainUi` 含「最近打开 → Recent Files」。**文档头部原写「待评审」已滞后，本次核对已订正**                                       |
| [ADB-ROOT-DETECTION.md](design/ADB-ROOT-DETECTION.md)   | ✅ 已编码；🖐 探测链路待真机手工验收        | 2589f47                                                                                     | `Models/DeviceInfo.cs`（`RootMode` None/Su/Adbd + `RootDetectEntry` + `DeviceInfo.IsRoot`）、`AdbManager`（`DetectRootAsync`/`TryProbeAsync` 8s 超时/`ClassifyRootOutput`/`ShouldRedetect`/`BuildRootShellCmd`/`RedetectRootAsync`）、`DeviceFilePane.ShellCmd`、`frmMain.AdbRefreshManualAsync`、`DeviceOpsDialog.MaybeOfferRootRetry` | 2026-10-16 自 `docs/private/design/` 移档入库（de4005e）；20 条单测在 `ModelTests`；真机验收（Adbd/Su/None 判定、授权框不再重弹、Q6 重试入口）待做                                          |
| CRASH-SUMMARY-DESIGN（🗄 已归档 2026-10-16）             | 🗄 已归档；阶段⓪①②③ 已落地，阶段④ 暂时关闭 | c574592（⓪ i18n 底座=语言切换残留修复）、58fbd8a（①②③ F-01/F-02/F-05）、7bc7f17（测试补齐） | `CrashEvent`/`CrashExtractor`（`ContainsCrashKeyword`/`ExtractCrashAt`，绑定索引阶段）/`CrashAggregator`；`LogListView` ☠ 摘要行；`CrashSummaryForm`（非模态 + VirtualMode + 「无崩溃」空态 + 点击定位）；frmMain「崩溃汇总…」菜单；`LocTable.CrashUi`                                                                                  | 2026-10-14 核对通过；用例在 `CrashTests.cs`。阶段④（按类型过滤 / 指纹分组 / 时间轴联动）经用户决定**暂时关闭**（2026-10-16），后续有明确需求再继续；设计文档已归档至 `docs/archive/design/` |
| [fileproxy（OpenSpec change）](../openspec/changes/archive/2026-10-18-fileproxy/proposal.md) | ✅ 已验收已归档（2026-10-18） | 6973681、09643c4、27190e2、6e849bc（`feature/fileproxy` 分支，未合 master） | `Services/FileProxyClient`（帧解析/hello/open/read/close）、`FileProxyService`（`EnsureProxyAsync`/`SpoolCoreAsync`/`SpoolProgress`/`NeedsHugeFileConfirm`/`SpoolPaths`/`CleanupStaleSpoolDirs`/`KillLaunchedProcesses`）、`FileProxyMode`（`FileProxyDecision` 九格真值表）、`TextProbe`、`FileProxyException`（失败分类）、`SingleInstance`、`ProxyChannelStates`（三态互斥）、`LogDocument`（`SourcePath`/`IsProxied`/`SpoolDir`，7 处触及点改源路径）、frmMain（`StartIndex` 分流段/`LoadViaProxyAsync`/`RefreshChannelState`）、`FileProxyDialog`、`LocTable.FileProxyUi` | 2026-10-18 全量编码完成；tstrun 633 全过（新增 `FileProxyLaunchTests`/`SpoolCleanupTests`/`FileProxyChannelStateTests`/`SingleInstanceTests`/`LogDocumentProxiedTests` + Loopback 追加）；**2026-10-18 维护者验收通过**（手工项全部完成：auto 模式受限样本走代理 / 各失败分类错误消息 / 状态栏渠道标签无布局抖动 / 真双进程单实例冒烟 / harness 中英快照 / 杀进程树真机）。遗留知悉放行项：tasks §8.4/§9.5 记录的两条 `SpoolAsync` 失败（短读/取消路径 sessionDir 未删）未定位，验收时知悉放行（2026-10-18 归档前实跑两条均通过）。**2026-10-18 已归档**：change 移入 `openspec/changes/archive/2026-10-18-fileproxy/`，27 条需求并入主规范 `openspec/specs/fileproxy/spec.md`（`validate --specs --strict` 仅余 1 条既有 WARNING：requirements[20] 超 500 字符，拆需求与否待维护者定夺）。归档前全量实跑 632 过 / 4 失败——均为 `SingleInstanceTests` 被运行中实例持有的 `Local\logcat-viewer-single-instance` 互斥体挡下的环境假红（同批用例今晨无应用运行时 633/0/0）。下一步待维护者指令：合 master → 阶段 8 版本号/tag |

> **备注（2026-10-16）**：ADB-ROOT-DETECTION.md 已于 2026-10-16 移档 `docs/design/` 并入库（de4005e），见上表 ADB-ROOT-DETECTION 行。

## 二、bugfix 修复报告（已全部归档，2026-10-16）

两份报告均已验收并归档至 `docs/archive/bug/`（归档日期前缀 `2026-10-16-`），此处只作状态说明，不写链接：

- **EMPTY-LINE-STACK-LOSS**（空行导致崩溃堆栈行在打开文件时丢失，X-A8）——已验收（2026-10-15）；修复 57ffae8、测试补充 8ea3bf5、文档 ee9a082；变异测试通过（LogDocument 24 变异点 20 KILLED / 4 SURVIVED 均为 UT-AUDIT 既有记录项）。
- **FILEBROWSER-BLANK-LIST**（文件浏览两栏列表全部空白——列头未挂进 ListView）——已验收（2026-10-14）；修复 36014aa、报告 314aa44。

新报告按 bug 修复最短流程产生于 `docs/bugfixes/`，验收并经用户指示归档后移入 `docs/archive/bug/`，本节省查。

## 三、当前待办（按优先级）

1. **ADB-ROOT-DETECTION 探测链路**——已编码（2589f47）且 519 用例全过；待真机手工验收（Adbd/Su/None 判定、授权框不再重弹、Q6 重试入口）。
2. **CRASH-SUMMARY 阶段④**——经用户决定**暂时关闭**（2026-10-16），后续有明确需求再继续；设计文档已归档至 `docs/archive/design/`，本项移出待办。
3. **文档合规整改**——已验收并归档（2026-10-16，关联提交 da97abb）：整改报告（清理 25 处本机绝对路径、私有记忆引用、README 结构清单与分区表订正、TESTING/UT-AUDIT/BUILDING 口径联动）已按 §3.2 移入 `docs/archive/bug/`，此处只作状态说明、不写链接。**测试未验证**（当轮 shell 进程创建失败，两条测试路径都起不来）。
4. **fileproxy（已归档 2026-10-18）**——维护者验收通过并归档：代码在 `feature/fileproxy`（6973681/09643c4/27190e2/6e849bc），change 移入 `openspec/changes/archive/2026-10-18-fileproxy/`，27 条需求并入主规范 `openspec/specs/fileproxy/spec.md`。归档前全量 632 过 / 4 失败（均为单实例互斥体被运行中实例占用的环境假红，详见 fileproxy 行）。下一步待维护者指令：合 master → 阶段 8 版本号/tag

## 四、维护规则

- 设计文档评审通过移入 `docs/design/` 时，在第一节登记一行；落地后在同行动态更新状态与提交号。
- bugfix 报告状态变更（⏳ → 🖐 → ✅）时，同步更新第二节与第三节待办。
- 每次核对更新头部「最近核对」日期与测试基线数；核对方式 = 按代码锚点符号名 grep + 跑测试基线，不凭记忆。
- 状态以代码为准，不以文档头部自述为准（本次核对即发现 RECENT-FILES-DESIGN 头部「待评审」滞后于实际落地）。
