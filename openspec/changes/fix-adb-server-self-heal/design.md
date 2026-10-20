# Design：刷新设备时 adb server 自愈（fix-adb-server-self-heal）

## 已实证事实（探索阶段，.tmp-test/adbprobe 临时工程，2026-10-20，server 运行态）

1. `AdbServer.Instance.StartServer("adb", false)` 幂等：server 运行中再次调用返回 OK，不重复拉起。
2. adb 解析不到（模拟不在 PATH）时 `StartServer` 抛 `ArgumentOutOfRangeException`，消息为「…does not seem to be a valid adb.exe executable. The path must end with `adb.exe`」。该消息对「adb 不在 PATH」场景有误导，但修复侧 catch 后统一转成手动处理提示，不受影响。
3. `DeviceMonitor` 在 server 正常时 `StartAsync` 立即返回、循环在后台跑，且会创建真实 socket 连接 5037 —— 单测中直接/间接触达会真实联网（草稿实现 3/5 用例失败的根因即 `EnsureServerRunning` 内部顺带调 `StartMonitor`）。

## 实证结论（探索阶段，.tmp-test/adbprobe 临时工程，2026-10-20，含 server 停止态与运行中被杀场景）

1. `AdbServer.Instance.GetStatus()` 双态均正常：运行中返回 `IsRunning=True`；停止时返回 `IsRunning=False`，**不抛异常**。
2. `AdbServer.Instance.StartServer("adb", false)` 在 server 运行中幂等（返回 OK）；server 停止态在本机实测**抛 `AdbException: adb process returned error code -1`**（注：该次失败同时叠加了下面的 adb.log 环境问题，healthy 环境下停止态未取得成功样本，故不作为自愈拉起的首选原语）。路径解析不到 adb.exe 时抛 `ArgumentOutOfRangeException`（消息「…must end with adb.exe」对不在 PATH 场景有误导）。
3. **`new DeviceMonitor(new AdbSocket())` 构造即急切连接**：server 停止时构造直接抛 `SocketException`（应用现有 `StartMonitor` 的 try/catch 能接住并置空，启动期无泄漏）。
4. **监控僵尸态（甲/乙判定的实际答案）**：server 运行中被外部 kill-server，已存活的监控任务**既不抛异常也不退出**（fault=null），server 恢复、设备重连后**永远不再收到事件**——不崩、也不工作。因此自愈拉起成功后必须**无条件重建监控**（StopMonitor + StartMonitor），不能依赖异常触发。
5. 环境特有发现（本机根因）：`%TEMP%\adb.log` 曾膨胀至 **3.7 GB** 且打开时 Permission denied，导致**任何 adb 都无法启动 server**（`* failed to start daemon`）——这很可能就是原始问题「系统没开 adb server 找不到设备」在本机的真实根因（server 不是没开，是起不来，而旧代码把异常吞成一行日志）。文件删除后恢复正常。另：本机 PATH 存在 4 个 adb.exe（Aibote×2 + SDK platform-tools），多版本共存。
6. 沙箱注意事项：在 Qoder 会话内，**dotnet 子进程派生的 adb 无法创建 `%TEMP%\adb.log`**（Permission denied），bash 直跑同一命令正常——属父进程相关的沙箱拦截，不影响真实用户环境，但影响在会话内做停止态实验（对策：bash 侧编排 server 启停）。

## 方案

### AdbManager.EnsureServerRunningAsync（自愈入口）

- `public async Task<string?> EnsureServerRunningAsync()`：
  1. `GetStatus()` 探测（双态行为已实证，异常按未运行处理）——运行中直接返回 null，零副作用；
  2. 未运行 → 复用既有原语 `RunAdbAsync("start-server", null, CancellationToken.None)` 拉起（比库 `StartServer` 可靠：有逐行输出、异常自带「请确认 adb 已加入 PATH」指引；exit code ≠ 0 视为失败）；
  3. 拉起后再 `GetStatus()` 复核，仍不可达 → 返回提示；
  4. 失败统一返回提示串：`未能启动 adb server（请确认 adb 已加入 PATH，或手动执行 adb start-server）：{原因}`；异常消息按仓库约定保持中文原文、不本地化（先例：`RunAdbAsync`）。
- `RunAdbAsync("start-server")` 属 host 子命令（`AdbHostCommands` 已含 start-server），参数拼装无需 serial。

### 构造注入点（草稿教训固化）

- 现有公共构造签名不变；新增 internal 测试构造注入「状态探测 / 拉起」假实现，单测不触真实 `AdbServer` 与真实进程。
- **`EnsureServerRunningAsync` 自身 SHALL NOT 触及监控**（解耦，保证单测零触网——草稿 3/5 用例失败的根因即内部顺带调 `StartMonitor`）。

### 监控重建（实证 4 → 无条件重建）

- `AdbManager` 新增 `RestartMonitor()`（`StopMonitor(); StartMonitor();`）；`frmMain` 在「探测判定 server 原本未运行且自愈拉起成功」时调用（需要 `EnsureServerRunningAsync` 返回自愈是否发生的信号，如返回值区分 null / 重试提示 / 是否拉起，或拆两个方法——实施时定，倾向返回一个含 `Restarted` 标记的小结果对象）。
- `StartMonitor` 的 fire-and-forget 仍加 `Task.Run` 包裹防未观察异常（实证：异常进不了既有 catch）；但**不**以异常作为重建触发器（僵尸态无异常）。
- `OperationCanceledException`（StopMonitor 取消）静默。

### frmMain.AdbRefresh 接入

- 入口先 `await EnsureServerRunningAsync()`；返回提示时：`ClearDeviceList()` + `ShowStatusRaw(提示串)` + 保持刷新按钮可用（可重试）+ return；自愈发生时调 `RestartMonitor()`。
- 用 `ShowStatusRaw` 而非 `ShowStatus`：后者走 `Loc.F` 格式化，异常消息含花括号时会炸 `FormatException`（运行时数据消息的既有出口，先例：后台进度上报）。
- 提取 `ClearDeviceList`（清 `_adbDevices`、下拉 Items、残留 Text），空列表分支复用；`OnDevicesChanged` 不动。

## 测试设计

`AdbManagerServerSelfHealTests`（注入假实现，零触网）：

1. server 已运行（探测 true）→ 返回 null，拉起动作未被调用
2. server 未运行、拉起成功且复核通过 → 返回成功结果，拉起动作恰被调用一次
3. 拉起抛异常 → 返回提示串，包含「adb start-server」引导与原始异常消息
4. 拉起 exit code ≠ 0 / 复核仍不可达 → 返回提示串
5. 探测抛异常 → 按未运行处理，仍尝试拉起
6. 构造时拉起失败 → 不向外抛（维持既有「失败仅日志」口径）

## 手工验收清单（登记 IMPLEMENTATION-TRACKER）

- 真 server 停止 + adb 在 PATH：kill-server 后点刷新 → 设备恢复枚举（需 5555 无线设备重连配合，场景即探索时的两台真机）
- adb 不在 PATH（或临时改名模拟）：点刷新 → 状态栏出现手动处理提示、下拉被清空、再点可重试
- server 正常：刷新行为与改动前一致
