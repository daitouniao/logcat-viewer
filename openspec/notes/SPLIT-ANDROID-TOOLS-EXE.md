# 探索结论：拆分为 logcat.exe + 安卓工具 exe

- 状态：探索结论；2026-10-25 复核补充 4 项新决策（D8/D10/D11/D12-D13），其中配置共享方式（D9）已拍板
- 日期：2026-10-21（初版）/ 2026-10-25（复核修订）
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

## 复核修正（2026-10-25，对照代码与 OpenSpec 现状）

1. **原文「`openspec/specs/` 现仅有 fileproxy capability」已过期**：现有 5 个 capability —— `adb-capture`、`adb-server`、`device-file-transfer`、`fileproxy`、`time-column`。原文 §下一步 中据此作出的判断作废。
2. **新增在途 change `device-scoped-file-browser-path`**：proposal / specs delta / design / tasks 均已就绪，0/14 任务完成，worktree 分支 `wt/device-scoped-file-browser-path` 已按 §2.3 记入其 tasks.md。它要改的文件正是拆分要搬走的 5 个：`Services/AppSettings.cs`、`Forms/FileBrowserDialog.cs`、`Forms/DeviceOpsDialog.cs`、`Controls/FilePane.cs`、`Controls/DeviceFilePane.cs`。→ 新增决策 **D8**。
3. **同目录共享 `settings.json` 不是零成本**，实测存在三处字段级冲突（窗口几何抢同一槽、退出时全量覆盖对方改动、启动日志共写同一文件）。→ 新增决策 **D9**（已拍板）。
4. **新增第二个 exe 会撞上两条既有规则**：发版版本号的多处同步（AGENTS §2.8）、行为契约的 capability 归属。→ 新增决策 **D10 / D11**。
5. 原文第 6 项（与 `fix-adb-server-self-heal` 的先后）维持「已解决」，但同类问题被 D8 重新引燃——顺序问题不能只看已归档的 change，还要看在途的。

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

## 待拍板（2026-10-25 重排，按依赖顺序）

### 阻塞级（不定就没法写 tasks）

**D8 与在途 change `device-scoped-file-browser-path` 的先后**

- 为什么必须定：那个 change 要改的 5 个文件，就是拆分要 `git mv` 的 5 个文件。顺序不定，拆分的 tasks 写不准。
- 选项：
  - **甲（推荐）先实施完该 change（验收归档）再拆分**：拆分直接搬"改完后的版本"，基线稳定，该 change 的文档一行不用改。代价：拆分排在它后面。
  - **乙 先拆分，再实施该 change**：代价是它的 design/tasks 里所有文件路径（`Services/AppSettings.cs`、`Forms/FileBrowserDialog.cs` 等）和 `file-browser` spec 的归属位置都要改写，实施前得先 update 一次 change。
  - **丙 两个 worktree 并行**：两边改同一批文件，合并必冲突。

**#1 共享代码怎么组织（方案 A / B 的白话版）**

- **A 共享类库（推荐）**：新建 `logcat.Core` 项目，把两边都要用的代码（`AdbManager`、`Loc`、三个存储、设备六个对话框、文件面板等）从现有目录搬进这个项目；logcat 和 andtools 各自引用它。就像把共用零件放进一个工具箱，两个程序都从工具箱取——改一次两边同时生效，编译器保证两边用的是同一份类型。代价：现有 `Services/`、`Forms/`、`Controls/`、`Models/` 要按归属拆到三个项目目录（一次性 `git mv`，历史保留）。
- **B 链接源码**：文件留在原地，两个 exe 项目在 csproj 里各自把同一批 `.cs` 用 `<Compile Include="../...">` 编进自己程序。好处是目录不动；代价是同一份源码被编两次，构建配置容易踩坑（同一类型在两边各有一份、`InternalsVisibleTo` 要分别配），长期是暗账。

**#2 数据文件怎么共享**

- **甲（推荐）两个 exe 发布到同一目录**：`AppContext.BaseDirectory` 相同 → 直接共享同一批配置文件。代价：两个 exe 必须一起分发，不能只给其中一个。
- **乙**：配置固定放在 logcat.exe 所在目录，andtools 启动时向上查找。支持分目录部署，但多一层查找与失败兜底逻辑。

**D9 共享配置的字段冲突（已拍板 2026-10-25）**

结论：**按归属拆文件 + 语言独立小文件**。

- `settings.json` 归 logcat：窗口几何、显示选项、fileproxy 配置等
- `andtools.json` 归 andtools：APK 选项、上次路径、文件浏览列宽等
- `language.json` 只存语言，两个 exe 共读共写
- 旧 `settings.json` 里的键在首次启动时迁移到对应新文件

这样一次解决三件事：窗口几何不再抢同一个槽；退出时的全量 `Save()` 不会覆盖对方刚改的值；语言仍然"跟随 logcat"（andtools 读同一个 language.json，切换后重启生效，见 #7）。具体类怎么拆（`AppSettings` 拆成几个类、迁移代码放哪）在 propose 的 design 里定。

**#3 单实例策略**

- **甲（推荐）** andtools 用自己的互斥量名（如 `Local\logcat-andtools-single-instance`），两个 exe 可同时运行。同时运行的写冲突已由 D9 的拆文件方案化解。
- **乙** 不设单实例守卫，双击就多开。多开时同一份 `andtools.json` 会被多个进程互相覆盖（回到 D9 想避免的问题）。

**#5 logcat 是否保留「设备操作」入口**

- **甲（推荐）保留为启动器**：点击后若 andtools 已在运行则唤到前台，否则拉起它。隐藏成本：选了 #3 甲的独立互斥量后，还需要一个"找到已运行实例"的手段（命名事件 / 按窗口标题查找）。
- **乙 彻底移除**按钮与入口：干净断裂，老用户需重新找入口；#3 的唤醒问题一并消失。

**#4 发布形态**

- **甲（推荐）一个 zip，两个 exe 并列**：自包含两次 publish 到同一目录，runtime 同名文件互相覆盖，总量约 120 MB + 数 MB；框架依赖形态约 4 MB。
- **乙 两个独立 zip**：用户容易只拿其中一个，导致 andtools 找不到共享配置（与 #2 甲冲突）。

### 非阻塞（可后补或给默认值）

**#7 跨进程语言实时跟随**：一边切语言另一边不刷新（`Loc` 是进程内静态）。甲（推荐）接受"重启生效"；乙用命名事件做跨进程通知，成本更高。

**D10 版本号与发版口径**：甲（推荐）两个 exe 统一版本号——只改 `logcat.csproj` 的 `<Version>`（或抽 `Directory.Build.props` 共用），AGENTS §2.8 的"5 处同步"不用扩成 8 处；乙各自版本号，发布时两处改、两份 README 各写一个版本。无论选哪个，两份 README 与 BUILDING 都要同批补"两个 exe"的说明。

**D11 是否为新行为建 spec capability**：甲（推荐）新建一个 capability（暂名 `android-toolbox`），用 GIVEN/WHEN/THEN 写清"独立 exe、配置共享与语言跟随、单实例语义"，以后改这些行为有契约可依；乙不建，只写 proposal，省事但这类行为以后没有 spec 约束。

**D12 命名与标识（可默认）**：exe 文件名（暂定 `logcat-andtools.exe`）、窗口标题、图标是否沿用 `app.ico`。

**D13 测试工程结构（可默认）**：甲（推荐）仍一个 `tests/logcat.Tests`，同时引用 Core 与两个 exe 项目；乙拆成 Core 测试 + 各 exe 测试。

**#6 与 `fix-adb-server-self-heal` 的先后**：已解决（2026-10-21 验收归档），无需再等；但 D8 是同类问题的新实例。

## 下一步

拍板 D8、#1、#2 后走 `/opsx:propose` 生成 OpenSpec change（proposal + specs delta + design + tasks）。D9 已定，design 直接按"按归属拆文件 + 语言独立小文件"写。specs 现状以 §复核修正 第 1 条为准，原文"仅有 fileproxy"的判断不再适用。编码前不在探索阶段动手。
