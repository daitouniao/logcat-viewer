# Tasks

## 1. 前置与 worktree

- [x] 1.1 在独立 git worktree 中实施本 change：worktree 标识 = 分支 `wt/next-recent-files`（路径以 `git worktree list` 定位，禁写本机绝对路径），目标分支 `master`；验证：`git worktree list` 列出该分支，主工作树 `git status` 保持干净
- [x] 1.2 复核 design 与本 change 一致：D1–D11 无「待拍」标记，design 无未决 Open Questions；验证：通读 `design.md`，决策与 `specs/next/recent-files/spec.md`、`specs/next/app-storage/spec.md` 无冲突

## 2. 设置层

- [x] 2.1 设置模型与读写：`NextSettings` 增加最近文件列表（可空字符串数组）；`JsonNextSettingsStore.Load` 在该键缺失或为空时按空列表处理，超过上限时截断；`Save` 只写已识别的四个键（版本、语言、主题、最近文件）；验证：`SettingsStoreTests.SavesAndLoadsRecentFiles`、`MissingRecentFilesKeyYieldsEmpty`、`TruncatesOverlongRecentFilesOnLoad`、`SaveDropsUnknownKeys`，且既有 `RoundTripsKnownKeys` / `UnknownVersionYieldsDefaults` 仍通过
- [x] 2.2 列表维护逻辑：设置服务提供最近文件只读视图、「记录一次成功打开」与「移除指定条目」两个入口——空白路径忽略、按本机路径语义大小写不敏感去重、命中后移到最前并保留本次文本、超过 10 条从尾部淘汰；移除未命中不改动列表；变化时发出可供界面订阅的变更通知；验证：`RecentFilesTests.AddsNewestFirst`、`DedupesCaseInsensitive`、`KeepsNewestPathText`、`CapsAtTen`、`IgnoresBlankPath`、`RemovesSpecifiedEntries`、`RemovingUnknownEntryLeavesListUnchanged`
- [x] 2.3 持久化与降级：记录一次打开即整体保存，且不清除语言与主题；保存失败不抛、不打断打开；设置不可持久化时列表仅内存有效；验证：`RecentFilesTests.PersistsAcrossReload`、`SessionOnlyKeepsInMemory`，且 `SettingsStoreTests.ThemeAndLanguagePersistIndependently` 仍通过

## 3. 菜单展示逻辑与文案

- [x] 3.1 展示构建函数（无 UI 依赖）：本地路径按存在性过滤并把缺失路径上报为待移除（异常按不存在）、UNC 网络路径跳过探测始终显示、可见集合为空时标记为禁用、超过 80 字符中部省略并保留完整路径供 tooltip；验证：`RecentFilesMenuTests.HidesMissingLocalEntry`、`ReportsMissingLocalEntryForRemoval`、`ShowsNetworkEntryWithoutProbe`、`ElidesMiddleBeyondEighty`、`DisablesWhenNoVisibleEntry`、`TreatsProbeFailureAsMissing`
- [x] 3.2 双语文案：中文与英文目录同步新增「最近打开」子菜单标题键；验证：`LocalizationTests.KeySetsMatch`、`NoEmptyTranslations` 通过

## 4. 装载流程与主窗接线

- [x] 4.1 记录时点：装载完成（`done`）且未被取消时记录该文件（0 行文件也记录）；错误、取消、被下一次打开顶替时均不记录；验证：`RecentFilesTests.RecordsAfterSuccessfulLoad`、`IgnoresFailedLoad`、`IgnoresSupersededLoad`
- [x] 4.2 菜单接线：文件菜单新增「最近打开」子菜单，条目按构建函数生成，点击调用既有装载入口，打开失败沿用状态栏错误反馈；订阅设置服务的变更通知与语言切换，重建子菜单；无可显示条目时子菜单禁用；构建时把本地缺失条目交设置服务移除；验证：手工展开菜单核对五种状态——空列表禁用、有记录按最近在前、缺失条目移除后不再出现、长路径中部省略且悬停显示完整路径、打开失败条目消失
- [x] 4.3 失败移除接线：文件原因（`LogcoreException` 与其它打开异常）装载失败时移除该最近条目；数据组件不可用、取消与被顶替不移除；验证：手工造一个不可读文件并作为最近条目打开，确认错误落在状态栏且条目消失；再在不带数据组件的情况下打开最近条目，确认条目保留

## 5. 集成验证与合入

- [x] 5.1 全量测试：`dotnet test`（不可用则走 tstrun 反射 runner，并声明「tstrun 口径 N 条 + 已知跳过类别」），相对基线无新增失败；同批同步 `docs/TESTING.md` 的用例数，README 测试数字如有变化一并更新；验证：测试输出全绿，文档数字与实测一致
- [x] 5.2 wt 合入目标分支：把 `wt/next-recent-files` 合入 `master`；验证：`git log` 在 `master` 上可见本 change 提交，`git worktree list` 仍列出该 wt，主工作树干净
