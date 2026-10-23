# Tasks

> 状态说明：本 change 的**代码已实施完毕**（2026-10-22），故下列任务均标记为已完成。
> 剩余的归档动作需维护者明确指示（`openspec archive`）。
> 测试口径：`dotnet test`（本机已可用，见 `docs/TESTING.md` §1.1）；实施时基线 655，
> 后续 `fix-msgfilter-capacity-scan` 增至 662，本次新增 1 条区域硬断言 → **663**。

## 1. 诊断：确认 CI 翻车的机制

- [x] 1.1 拉 GitHub Actions 运行历史，定位 4 次失败的具体提交（10-09 ×2、10-10、10-18）与最近三次全绿
- [x] 1.2 确认语言探测路径：`Loc` 静态构造期按 `CurrentUICulture` 探测；仓库与测试输出目录均无 `settings.json` → 完全由系统区域决定
- [x] 1.3 本地复现的机制确认：**更正（2026-10-24）**——Windows 上 `LANG`/`LC_ALL`/`DOTNET_CLI_UI_LANGUAGE` 均不改变 `CurrentUICulture`（探针：不设 zh-CN、设了仍 zh-CN），原「设环境变量」方案在 Windows 是假绿；改由测试进程内强制区域（见 4.8）
- [x] 1.4 交叉验证：确认 `dotnet test` 本机已可用（旧的 hostfxr 0x80070005 不复现），且它会 await `async Task` 用例（tstrun 不 await → 既有 3 个 async 用例从未真跑过）

## 2. 修复：自愈提示本地化

- [x] 2.1 `Services/AdbManager.cs`：`EnsureServerRunningAsync` 的失败提示由硬编码中文插值改为 `Loc.F`（引导语本地化 + 原因作实参）
- [x] 2.2 `Services/LocTable.RuntimeMsg.cs`：新增「未能启动 adb server（…）：{0}」中英条目
- [x] 2.3 确认 `frmMain` 侧仍走 `ShowStatusRaw`（`ShowStatus` 会把含花括号的原因当格式串再格式化）
- [x] 2.4 测试 `AdbManagerServerSelfHealTests`：2 条语言相关断言改为 `LangScope.InZh/InEn` 双语各「生成+断言」一遍；该类补 `[Collection(LangCollection.Name)]`
- [x] 2.5 测试新增 3 条：`英文界面下自愈提示不含中文引导语`、`英文界面下原始原因保持上游原文不翻译`、`原因含花括号时提示仍可生成`

## 3. 补齐：新增功能的测试覆盖

- [x] 3.1 `frmMain.cs` 提取 3 个 `internal static` 纯函数：`CanStartCapture` / `CanClearLog` / `ClearDeviceCombo`（对外行为不变）
- [x] 3.2 新增 `tests/logcat.Tests/AdbClearLogButtonTests.cs`（12 条）：可用性状态机三场景、与「开始采集」同状态机、停止后恢复、下拉框 Text 不残留、空框幂等
- [x] 3.3 实测并钉住 WinForms 行为：`ToolStripComboBox.Items.Clear()` 不清 `Text`（残留旧序列号）→ 显式赋空不可省
- [x] 3.4 变异测试：删「显式清 Text」→ 1 条红；「采集中也允许清空」→ 3 条红

## 4. 防线：把「靠人自觉」变成「自动拦截」

- [x] 4.1 新增 `tests/logcat.Tests/CiLocaleAssertionTests.cs`：扫描「期望值位置的中文字面量 + 该字面量是本地化资源键 + 未锁语言」的高危组合
- [x] 4.2 判据精化（消除误报）：只取期望值位置、要求字面量恰好是资源键、识别类级 `SetLangGuard`
- [x] 4.3 豁免清单 `KnownLocaleSensitiveAssertions`（3 条，每条带理由）
- [x] 4.4 防假绿：加「扫到的文件数 > 10、中文期望值数 > 20」硬断言
- [x] 4.5 变异验证：注入 CI 会挂的写法 → 防线立刻红；同一变异在英文界面下确实失败（`Expected "（无过滤）"` vs `Actual "(no filter)"`）
- [x] 4.6 新增 `scripts/run-tests-en-us.sh`：本地一键复现 CI 语言环境（设 `LOGCAT_FORCE_UI_CULTURE`，见 4.8）
- [x] 4.7 `.github/workflows/ci.yml`：Test 步骤声明 `LOGCAT_FORCE_UI_CULTURE: en-US`（保留 `LANG` 等供 Unix runner；不做双跑，见 design D7）
- [x] 4.8 **（2026-10-24 更正）** 新增 `tests/logcat.Tests/TestCultureBootstrap.cs`：`[ModuleInitializer]` 按 `LOGCAT_FORCE_UI_CULTURE` 在测试进程内钉住区域并自检；`CiLocaleAssertionTests.强制界面语言已生效` 硬校验实际区域（LANG 在 Windows 无效，原方案会假绿）

## 5. 文档与规格

- [x] 5.1 `openspec/specs/adb-server/spec.md`：新增「自愈提示的本地化与原因保真」需求
- [x] 5.2 `openspec/specs/adb-capture/spec.md`：新增「清除入口的文案本地化」需求
- [x] 5.3 `docs/TESTING.md`：新增 §3.4「CI 语言环境：本地全绿、推送就红」（机制 + 三道防线 + 历史翻车记录）
- [x] 5.4 `docs/TESTING.md`：更正 §1（`dotnet test` 已可用、优先用它、tstrun 的剩余价值与口径差异）
- [x] 5.5 `openspec/config.yaml`：更正「本机 dotnet test 不可用」的过时上下文 + 补 CI 语言环境规则
- [x] 5.6 重采覆盖率并同步 README×2 徽章/版本行与 `docs/TESTING.md` §0 表格（当时基线 655；后续 662，本次 663 见 5.7）
- [x] 5.7 **（2026-10-24）** 同步 `docs/TESTING.md` §0/§3.4 与 README×2 到 663 用例；更正「设 `LANG` 即可切英文」的错误结论（Windows 无效，见 1.3/4.8）；在 §3 记录沙箱内 `Directory.Delete` 伪失败现象

## 6. 验证

- [x] 6.1 Release 构建：0 警告 0 错误
- [x] 6.2 zh-CN 环境全量：实施时 655/0/0
- [x] 6.3 en-US 环境全量：实施时 655/0/0（当时的区域切换方式已于 2026-10-24 更正，见 1.3/4.8）；2026-10-24 复核：受影响类 26 条在强制 en-US 下全过；同日全量（`LOGCAT_FORCE_UI_CULTURE=en-US`）663 条中 656 过、7 条为沙箱伪失败（见 6.5），无新增失败
- [x] 6.4 覆盖率重采：行 97.47%（3,504/3,595）、分支 88.46%、方法 95.66%（375/392）；后续 2026-10-23 重采为 3,507/3,598、377/394，见 `docs/TESTING.md` §0
- [x] 6.5 **（2026-10-24）复核结论**：AI 执行沙箱内 .NET 子进程 `Directory.Delete(recursive:true)` 静默失效（返回成功但目录仍在；同路径 PowerShell 可删、进程退出后仍在），导致 7 条 spool 删除类用例伪失败——**非仓库缺陷**，普通终端 / CI 不受影响

## 7. 待维护者指示

- [x] 7.1 **验收**（阶段 6）：维护者 2026-10-24 认定 7 条 spool 红为执行沙箱伪失败，放行验收（依据见 6.5）
- [x] 7.2 **归档**（阶段 7）：2026-10-24 维护者指示归档，已执行 `openspec archive fix-locale-prompt-and-ci-guard`（delta 已并入 `openspec/specs/adb-capture`、`adb-server`）
- [ ] 7.3 **发版**（阶段 8）：需维护者明确指令；版本号同步 5 处
