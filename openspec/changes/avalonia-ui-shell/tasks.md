# Tasks

## 1. 准备与工程骨架

- [x] 1.1 在独立 worktree 中实施：分支 `wt/avalonia-ui-shell`（路径以 `git worktree list` 定位，不写本机绝对路径）；验证：`git worktree list` 中能看到该分支
- [x] 1.2 新建 `logcat-next/` 下的 `Next.Interop`、`Next.App`、`Next.Tests` 三工程与解决方案，`Next.App` 引入 Avalonia 当前稳定主版本并设 `net10.0`；验证：`dotnet build` 成功，空窗口可启动
- [x] 1.3 把 `logcore.dll` 作为构建项复制到应用输出，并实现「按名解析导出表、缺失即明确报错」；验证：`LogcoreBindingTests.MissingExportReportsClearError` 与 `LoadsWhenLibraryPresent` 通过

## 2. 互操作与数据取页

- [x] 2.1 实现句柄包装与调用封装（`plan` / `commit` / `poll_events` / `row_count` / `page` / `free_page` / `free_handle` / `take_last_error`），错误码经线程本地取回；验证：`LogcoreBindingTests.PlanCommitAndReadFirstPage`
- [x] 2.2 实现「后台线程取页 → 解码为托管行模型 → 立即释放原生缓冲」；行模型含序号、时间、级别、tag、正文；验证：`PageDecodeTests.ReleasesNativeBufferAfterDecode`
- [x] 2.3 覆盖分页边界：跨页连续不重不漏、记录数少于视口、空文件；验证：`PageDecodeTests.CrossPageBoundaryIsContiguous`、`ShortFileYieldsAllRows`、`EmptyFileYieldsNoRows`

## 3. 主题

- [x] 3.1 建 `DesignTokens.axaml`：`Surface.*` / `Text.*` / `Accent` / `Level.*` / `Mark.*` / `Filter.*` 语义角色，Light 与 Dark 各一份 `ThemeDictionaries`；验证：`ThemeTokenTests.LightAndDarkDefineSameKeys` 通过
- [x] 3.2 窗口、列标题、滚动条、输入区全部改为引用 token，禁写颜色字面量；验证：仓库内检索新工程无颜色字面量，手测三态切换（浅色 / 深色 / 跟随系统）无浅色残留
- [x] 3.3 自绘列表订阅主题变更并重绘；验证：手测深色下切换后可见首行不变、级别色与标记底清晰可辨

## 4. 本地化

- [x] 4.1 建 `Strings.zh-CN.json` 与 `Strings.en.json`、`LocalizationService`（可观察索引器）与 `{loc Key=...}` 标记扩展；验证：`LocalizationTests.KeySetsMatch`、`PlaceholdersMatch`、`NoEmptyTranslations` 通过
- [x] 4.2 实现启动语言来源（上次选择 → 系统区域，非中英一律英文）与选择持久化；验证：`LocalizationTests.StartupPrefersSavedChoice`、`NonChineseEnglishSystemFallsBackToEnglish`
- [x] 4.3 切换语言即时刷新且保持打开文件与滚动位置；验证：手测中英互切无重启、无裁字、无中英混杂

## 5. 大列表渲染

- [x] 5.1 实现自绘列表：定行高、滚动偏移、仅绘制视口行，列标题与滚动条联动；验证：手测 64 万行样本滚动与 Ctrl+End 跳转可用，首次拖动因首屏取页略慢、页缓存后跟手
- [x] 5.2 实现按视口请求「起始行 + 行数」，滚动与跳转时数据与渲染对齐；验证：`ViewportTests.RequestsOnlyVisibleRange`、`JumpToTailRendersTailRows`
- [x] 5.3 主题与语言切换、窗口缩放后保持可见首行与标记状态；验证：手测三项操作后可见首行不变

## 6. 集成验收

- [x] 6.1 按探索笔记的五项验收清单跑一遍并记录数字（滚动帧时间、主题切换耗时、DPI 三档、IME）；验证：滚动、主题、语言已手测通过；DPI 三档与 IME 按用户意见暂记通过、未单独实测
- [x] 6.2 把新工程的测试口径与基线数字同步到 `docs/TESTING.md`；验证：文档数字与实际 `dotnet test` 输出一致

## 7. 合入

- [ ] 7.1 将 `wt/avalonia-ui-shell` 合入目标分支 `master`；验证：合入后 `master` 上 `dotnet build` 与 `dotnet test` 全过，并报告用例口径数字

## Workflow follow-up

- 用户验收通过后，按 AGENTS §2.2 停在原状等归档指令；未验收不得归档。
- 归档时同批把 `openspec/notes/AVALONIA-UI-APPLICATION-STACK.md` 移入 `openspec/notes/archive/` 并清除该 change 的 worktree。
