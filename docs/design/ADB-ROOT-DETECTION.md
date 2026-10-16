# ADB root 检测与初始化调查

> 状态：调查完成，**已编码（2026-10-15）**。2026-10-15 评审第三轮：Q6 定案（弹窗确认 + 设备操作入口重试项）、Q3 经用户确认、Q7 查证完成——**结论：AdvancedSharpAdbClient 通道无任何超时机制，方案 b 前提不成立**；第四轮：Q7 定案（方案 a1，见 §6.1 第 8 条），**待决问题全部关闭**，另补录 2 项编码前输入（见 §6.4）。
> 关联提交：2589f47「feat: root 检测升级 RootMode 三态（Su/Adbd/None），探测带超时防授权框挂起，Adbd 型跳过 su 包裹（ADB-ROOT-DETECTION）」
> 现象（用户报告）：root 设备需要先在控制台执行 `adb root`，应用才有 shell 的 root 权限。
> 引用一律用符号名，不用行号。
> 实施摘要（2026-10-15）：`DeviceInfo` 增 `RootMode` 枚举与 `RootDetectEntry(Mode, SuFailed)`；`AdbManager` 检测改异步（`DetectRootAsync` 先裸 `id` 短路判 Adbd，再 `su -c id`；`TryProbeAsync` 用 `ExecuteRemoteCommandAsync` + CTS 超时 8 秒，超时值按 §6.1 第 8 条建议区间取中）；`_rootCache` 存 `RootDetectEntry`，重测范围按 Q5 修订（`ShouldRedetect`）；Adbd 跳过 su 包裹（`BuildRootShellCmd`，`ShellRoot`/`DeviceFilePane.ShellCmd` 共用语义）；Q6 两入口 = `frmMain.AdbRefreshManualAsync`（手动刷新 Yes/No 确认，仅存在 Su/None 缓存时弹）+ `DeviceOpsDialog.MaybeOfferRootRetry`（OnShown 时，仅 None+SuFailed 触发）；`OnDeviceEvent` 改 `await ListDevicesAsync()` 解除事件循环卡死。新增单测 20 条（ModelTests），tstrun 全量回归 519/519 过。探测链路（真实设备上 Adbd/Su/None 判定、授权框不再重弹、重试入口）待手工验收。

## 1. 现象

在 root 设备上，应用的 shell 操作（文件浏览、设备操作等）默认没有 root 权限；用户在控制台手动执行 `adb root` 之后，应用的 shell 才具备 root 权限。

## 2. 背景：两种 root 机制（易混淆）

| 机制 | 原理 | 适用 |
| --- | --- | --- |
| **adbd root**（`adb root`） | 重启设备端 adbd 进程，使其以 uid=0 运行；此后所有 `adb shell` 命令**天生就是 root** | 仅 userdebug/eng 构建（模拟器、工程机）；生产 user 构建会报 `adbd cannot run as root` |
| **su 提权**（Magisk 等） | shell（uid=2000）执行设备上的 `su` 二进制，向 root 守护进程申请提权，单条命令生效；与 adbd 是否 root 无关 | 已刷入 Magisk/KernelSU 的真机 |

两者互不蕴含：adbd root 的设备可以完全没有 `su` 二进制；有 Magisk 的真机 adbd 依旧非 root。

## 3. 现状：应用的 root 初始化链路

### 3.1 检测（唯一的 root 初始化入口）

- `AdbManager.ListDevices()` 枚举设备时，对每台 Online 设备调用 `DetectRootCached`。
- `DetectRoot` 执行 `su -c id`，输出包含 `uid=0` 即判定支持 root；无 `su` 或执行异常一律判 false。
- 结果写入 `DeviceInfo.IsRoot`。

**应用从不调用 `adb root`，也没有「adbd 已 root」（shell 本身 uid=0）的识别路径。** root 检测只认 `su` 一条路。

### 3.2 缓存

- `AdbManager._rootCache`（序列号 → bool）按 serial 缓存检测结果，**进程生命周期内只检测一次**。
- 缓存动机（`DetectRootCached` 注释）：`su -c` 每执行一次，手机上就弹一次 root 授权框，设备列表每次刷新都重测会让授权请求反复出现。
- 后果：即使设备断开重连（serial 不变），甚至用户在外部执行 `adb root` 改变了设备的 root 状态，缓存也不会失效，`IsRoot` 保持首次检测值。

### 3.3 消费

- `DeviceInfo.ToString()`：设备下拉列表中显示 `xxx [root]` 标记。
- `frmMain.SelectedIsRoot()`：取当前选中设备的 `IsRoot`，作为构造参数传给 `DeviceOpsDialog` / `FileBrowserDialog` / `ApkInstallDialog` / `ApkUninstallDialog`。
- `CommandDialog`（命令窗口，经 `DeviceOpsDialog` 透传 `_rootProvider`，即 `SelectedIsRoot`）：用于「未检测到 root，su -c 可能失败」提示与设备标签 `[root]` 标记（2026-10-15 第二轮评审补充）；命令的 su -c 包裹另由 root 复选框由用户手动控制。
- `DeviceFilePane._isRoot`（文件浏览）：
  - `NeedsElevation`：非 sync 直达路径（如 `/data/*`）且 `_isRoot`（或 run-as）时才提权；
  - `ShellCmd`：提权时把命令包成 `su -c '...'`（`AdbManager.ShellQuote` 转义）；
  - pull/push 对受限文件走「cp 到 shell 可写中转目录 → sync 通道收发」（`DeviceFilePane` 中转目录逻辑）。

### 3.4 `adb root` 在项目中的唯一触点

- 命令窗口预置命令：`CommandStore` 内 `CommandKind.Adb` 预置 `"root"`（「以 root 重启 adbd」）与 `"unroot"`，需用户在命令窗口手动执行，经 `AdbManager.RunAdbAsync`（本机 adb 进程）运行。
- `AdbManager.AdbArgs` 中 `root` 不在 `AdbHostCommands` 集合内，会自动补 `-s <serial>`，参数拼接正确。
- 该命令执行后对 `_rootCache` **没有任何失效处理**。

## 4. 根因分析

用户遇到的设备**最可能属于 adbd root 型**（模拟器/工程机：无 `su`，或 shell 域无权执行 `su`）——该定性由症状反推，未经实测（可在设备端跑 `getprop ro.build.type` 验证）。此时：

1. 首次枚举设备，`DetectRoot` 的 `su -c id` 失败 → `IsRoot=false` 并写入 `_rootCache`。
2. UI 无 `[root]` 标记；`DeviceFilePane.NeedsElevation` 恒 false，所有 shell 命令不加提权包裹，以普通 shell（uid=2000）身份执行 → 受限目录全部失败。
3. 用户在控制台执行 `adb root` → adbd 以 uid=0 重启。
4. 应用后续的**普通 shell 命令**（不带 `su` 包裹的那些）本身已是 root → 受限目录能访问了，表现为「控制台 adb root 之后应用才有 root」。
5. 但 `_rootCache` 不失效、检测路径只认 `su`：即使重启应用重测，`su -c id` 在无 su 设备上依然失败，`IsRoot` 永远为 false。`[root]` 标记、`su -c` 提权路径（`AdbManager.ShellRoot`、`DeviceFilePane.ShellCmd` 的 root 分支）对这类设备完全不可达；功能「碰巧」依赖了未被识别的 adbd root 状态。

即：**应用把「root」等价于「su 可用」，而这类设备的 root 等价于「adbd root」，两者交集为空。**

## 5. 修复方向（均已定案，未编码）

- **方向 A：检测补充 adbd root 识别**（**已采纳，2026-10-15**）
  `DetectRoot` 除 `su -c id` 外再跑一条不带 su 的 `id`（探测顺序按 §6.4 输入项 1：先裸 `id`、探到 `uid=0` 短路）；输出已含 `uid=0` 即判定「adbd root 模式」。
  su 授权挂起防护按 Q7 定案（方案 a1，见 §6.1 第 8 条）在 `DetectRoot` 内落地。
  需区分两种 root 模式：`DeviceInfo` 增加 `RootMode` 枚举（None / Su / Adbd，**API 形态已定案，见 §6.1.2**）：Adbd 模式下提权包裹应**跳过**（shell 已 root，`su -c` 反而可能失败），`NeedsElevation`/`ShellCmd`/`ShellRoot` 相应调整。

- **方向 B：应用内一键 `adb root`**（**已定案：出本期，2026-10-15 第二轮**）
  在设备操作入口提供按钮/菜单，经 `RunAdbAsync` 执行 `adb -s <serial> root`（通道已具备，见 §3.4），成功后失效该 serial 的 `_rootCache` 并立即重测。
  出本期理由：命令窗口已有预置 `root`/`unroot` 命令，出本期成本损失小；且其依赖 C 的失效逻辑，user 构建真机执行会报 `adbd cannot run as root`，UI 需如实呈现错误——留待后续与 C 的失效入口一并设计更顺。

- **方向 C（配套，独立于 A/B 也有价值）：缓存失效时机**（**已采纳，2026-10-15 第一轮**；重测范围修订已定案，逃生门交互形态已定案，见 §6.1 第 6 条）
  `DeviceMonitor.DeviceConnected` 或手动刷新设备列表时失效对应 serial 的缓存重测；否则任何外部 root 状态变化（控制台 `adb root`/`unroot`、命令窗口预置命令）应用内都无感知。
  修订版重测范围（2026-10-15 第二轮定案）：Magisk 已授权（true）不重测不弹框；adbd 型（裸 `id` 探得 uid=0）false 重测成本仅一条裸 `id`、无授权框，可自动重测；**su 阶段失败的 false 不自动重测**（否则 Magisk 拒绝授权/超时的设备每次重连都重弹授权框，正是 §3.2 缓存的动机），只走手动刷新强制重测逃生门（交互形态已定案，见 §6.1.6）。
  残留边角：设备从 true 变 false（unroot、授权被撤）感知不到，可接受。

> **2026-10-15 评审结论**：第一轮采纳 A（能力识别）+ C（状态跟随）+ `RootMode` 枚举；第二轮定案 Q3（Adbd 跳过 su 包裹）、Q4（方向 B 出本期）、Q5 修订版重测范围；第三轮定案 Q6（弹窗确认 + 重试项）；第四轮定案 Q7（方案 a1）。**待决问题已全部关闭**，编码前输入项见 §6.4。

## 6. 决策记录与编码前输入

### 6.1 已定案

1. **采纳「A + C」组合**（2026-10-15 第一轮）。
2. **引入 `RootMode` 枚举（None / Su / Adbd）**（2026-10-15 第一轮）。理由：Adbd 模式下必须跳过 su 包裹，仅扩 `IsRoot`（bool）无法表达模式差异，会把检测不准的坑转移给消费端——Adbd 设备若被当普通 root 设备照旧包 `su -c`，不同 ROM 可能反而失败。影响 **6** 个消费点（第二轮补充 `CommandDialog`）：`DeviceInfo.ToString`、`frmMain.SelectedIsRoot` 及 4 个对话框、`CommandDialog`（经 `DeviceOpsDialog` 透传）、`DeviceFilePane`。
3. **Adbd root 模式下直接跳过 `su -c` 包裹，不留兜底**（2026-10-15 第二轮）。理由：检测已确认 shell 本身 uid=0，兜底实际是为「检测不准」兜底；无 su 二进制的设备上 `su -c` 必然失败，而真机 Magisk 场景走 Su 模式不受影响。
4. **方向 B（一键 `adb root`）出本期**（2026-10-15 第二轮）。理由见 §5 方向 B。
5. **Q5 修订版重测范围**（2026-10-15 第二轮）：true（Magisk 已授权）不自动重测；adbd 型 false 自动重测（裸 `id`，无授权框）；**su 阶段失败的 false 不自动重测**，只走手动强制重测逃生门（其交互形态见第 6 条）。修订原因：原方案「false 设备重测」会把 Magisk 拒绝授权/授权超时的设备也纳入自动重测，每次重连重弹授权框，与 §3.2 缓存动机冲突。残留边角：true→false 感知不到，可接受。
6. **Q6 定案：弹窗确认（方案 c）+ 设备操作入口重试项**（2026-10-15 第三轮）。手动刷新强制重测时弹确认框（「将重新检测 root，设备可能弹出授权提示」）；另在 `DeviceOpsDialog` 打开时提供弹窗，让用户选择重试 root 检测（触发条件是否仅限「该设备上次 su 检测失败」属实现细节，编码时定）。不新增全局配置项。
7. **Q3 复核确认：Adbd 模式跳过 su 包裹，su 包裹严格按 `RootMode` 匹配**（2026-10-15 第三轮用户确认第二轮结论）。
8. **Q7 定案：方案 a1——`DetectRoot` 内改用 `ExecuteRemoteCommandAsync` + `CancellationTokenSource`，超时判失败**（2026-10-15 第四轮用户确认）。理由：超时是必须的（通道无超时机制已查证，见 §6.2；方案 b 前提不成立）；a1 是唯一真正释放等待且无残留连接的方案——a2 的被弃 shell 连接会挂后台直至 EOF，a3 不解决挂起本身；异步重载自带 `CancellationToken` 参数已在 v3.6.16 源码核实。超时值（建议 5~10s，覆盖 Magisk 默认授权等待）编码时定。

### 6.2 Q7 查证记录（2026-10-15 第四轮定案关闭）

**Q7：su 授权挂起防护的落地形态**（2026-10-15 第三轮查证完成，第四轮定案，见 §6.1 第 8 条）

- **查证结论（方案 b 的前提不成立）**：AdvancedSharpAdbClient v3.6.16 源码（tag `v3.6.16`）核实：
  - `AdbSocket.GetShellStream()` 直接返回 `new ShellStream(Socket.GetStream())` —— 裸 TCP 流，未设置任何超时；
  - 全仓库搜索 `ReceiveTimeout` / `ReadTimeout` 均为 0 处——**通道没有任何超时机制**；
  - `ExecuteRemoteCommand` 的读循环以 `reader.ReadLine()` 返回 null（TCP EOF）为终止条件（源码注释自述），同步重载无 CancellationToken；
  - 因此设备端 `su` 弹授权框等待用户操作期间，调用会**无限阻塞**，直到连接断开或用户在设备上做出选择。
- **本项目的阻塞影响面**（已核实调用路径）：
  - `frmMain.AdbRefresh` 走 `ListDevicesAsync`（`Task.Run` 包裹）→ UI 不冻结，但任务永不完成，状态栏停留在「刷新中…」、设备列表永远出不来；
  - `OnDeviceEvent`（`DeviceMonitor.DeviceConnected/Disconnected/Changed`）→ `ListDevices()` **在监控事件线程上同步执行** → 首次遇到待授权设备时整个监控事件循环被卡死，后续所有设备增删事件不再处理——这是最重的后果。
- **方案**：
  a1. `DetectRoot` 内部改用 `ExecuteRemoteCommandAsync` + `CancellationTokenSource`（超时判 None）——异步接口自带取消支持，改动最小；
  a2. 调用侧包 `Task.Run` + `Wait(timeout)`，超时判 None——不动库用法，但被放弃的 shell 连接仍挂在后台直至 EOF；
  a3. 不加超时，仅把 `OnDeviceEvent` 里的检测挪出监控线程 + 缓存「检测中」状态——防事件循环卡死，但挂起的检测任务本身无解。
- **定案**：a1（2026-10-15 第四轮用户确认，见 §6.1 第 8 条）。
- **状态**：已定案关闭。本节保留为查证记录。

### 6.3 已知限制（本期不动，2026-10-15 第二轮记录）

- 命令窗口 / 命令对话框的 root 复选框（`CommandDialog._ckRoot`、`CommandEntry` root 标记）：Adbd 模式下用户手动勾选 su -c 包裹会失败（无 su 二进制）。本期不做自动禁用/改写，交由用户自行判断；待 `RootMode` 落地后视实际反馈再议是否在 Adbd 模式下给出提示或禁用。

### 6.4 编码前输入项（2026-10-15 第四轮核查补录，默认按倾向实施、评审时可改）

> **实施结果（2026-10-15）**：两条均按默认倾向落地——①探测顺序=先裸 `id` 短路（`DetectRootAsync`）；②缓存存 `RootDetectEntry(Mode, SuFailed)` record（`Models/DeviceInfo.cs`）。超时值定 8 秒（`RootProbeTimeoutSec`，§6.1 第 8 条建议 5~10s 区间取中）。附带决策（属实现细节自定范围）：Q6 重试项触发条件定为「Mode==None 且 SuFailed==true」（明确无 su 的设备重测大概率仍失败，不打扰）；None 型无论 SuFailed 与否一律不自动重测（`ShouldRedetect`，SuFailed 标志仅供重试入口触发——Magisk「拒绝授权」不挂起、su 立即返回非 uid=0 输出，若按 SuFailed 才重测会让这类设备反复弹框）。

1. **`DetectRoot` 探测顺序与短路**：先跑不带 su 的裸 `id`，探到 `uid=0` 即短路判 Adbd 模式，未探到再跑 `su -c id`。理由：adbd 设备仅需 1 条命令，且不对无 su 设备发出注定失败的 `su`（部分 ROM 上失败 su 有额外报错延迟）；Magisk 设备只多一条无害裸 `id`。备选（先 `su` 后裸 `id`）会让 adbd 设备多一次失败 su 调用与潜在延迟，不如默认方案。
2. **`_rootCache` 缓存值形态**：存结构（`RootMode` + 「上次 su 阶段失败」标志）而非仅 `RootMode`。理由：Q5 重测范围与 Q6 重试项触发条件（「该设备上次 su 检测失败」）都依赖该子状态，单存 `RootMode` 表达不了。属 §6.1 第 2 条「枚举已定案」的落地细节，不与之冲突。
