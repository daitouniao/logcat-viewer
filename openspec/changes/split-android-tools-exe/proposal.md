# Proposal

## Why

安卓工具（设备操作、文件浏览、APK 安装/卸载、截图录屏、命令等）目前寄生在 logcat 主窗口的「设备操作」对话框里：不想看日志、只想用工具的用户必须先打开日志查看器；代码上安卓侧 UI 与日志引擎同住一个工程，边界靠约定维持。拆出独立 exe 后工具可直接启动，两边的职责边界固化为工程结构。

## What Changes

- 新增共享类库 `logcat.UiCore`（`net10.0-windows`），`git mv` 承载 AdbManager、Loc + LocTable、FavoritesStore、CommandStore、设备对话框、文件面板、设备侧 Models 及支撑件（StartupLog / DpiDiag / DpiFix / ClipboardHelper / SimpleInputBox / AppInfo）；日志引擎与 logcat 专属 UI 留在 logcat.exe。
- 新增 exe 工程 `logcat.AndTools`（`logcat-andtools.exe`）：主窗体 = 设备选择 + 页签式工具，由现有设备操作对话框的容器形态升格；独立单实例互斥量与唤醒事件。
- **BREAKING**（配置布局）：`settings.json` 拆为三份——`settings.json`（logcat 侧）、`andtools.json`（andtools 侧）、`language.json`（语言，两 exe 共读共写）；各文件带 schema 版本号，旧布局启动时自动迁移，幂等且不丢键。
- 语言读写改指 `language.json`：跨进程语言跟随为「重启生效」，不做实时通知。
- logcat 的「设备操作」按钮改为 andtools 启动器；目标 exe 缺失时禁用按钮并提示。
- 版本号抽 `Directory.Build.props` 单一来源，两 exe 统一。
- 测试工程改为引用共享库与两个 exe，同步修正路径规则；发布为一个 zip 两 exe 并列（自包含 + 框架依赖两套）。

## Capabilities

### New Capabilities

- `android-toolbox`: 安卓工具箱应用本体契约——独立进程与单实例唤醒语义、logcat 启动器与缺失探测、andtools 侧设备选择与 adb 自愈、andtools 窗口几何持久化；工具页签逐项功能本次不展开，以「与拆分前一致」总括。
- `settings-storage`: 两应用配置拆分与持久化契约——配置按归属拆三份各带 schema 版本、旧布局自动迁移（幂等不丢键）、并存写入互不覆盖。
- `ui-language`: 界面语言契约——语言存两 exe 共享配置、跨进程跟随重启生效（不做实时通知）、无配置时按系统区域兜底。

### Modified Capabilities

（无——现有 8 个 capability 的需求不受影响：文件搬迁是实现细节；logcat.exe 侧单实例、fileproxy、采集等行为不变。）

## Out of Scope

- `Loc` 的 UI 辅助半与纯译文半不拆（187 处调用点属重构，留待引擎切片 change 的前置工作，见 `openspec/notes/BACKLOG-MCP-AND-FORMATS.md` D1）。
- 跨平台引擎库 `logcat.Core` 不在本卡创建（名字留白给引擎切片）。
- 跨进程语言实时通知（不做，重启生效）。
- 唤醒协议的完整 IPC 握手（PID 传递 + 超时重试）不做，取降级版。
- DLT 格式接入、sidecar 索引缓存等其他在途探索（与本卡文件零重叠，时序上本卡先行）。

## Impact

- 工程：新增 `logcat.UiCore.csproj`、`logcat.AndTools.csproj`、`Directory.Build.props`；`logcat.csproj` 调整引用与 `InternalsVisibleTo`；`tests/logcat.Tests.csproj` 增加引用并修正源码扫描路径规则（`LocTableTests`）。
- 代码搬迁：Services（精确清单与逐文件归属见 design.md D2，apply 首项以依赖扫描复核）、Forms 10 个、Controls 4 个（含 resx）、Models 若干。
- 行为变更点：配置文件布局（迁移协议兜底）、「设备操作」按钮语义（对话框 → 启动器）、语言持久化位置（`language.json`）。
- 文档：`docs/BUILDING.md`（新 publish 流程）、双 README（功能描述）、`docs/TESTING.md`（用例数）随实施同步。
- 发布：单 zip 两 exe 并列；自包含口径下 publish 合并流程需按 design 冒烟取证。
