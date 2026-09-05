# logcat viewer

Windows 桌面端 Android 日志（logcat）查看器。基于 WinForms，使用内存映射文件 + 列式索引，可直接打开并流畅浏览千万行级别的日志文件，同时支持通过 ADB 实时采集设备日志、截图、录屏、文件互传与分类收藏常用命令。

## 目录

- [logcat viewer](#logcat-viewer)
  - [目录](#目录)
  - [特性](#特性)
    - [日志查看](#日志查看)
    - [过滤](#过滤)
    - [标记与导出](#标记与导出)
    - [ADB 设备功能](#adb-设备功能)
    - [命令窗口](#命令窗口)
  - [环境要求](#环境要求)
  - [构建与运行](#构建与运行)
  - [快捷键](#快捷键)
  - [过滤语法](#过滤语法)
  - [目录结构](#目录结构)
  - [数据存储](#数据存储)
  - [第三方依赖](#第三方依赖)

## 特性

### 日志查看

- **大文件秒开**：内存映射 + 并行分块建索引，索引期间显示进度条，可随时取消
- **虚拟列表**：`ListView` 虚拟模式，只渲染可见行，滚动流畅
- **多格式解析**：自动识别 `threadtime` / `time` / `long` / `brief` / `tag` / `ymd` 六种格式（字节级快速路径 + 正则回退）
- **续行合并**：异常堆栈、多行正文可并入上一条记录（可开关）
- **级别配色**：V/D/I/W/E/F/A 分色显示，默认隐藏 V、D
- **完整记录查看**：双击行（或 `Enter`）打开详情窗口查看未被截断的原文

### 过滤

- **级别**：V/D/I/W/E/F/A 自由勾选，一键全选/清空
- **Tag / Message**：支持 `or` / `and` 组合、正则、大小写敏感、排除模式
- **PID / TID**：多值空格分隔，可排除
- **分钟**：按时间戳的分钟值过滤，如 `05 20`
- **仅标记行**：只看被标记的记录
- **自动应用**：输入后延迟 400 ms 自动刷新，也可用 `Ctrl+Enter` 手动应用

### 标记与导出

- 按 `M` 或双击行号列标记/取消标记，`F2` / `Shift+F2` 在标记间跳转
- 右键菜单可直接「按此 tag / PID / TID / 分钟过滤」
- 导出当前结果或仅导出标记行，可选「单行化」（换行转 `\n`，便于表格处理）
- 复制原始文本或复制为表格行（Tab 分隔）

### ADB 设备功能

工具栏提供以下功能：

| 功能          | 说明                                                                                                                             |
| ------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| 刷新设备      | 枚举在线设备，设备插拔自动监听                                                                                                   |
| 开始/停止采集 | 实时抓取 `logcat` 到临时文件并增量同步到列表，自动跟随尾部                                                                       |
| 保存日志      | 把本次采集内容另存为 `.log`                                                                                                      |
| 截图 / 录屏   | 截图预览并保存；录屏可拉取到本地                                                                                                 |
| 文件浏览      | 双栏文件管理器：设备端 ↔ 本机端互传、刷新、目录收藏；`/data/data` 等受限目录需 root 才能双向传输（未 root 可 run-as 浏览与上传） |
| 命令          | 打开命令窗口，见下节                                                                                                             |

### 命令窗口

把敲过的命令攒起来、分类收藏，随时在选中设备上重跑（`Ctrl+Shift+C`）。

- **两条执行通道**
  - `设备 shell`：走 `adb shell`，勾选 root 后自动包成 `su -c '...'`（单引号按 POSIX 规则转义）
  - `本机 adb`：直接调用 `adb.exe`，用于 `install` / `reboot` / `push` 这类子命令，`devices`、`version` 等主机命令之外的会自动补 `-s <序列号>`，并回报退出码
- **分类管理**：内置 5 类共 112 条常用命令（`shell`、`dumpsys`、`应用与包`、`日志与异常`、`adb`），可新建 / 重命名 / 删除自己的分类，删分类会连带清掉其中的收藏
- **收藏与历史**：收藏上限 300 条（按「命令正文 + 通道」去重），执行记录自动进「最近使用」（上限 120 条，带使用次数）
- **占位符参数**：命令里的 `{名称}` 都是参数，执行前提示填值并记住上次的输入（`{pkg}` 默认取当前 run-as 包名）；内置命令用到 `{pkg}` `{pid}` `{apk}` `{ip}` `{activity}` `{url}` `{tag}` `{file}`，自己收藏时可用任意名字
- **流式输出**：逐行回显并显示行数与耗时，`logcat` 这类不结束的命令可中途「停止」或按超时（默认 30 秒，0 为不限）中断；输出可复制 / 另存，超过 40 万字符丢弃最早的行
- **不误触**：双击或 `Enter` 列表项只把命令填入输入框，需要再点「执行」才会真的跑

## 环境要求

- Windows
- .NET 10 SDK（`net10.0-windows`，WinForms）
- 使用 ADB 功能需要 `adb` 可用（程序会自动尝试启动 ADB server）

## 构建与运行

```powershell
dotnet build                # 构建
dotnet run                  # 运行
dotnet publish -c Release   # 发布
```

也可直接用 Visual Studio 打开 `logcat.slnx` / `logcat.csproj`。

## 快捷键

| 快捷键            | 功能                                      |
| ----------------- | ----------------------------------------- |
| `Ctrl+O`          | 打开日志文件                              |
| `F5`              | 重载（增量，文件无变化则跳过）            |
| `Ctrl+E`          | 导出当前结果                              |
| `Ctrl+Enter`      | 应用过滤                                  |
| `Ctrl+F`          | 聚焦 Message 输入框                       |
| `Ctrl+C`          | 复制选中行                                |
| `Ctrl+Shift+C`    | 打开命令窗口                              |
| `F2` / `Shift+F2` | 上一个 / 下一个标记                       |
| `F3` / `Shift+F3` | 在结果中查找下一个 / 上一个（非正则模式） |
| `M`               | 标记当前行                                |
| `Enter`           | 查看当前行完整记录                        |
| `Esc`             | 停止当前任务并清空选择                    |

也支持把日志文件直接拖拽到窗口中打开。

## 过滤语法

- 多个关键词用空格分隔：`crash anr`
- 短语用双引号包裹：`"null pointer"`
- `or` / `and` 下拉框决定多词之间的关系（Tag 默认 `or`，Message 默认 `and`）
- 勾选「正则」后按 .NET 正则语法匹配
- 勾选「排除」表示剔除命中项

## 目录结构

```
Program.cs              入口
frmMain.cs              主窗口：布局、过滤面板、ADB 工具栏、快捷键
Controls/
  LogListView.cs        虚拟模式日志列表（列定义、级别配色、标记、视图快照）
  FilePane.cs           文件面板基类
  DeviceFilePane.cs     设备端文件面板
  LocalFilePane.cs      本机端文件面板
Forms/
  FileBrowserDialog.cs  设备 ↔ 本机双栏文件管理器
  CommandDialog.cs      命令窗口（分类列表、收藏、历史、执行与输出）
  CommandEditDialog.cs  收藏条目的新建 / 编辑
  ScreenCaptureDialog.cs 截图 / 录屏
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
  LogDocument.cs        列式索引文档（mmap、并行建索引、增量 Reload）
  LogParser.cs          行解析（六种格式 + 续行判定）
  FilterEngine.cs       过滤引擎（预筛 → message 匹配 → 导出）
  AdbManager.cs         ADB 封装（设备枚举、Shell、流式执行、本机 adb、截图、推拉文件）
  LogcatStream.cs       实时 logcat 采集流
  FavoritesStore.cs     目录收藏持久化
  CommandStore.cs       命令分类 / 收藏 / 历史持久化 + 内置命令库
```

## 数据存储

- 窗口大小/位置、续行合并、自动应用、字号等设置保存在用户配置（`Properties.Settings`）
- 目录收藏以 JSON 持久化到 `%LOCALAPPDATA%\logcat\favorites.json`
- 命令窗口的分类、收藏、历史与占位符取值以 JSON 持久化到 `%LOCALAPPDATA%\logcat\commands.json`（首次打开时写入内置命令库）
- 实时采集的日志写入系统临时目录 `logcat_live_*.log`，关闭窗口时释放

## 第三方依赖

- [AdvancedSharpAdbClient](https://github.com/SharpAdb/AdvancedSharpAdbClient) — ADB 通信
- Microsoft.Extensions.Logging（Console / Debug）
