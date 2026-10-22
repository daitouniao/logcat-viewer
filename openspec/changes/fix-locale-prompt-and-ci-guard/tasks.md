# Tasks

> 状态说明：本 change 的**代码已实施完毕**（2026-10-22），故下列任务均标记为已完成。
> 剩余的归档动作需维护者明确指示（`openspec archive`）。
> 测试口径：`dotnet test`（本机已可用，见 `docs/TESTING.md` §1.1），基线 **655**。

## 1. 诊断：确认 CI 翻车的机制

- [x] 1.1 拉 GitHub Actions 运行历史，定位 4 次失败的具体提交（10-09 ×2、10-10、10-18）与最近三次全绿
- [x] 1.2 确认语言探测路径：`Loc` 静态构造期按 `CurrentUICulture` 探测；仓库与测试输出目录均无 `settings.json` → 完全由系统区域决定
- [x] 1.3 本地复现：造 en-US 仿真（`LANG`/`LC_ALL`/`DOTNET_CLI_UI_LANGUAGE`），临时探针实证 `CurrentUICulture` zh-CN→en-US、`Loc.Current` ZhCn→En
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
- [x] 4.5 变异验证：注入 CI 会挂的写法 → 防线立刻红；同一变异在 en-US 下确实失败（`Expected "（无过滤）"` vs `Actual "(no filter)"`）
- [x] 4.6 新增 `scripts/run-tests-en-us.sh`：本地一键复现 CI 语言环境
- [x] 4.7 `.github/workflows/ci.yml`：Test 步骤显式声明 en-US 环境变量（不做双跑，见 design D7）

## 5. 文档与规格

- [x] 5.1 `openspec/specs/adb-server/spec.md`：新增「自愈提示的本地化与原因保真」需求
- [x] 5.2 `openspec/specs/adb-capture/spec.md`：新增「清除入口的文案本地化」需求
- [x] 5.3 `docs/TESTING.md`：新增 §3.4「CI 语言环境：本地全绿、推送就红」（机制 + 三道防线 + 历史翻车记录）
- [x] 5.4 `docs/TESTING.md`：更正 §1（`dotnet test` 已可用、优先用它、tstrun 的剩余价值与口径差异）
- [x] 5.5 `openspec/config.yaml`：更正「本机 dotnet test 不可用」的过时上下文 + 补 CI 语言环境规则
- [x] 5.6 重采覆盖率并同步 README×2 徽章/版本行与 `docs/TESTING.md` §0 表格（655 用例；行 97.47%、分支 88.46%、方法 95.66%）

## 6. 验证

- [x] 6.1 Release 构建：0 警告 0 错误
- [x] 6.2 zh-CN 环境全量：655/0/0
- [x] 6.3 en-US 环境全量（CI 实际环境）：655/0/0，连跑 3 次无偶发
- [x] 6.4 覆盖率重采：行 97.47%（3,504/3,595）、分支 88.46%、方法 95.66%（375/392）

## 7. 待维护者指示

- [ ] 7.1 **验收**（阶段 6）：维护者确认本次改动
- [ ] 7.2 **归档**（阶段 7）：维护者明确指示后执行 `openspec archive fix-locale-prompt-and-ci-guard`（delta 并入主规范）
- [ ] 7.3 **发版**（阶段 8）：需维护者明确指令；版本号同步 5 处
