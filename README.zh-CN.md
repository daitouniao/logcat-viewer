# logcat viewer

[![Version: V0.1.3](https://img.shields.io/badge/version-V0.1.3-green)](logcat.csproj)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows-lightgrey)](#环境要求)
[![CI](https://github.com/daitouniao/logcat-viewer/actions/workflows/ci.yml/badge.svg)](https://github.com/daitouniao/logcat-viewer/actions/workflows/ci.yml)

Windows 桌面端 Android 日志（logcat）查看器。基于 WinForms，使用内存映射文件 + 列式索引，可直接打开并流畅浏览千万行级别的日志文件，同时支持通过 ADB 实时采集设备日志、截图、录屏、文件互传、APK 安装/卸载与分类收藏常用命令。

[English](README.md) | **简体中文**

项目地址：

- GitCode（主仓库）：<https://gitcode.com/gcw_WDXl5paK/logcat-viewer>
- GitHub（镜像）：<https://github.com/daitouniao/logcat-viewer>

> 当前版本 **V0.1.3**。版本号以 [logcat.csproj](logcat.csproj) 的 `<Version>` 为唯一来源，发布时只改该处，程序标题、关于对话框与 exe 文件属性会自动同步。
>
> 界面**中英双语**，`帮助 → 语言` 运行时即时切换，选择记在 `settings.json`；首次启动跟随系统区域。

![logcat viewer — 过滤演示](docs/images/main.gif)

## 日志查看

大多数 logcat 查看器在处理大日志文件时会卡顿。本工具将文本保留在磁盘上（mmap），仅将固定宽度的列数据加载到内存中，因此即使数 GB 的日志文件也能秒开，滚动帧率稳定。

| 能力 | 实现 |
| --- | --- |
| **大文件秒开** | 内存映射 + 并行分块建索引，可随时取消 |
| **虚拟列表** | 只渲染可见行，10M+ 行时滚动依然流畅 |
| **六种格式自动识别** | `threadtime` / `time` / `long` / `brief` / `tag` / `ymd`，字节级快速路径 + 正则回退 |
| **续行合并** | 异常堆栈、多行正文可并入上一条记录（可开关） |
| **级别配色** | V/D/I/W/E/F/A 分色显示 |
| **完整记录查看** | 双击行或 `Enter` 查看未被截断的原文 |
| **增量重载** | `F5` 只重读追加的尾部，批次边界做行对齐，多行日志不会被切断 |

## 过滤

- **级别**：V/D/I/W/E/F/A 自由勾选（默认全选），一键全选/清空
- **Tag / Message**：支持 `or` / `and` 组合、大小写敏感、排除模式
- **PID / TID**：多值空格分隔，可排除
- **分钟**：按时间戳的分钟值过滤，如 `05 20`
- **仅标记行**：只看被标记的记录
- **自动应用**：输入后延迟 400 ms 自动刷新，也可用 `Ctrl+Enter` 手动应用
- **独立过滤窗口**：过滤面板放在非模态、始终悬浮于主窗口之上的窗口里，主区域整片留给日志列表；点关闭只是收起（过滤条件不丢），可从 `Ctrl+F` 或菜单重新打开
- **过滤条件收藏**：Tag 与 Message 各自一份收藏，每份上限 60 条并持久化

## 标记与导出

- 按 `M` 或双击行号列标记/取消标记，`F2` / `Shift+F2` 在标记间跳转
- 右键菜单可直接「按此 tag / PID / TID / 分钟过滤」
- 导出当前结果或仅导出标记行，可选「单行化」（换行转 `\n`，便于表格处理）
- 复制原始文本或复制为表格行（Tab 分隔）

## ADB 设备功能

工具栏提供实时采集、设备操作、保存日志：

| 功能          | 说明                                                                                                                                                                                                                 |
| ------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 刷新设备      | 枚举在线设备，设备插拔自动监听                                                                                                                                                                                       |
| 开始/停止采集 | 实时抓取 `logcat` 到临时文件并增量同步到列表，自动跟随尾部；采集与过滤/导出互斥（串行访问），纯尾部追加只对新增行做增量过滤，开销从 O(全量) 降到 O(新增行) |
| 保存日志      | 把本次采集内容另存为 `.log`                                                                                                                                                                                          |
| 设备操作      | 截图 / 录屏 / 文件浏览 / 安装卸载 APK / 命令窗口，合并为一个「设备操作」窗口的页签          |

## 设备操作窗口

截图、录屏、文件浏览、APK 安装/卸载、命令窗口统一为一个非模态、单例窗口，用页签切换。页签按需创建并保持状态；切换设备时绑定设备的页签自动重建；关窗时清理（停止录屏、取消命令任务、写入选项）。

### 截图 / 录屏 / 文件浏览

**截图 / 录屏**：截图预览并保存，录屏可拉取到本地。

**文件浏览**：双栏设备 ↔ 本机文件管理器：
- 互传、刷新、目录收藏、重命名
- `/data/data` 等受限目录需 root（工具栏 run-as 按钮即模式下拉菜单）
- 互传容忍文件被占用
- 面板间支持拖拽互传（拖入文件夹保留相对结构）
- 上传成功后执行 `sync` 落盘，防重启/拔线丢数据

### 安装 / 卸载 APK

- **安装两条通道**
  - `adb install`：可勾选 `-r`（保留数据，默认）、`-d`（允许降级）、`-g`（授予全部权限）、`-t`（允许测试包）
  - `pm install`：`adb install` 被禁用时的备选，推 APK 到设备临时目录再安装，完成后自动清理
- **卸载两条通道**：`adb uninstall` 或 `pm uninstall`，可勾 `-k` 保留数据；`pm` 通道可选「以 root 执行」
- **已装应用列表**：开窗自动拉取三方应用，双击或 `Enter` 直接卸载
- **收藏**：APK 本机路径与应用包名可收藏，输入框旁的下拉框直接复用
- **选项记忆**：通道、安装参数、临时目录、root 与保留数据等选项在关窗时写入配置

### 命令窗口

把敲过的命令攒起来、分类收藏，随时在选中设备上重跑（`Ctrl+Shift+C`）。

- **两条执行通道**
  - `设备 shell`：走 `adb shell`，勾选 root 后自动包成 `su -c '...'`
  - `本机 adb`：直接调用 `adb.exe`，用于 `install` / `reboot` / `push` 等子命令，自动补 `-s <序列号>`
- **分类管理**：内置 5 类共 112 条常用命令，可新建 / 重命名 / 删除自己的分类
- **收藏与历史**：收藏上限 300 条，执行记录自动进「最近使用」（上限 120 条，带使用次数）
- **占位符参数**：命令里任意 `{名称}`（字母/数字/`_`/`-`）都是执行前提示填值的参数，并记住上次输入。内置命令库用到 `{pkg}`、`{pid}`、`{file}`、`{path}`、`{activity}`、`{url}`、`{tag}`、`{ip}`、`{apk}`、`{name}`；其中 `{pkg}` 还会拿 run-as 收藏当默认值
- **流式输出**：逐行回显并显示行数与耗时，可中途停止或设超时（默认 30 秒）
- **不误触**：双击或 `Enter` 列表项只把命令填入输入框，需再点「执行」

## 用户体验

### 窗口位置记忆（多屏）

关窗时记录主窗口的位置 / 大小 / 状态，下次启动自动回到上次所在的屏幕（已验证可用；屏幕上下或左右排列均可）。

- 若上次所在的屏幕本次未连接，坐标校验不过，自动回退默认布局（不会跑到屏外）
- 验证方式：`Log\startup.log`（exe 同级目录）记录每次启动的屏幕拓扑、读取的坐标、校验结果、应用后实际落点与所在屏，以及关闭时写入的坐标

### 高 DPI 适配

`Forms/` 下 12 个手写窗体声明 `AutoScaleMode.Dpi`，随系统 DPI 缩放——2K 等高缩放屏上不再出现按钮文字被截断。界面字体随语言切换：中文用「微软雅黑 UI」，修复默认 Segoe UI 无中文字形导致的高 DPI 中文裁字；英文用原生 Segoe UI。

多屏不同缩放（如主屏 150%、副屏 100%）下的兜底：
- 过滤设置窗口高度在显示后按内容实测撑开
- 工具栏行高改用 AutoSize，始终跟随控件实测尺寸
- 收藏下拉框宽度在面板变窄时自动收缩
- DpiFix 动态撑大「装不下内容的 TableLayoutPanel 绝对行」与「固定高停靠按钮栏」

验证方式：`Log\startup.log`（exe 同级目录）的 `[DPI]` 段记录各窗口的 DPI、实测需要尺寸与兜底动作

### 中英双语界面

全部界面文案都有中英两版，**运行时即时切换**，无需重启。

- **切换入口**：`帮助 → 语言 → 简体中文 / English`，选择写入 exe 同级目录的 `settings.json`
- **默认语言**：首次启动跟随系统区域（`CultureInfo.CurrentUICulture`）；设置缺失或损坏时也回退到它
- **切换时不只换文字**：整个 UI 是代码构建的（`frmMain.Designer.cs` 只有 37 行），所以切换会遍历所有已打开窗体，换上目标语言的字体（中文用「微软雅黑 UI」——Segoe UI 无中文字形，高 DPI 下会被裁字；英文用原生 Segoe UI），再按实测文本宽度重算定宽按钮与绝对定位对话框的尺寸——英文普遍比同义中文长 30~50%
- **规模**：9 张分表共 556 条译文，另有一批按语言区分的列宽与对话框尺寸常量

双语布局审计会在两种语言下按「最小 / 默认 / 半宽」三档遍历控件树，断言**裁字数为 0**。

## 快速开始

### 环境要求

**运行已发布版本：Windows 即可。** 每个版本发两个包，按目标机选一个：

| 包 | 目标机要求 | 体积 |
|---|---|---|
| `logcat-V<版本>-win-x64.zip` | **只需 Windows**，解压双击即用 | 约 120 MB |
| `logcat-V<版本>-win-x64-fd.zip` | 须先装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | 约 1.8 MB |

不确定选哪个就下自包含包（第一种）。

- Windows x64（10 及以上）
- 使用 ADB 功能需要 `adb` 在 `PATH` 中（实时采集、截图、文件互传、APK 安装等；
  程序会自动尝试启动 ADB server）。**不涉及设备的功能（打开 / 过滤日志文件）
  不需要 adb。**
- 从源码构建才需要 .NET 10 SDK（`net10.0-windows`，WinForms）

### 构建与运行

```powershell
dotnet build                # 构建
dotnet run                  # 运行
dotnet publish -c Release   # 发布（框架依赖，约 1.8 MB；自包含见 docs/BUILDING.md）
```

也可直接用 Visual Studio 打开 `logcat.slnx` / `logcat.csproj`。

> 二进制发行版会随每个版本一同发布，可在以下 Release 页面下载现成构建：
> - GitCode：<https://gitcode.com/gcw_WDXl5paK/logcat-viewer/releases>
> - GitHub：<https://github.com/daitouniao/logcat-viewer/releases>
>
> 如需自行定制（裁剪依赖、改默认路径、加签名等），按上方命令从源码构建。

## 单元测试

日志解析、列式索引（含增量追加与行边界对齐）、过滤引擎、各类持久化存储与本地化译文表都有 xUnit 测试覆盖；UI 层与设备/系统集成层不在单测口径内。

```powershell
# 跑全量测试
dotnet test tests/logcat.Tests/logcat.Tests.csproj

# 跑测试并采集覆盖率（coverlet → cobertura），再生成可读报告
dotnet test tests/logcat.Tests/logcat.Tests.csproj --collect:"XPlat Code Coverage" --settings tests/logcat.Tests/coverlet.runsettings
python tests/coverage-report.py     # 输出 tests/coverage-report.html
```

当前状态：**412 个用例全部通过**，行覆盖率 **99.38%**（2,226 / 2,240 行），分支覆盖率 **87.56%**（1,028 / 1,174 分支）。

覆盖口径见 `tests/logcat.Tests/coverlet.runsettings`，排除两类代码：
- **UI 层**：`Forms` / `Controls` / `frmMain` / `Program`，以及 `DpiFix` / `DpiDiag` / `Loc`。`Loc`（本地化运行时）与 `DpiFix` 同理——它一半的代码是控件树遍历、字体替换与文本像素测量，没有真实窗体与消息泵无从验证
- **系统/设备集成层**：`AdbManager`、`LogcatStream`、`ClipboardHelper`、`StartupLog`

> 与 `Loc` 同目录的 `LocTable.*`（纯数据译文表）**不排除**：它是本轮 i18n 的核心验证面，由 `LocTableTests` 的 18 条断言盯住（含扫源码查漏翻）。
> 判断标准是「这条代码能否在无窗体条件下被有意义地验证」，不是「这个文件新不新」。

> 在受限沙箱（部分 IDE 托管终端）里测试宿主可能被拒绝写入 `%TEMP%`，导致大量用例失败。
> 把临时目录指到工作区内：`TMP=<工作区内目录> TEMP=<同> dotnet test ...`。

## 快捷键

| 快捷键            | 功能                                      |
| ----------------- | ----------------------------------------- |
| `Ctrl+O`          | 打开日志文件                              |
| `F5`              | 重载（增量，文件无变化则跳过）            |
| `Ctrl+E`          | 导出当前结果                              |
| `Ctrl+Enter`      | 应用过滤                                  |
| `Ctrl+F`          | 打开过滤设置窗口并聚焦 Message 输入框     |
| `Ctrl+C`          | 复制选中行                                |
| `Ctrl+Shift+C`    | 打开命令窗口                              |
| `F2` / `Shift+F2` | 上一个 / 下一个标记                       |
| `F3` / `Shift+F3` | 在结果中查找下一个 / 上一个               |
| `M`               | 标记当前行                                |
| `Enter`           | 查看当前行完整记录                        |
| `Esc`             | 停止当前任务并清空选择                    |
| `Ctrl+Q`          | 退出应用                                  |

也支持把日志文件直接拖拽到窗口中打开。

## 过滤语法

- 多个关键词用空格分隔：`crash anr`
- 短语用双引号包裹：`"null pointer"`
- `or` / `and` 下拉框决定多词之间的关系（Tag 与 Message 默认均为 `or`）
- 输入框颜色即语义：**背景色**表示匹配关系（`or` 浅蓝 / `and` 浅橙），**文字色**在勾选「大小写」时变暗红；空框不着色
- 工具栏与过滤窗口的输入框**双向镜像**，任一侧输入都会同步到另一侧
- 勾选「排除」表示剔除命中项

## 目录结构

```
Program.cs              入口
frmMain.cs              主窗口：布局、双行工具栏、过滤面板构建与收藏、ADB 功能入口、快捷键
app.ico                 程序图标（csproj 的 ApplicationIcon）
Controls/
  LogListView.cs        虚拟模式日志列表（列定义、级别配色、标记、视图快照）
  FilePane.cs           文件面板基类（导航、列表渲染、收藏、重命名、拖放）
  DeviceFilePane.cs     设备端文件面板
  LocalFilePane.cs      本机端文件面板
Forms/
  DeviceOpsDialog.cs    设备操作总窗口（截图/录屏/文件浏览/APK 安装·卸载/命令合并为页签）
  FileBrowserDialog.cs  设备 ↔ 本机双栏文件管理器（设备操作窗口的文件浏览页）
  ApkInstallDialog.cs   APK 安装（adb install / pm install 双通道、路径收藏）
  ApkUninstallDialog.cs APK 卸载（已装应用列表、包名收藏）
  FilterDialog.cs       过滤设置的非模态悬浮窗口（承载主窗体的过滤面板）
  CommandDialog.cs      命令窗口（分类列表、收藏、历史、执行与输出，设备操作窗口的命令页）
  CommandEditDialog.cs  收藏条目的新建 / 编辑
  ScreenShotDialog.cs   截图页（设备操作窗口）
  ScreenRecordDialog.cs 录屏页（设备操作窗口）
  RecordDialog.cs       单条记录详情
  RunAsDialog.cs        run-as 包名选择
  SimpleInputBox.cs     简易输入对话框
Models/
  FilterSpec.cs         过滤条件
  DeviceInfo.cs         设备信息
  FileEntry.cs          文件条目
  FavoriteDir.cs        收藏目录
  CommandEntry.cs       收藏命令 / 执行记录 / 通道枚举
Services/
  Loc.cs                本地化运行时（资源键、控件绑定、字体切换、按语言实测宽度）
  LocTable.*.cs         按区域拆分的译文表（Common / MainUi / FilterUi / FileUi / ApkUi / DeviceUi / CommandUi / CommandWindow / RuntimeMsg）
  DpiDiag.cs            高 DPI 布局诊断打点（写入 startup.log [DPI] 段）
  DpiFix.cs             高 DPI 布局兜底（显示后按内容实测撑大装不下的 TLP 绝对行与固定高停靠栏）
  StartupLog.cs         启动诊断日志（exe 同级目录 Log\startup.log，多屏几何与 DPI 排查用）
  LogDocument.cs        列式索引文档（mmap、并行建索引、增量 Reload）
  LogParser.cs          行解析（六种格式 + 续行判定）
  FilterEngine.cs       过滤引擎（预筛 → message 匹配 → 导出）
  AdbManager.cs         ADB 封装（设备枚举、Shell、流式执行、本机 adb、截图、推拉文件）
  LogcatStream.cs       实时 logcat 采集流
  ClipboardHelper.cs    带重试的剪贴板写入（剪贴板被其他进程短暂占用时不抛 ExternalException）
  FavoritesStore.cs     收藏持久化（目录、run-as 包名、APK 路径、应用包名、tag / message 过滤条件）
  CommandStore.cs       命令分类 / 收藏 / 历史持久化 + 内置命令库
  AppSettings.cs        用户级设置（窗口几何、显示选项、上次路径、界面语言、安装/卸载窗口选项，JSON 持久化到 exe 同级目录 settings.json）
  AppInfo.cs            产品名与版本号（读取程序集 InformationalVersion，标题与关于对话框共用）
tests/
  coverage-report.py      覆盖率报告生成（coverlet 的 cobertura XML → 可读 HTML）
  logcat.Tests/           xUnit 测试工程（412 个用例：日志解析 / 列式索引 / 过滤引擎 / 持久化 / 本地化）
    coverlet.runsettings  覆盖率统计口径（Include / Exclude 规则）
docs/
  BUILDING.md             怎么编译与发布（两个包各自的命令与产物验证）
  TESTING.md              怎么跑测试、怎么出覆盖率（唯一操作入口）
  UT-AUDIT.md             变异测试审计——测试到底咬不咬得住
  FEATURES.md             计划实现的功能（模板，内容待补）
  design/
    CRASH-SUMMARY-DESIGN.md     崩溃一键定位与摘要展示的产品设计说明（定稿）
    FILTER-REFACTOR-PLAN.md     工具栏过滤与过滤面板解耦的改造计划（已执行完毕，留档）
    TOOLBAR-FAVORITES-DESIGN.md 工具栏输入框星号收藏 + 收藏下拉的设计定稿（已落地，留档）
  images/main.gif         README 顶部的演示动图
LICENSE                 Apache-2.0 全文
THIRD-PARTY-NOTICES.md  第三方库与工具链的许可声明
DISCLAIMER.md           免责声明全文
```

## 文档

| 文档 | 什么时候看它 |
|---|---|
| [docs/BUILDING.md](docs/BUILDING.md) | 要编译或发版——两个包各自的 `dotnet publish` 命令、产物结构验证、踩坑速查 |
| [docs/TESTING.md](docs/TESTING.md) | 要跑测试或出覆盖率——含「智能体沙箱里 `dotnet test` 起不来」的绕法 |
| [docs/UT-AUDIT.md](docs/UT-AUDIT.md) | 想知道测试到底咬不咬得住——变异测试审计结论 |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | 关心依赖与许可——逐个包的版本、许可、版权与分发检查项 |
| [DISCLAIMER.md](DISCLAIMER.md) | 使用前必读的完整免责条款 |

## 数据存储

- 用户设置统一以 JSON 持久化到 exe 同级目录 `settings.json`：窗口位置/大小/状态（多屏记忆已验证可用）、续行合并、自动应用、字号、换行可视符、界面语言、上次浏览路径，以及安装/卸载窗口的通道、安装参数、临时目录、root 与「保留数据」选项
- 各类收藏以 JSON 持久化到 exe 同级目录 `favorites.json`：设备端/本机端目录收藏（含别名）、run-as 包名、APK 本机路径、应用包名、tag 与 message 过滤条件，每类上限 60 条（最近使用在前）
- 命令窗口的分类、收藏、历史与占位符取值以 JSON 持久化到 exe 同级目录 `commands.json`（首次打开时写入内置命令库）
- 实时采集的日志写入系统临时目录 `logcat_live_*.log`，关闭窗口时释放
- 多屏窗口几何在每次启动时校验，若上次所在屏幕未连接则自动回退默认布局

## 许可

本项目采用 [Apache License 2.0](LICENSE)（全文见 `LICENSE`）。选择它的依据是「跟随所使用的三方库与编译工具的许可」：唯一的非微软依赖就是 Apache-2.0 的 ADB 客户端库，其余皆为 MIT，因此 Apache-2.0 可以无冲突地覆盖整棵依赖树，同时额外提供专利授权。可按 Apache-2.0 条款自由使用、修改、分发（含商用）。

> 源码由 AI 辅助生成，不指定自然人作者，版权以 `logcat viewer contributors` 集体名义声明。若需换成自己的署名，改两处即可：`LICENSE` 附录的 `Copyright` 行、`logcat.csproj` 的 `<Copyright>`。

### 许可相容性

| 使用的第三方                                                   | 许可                                                             | 与本项目的关系                                                                                  |
| -------------------------------------------------------------- | ---------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| AdvancedSharpAdbClient 3.6.16                                  | Apache-2.0                                                       | 唯一的非微软 NuGet 库，DLL 随产物分发 → 本项目同样用 Apache-2.0 最省事                          |
| Microsoft.Extensions.* 10.0.11（Logging 及其依赖，共 13 个包） | MIT                                                              | MIT 代码可无限制并入 Apache-2.0 项目，只需保留其版权声明                                        |
| .NET SDK 10.0.400 / C# 编译器 / WinForms                       | 源码 MIT，安装的二进制受《Microsoft 软件许可条款 — .NET 库》约束 | 构建工具链与运行框架，条款允许免费构建并分发应用                                                |
| adb.exe（Android platform-tools）                              | Apache-2.0                                                       | 只调用用户环境中的外部程序，不打包、不修改；若你一并分发 platform-tools 需保留其 LICENSE/NOTICE |

### 明细与合规

- 完整清单（包名、版本、许可标识、版权行、传递依赖、分发检查项）见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
- `dotnet build` / `dotnet publish` 会把 `README.md`（英文）、`README.zh-CN.md`（中文）、`LICENSE`、`THIRD-PARTY-NOTICES.md` 与 `DISCLAIMER.md` 复制到输出目录，保证二进制发布包自带说明与声明。
- 程序集版权信息已写入 exe 的「属性 → 详细信息」。

### 贡献即授权

遵循 Apache-2.0 第 5 条：向本仓库提交的贡献默认按 Apache-2.0 授权，无需另附声明。修改源文件时请按第 4(b) 条在改动处注明已修改。

## 免责声明

完整条款见 [DISCLAIMER.md](DISCLAIMER.md)。使用本软件即表示您已阅读、理解并同意其中的全部条款；**若不同意任何条款，请立即停止使用并删除本软件。**

要点摘要：

- **设备操作风险自担**：截图、录屏、文件推拉、APK 安装/卸载、`adb shell` / root 命令等操作直接作用于你的设备，请自行确认命令含义；因误操作导致的数据丢失、系统异常或设备损坏由使用者本人承担。
- **root 与受限目录**：访问 `/data/data` 等受限目录需设备已 root 或可 run-as，此类操作可能破坏应用数据或影响保修，请谨慎评估。
- **合法合规使用**：仅限用于自己拥有授权的设备与数据，不得用于未授权访问、采集他人隐私或任何违反当地法律法规的用途。
- **日志含敏感信息**：logcat 日志常包含账号、token、位置等隐私数据，导出与分享前请自行脱敏。
- **非官方工具**：本项目与 Google、Android 及 ADB 官方团队无关联。
