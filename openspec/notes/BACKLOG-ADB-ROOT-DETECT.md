# 待重启卡 A1：ADB root 检测与初始化（RootMode 三态）

- 状态：已实施，**真机手工验收开放**（重启卡：未结项条目一卡一条 `openspec/notes/BACKLOG-*.md`，结项后移入 `openspec/notes/archive/`）
- 日期：2026-10-21 登记；2026-10-29 从登记簿拆为独立卡片
- 关联提交：`2589f47`（feat: root 检测升级 RootMode 三态（Su/Adbd/None），探测带超时防授权框挂起）
- 验收结论：待真机验收
- 来源：原设计文档已归档（`docs/archive/design/2026-10-21-ADB-ROOT-DETECTION.md`，快照类不再维护）；本卡自包含需求要点，不依赖该快照

## 基础需求

**现象（用户报告）**：root 设备上应用的文件浏览、设备操作等 shell 操作默认没有 root 权限；用户在控制台手动执行 `adb root` 之后，应用的 shell 才有 root 权限。

**根因口径**：两种 root 机制互不蕴含——adbd root（`adb root` 重启设备端 adbd 为 uid=0，仅 userdebug/eng 构建；生产 user 构建报 `adbd cannot run as root`）与 su 提权（Magisk 等真机，与 adbd 是否 root 无关）。旧实现把「root」等价于「su 可用」：`DetectRoot` 只跑 `su -c id`，对 adbd root 型设备（模拟器/工程机，无 su 或 shell 域无权执行 su）恒判 false → 无 `[root]` 标记、`DeviceFilePane.NeedsElevation` 恒 false、命令不加提权包裹，受限目录全部失败；功能「碰巧」依赖了未被识别的 adbd root 状态。

**需求（已定案）**：

1. **三态检测**：`RootMode` = `Su` / `Adbd` / `None`；探测顺序先裸 `id`（命中 `uid=0` 即短路判 Adbd），再 `su -c id` 判 Su。
2. **授权框挂起防护**：su 探测走带 8 秒超时的异步探测（`ExecuteRemoteCommandAsync` + CTS）——实测 AdvancedSharpAdbClient 通道无任何超时机制，裸调用会被 root 授权框挂死。
3. **缓存语义**：`_rootCache` 存 `RootDetectEntry(Mode, SuFailed)`，按 serial 记；重测范围由 `ShouldRedetect` 决定（不再「进程内只测一次」，避免外部 `adb root` 后缓存永不失效）。
4. **Adbd 型跳过 su 包裹**：`BuildRootShellCmd` 统一语义，`AdbManager.ShellRoot` 与 `DeviceFilePane.ShellCmd` 共用。
5. **两处重试入口**：手动刷新时的确认弹窗（仅存在 Su/None 缓存时弹）；`DeviceOpsDialog.MaybeOfferRootRetry`（`OnShown` 时，仅 None + SuFailed 触发）。
6. **事件链路**：`OnDeviceEvent` 改 `await ListDevicesAsync()`，解除事件循环卡死。

## 现状

- 已编码（提交 `2589f47`）：`Models/DeviceInfo.cs`（`RootMode`、`RootDetectEntry`）、`Services/AdbManager.cs`（`DetectRootAsync`、`TryProbeAsync`、`_rootCache`、`ShouldRedetect`、`BuildRootShellCmd`）、`frmMain.AdbRefreshManualAsync`、`DeviceOpsDialog.MaybeOfferRootRetry`。
- 新增单测 20 条（`ModelTests`），实施时点 tstrun 全量 519/519 过。

## 开放项

1. 真机验证 Adbd / Su / None 三态判定与真机实际一致。
2. su 探测不再让 root 授权框反复重弹。
3. 两处重试入口（手动刷新确认弹窗、设备操作窗 OnShown 重试）行为符合预期。

## 重启条件

维护者有真机（已 root / Magisk / 普通 user 构建各一）时补验收；验收失败暴露新缺陷则按 bug 流程处理（分析写入 change 的 proposal / design）。
