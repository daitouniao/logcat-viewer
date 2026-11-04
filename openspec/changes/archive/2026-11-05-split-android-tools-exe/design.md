# Design

## Context

动机与范围见 proposal.md。探索结论与代码事实（依赖方向、UI 依赖面、测试路径耦合、互斥量常量、`Loc` 混合体、依赖扫描支撑件等，逐条带 file:line 依据）见 `openspec/notes/SPLIT-ANDROID-TOOLS-EXE.md` §二；决策背景见同文 §三/§四。本文记录工程落法，不重复探索证据。

当前基线：单工程 `logcat.csproj`（`net10.0-windows`，`<Version>0.1.7`），测试工程单引用 `../../logcat.csproj`，CI 只跑一条 `dotnet test`。

## Goals / Non-Goals

- Goals：三工程结构落地、配置拆分与迁移、跨进程语言跟随（重启生效）、两 exe 发布口径；验收线 = 既有测试基线完全一致（纯搬迁不稀释）+ 新增迁移/语言/单实例用例。
- Non-Goals：`Loc` 半拆、`logcat.Core` 引擎切片、跨进程语言实时通知、唤醒完整握手（见 proposal Out of Scope）。

## Decisions

> D-S2–D-S8 七条按探索笔记偏向收口（维护者已确认「全部按偏向定」），评审时可逐条推翻；推翻只影响 design 与 tasks，不影响 specs 行为契约。

### D1 共享库 `logcat.UiCore`（N14，原探索决策）

`logcat.UiCore` 类库，TFM `net10.0-windows`；`git mv` 搬入 `logcat.UiCore/` 子目录；`namespace logcat` 保持不变（只搬文件不改名，diff 最小化）；程序集名 `logcat.UiCore`。`logcat.Core` 名字留白给将来引擎切片（`BACKLOG-MCP-AND-FORMATS.md` D1），防止 WinForms 代码误入。
理由：依赖扫描证实共享面全部依赖 `System.Drawing`，TFM 只能 windows；链接源码（csproj `Compile Include`）会让搬迁 diff 追责困难，弃用。

### D2 搬迁清单（apply 首项以依赖扫描复核，以编译通过为准）

进 `logcat.UiCore/`：

| 类别 | 文件 |
| --- | --- |
| Services | `AdbManager`、`Loc`、`LocTable.*`（11 个）、`FavoritesStore`、`CommandStore`、`StartDirResolver`、`TransferProgress`、`StartupLog`、`DpiDiag`、`DpiFix`、`ClipboardHelper`、`AppInfo`、`SingleInstance`（见 D6）、`AdbServerSelfHealResult` |
| Forms | `DeviceOpsDialog`、`FileBrowserDialog`、`ApkInstallDialog`、`ApkUninstallDialog`、`RunAsDialog`、`ScreenShotDialog`、`ScreenRecordDialog`、`CommandDialog`、`CommandEditDialog`、`SimpleInputBox` |
| Controls | `DeviceFilePane`（含 `.resx`）、`FilePane`、`LocalFilePane` |
| Models | `DeviceInfo`、`DeviceLastDir`、`FavoriteDir`、`CommandEntry`、`TryOpenOutcome`、`FileEntry` |

留 logcat.exe：`Program`、`frmMain*`、`RecordDialog`、`FileProxyDialog`、`CrashSummaryForm`、`FilterDialog`、`LogListView`、`LoadPublishBuffer`、日志引擎 Services（`LogDocument`/`LogParser`/`LogcatStream`/`FilterEngine`/`TextProbe`/`Crash*`/`FileProxy*`/`ProxyChannel`）、引擎侧 Models（`CrashEvent`、`FilterSpec`）。
特殊：`AppSettings` 不整体搬——按 D4 拆分后各自落位；`FileEntry` 归属待编译复核（若 logcat 侧也用则留引擎侧副本评估，倾向不复制、以扫描结果为准）；`Loc.PinFont` 实测 0 个外部调用点，随搬迁原样保留不删（纯搬迁口径，处理留给引擎切片的 Loc 半拆）。

**apply 依赖扫描复核结论（2026-11-01，任务 1.2）**：D2 清单成立，无修正。`FileEntry` 仅被搬走的三个 Pane 与测试引用，整体进 UiCore。exe 侧留置文件引用搬走类型的点全部是 Services 支撑件（`Program`→StartupLog/SingleInstance/Loc/AppInfo、`AppSettings`→DeviceLastDir/StartupLog、`LogcatStream`→AdbManager、`RecordDialog`→ClipboardHelper、`FilterDialog`→DpiDiag），由 logcat.csproj 引用 UiCore 解决；搬走文件对 exe 侧的唯一实质依赖是 `AppSettings.Default`（FilePane 列宽、DeviceFilePane.RunAsRelayDir、ApkInstall/ApkUninstall 选项、FileBrowserDialog 起始路径参数、`Loc.SetLang/DetectLang`），全部按 D3/D4 拆分消解。`frmMain` 引用 AdbManager/DeviceInfo/DeviceOpsDialog/FavoritesStore/SimpleInputBox，其中 DeviceOpsDialog 5 处由任务 2.4 改启动器。

### D3 `AppSettings` 拆三个类 + 迁移代码放置（D-S3 收口）

- 类：`LogcatSettings`（logcat.exe）、`AndToolsSettings`（UiCore，andtools 用）、`LanguageSettings`（UiCore，`Loc` 用）；迁移期加只读 legacy DTO（完整旧键，含 `System.Drawing` 几何类型——UiCore TFM 允许）。弃「单类分 section」（两进程全量互覆盖，与拆文件矛盾）与「基类+薄壳」（三类配置收益不足）。
- 迁移代码：共享库只放**原语**（legacy 读取、schema 版本判定、原子写原语），logcat 侧与 andtools 侧落盘分支各在自工程。理由：logcat 侧配置类按 N14 边界留 exe，共享库引用不到 exe，「单一迁移入口」写不出 logcat 分支——依赖方向矛盾只能用原语拆分解。
- `InternalsVisibleTo("logcat.Tests")` 复制进 UiCore csproj；exe 侧是否仍需要，apply 时扫描 `frmMain` 等被测 internal 成员后定（保留无害）。

### D4 迁移协议落地（D-S4 收口）

- 原子写：所有配置落盘改 tmp + `File.Move`（约 5 行原语，三文件共用），且「内容写全才置 `SchemaVersion=2`」——中途崩溃留下的是无版本号/不完整文件，下次按未迁移分支重走，不静默丢键。顺带修掉现状 `SaveTo` 非原子写的问题（`AppSettings.cs:165`）。
- 旧版回写容忍：0.1.x 旧 exe 会抹 `SchemaVersion`（STJ 忽略未知属性）→ 新版再启动重迁移一次；重迁移不覆盖已存在的新布局文件，最坏 logcat 侧一次改动回退。由 specs「迁移 SHALL 幂等」总括覆盖，不单列场景；不为降级加 marker 机制。
- 迁移分支按探索笔记 §三 显式条件实现（andtools 版本 2 即权威不回填；安卓侧键复制不移动；Language 抽出写共享文件幂等；全新安装不迁移走系统语言兜底）。
- 并发首启：全新安装后两 exe 同时首次启动会并发写 `andtools.json`/`language.json`——两侧从同一份 legacy `settings.json` 派生内容，原子写保证无半写文件，最坏结果等价，不加锁。

### D5 语言读写改指 `language.json`

`Loc.SetLang` 与 `DetectLang` 都经 `LanguageSettings`（UiCore）读写 `language.json`；不再经 logcat 配置全量 `Save()`。理由：读写两侧都改（探索笔记 §二.17），只改写侧会静默丢已存语言。跨进程一致靠「同目录同文件 + 启动时读」，接受重启生效（N7）。

### D6 `SingleInstance` 名可注入

构造加可选参数 `mutexName`，默认值 = 现名 `Local\logcat-viewer-single-instance`；andtools 传 `Local\logcat-andtools-single-instance`。理由：测试侧 7 处无参构造（`SingleInstanceTests.cs`）零改动、继续拿真实名；测试与运行中 exe 抢锁是既有现状，不变。唤醒事件名 `Local\logcat-andtools-wake`，由 andtools 首实例创建（见 D8）。

### D7 andtools 主窗体：`DeviceOpsDialog` 升格

`logcat.AndTools` 主窗体复用 `DeviceOpsDialog` 的容器形态：页签构建逻辑不动，窗体级差异（Text/Icon/窗口几何持久化、启动器语义）在 andtools 工程侧改；构造注入 `(AdbManager, serialProvider, rootProvider)`，`serialProvider` 指向 D9 设备选择逻辑类。理由：探索证实它已是注入式容器（`DeviceOpsDialog.cs:27`），页签按需创建，升格比重写省且行为对齐拆分前。

### D8 唤醒协议（D-S6 收口：降级版）

- andtools 首实例启动即创建命名事件 `EventWaitHandle`（`Local\logcat-andtools-wake`）并起监听线程。
- 二次启动：新实例拿互斥量失败 → `Set` 事件 → 提示「已在运行」退出（与 logcat 现有单实例提示同型）；首实例收到事件 → 自身 `TrySetForegroundWindow`，失败退 `FlashWindow`（任务栏闪烁，不加提示文案），不做 PID 握手与超时重试。
- logcat 启动器：`File.Exists` 探测（缺失 → D-S5 甲禁用+提示）→ `Process.Start` 拉起；不调 `AllowSetForegroundWindow`（前台进程拉起的子进程默认可获前台权，apply 冒烟验证前置效果，不行再补）；已在运行的前置交给上面的事件路径（logcat 无法替已运行实例授权前台，不硬做）。完整握手（PID 传递 + 超时重试）留作升级空间，事件名已固化。

### D9 设备选择复用（D-S2 收口：逻辑类）

新抽 `DeviceSelector`（UiCore）：持 `AdbManager`，暴露刷新、当前 serial、`DevicesChanged` 事件、adb 自愈接入；两 exe 各画各的下拉 UI（logcat 侧沿用现 `ToolStripComboBox` + 宽度联动，约 30 行重复）。理由：沿 `DeviceOpsDialog` 注入范式，不跨窗口类型抽象 `UserControl`（`ToolStripControlHost` 包装别扭）；自愈与刷新逻辑单份在逻辑类。

### D10 版本号与启动日志（N6/N12）

`Directory.Build.props` 放仓库根，`<Version>` 单一来源，两 exe 与 UiCore 继承（`logcat.csproj` 移除自有 `<Version>`）；props 初始值沿用 `0.1.7` 原值不动，版本递增留给发版阶段（由维护者指令驱动）。`StartupLog.SessionStart` 增加日志文件名参数（默认现名），andtools 传 `startup-andtools.log`。`AppInfo` 版本读取走程序集版本，两 exe 自动各自正确。

### D11 测试工程（N10）

`logcat.Tests.csproj` 增加对 UiCore 与两 exe 的 `ProjectReference`；`LocTableTests` 排除规则由 `StartsWith("Services/LocTable.")` 改为同时匹配 `UiCore` 子目录新路径；`InlineData("Forms/...")` 3 处同步改。`SelfContained` 不得无条件写死（沿 `logcat.csproj` 现注释口径，否则 NETSDK1151）。迁移/语言新用例注意 CI 语言环境规则（`LangScope` 双语，config 语境）。

### D12 发布（D-S8 收口：分别 publish 再合并）

框架依赖与自包含两套 zip 均为「两 exe 同目录」：两 exe **分别** publish 到独立临时目录，再把输出合并进同一 zip（运行时文件同名同版本，以最后一次拷贝为准，脚本不特判）。BUILDING 写死步骤 + 发布冒烟清单（两 exe 均能启动、改语言互见、配置三文件齐）。留验证项：`dotnet publish -o` 同目录一次成型若 SDK 原生可行则替换合并步骤（apply 时实测，二选一，行为等价）。

**apply 实测结论（任务 4.1，2026-11-02）**：流程定为「**两条 publish 命令先后 `-o` 同一目标目录**」——

- `dotnet publish logcat.csproj -c Release -o <目录>` 后接 `dotnet publish logcat.AndTools/logcat.AndTools.csproj -c Release -o <同一目录>`，SDK 原生完成合并（同名运行时文件以最后一次拷贝为准），无需脚本特判，实测产出 31 个文件、两 exe 齐全。
- sln 级 `dotnet publish logcat.slnx -o` 一次成型也实测可行，但会把测试工程一并 publish（99 文件，含 logcat.Tests.*），需给 tests csproj 加 `IsPublishable=false` 才干净——多一处工程配置改动，不值；两条命令方案零配置改动，产物等价，故选它。
- 附带观察：单工程 publish（logcat.csproj）不会带出 logcat.AndTools（ProjectReference 只指向 UiCore），「两条命令」因此是必须而非可选。

## Verification Mapping（apply 任务 3.2 回填，2026-11-02）

specs delta 场景 → 单测 / 人工清单归属：

| spec 文件 · 场景 | 自动化用例 | 人工清单（BUILDING.md §四.5 冒烟） |
| --- | --- | --- |
| settings-storage · 旧布局首次保存拆三份键值完整 | `ConfigMigrationTests`（①拆三份 / ②工具箱先跑语言不丢 / ③幂等 / ④Language 抽出旧键清除）、`LegacySettingsDtoTests`（22 键面）、`SettingsAtomicWriteTests`（原子写/半写重走迁移） | 旧版 settings.json 放入目录再启动自动拆三份 |
| settings-storage · 双开互不覆盖（真双进程并发写） | 键面不相交 + 各自 StorePath 由 `LegacySettingsDtoTests`/`SettingsAtomicWriteTests` 间接保证 | 两 exe 同时开着各改各的设置，退出后互不覆盖 |
| ui-language · 语言跟随重启生效 | `LanguageStoreTests`（保存后读回重启生效口径 / 落盘键面 / SetLang 双语切换） | 工具箱改语言退出再开日志查看器语言一致 |
| ui-language · CI 语言环境 | `LocTableTests` 双语断言规则沿用，新增 4 键（工具箱标题/已在运行/缺失/启动失败）已入双语表 | — |
| android-toolbox · 独立进程（不打开日志查看器直接用） | `DeviceSelectorTests`（刷新/自愈/选择联动）+ AndToolsMainForm 组合 | 工具箱双击独立启动出主窗体 |
| android-toolbox · 启动器拉起 / 缺失禁用 | `AndToolsLauncherTests`（存在启动一次 / 缺失不启动不异常） | 点「设备操作」拉起；缺失时禁用态悬停提示 |
| android-toolbox · 单实例与唤醒 | `WakeUpTests`（前置失败退闪烁判定）、`SingleInstanceNameTests`（独立互斥量名/唤醒事件常量） | 工具箱二次启动不出第二窗体、已有实例被前置（前置失败任务栏闪烁） |
| android-toolbox · 设备选择与 adb 自愈 | `DeviceSelectorTests`（含自愈失败清空不误报） | 工具箱内刷新列出在线设备 |
| android-toolbox · 窗口几何持久化 | `AndToolsWindowGeometryTests`（保存三态口径 / 恢复校验屏内与宽度） | 两边各自拖动/最大化退出重启，几何互不干扰 |

## Risks / Trade-offs

- [搬迁 diff 巨大，git 历史跨工程移动] → `git mv` 保 rename 检测；apply 首项依赖扫描复核 + 编译零警告推进，不改逻辑。
- [测试与运行中 exe 抢互斥量既有现状] → 名注入默认值不变现状，风险不新增。
- [自包含合并 publish 的运行时文件覆盖未实测] → D12 冒烟取证兜底；失败则回退「两子目录」口径需重议 N2（已列为 open question 前置探测）。
- [旧 exe 回写触发重迁移] → 容忍（由 spec 幂等口径总括，不单列场景）；不丢键由「不覆盖已存在新布局文件」保证。
- [`Loc` 整体进 UiCore 使 logcat.exe 对 UI 辅助的依赖过库] → 接受；半拆是引擎切片前置（BACKLOG D1），本卡不做。

## Migration Plan

运行时配置迁移由 D4 协议自动完成，用户无感；发布侧无数据回滚需求。代码回滚 = 归档前任一 commit revert（三工程结构独立成 commit 批次，tasks 按可回退粒度切）。旧版 0.1.x 与新版并存同一目录不受支持（发布即替换目录），不做兼容 shim。

## Open Questions

- `InternalsVisibleTo` 在 logcat.exe 侧去留——**已定（apply 任务 1.10，2026-11-01）：保留**。测试直接使用 frmMain 的 internal 静态成员（`CanClearLog`/`CanStartCapture`/`ClearDeviceCombo`/`IsNetworkPath`，见 AdbClearLogButtonTests、frmMainHelperTests），删除会破坏既有用例。
- D12 的 `dotnet publish -o` 同目录一次成型是否可行——**已定（apply 任务 4.1，2026-11-02）：可行但不用**。两条 publish 命令先后 `-o` 同一目录即 SDK 原生合并成型（见 D12 实测结论）；sln 级 publish 会带出测试工程，不选。
