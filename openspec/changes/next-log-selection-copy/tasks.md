# Tasks

## 1. 前置

- [ ] 1.1 在独立 git worktree 中实施本 change：worktree 标识 = 分支 `wt/next-log-selection-copy`（路径以 `git worktree list` 定位，禁写本机绝对路径），目标分支 `master`；验证：`git worktree list` 列出该分支，主工作树 `git status` 保持干净

## 2. 选择模型（纯逻辑）

- [ ] 2.1 实现区间选择集合：单行替换、单行切换、区间合并/拆分、成员判定、按行序输出区间；验证：新增 `SelectionRangeTests.ReplaceWithSingleRow`、`ToggleRowAddsAndRemoves`、`AddRangeMergesOverlappingIntervals`、`ContainsUsesBinarySearchOnSortedIntervals`、`OrderedRangesExposeAscendingRows`
- [ ] 2.2 实现选择手势控制器：普通单击重设选择并置锚点、Ctrl 单击切换并置锚点、Shift 单击从锚点扩到目标行；验证：`SelectionGestureTests.PlainClickReplacesSelectionAndSetsAnchor`、`CtrlClickTogglesAndMovesAnchor`、`ShiftClickExtendsFromAnchorBothDirections`、`ShiftClickWithoutAnchorFallsBackToPlainClick`
- [ ] 2.3 大区间不逐行分配：Shift 选中 200 万行只产生 1 个区间；验证：`SelectionRangeTests.LargeShiftRangeStaysSingleInterval`（断言区间数为 1，不遍历行）

## 3. 交互与绘制

- [ ] 3.1 指针命中：在滚动容器上把点击位置换算为行序，落在滚动条/滑块时跳过，越界与空文档不产生选择；验证：`SelectionHitTestTests.MapsOffsetToRow`、`IgnoresScrollBarPress`、`OutOfRangeYieldsNoRow`
- [ ] 3.2 主窗接入：单击 / Ctrl+单击 / Shift+单击调用手势控制器，选择变化触发画布重绘；文件重新装载时清空选择与锚点；验证：`SelectionGestureTests` 覆盖手势，手测重新打开文件后无残留选中行
- [ ] 3.3 选中底色 token：在明暗两套主题各加一个选中底色 token，画布按行绘制选中底色且不写颜色字面量；验证：`ThemeTokenTests.LightAndDarkDefineSameKeys` 通过，手测两套主题下选中行可辨识
- [ ] 3.4 外观变化保持选择：主题、语言、窗口尺寸变化只重绘不清选择；验证：手测三项操作后已选行仍为选中状态

## 4. 复制

- [ ] 4.1 页供给层增加「按行区间取行」异步入口：命中缓存直接返回，缺页按页大小补齐并复用现有缓存与失败集；验证：`LogPageProviderTests.RangeFetchReusesCachedPages`、`RangeFetchSkipsFailedPages`
- [ ] 4.2 复制内容拼装：选中行按行序取原始文本，`\n` 连接、末尾不加换行；无选中返回空结果；验证：`CopyTextBuilderTests.JoinsInAscendingOrder`、`NoTrailingNewline`、`EmptySelectionYieldsEmpty`
- [ ] 4.3 主窗复制入口：`Ctrl+C` 触发后台拼装并写剪贴板，成功后状态栏「已复制 N 行」，失败给出失败提示且不崩溃；无选中不消费按键；验证：`ClipboardCopyTests` 使用假剪贴板覆盖成功、失败、无选中三条路径
- [ ] 4.4 文案：本地化目录新增复制成功/失败两条稳定键，中英各一份；验证：`LocalizationTests.KeySetsMatch`、`PlaceholdersMatch`、`NoEmptyTranslations` 通过

## 5. 验证与文档

- [ ] 5.1 `dotnet build logcat-next/logcat-next.slnx` 成功；验证：命令退出码 0
- [ ] 5.2 `dotnet test logcat-next/logcat-next.slnx` 全过，并复跑主仓测试确认 923 基线无新增失败；验证：报告两套口径数字
- [ ] 5.3 手测验收：大文件（千万行级）单击/Ctrl/Shift 选择、滚动后选择保持、`Ctrl+C` 复制多行原文、无选中按 `Ctrl+C` 无变化、主题与语言切换后选择保持；验证：逐条记录结果
- [ ] 5.4 `docs/TESTING.md` 的 `Next.Tests` 数字按联动更新；验证：文档数字与 5.2 实测一致

## 6. 收口

- [ ] 6.1 wt 合入目标分支：`git merge wt/next-log-selection-copy` 合入 **`master`**；验证：`git log master` 出现该合并、`git status` 干净

## Workflow follow-up

- 用户验收通过后追问是否归档；仅在用户明示「可以归档 next-log-selection-copy」时执行 `openspec archive`，并同批清除 `wt/next-log-selection-copy` 的 worktree。
- 本 change 无探索笔记需要归档。
