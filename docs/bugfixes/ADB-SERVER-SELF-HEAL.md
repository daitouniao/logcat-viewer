# 刷新设备时 adb server 未运行被误报「无设备」（fix-adb-server-self-heal）

| 状态 | 已实施待验收（OpenSpec change `fix-adb-server-self-heal` 在途；手工验收三项开放，见 IMPLEMENTATION-TRACKER 第三节） |
| 日期 | 2026-10-21 |
| 关联提交 | 待回填（git 写操作需维护者当轮授权，本轮未提交） |
| 验收结论 | 待验收 |

## 现象

adb server 不在运行时（被其他工具 `adb kill-server`、异常退出、或 adb 根本不在 PATH 导致启动时从未拉起），手动「刷新设备」得到空列表，状态栏显示「无设备」。用户无法区分「没插设备」与「adb server 没跑」，也没有任何引导：

- `AdbManager` 构造时尝试过一次 `StartServer("adb", false)`，失败仅写一行日志，之后整个生命周期不再重试；
- `ListDevicesAsync` 吞掉 `GetDevices()` 的连接异常返回空列表，`AdbRefresh` 拿到空列表只能显示「无设备」；
- 设备监控（`DeviceMonitor`）在 server 不可达时的失败形态未完全实证，其 fire-and-forget 启动的异常进不了既有 catch（未观察异常）。

本机真实根因（探索阶段意外发现）：`%TEMP%\adb.log` 曾膨胀至 3.7 GB 且打开时 Permission denied，导致**任何** adb 都无法启动 server——server 不是没开，是起不来，而旧代码把异常吞成一行日志，表现为「找不到设备」。文件删除后恢复正常。

## 根因

1. **自愈缺位**：server 拉起只在构造时尝试一次，刷新入口不探测 server 状态，失败后无任何重试路径。
2. **「不可用」与「无设备」无法区分**：`ListDevicesAsync` 的 catch 把「连不上 server」和「连上了但没插设备」压成同一个空列表，UI 只能显示「无设备」。
3. **监控僵尸态（实证）**：server 运行中被外部 kill 后，已存活的监控任务既不抛异常也不再收事件（fault 为 null，事件数冻结）。即使自愈把 server 拉起来，不重建监控也永远收不到后续设备事件——重建不能以异常为触发器。

## 修复

OpenSpec change `fix-adb-server-self-heal`（proposal / specs delta `adb-server` / design / tasks 四件套在途）：

- **`AdbManager.EnsureServerRunningAsync()`**：`GetStatus()` 探测（异常按未运行）→ 未运行复用 `RunAdbAsync("start-server")` 拉起（退出码 ≠ 0 视为失败）→ 拉起后 `GetStatus()` 复核；失败统一返回提示串「未能启动 adb server（请确认 adb 已加入 PATH，或手动执行 adb start-server）：{原因}」。方法自身 SHALL NOT 触及监控（解耦，保证单测零触网）。
- **结果对象 `AdbServerSelfHealResult(Prompt, Restarted)`**：`Prompt` 非 null = 失败提示；`Restarted` = 本次确曾拉起，调用方据此无条件重建设备监控。
- **`RestartMonitor()`**（Stop + Start）与加固：`StartMonitor` 的 fire-and-forget 加 `Task.Run` 包装（OCE 静默、其余 LogWarning、不置空 `_monitor`）；`StopMonitor` 的 `Dispose` 加 try/catch——实证监控循环已故障时 `DeviceMonitor.Dispose` 会把故障异常再抛一遍，不防护则 `RestartMonitor` 把异常抛进刷新流程（async void 链路直接崩）。
- **`frmMain.AdbRefresh` 接入**：入口先自愈；提示非 null → `ClearDeviceList()` + `ShowStatusRaw(提示串)`（运行时数据消息，走 `Loc.F` 会被消息里的花括号炸 `FormatException`）+ 刷新按钮保持可用（可重试）+ return。提取 `ClearDeviceList` 供空列表分支复用；`OnDevicesChanged` 不动。
- **测试注入**：internal 构造注入四个 seam（状态探测 / 自愈拉起 / 构造时拉起 / 监控 socket 工厂），公共构造签名不变、生产行为逐字一致。

## 验证

- **自动化**（`AdbManagerServerSelfHealTests`，9 条，经注入假实现零触网——不触真实 `AdbServer`、不派生 adb 进程、无 5037 连接）：server 已运行零副作用直接返回；未运行拉起成功复核通过（拉起恰一次）；拉起抛异常返提示（含引导词与原始消息）；退出码 ≠ 0 / 复核仍不可达各返提示；探测抛异常按未运行处理仍尝试拉起；构造时拉起失败不抛且对象可用；监控启动失败置空且可重建；监控取消不置空已重建的监控。tstrun 全量口径 **637 过 / 0 失败 / 0 跳过**（628 基线 + 9 新增；本机 `dotnet test` 不可用，口径以 tstrun 为准）。
- **库行为实证**（`.tmp-test/adbprobe`，2026-10-20 ~ 21，server 运行态 / 停止态 / 运行中被杀三态）：`GetStatus` 双态均不抛；`new AdbSocket()` 构造即急切连接（停止态抛 `SocketException`）；监控僵尸态（被杀后不 fault 也不再收事件）；`StartAsync` 返回「首批设备列表」任务、取消可传播到阻塞读、循环故障时 `Dispose` 抛 `AggregateException`。
- **维护者手工**（三项，登记于 `docs/IMPLEMENTATION-TRACKER.md` 第三节，待做）：真 server 停止 + adb 在 PATH → 刷新自愈恢复枚举；adb 不在 PATH（或临时改名模拟）→ 状态栏手动处理提示、下拉清空、可重试；server 正常 → 刷新行为与改动前一致。
