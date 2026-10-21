# Proposal

## Why

实时采集启动时会无条件执行 `logcat -c` 清空设备日志缓冲区（`frmMain` 调 `LogcatStream.Start(clearFirst: true)`，硬编码、无开关）。崩溃若发生在采集开始之前，其现场（Java `FATAL EXCEPTION`、native tombstone、ANR）随缓冲区被清而永久丢失——实测设备 crash buffer 中存有数小时前的完整 SIGSEGV tombstone，点一次「开始采集」即不可恢复。同时清空行为对用户不可见、不可关，与「日志查看器应保留现场」的定位相悖。

## What Changes

- **BREAKING（行为变更）**：开始采集不再自动执行 `logcat -c`。采集从设备缓冲区现有内容（含历史与崩溃情报）起流，之后转实时；`LogcatStream` 的 `clearFirst` 参数及内部清空分支一并移除。
- 主工具栏「■ 停止采集」旁新增「清空日志」按钮：显式执行 `logcat -c` 清空设备日志缓冲区，结果反馈至状态栏；点击不弹确认框。
- 按钮可用性跟随设备连接状态：有设备连接且未在采集中时可用，否则禁用（与「▶ 开始采集」同一状态机）。
- 新增界面文案随界面语言切换（i18n 键 = 中文原文）。

## Out of Scope（非目标）

- 多设备并发采集（单设备限制不变）。
- 采集开始前对缓冲区做 `logcat -d` 历史快照/dump（本 change 只保留不清，快照方案另议）。
- 批界崩溃提取前瞻断裂（崩溃头行落在增量批次尾部时事件丢失）的修复——独立缺陷，另开 change。
- 清除操作的确认对话框、撤销机制。

## Capabilities

### New Capabilities

- `adb-capture`: 设备日志实时采集——采集启动的缓冲区语义（不清空、从现有内容起流）、缓冲区手动清除入口、采集/清除按钮的可用性状态机、采集会话的启停行为。

### Modified Capabilities

（无——采集目前无既有 spec。）

## Impact

- `frmMain.cs`：`AdbStartCapture`（去掉 `clearFirst: true`）、工具栏构建（新增按钮）、`UpdateAdbCaptureUI`（可用性接线）。
- `Services/LogcatStream.cs`：移除 `Start(bool clearFirst)` 参数与 `RunAsync` 内 `logcat -c` 分支。
- `Services/LocTable.*`：新增「清空日志」按钮及状态栏消息的中英文条目（LocTableTests 扫描器要求键齐全）。
- `docs/FEATURES.md`：实时采集条目更新（采集不再清空缓冲区、新增清除入口），随本 change 落地同批更新。
- 测试：`LocTableTests` 键覆盖；`LogcatStream` 无既有直测（网络依赖），参数移除由编译保证。
