# Proposal

## Why

界面语言由 `Services/Loc.cs` 的**静态构造期**按 `CultureInfo.CurrentUICulture` 探测，仓库无 `settings.json` 时完全由系统区域决定 —— **本地开发机 zh-CN、GitHub runner en-US**。于是「被测输出走 `Loc.T`/`Loc.F` + 断言硬编码中文」的用例必然**本地全绿、推送就红**，且本地反复跑也复现不出。

该机制已导致 CI 连续翻车 4 次（2026-10-09 ×2、10-10、10-18；最近一次是 fileproxy 越界消息，623 条挂 1 条）。前三轮的修法都是「就事论事把那条断言改成双语」—— 靠人自觉，已经失败 4 次，缺的是**系统性防线**。

同时排查发现两处实际缺陷：
1. adb server 自愈失败提示是**硬编码中文**（未走 `Loc`），英文界面下用户看到整句中文引导；
2. 新增的「清空日志」按钮，其可用性状态机与设备下拉框清理**零自动化覆盖** —— spec 明确列了三个场景却只能靠手工验收。

## What Changes

- **修复**：adb server 自愈失败提示的**引导语**改走本地化（中英双语）；尾部的**失败原因**保持上游原文不翻译（翻译 adb 自身抛出的异常等于伪造设备端信息，排查时无法与上游输出对照）。
- **修复**：测试中硬编码中文断言本地化文案的写法，改由**扫描器测试自动拦截**（在提交前就拦住，不依赖人自觉）。
- **新增**：CI 显式声明 en-US 环境变量，把「隐式依赖 runner 区域」变成「写明要 en-US」——换 runner / 改系统语言时不会静默变中文而突然红一片。
- **新增**：本地一键复现 CI 语言环境的脚本（`scripts/run-tests-en-us.sh`），让「本地全绿、推送就红」当场可见。
- **新增**：「清空日志」按钮可用性状态机与设备下拉框清理的自动化覆盖（spec 已有需求，此前零覆盖）。
- **文档**：`docs/TESTING.md` 新增「CI 语言环境」章节（机制 + 三道防线 + 历史翻车记录）；更正「`dotnet test` 不可用」的过时记载。

### 非目标（Out of Scope）

- **不改** `Loc` 的语言探测机制本身（改为读仓库内配置 / 显式参数）。那会牵动全部语言相关代码，且当前机制有可用的规避手段。
- **不改** `CommandStore` 等既有条目的中文原文存储方式（`Category` / `Remark` 存原文、本地化副本走 `CategoryZh` / `RemarkText`）—— 该设计是「落盘稳定、显示可译」的正确取舍。
- **不做**全量文案翻译审计。本次只修「新增功能 / 被改动路径」上的漏译，既有文案的漏译另开 change。
- **不补** `LogcatStream` 的直测（需真实设备与 adb，属仓库既有排除口径）。
- **不改** CI 的 runner 镜像与触发条件。

## Capabilities

### New Capabilities

（无。）

### Modified Capabilities

- `adb-server`: 新增「自愈提示的本地化与原因保真」需求 —— 引导语随界面语言变化、失败原因保持上游原文。
- `adb-capture`: 新增「清除入口的文案本地化」需求 —— 「清空日志」按钮文本与清除结果反馈随界面语言变化。

> 需求本身的**存在**已在既有主规范里（自愈提示、清除入口），本次只补「文案随界面语言变化」这一维度的契约，
> 故走 MODIFIED 增量而非新建能力。

## Impact

- `Services/AdbManager.cs`：自愈失败提示的构造方式（硬编码插值 → `Loc.F`）。
- `Services/LocTable.RuntimeMsg.cs`：新增 1 条译文（自愈失败提示的引导语）。
- `frmMain.cs`：提取 3 个 `internal static` 纯函数（`CanStartCapture` / `CanClearLog` / `ClearDeviceCombo`），
  供脱离窗体单测；**对外行为不变**。
- `tests/logcat.Tests/`：新增 2 个测试文件（`AdbClearLogButtonTests` / `CiLocaleAssertionTests`），
  既有 `AdbManagerServerSelfHealTests` 的 2 条断言改为双语。
- `.github/workflows/ci.yml`：Test 步骤显式声明 en-US 环境变量。
- `scripts/run-tests-en-us.sh`：新增本地复现 CI 语言环境的脚本。
- `docs/TESTING.md` / `README.md` / `README.zh-CN.md`：测试数字与覆盖率重采、新增章节。
- 无公开 API 变更，无数据模型变更，无新依赖。
