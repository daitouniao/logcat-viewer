# Design

## Context

采集链路：`frmMain.AdbStartCapture` → `LogcatStream.Start(clearFirst: true)` → `RunAsync` 内 `ShellAsync("logcat -c")` → `ExecuteRemoteEnumerableAsync("logcat -v threadtime")` 起流。清空点全仓库唯一、硬编码，`AppSettings` 无相关项。工具栏 ADB 分组的按钮可用性已集中在 `UpdateAdbCaptureUI`（`hasDevice = _comboDevice.Items.Count > 0`），「刷新设备」/「设备操作」用 `Tag = "adb-free"` 标记为无设备也可点。测试唯一口径为 `.tmp-test/tstrun` 反射 runner；`LocTableTests` 扫描器校验界面键齐全。

## Goals / Non-Goals

**Goals:**
- 采集启动的缓冲区语义变为「不清空、从现有内容起流」；清空动作唯一入口 = 新按钮。
- 新按钮接入现有 `UpdateAdbCaptureUI` 状态机，不引入第二套可用性判断。
- 新增界面文案全部过 i18n（键 = 中文原文），`LocTableTests` 零豁免。

**Non-Goals:**
- 不做采集前的 `logcat -d` 历史快照（另议）。
- 不做多设备并发采集。
- 不做确认对话框与撤销。
- 不修批界崩溃提取前瞻断裂（独立 change）。

## Decisions

1. **移除 `clearFirst` 参数而非保留默认 false。** 全仓库仅一处调用且传 true，参数移除后编译器保证无残留清空路径；将来若要「采集前清空」开关，届时再加参数，成本低于现在养一个死参数。备选（保留参数默认 false）被否：死代码 + 给读者「还有别的模式」的错觉。
2. **按钮放「■ 停止采集」之后、ADB 分组分隔符之前，`Tag = "adb"`。** 清除必须针对具体设备，不能标 `adb-free`；与开始/停止采集同组符合操作语义。
3. **可用性 = `hasDevice && !_adbCapturing`，实现在 `UpdateAdbCaptureUI` 内一行。** 与 `_btnAdbStart` 完全同条件；设备列表刷新（`AdbRefresh`）与采集启停最终都汇聚到该方法，状态机天然一致。
4. **点击不弹确认框。** 按钮文案「清空日志」已明示后果；维护者偏好少弹窗（triage 场景高权重）。清空不可恢复的风险由文案承担，评审如需确认框再加。
5. **清除执行走 `AdbManager.ShellAsync(serial, "logcat -c", 5)`，await + try/catch。** 成功 `ShowStatus`、失败 `ShowError`（含异常消息）；点击时 `SelectedSerial()` 为 null 视为失败提示。不做 root 提权包裹——adb shell uid 一直具备清空权限。
6. **采集期间禁用清除按钮，不做「采集中清除」。** 采集中清空语义含混（活跃流与缓冲区交互随设备实现而异），且无明确用例；禁用与开始采集按钮对称。
7. **i18n 落点：** 按钮文案与状态栏消息键进 `LocTable.MainUi.cs`（设备区文案既有归属），键 = 中文原文；新增英文翻译随键同批提交，`LocTableTests` 扫描器把关。

## Risks / Trade-offs

- [首次采集会把缓冲区几万行历史灌入，首次建索引变慢] → 实测单设备 3~6 万行在秒级；`BuildAsync` 已有进度条与取消；与「打开一个大日志文件」同级成本。
- [历史内容跨午夜/跨月进入文档] → 采集与打开文件共用同一解析管线，非新路径；但任务清单里补一条跨午夜历史的采集文档用例，防时间戳回归。
- [用户依赖旧的「干净起点」行为] → 手动清除入口覆盖该诉求（采集前点一下即可）；release note 说明行为变更。
- [`logcat -c` 执行瞬间与起流之间存在毫秒级缝隙] → 仅影响清除入口自身场景（采集前手动清），采集路径已不涉及。

## Migration Plan

无持久化迁移。单提交实施；回滚 = revert。行为变更需在 release note 标注（**BREAKING**：采集不再自动清空设备日志缓冲区）。

## Open Questions

无。
