# Proposal：刷新设备时自愈 adb server 并区分「不可用」与「无设备」（fix-adb-server-self-heal）

## Why

adb server 不在运行时（被其他工具 `adb kill-server`、异常退出、或 adb 根本不在 PATH 导致启动时从未拉起），手动「刷新设备」得到空列表，状态栏显示「无设备」。用户无法区分「没插设备」与「adb server 没跑」，也没有任何引导：

- `AdbManager` 构造时尝试过一次 `StartServer("adb")`，失败仅写日志，之后整个生命周期不再重试；
- `ListDevicesAsync` 吞掉 `GetDevices()` 的连接异常返回空列表，`AdbRefresh` 拿到空列表只能显示「无设备」；
- 设备监控（`DeviceMonitor`）在 server 不可达时的失败形态未完全实证，无论何种形态，其 fire-and-forget 启动的异常都进不了既有 catch（未观察异常）。

用户拍板的期望行为（2026-10-20）：adb 在 PATH → 刷新时自动拉起 server（自愈）；adb 不在 PATH → 明确提示用户手动处理（确认 PATH 或手动执行 `adb start-server`）。

## What Changes

- **新增「刷新设备时 adb server 自愈」需求**（新 capability `adb-server`）：手动刷新设备列表（含启动后的首次刷新）SHALL 先确保 adb server 在运行；未运行则自动拉起；拉起失败 SHALL 展示手动处理提示，SHALL NOT 误报「无设备」。
- 实现侧：`AdbManager` 新增 `EnsureServerRunning`（直接幂等拉起，异常转提示串，探索已实证可行）；`frmMain.AdbRefresh` 刷新入口接入；设备监控失败防护按探索实证③补跑后拍板（细节见 design.md）。
- 对用户可见行为：server 在跑时刷新行为零变化；server 未跑时从「无设备」误导变为自动恢复或明确提示。

### 不在范围（out of scope）

- adb 可执行文件路径的显式配置项（PATH 之外的场景仍提示手动处理；登记候选池）
- server 死亡期间的监控自动重连定时器（仅在刷新入口自愈，监控策略见 design.md）
- `ListDevicesAsync` 内部 `GetDevices()` 异常的全面透传重构（自愈已覆盖主要不可用路径，探测与枚举之间的极小竞态窗口维持现状）

## Capabilities

### New Capabilities

- `adb-server`：adb server 生命周期自愈与不可用提示（首次建立该 capability 的行为契约）。

### Modified Capabilities

（无。`openspec/specs/` 现仅有 `fileproxy`，不涉及。）

## Impact

- 改动代码：`Services/AdbManager.cs`（新增 `EnsureServerRunning`；构造函数增加测试注入点；`StartMonitor` 失败防护）、`frmMain.cs`（`AdbRefresh` 接入自愈与 `ShowStatusRaw` 提示；提取 `ClearDeviceList` 复用）；API 面：`AdbManager` 新增公共方法，限仓内调用点
- 测试：新增 `AdbManagerServerSelfHealTests`（经构造注入假实现，单测零触网）；无既有用例改动
- 无新第三方依赖；无设置字段变化；不改变设备枚举、root 检测、采集等既有行为
- 公开文档：设备刷新相关描述在打 tag 时按活文档规则联动更新；bug 报告 `docs/bugfixes/ADB-SERVER-SELF-HEAL.md` 随实施撰写（双文档实践）
