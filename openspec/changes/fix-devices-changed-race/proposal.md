# Proposal

## Why

设备列表变更事件把 `DeviceSelector` 内部的**活** `List<DeviceInfo>` 直接交给订阅方，而事件在后台监控线程触发、处理器经 `SafeInvoke` 投递到 UI 线程执行。两次刷新交错时，UI 线程正在枚举的正是后台线程随后 `Clear()` 的同一个列表，抛 `System.InvalidOperationException: Collection was modified` 让整个应用崩溃（Debug 构建实测栈顶 `frmMain.OnDevicesChanged`，`DeviceSelector.ApplyDevices` 为事件源）。

## What Changes

- `DeviceSelector` 向订阅方一律传递**变更时刻的快照**（`DeviceInfo[]`），不再暴露内部可变列表。
- `DeviceSelector` 的列表读写统一进同一把锁（`ApplyDevices` / `Clear` / `Devices` / `CurrentSerial` / `CurrentRootMode`），消除「读到半改状态」；事件仍在锁外发出。
- 补回归用例：连续两次变更交错时先交付的通知参数内容不变、枚举不抛；自愈失败清空与后台刷新并发不抛。

### 非目标（Out of Scope）

- 不改设备监控策略、自愈流程与下拉框展示口径。
- 不改 `AdbManager` 的设备枚举实现与其事件。
- 不引入线程封送机制（UI 线程切换仍由各 exe 的 `SafeInvoke` 负责）。

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- `adb-server`: 新增「设备列表变更通知的线程安全与快照语义」需求；既有「刷新时自愈」需求不变。

## Impact

- **代码**：`logcat.UiCore/Services/DeviceSelector.cs`（单锁 + 快照）；各 exe 的订阅方（`frmMain`、工具箱）**无需改动**——收参类型仍是 `IReadOnlyList<DeviceInfo>`，只是不再随内部列表变化。
- **语义**：通知参数变为「变更那一刻的快照」，订阅方不应再依赖「拿到的是活列表」这一未言明的约定。
- **测试**：`DeviceSelectorTests` 增并发/快照用例；`docs/TESTING.md` 与两份 README 的用例数联动更新。
