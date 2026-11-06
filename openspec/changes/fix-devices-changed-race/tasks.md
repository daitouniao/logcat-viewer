# Tasks

## 1. 工作区

- [x] 1.1 本 change 与 `sidecar-cache-index` 共用 worktree：分支 `wt/sidecar-cache-index`（路径以 `git worktree list` 定位，此处只记标识）。共用理由：sidecar 的真实文件冒烟需要同一份可运行构建，而设备列表崩溃会直接打断冒烟。**提交按 change 拆开**，`fix(...)` 与 `feat(...)` 提交互不夹带。

## 2. 修复

- [x] 2.1 `DeviceSelector`：单锁（`_gate`）保护 `_devices` / `_selectedIndex`；`ApplyDevices` 在锁内产出快照、**锁外** `DevicesChanged?.Invoke(snapshot)`；`Clear` / `Devices` / `CurrentSerial` / `CurrentRootMode` 一并进锁。测试：`DeviceSelectorTests` 增 `通知参数是快照_后续变更不影响已交付内容`、`后台刷新与清空并发_枚举通知参数不抛`。验证：`dotnet test --filter DeviceSelectorTests` 全过（7/7）。

## 3. 集成验证

- [x] 3.1 全量回归：`dotnet test` 全过（848/848，改动前基线 846 + 本 change 2 条）；`docs/TESTING.md` 用例数与两份 README 的徽章/数字同步（§3.3/§3.4 联动，随 docs 提交落地）。验证：命令输出与口径声明。

## 4. 收口

- [ ] 4.1 wt 合入目标分支：本 change 的提交随 `wt/sidecar-cache-index` 一并合入 `master`（归档前置条件）。验证：`master` 可见该次合入。
