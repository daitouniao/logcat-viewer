# Tasks

> apply worktree 标识：分支 `wt/split-android-tools-exe`（2026-11-01 创建，自 ec675c8）。

> 基线口径：`docs/TESTING.md` 为唯一权威源（当前全量 791 全过）；每批任务完成后跑 `dotnet test` 并记录口径数字，禁止声称未验证的通过。

## 1. 准备与搬迁

- [x] 1.1 创建 worktree 并记录标识：分支 `wt/split-android-tools-exe`（路径以 `git worktree list` 定位，不写入任何入库文件）；后续任务均在 worktree 实施
- [x] 1.2 依赖扫描复核：按 design D2 清单逐文件核对归属（重点 `FileEntry`、Forms 引用面），修正后把最终清单回填 design.md
- [x] 1.3 新建 `Directory.Build.props`（`<Version>` 单一来源），`logcat.csproj` 移除自有 `<Version>`
- [x] 1.4 新建 `logcat.UiCore.csproj`（`net10.0-windows`、`InternalsVisibleTo("logcat.Tests")`、不写死 `SelfContained`），`git mv` D2 清单文件进 `logcat.UiCore/`，`namespace logcat` 不变
- [x] 1.5 `AppSettings` 拆分：`LogcatSettings`（exe）/ `AndToolsSettings` + `LanguageSettings`（UiCore）/ 只读 legacy DTO；落盘改 tmp+rename 原子写原语（三个 store 共用）。测试：`SettingsAtomicWriteTests`（半写文件无版本号、重走迁移）、`LegacySettingsDtoTests`（11+1+10 键完整读出）
- [x] 1.6 迁移原语与两侧落盘分支（schema 版本判定、显式条件：andtools v2 权威不回填、安卓键复制不移动、Language 抽出幂等、全新安装不迁移）。测试：`ConfigMigrationTests`——①旧布局 logcat 先跑拆三份；②工具箱先跑且 logcat 从未启动语言不丢；③新布局重复启动幂等；④Language 抽出后旧键清除
- [x] 1.7 `Loc.SetLang`/`DetectLang` 改经 `LanguageSettings` 读写 `language.json`。测试：`LanguageStoreTests`（用 `LangScope` 双语各一遍，CI 语言环境规则）
- [x] 1.8 `SingleInstance` 构造加可选 `mutexName`（默认现名），andtools 唤醒事件常量。测试：既有 `SingleInstanceTests` 5 用例零改动全过 + 新增 `SingleInstanceNameTests`（注入名生效）
- [x] 1.9 logcat.exe 侧编译修复（`AppSettings.Default` 引用点改新类、搬迁路径调整），不改任何行为；`dotnet test` 全量与基线一致（记录数字）
- [x] 1.10 `InternalsVisibleTo` exe 侧去留：扫描 `frmMain`/`RecordDialog` 等被测 internal 成员后定，结论回填 design.md Open Questions

## 2. andtools exe

- [x] 2.1 新建 `logcat.AndTools.csproj` + `Program`：独立互斥量（D6 名）、唤醒事件监听、`StartupLog` 传 `startup-andtools.log`、默认字体与 `ApplicationConfiguration.Initialize` 同 logcat 口径
- [x] 2.2 `DeviceSelector` 逻辑类（UiCore）：持 `AdbManager`、刷新、当前 serial、`DevicesChanged`、自愈接入；logcat 侧 `frmMain` 改用同一逻辑类，下拉 UI 不动。测试：`DeviceSelectorTests`（刷新/事件/serial 切换，AdbManager 以可注入假件）
- [x] 2.3 andtools 主窗体：`DeviceOpsDialog` 容器形态升格（design D7），窗口几何三键存 `andtools.json`（N11）。测试：`AndToolsWindowGeometryTests`（保存/恢复/最大化）
- [x] 2.4 logcat「设备操作」启动器：`File.Exists` 探测 → 缺失禁用按钮 + 悬停/点击提示（2 条译文走 `Loc`），存在则 `Process.Start`。测试：探测判定函数单测（存在/缺失两分支）
- [x] 2.5 二次启动唤醒路径：新实例 `Set` 事件后退出，首实例 `TrySetForegroundWindow` 失败退 `FlashWindow`。可单测部分（事件触发回调）单测，UI 前置行为列入手工验收清单

## 3. 测试工程与 CI

- [x] 3.1 `logcat.Tests.csproj` 增加对 UiCore 与两 exe 的 `ProjectReference`；`LocTableTests` 排除规则与 3 处 `InlineData("Forms/...")` 改新路径（`:535`/`:1041`/`:166-168` 附近，以现文件为准）
- [x] 3.2 specs 场景→用例映射补齐：迁移四场景（1.6）、双开互不覆盖、语言跟随重启生效——能单测的进 `ConfigMigrationTests`/`LanguageStoreTests`，需真双进程的列入手工验收清单
- [x] 3.3 全量 `dotnet test` 数字入档 `docs/TESTING.md`（基线 + 新增用例），CI 无需改动的结论复核一遍（仍单条 test 命令）

## 4. 发布与文档

- [x] 4.1 publish 方式探测：`dotnet publish -o` 同目录一次成型 vs 两目录分别 publish 再合并，实测二选一，结论回填 design.md D12（行为等价，只定流程）
- [x] 4.2 `docs/BUILDING.md`：两套 zip 的发布步骤 + 冒烟清单（两 exe 均能启动、改语言互见、三配置文件齐）
- [x] 4.3 双 README：功能描述改「双 exe 并列」、语言持久化位置、`settings.json` 拆分布局说明（数字与徽章按 §3.3 联动规则随 3.3 同批）；`docs/FEATURES.md` 不动
- [x] 4.4 自包含 publish 冒烟：NETSDK1151 不复现、合并目录两 exe 可启动（对应 4.1 定稿流程跑一遍）

## 手工验收清单（验收阶段，真机）

> 验收结论：2026-11-03 用户以 V0.1.7-pre1-fdd 测试构建（tag `V0.1.7-pre1-fdd`，commit 6765c88）实测，反馈「测试成功」，本清单通过。

- 双 exe 并存启动/退出/重启，配置三文件迁移与幂等（含 0.1.7 旧配置真机升迁移）
- logcat 点「设备操作」拉起/前置 andtools；缺失 exe 时按钮禁用与提示
- andtools 二次启动唤醒与任务栏闪烁兜底
- 语言切换跨 exe 重启生效
- andtools 窗口几何（含最大化）保存与恢复
- andtools 各页签工具逐项走查（文件浏览、APK 安装/卸载、截图录屏、命令），与拆分前行为一致
- andtools 在 2K 缩放屏下设备/文件控件不裁切，`Log/startup-andtools.log` 的 `[DPI]` 段正常
- SMB 大文件回归：两 exe 共用 adb server 时设备操作互不干扰

## 5. 收尾（归档前，V0.1.7-pre1-fdd 出包实测暴露）

- [ ] 5.1 `logcat.UiCore.csproj` 补 Release 条件化 `<DebugType>none</DebugType>` + `<DebugSymbols>false</DebugSymbols>`（与两 exe csproj 同款写法，Debug 保留 portable 供 coverlet 插桩），修复发布目录混入 `logcat.UiCore.pdb`
- [ ] 5.2 修正 4.1 回填的 publish 结论（design.md D12 与 docs/BUILDING.md）：相对 `-p:PublishDir=` 按**工程目录**解析而非 cwd，子目录工程（logcat.AndTools）第二条 publish 不会并入根 `publish/`——改为绝对路径方案或写明拷贝合并步骤，验证清单补 pdb 检查项
- [ ] 5.3 按 5.2 修正后的流程重出一版 fdd 包冒烟：UiCore pdb 不再出现、BUILDING.md 步骤照抄可跑通
