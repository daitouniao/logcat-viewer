# 探索结论：拆分为 logcat.exe + 安卓工具 exe

- 状态：探索结论，待用户拍板共享代码组织方式（方案 A/B）后转 OpenSpec change
- 日期：2026-10-21
- 关联提交：无（探索阶段，未改动代码）
- 验收结论：待定

## 结论摘要

用户诉求：安卓工具不再寄生 logcat 主窗口，独立 exe 直接可用；语言配置跟随 logcat；设备刷新 / 设备选择功能复用而非重写。

- 技术上低风险、高可行：安卓工具的依赖面已收敛清楚（`AdbManager` + `Loc`/译文分区 + 三个存储 + 少量 UI 辅助），`DeviceOpsDialog` 本身就是经 `(manager, serialProvider, rootProvider)` 注入的独立容器，可整体搬迁。
- 两个地基决策：**共享代码的组织方式**（共享类库 vs 链接源码）与**数据文件的共享方式**（同目录发布 vs 路径指向）。其余决策（单实例、发布形态、入口去留）都排在它们之后。
- 流程判定（AGENTS.md §一.3 清单逐条）：加 csproj/项目、跨 ≥3 文件、改 UI 交互 —— 触发「较大改动」，**必须先出 OpenSpec change 再编码**，不在探索阶段动手。

## 现状：寄生关系与依赖面

```
logcat.exe（单 exe，net10.0-windows WinForms）
│
├─ Program.cs ──► frmMain（日志列表 / 过滤 / ADB 采集 / 设备下拉）
│                    │  工具栏「设备操作」按钮（安卓工具唯一入口，默认落命令页）
│                    ▼
│              DeviceOpsDialog（非模态，TabControl 页签式容器）
│                ├─ 截图 ScreenShotDialog           ├─ 安装 APK ApkInstallDialog
│                ├─ 录屏 ScreenRecordDialog          ├─ 卸载 APK ApkUninstallDialog
│                ├─ 文件浏览 FileBrowserDialog      └─ 命令窗口 CommandDialog
│                │   (+DeviceFilePane/FilePane/LocalFilePane)  (+CommandEditDialog +RunAsDialog)
│                └─ 页签按需创建；换设备时重建绑定设备的页签（命令页走回调不重建）
│
└─ 设备下拉 _comboDevice + 刷新按钮 + AdbManager.DevicesChanged → OnDevicesChanged
   （即用户要求复用的「设备刷新 + 选择」，目前长在 frmMain 里）
```

关键事实：

- 安卓工具的依赖（当前全部同在 `logcat.csproj` 内）：
  - Services：`AdbManager`（枚举 / root 检测 / 监控 / shell / 截图 / 收发文件）、`Loc` + `LocTable.*`、`AppSettings`、`FavoritesStore`、`CommandStore`、`ClipboardHelper`、`DpiFix`、`DpiDiag`、`AppInfo`、`StartupLog`
  - Forms：六个设备对话框 + `RunAsDialog` + `CommandEditDialog` + `SimpleInputBox`
  - Controls：`DeviceFilePane`、`FilePane`、`LocalFilePane`
  - Models：`DeviceInfo`、`CommandEntry`、`FavoriteDir`、`FileEntry`
- logcat 侧**同样**依赖 `AdbManager`（设备下拉 + 采集）与 `Loc`/三个存储 —— 「复用」不是单方向赠与，两边都要用；设备刷新逻辑（含自愈）必须只有一份。
- logcat 侧保留：`Program.cs`（单实例 / spool 清理 / 退出杀代理进程树）、`frmMain`、日志引擎（`LogParser`/`LogDocument`/`FilterEngine`/`LogcatStream`/`Crash*`/`FileProxy*`）、`Controls/LogListView.cs`、`Forms` 里 `CrashSummaryForm`/`FilterDialog`/`FileProxyDialog`/`RecordDialog`。

## 关键发现（影响设计）

1. **数据文件全部落在 exe 同目录** —— 「语言跟随」的天然杠杆：

   | 存储 | 路径 | 内容 |
   |---|---|---|
   | `AppSettings` | `exe同目录/settings.json` | **Language**、APK 选项、上次路径、文件浏览列宽、fileproxy 配置 |
   | `FavoritesStore` | `exe同目录/favorites.json` | APK 路径收藏、目录收藏、run-as 包名 |
   | `CommandStore` | `exe同目录/commands.json` | 命令收藏 |
   | `StartupLog` | `exe同目录/Log/startup.log` | 启动诊断日志 |

   `Loc` 启动时从 `settings.json` 的 `Language` 推断语言。**两个 exe 发布到同一目录 → `AppContext.BaseDirectory` 相同 → 自动共享同一份 settings.json → 语言天然一致，零额外机制。**

2. **单实例互斥量会撞车**：`Local\logcat-viewer-single-instance`（`Services/SingleInstance.cs`）当前被 logcat 独占持有。安卓 exe 若复用同名互斥量，两个 exe 互相挤掉；需独立命名（如 `Local\logcat-andtools-single-instance`）或不设守卫。

3. **已归档 change 改过要拆的地基**：`fix-adb-server-self-heal`（adb server 自愈，2026-10-21 维护者验收通过并归档）已改 `AdbManager`（`EnsureServerRunningAsync`、internal 四 seam 构造注入、`RestartMonitor`）与 `frmMain` 的刷新入口（`ClearDeviceList`、自愈接入）。`AdbManager` 整体进 Core，其侧改动不受搬迁影响；自愈入口逻辑须落在抽出的 DevicePickerBar 里，两个 exe 才都能受益。

4. **译文表分区已按功能区划好**：`LocTable.DeviceUi/ApkUi/CommandUi/FileUi` 属安卓侧，`MainUi/FilterUi/CrashUi/FileProxyUi` 属 logcat 侧，`Common/RuntimeMsg` 共享 —— 搬迁基本是机械操作。少量混放：`DeviceUi` 里藏着 `RecordDialog` 的「记录 #{0}」（logcat 侧），搬表时需摘出或容忍（共享库被两边引用，容忍亦可）。

5. **测试工程直接引用主工程**：`tests/logcat.Tests` 经 `ProjectReference` 引 `logcat.csproj`，且靠编译期注入 `RepoRoot`（AssemblyMetadata）做源码扫描测试。拆项目后测试引用要调整为指向共享库（+ 按需分别引两个 exe 项目）。

## 目标架构草图

```
发布目录（一个 zip，两 exe 并列 → 同 BaseDirectory → 共享数据文件）
├─ logcat.exe           日志查看器（保留设备下拉 + 采集；「设备操作」入口去留待拍板 #5）
├─ logcat-andtools.exe  安卓工具箱（主窗体 = 设备选择条 + 页签式工具窗口）
├─ settings.json        共享（语言、APK 选项、收藏路径…）
├─ favorites.json / commands.json
└─ Log/startup.log

工程结构（方案 A：共享类库）：
┌─ logcat.Core（类库，双方复用）
│    AdbManager / Loc + LocTable / AppSettings / FavoritesStore / CommandStore
│    ClipboardHelper / DpiFix / DpiDiag / AppInfo / StartupLog
│    设备六件套 Forms + DeviceFilePane 等 Controls + DeviceInfo 等 Models
│    + 新抽出：DevicePickerBar（设备下拉 + 刷新 + DevicesChanged 订阅 + 自愈接入，
│                  从 frmMain 抽出，两个 exe 共用；采集专用的起停按钮 / 定时器留 frmMain）
├─ logcat（exe）Program + frmMain + 日志引擎 + logcat 侧 Forms/Controls
└─ logcat.AndTools（exe）Program（单实例新名）+ 主窗体（DevicePickerBar + DeviceOpsDialog）
```

## 方案对比：共享代码组织

| | **A. 共享类库 `logcat.Core`**（推荐） | B. 链接源码（`<Compile Include="../logcat/...">`） |
|---|---|---|
| 复用强度 | 类型级，编译期保证两边行为一致 | 源码重复编译，无类型级保证 |
| 目录代价 | `Services/Forms/Controls/Models` 按归属散到三个项目，`git mv` 一次性完成 | 目录结构不动 |
| 测试 | 测试工程引 Core 即覆盖共享逻辑 | 引用关系与 `InternalsVisibleTo` 都要跟着调 |
| 长期账 | 干净 | 构建配置怪异，暗账 |

推荐 A：设备刷新逻辑（含在途的自愈改动）只有一份，双端不会漂移。

## 待拍板（按依赖顺序）

1. **共享代码组织：A / B** —— 解锁工程结构、测试调整与 change 范围。推荐 A。
2. **数据共享方式**：同目录发布（推荐，语言零机制跟随，但要求两个 exe 必须一起分发）/ Core 存储路径解析指向 logcat.exe 所在目录（支持分目录部署，多一层查找逻辑）。
3. **单实例策略**：安卓 exe 用独立互斥量名（推荐，与 logcat 可同时运行）/ 不设守卫。
4. **发布形态**：一个 zip 内两 exe 并列（自包含两次 publish 到同一目录，runtime 同名文件互相覆盖，约 120 MB + 数 MB；框架依赖约 4 MB）/ 两个独立 zip。
5. **logcat 是否保留「设备操作」入口**：保留为启动器（点击后拉起 / 唤出安卓 exe，体验连续）/ 彻底移除（干净断裂，老用户需重新认知）。
6. **与 `fix-adb-server-self-heal` 的先后**：**已解决**——该 change 已于 2026-10-21 验收归档，拆分可直接进行，无需再等（`AdbManager` 整体进 Core，其侧改动不受搬迁影响；自愈入口逻辑须落在抽出的 DevicePickerBar 里，两个 exe 才都能受益）。
7. （次要）**跨进程语言实时跟随**：两个 exe 同时运行时，一边切语言另一边不刷新（`Loc` 是进程内静态）—— 接受「重启生效」/ 后续用命名事件做跨进程通知。

## 下一步

用户拍板 #1（顺带 #2）后，走 `/opsx:propose` 生成 OpenSpec change（proposal + specs delta + design + tasks）。`openspec/specs/` 现仅有 fileproxy capability，本次行为契约（设备选择条归属、数据共享、单实例语义）是否需要新 capability 的 spec delta，在 propose 阶段定。编码前不在探索阶段动手。
