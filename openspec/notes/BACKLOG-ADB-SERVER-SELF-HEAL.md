# 待重启卡 A2：adb server 自愈（刷新设备误报「无设备」）

- 状态：已实施并归档 change，**真机手工验收三项开放**（重启卡：未结项条目一卡一条 `openspec/notes/BACKLOG-*.md`，结项后移入 `openspec/notes/archive/`）
- 日期：2026-10-21 登记；2026-10-29 从登记簿拆为独立卡片
- 关联提交：随 change `fix-adb-server-self-heal` 实施（已归档：`openspec/changes/archive/2026-10-21-fix-adb-server-self-heal/`，需求并入主规范 `openspec/specs/adb-server/`）
- 验收结论：待真机验收
- 来源：原 bug 报告已归档（`docs/archive/bug/2026-10-21-ADB-SERVER-SELF-HEAL.md`，快照类不再维护）；本卡自包含需求要点

## 基础需求

**现象**：adb server 不在运行时（被其他工具 `adb kill-server`、异常退出、或 adb 根本不在 PATH 导致启动时从未拉起），手动「刷新设备」得到空列表、状态栏显示「无设备」。用户无法区分「没插设备」与「server 没跑」，也没有任何引导。本机实测过的另一形态：`%TEMP%\adb.log` 膨胀到 3.7 GB 且打开 Permission denied，导致**任何** adb 都无法启动 server——server 不是没开，是起不来。

**根因**：

1. 自愈缺位：server 拉起只在 `AdbManager` 构造时尝试一次（`StartServer("adb", false)`），失败仅写一行日志，之后整个生命周期不再重试；刷新入口不探测 server 状态。
2. 「不可用」与「无设备」不可区分：`ListDevicesAsync` 的 catch 把「连不上 server」和「连上了但没插设备」压成同一个空列表。
3. 监控僵尸态（实证）：server 运行中被外部 kill 后，已存活的监控任务既不抛异常也不再收事件——即使自愈把 server 拉起，不重建监控也永远收不到后续设备事件。

**需求（已落地）**：

1. `AdbManager.EnsureServerRunningAsync()`：`GetStatus()` 探测（异常按未运行）→ 未运行则 `RunAdbAsync("start-server")` 拉起（退出码 ≠ 0 视为失败）→ 拉起后复核；失败返回提示串「未能启动 adb server（请确认 adb 已加入 PATH，或手动执行 adb start-server）：{原因}」。该方法 SHALL NOT 触及监控（与单测零触网解耦）。
2. 结果对象 `AdbServerSelfHealResult(Prompt, Restarted)`：`Prompt` 非 null = 失败提示；`Restarted` = 本次确曾拉起，调用方据此**无条件重建**设备监控（不能以异常为触发器）。
3. 监控加固：`StartMonitor` 的 fire-and-forget 加 `Task.Run` 包装（OCE 静默、其余 LogWarning、不置空 `_monitor`）；`StopMonitor` 的 `Dispose` 加 try/catch（故障态 `Dispose` 会重抛异常，不防护则 `RestartMonitor` 把异常抛进刷新流程，async void 链路直接崩）。
4. `frmMain.AdbRefresh` 接入自愈：提示非 null → `ClearDeviceList()` + `ShowStatusRaw(提示串)`（走运行时数据消息，不用 `Loc.F`，避免提示里的花括号炸 `FormatException`）+ 刷新按钮保持可用（可重试）+ return。
5. 后续补强（change `2026-10-24-fix-locale-prompt-and-ci-guard`）：自愈失败提示的**引导语**走本地化，尾部失败原因保持上游原文不翻译。

## 现状

- 已实施并归档；主规范 `openspec/specs/adb-server/` 覆盖该行为。
- 自动化：`AdbManagerServerSelfHealTests` 9 条（注入四个 seam，零触网）；实施时点 tstrun 全量 637 过 / 0 失败。

## 开放项（手工验收）

1. 真机自愈：`adb kill-server` 后点「刷新设备」→ server 自动拉起、设备恢复枚举（需 5555 无线设备重连配合）。
2. adb 不在 PATH（或临时改名模拟）：点刷新 → 状态栏出现手动处理提示、设备下拉清空、再点刷新可重试（**不误报「无设备」**）。
3. 正常态零变化：server 正常时刷新行为与改动前一致，自愈路径零副作用。

## 重启条件

维护者有真机 + 可改 PATH 环境时补验收。
