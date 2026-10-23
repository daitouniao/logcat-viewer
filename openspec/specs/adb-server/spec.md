# adb-server Specification

## Purpose
管理 adb server 的生命周期可用性：用户刷新设备时确保 server 在运行（未运行则自动拉起）；server 不可用且应用无法代为拉起时明确提示手动处理，不把「adb 不可用」误报为「无设备」。

## Requirements

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

### Requirement: 自愈提示的本地化与原因保真

自愈失败提示的**引导语** SHALL 随界面语言变化（中文界面显示中文引导、英文界面显示英文引导），
SHALL NOT 在英文界面显示中文引导语。提示尾部的**失败原因** SHALL 保持上游原文不翻译 ——
该原因来自 adb 自身的异常消息或退出码，翻译它等于伪造设备端信息，排查时无法与上游输出对照。

#### Scenario: 英文界面下引导语已译

- **WHEN** 界面语言为英文，adb server 自愈失败
- **THEN** 状态栏提示的引导语为英文，且不含中文引导文案

#### Scenario: 中文界面下引导语为中文

- **WHEN** 界面语言为中文，adb server 自愈失败
- **THEN** 状态栏提示的引导语为中文

#### Scenario: 失败原因保持上游原文

- **WHEN** adb server 自愈失败，原因是 adb 抛出的异常消息
- **THEN** 该原因在提示中逐字保留，不因界面语言而改变

#### Scenario: 原因含特殊字符时提示仍可生成

- **WHEN** adb server 自愈失败，失败原因中含花括号等格式控制字符
- **THEN** 提示正常生成，不因格式化而抛异常或丢失原因内容
