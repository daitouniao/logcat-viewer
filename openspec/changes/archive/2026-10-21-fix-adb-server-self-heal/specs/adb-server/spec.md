# Spec Delta

## ADDED Requirements

### Requirement: 刷新设备时 adb server 自愈

用户手动刷新设备列表（含启动后的首次刷新）SHALL 先确保 adb server 在运行：server 未在运行时 SHALL 自动尝试拉起，拉起成功后 SHALL 继续正常刷新（枚举设备、按既有口径展示）；拉起失败（典型为 adb 不在 PATH，应用无法代为拉起）SHALL 清空设备列表并在状态栏展示指向手动处理的提示（确认 adb 已加入 PATH，或手动执行 adb start-server），SHALL NOT 显示「无设备」误导用户。server 已在运行时，刷新行为 SHALL 与既有口径一致。

#### Scenario: server 未运行且 adb 可用时自愈

- **WHEN** adb server 未在运行（如被外部工具 kill-server），adb 可执行文件可用，用户点「刷新设备」
- **THEN** 应用自动拉起 adb server 并完成设备枚举，下拉框与状态栏按既有口径展示，不出现「无设备」

#### Scenario: adb 不可用时提示手动处理

- **WHEN** adb server 未在运行且 adb 可执行文件不可用（不在 PATH），用户点「刷新设备」
- **THEN** 状态栏展示手动处理提示（确认 adb 已加入 PATH 或手动执行 adb start-server），设备下拉框被清空，不显示「无设备」

#### Scenario: server 正常时刷新行为不变

- **WHEN** adb server 已在运行，用户点「刷新设备」
- **THEN** 刷新行为与既有口径一致（枚举设备、root 检测缓存判定、无设备提示），无额外动作
