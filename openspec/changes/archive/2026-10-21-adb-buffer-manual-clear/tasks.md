# Tasks

## 1. 采集去除自动清空

- [x] 1.1 `Services/LogcatStream.cs`：移除 `Start(bool clearFirst)` 参数与 `RunAsync` 内 `logcat -c` 分支及相关日志字段，启动日志不再记录 clearFirst
- [x] 1.2 `frmMain.AdbStartCapture`：调用改为 `_streamThread.Start()`
- [x] 1.3 全仓库检索确认无残留 `clearFirst` / `logcat -c` 采集路径引用（`CommandStore` 中的快捷命令「logcat -c」为用户命令库条目，保留不动）

## 2. 清除按钮与状态机

- [x] 2.1 `frmMain` 工具栏构建：在「■ 停止采集」后新增 `_btnAdbClear`（`Loc.Bind`，键「清空日志」），`Tag = "adb"`，初始禁用，加入 ADB 分组
- [x] 2.2 `UpdateAdbCaptureUI`：`_btnAdbClear.Enabled = hasDevice && !_adbCapturing`
- [x] 2.3 点击处理 `AdbClearLogAsync()`：`SelectedSerial()` 为空时状态栏提示；否则 `await ShellAsync(serial, "logcat -c", 5)`，成功 `ShowStatus`、异常 `ShowError`；执行期间按钮防重入（禁用到完成，`finally` 走 `UpdateAdbCaptureUI` 恢复）

## 3. i18n

- [x] 3.1 i18n 键落 `LocTable.MainUi.cs`（实施修正：design 决策 7 原写 DeviceUi.cs，但「▶ 开始采集」等 ADB 工具栏键既有归属在 MainUi.cs，随既有分组落位）：「清空日志」「日志缓冲区已清空」「清空日志失败：{0}」「请先连接设备」四键中英文
- [x] 3.2 `LocTableTests` 扫描通过（新键零豁免；首轮失败系 tstrun 未重建用了旧 dll 副本，重建后通过）

## 4. 测试

- [x] 4.1 补跨午夜历史内容的文档解析/采集用例（`LogDocumentTests.Reload_追加批次跨午夜时时间戳正确推进`：Append 批 23:59:59 → 次日 00:00:01，Ts 差 +2s）
- [x] 4.2 现有测试全量回归：tstrun 口径 **638 过 / 0 失败 / 0 跳过**（637 基线 + 1 新增）
- [ ] 4.3 手工验收项（记入 tracker）：① 连接设备点「清空日志」，设备端 `logcat -d` 确认为空、状态栏成功提示；② 拔掉设备按钮变灰；③ 开始采集后按钮变灰、停止后恢复；④ 采集前已有崩溃的设备，采集内容包含该崩溃

## 5. 文档与收尾

- [x] 5.1 `docs/FEATURES.md`：实施修正——该文件是「计划实现的功能」清单（已完成条目移 CHANGELOG），无实时采集条目可更新，行为变更记录于本 change 与 release note（打 tag 时），不硬造条目
- [x] 5.2 `docs/IMPLEMENTATION-TRACKER.md`：头部基线 637→638、第三节新增第 7 项手工验收

## Workflow follow-up

- 验收通过后由维护者明确指示 `openspec archive adb-buffer-manual-clear`（delta 并入 `openspec/specs/adb-capture/`）
- 打 tag 前在 release note 标注 **BREAKING**：采集不再自动清空设备日志缓冲区
- 未提交（无当轮授权）：改动在工作区，提交需维护者授权
