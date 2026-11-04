# 探索结论：拆分为 logcat.exe + 安卓工具 exe

- 状态：**决策已收口**（事实见 §二，已定决策见 §三；D-S2–D-S8 已按偏向收口至 change `split-android-tools-exe` 的 design.md，本笔记随该 change 归档）
- 日期：2026-11-01
- 关联提交：无（探索阶段，未改动代码）
- 验收结论：不适用

## 一、目标与结构

用户诉求：安卓工具不再寄生 logcat 主窗口，独立 exe 直接可用；语言配置跟随 logcat；设备刷新 / 设备选择复用而非重写。

流程判定：本次改动同时满足「新增 csproj / 项目」「跨 ≥3 文件」「改 UI 交互」三条较大改动判据 → **必须先出 OpenSpec change 再编码**，不在探索阶段动手。

```
发布目录（一个 zip，两 exe 并列 → 同 AppContext.BaseDirectory）
+- logcat.exe            日志查看器（保留设备下拉 + 采集；「设备操作」作启动器）
+- logcat-andtools.exe   安卓工具箱（主窗体 = 设备选择 + 页签式工具）
+- settings.json  andtools.json  language.json  favorites.json  commands.json
+- Log/startup.log  Log/startup-andtools.log

工程结构
+- logcat.UiCore（类库，net10.0-windows）
|                  AdbManager / Loc + LocTable / 三个存储 / 设备六件套 Forms
|                  Controls/DeviceFilePane·FilePane·LocalFilePane / 设备侧 Models
|                  支撑件：StartupLog（日志名可注入）/ DpiDiag + DpiFix /
|                  ClipboardHelper / SimpleInputBox / AppInfo（§二.16 依赖扫描）
|                  + 新抽出：设备选择能力（下拉 + 刷新 + DevicesChanged + 自愈接入）
+- logcat（exe）       Program + frmMain + 日志引擎 + logcat 侧 Forms/Controls
+- logcat.AndTools（exe）Program（独立互斥量）+ 主窗体
                      两个 exe 都引 logcat.UiCore
```

## 二、代码事实（边界的依据，逐条可核）

1. **依赖方向单向**：安卓侧（六个设备对话框 + 三个文件面板 + `AdbManager`/`FavoritesStore`/`CommandStore`/`StartDirResolver`/`TransferProgress` 等）对日志引擎**零引用**。唯一反向点是 `Services/LogcatStream.cs:17,32` 引 `AdbManager` —— logcat.exe 本来就要引共享库，不影响搬迁可行性。→ `git mv` 是机械操作。
2. **UI 依赖面**：`Services/AdbManager.cs:2-3`、`Services/AppSettings.cs:2`、`Services/Loc.cs:1` 用 `System.Drawing`；日志引擎与 `Models/` 全部零 UI 依赖。→ 装 AdbManager/Loc 的共享库 TFM 只能是 `net10.0-windows`；跨平台的引擎库必须是另一刀（边界见 §三 N14，混合体事实见 §二.15）。
3. **数据文件全在 exe 同目录**：`settings.json` / `favorites.json` / `commands.json` / `Log/startup.log`（`AppSettings.cs:30`）。`Loc.SetLang` 直接调 `AppSettings.Default.Save()`（`Services/Loc.cs:83-84`）。→ 同目录发布天然共享语言，零额外机制。
4. **`AppSettings` 现有键（`Services/AppSettings.cs:48-131`）**：安卓侧 11 个（`LastLocalPath`、`LastRunAsRelayDir`、`DeviceLastDirs`、`DevicePaneColumnWidths`、`LocalPaneColumnWidths`、`ApkInstallViaPm`、`ApkInstallFlags`、`ApkTmpDir`、`ApkUninstallViaPm`、`ApkKeepData`、`ApkUseRoot`）；共享 1 个（`Language`）；logcat 侧 10 个（`AutoApply`、`FontPt`、窗口几何三键、fileproxy 五键）。窗口几何类型是 `System.Drawing.Point/Size`。
5. **单实例互斥量是常量**：`Services/SingleInstance.cs:11` 的 `MutexName = Local\logcat-viewer-single-instance`。测试侧（`tests/logcat.Tests/SingleInstanceTests.cs`，5 用例 / 7 处 `new SingleInstance()`）**不引用该常量**，全部走无参构造 = 拿真实互斥量名。真实耦合：参数化互斥量名时需给默认值（现名）或给测试注入专用名——不给默认值会牵动那 7 处构造；且测试与运行中的 logcat.exe 抢同一把锁是既有现状。
6. **双进程共用 adb server 不互踩**：`EnsureServerRunningAsync` 是 start-only（`Services/AdbManager.cs:84`）；`kill-server` 只出现在「不带 -s 的子命令」白名单里（`AdbManager.cs:274`），全仓库无调用点。→ 两 exe 各持 `AdbManager` 实例，最坏是两次并发 `start-server`。
7. **测试工程的三处路径耦合**：`tests/logcat.Tests/logcat.Tests.csproj:25` 单个 `ProjectReference ../../logcat.csproj`；`RepoRoot` 编译期注入指向仓库根（同文件 `:34-37`），拆分后仍有效；但源码扫描的排除规则写死 `StartsWith("Services/LocTable.")`（`LocTableTests.cs:535`、`:1041`）与 3 处 `InlineData("Forms/...")`（`:166-168`）→ 文件进子目录后必须同步改，否则译文表自身被当成调用点扫。
8. **CI 无需改**：`.github/workflows/ci.yml:58` 只有一条 `dotnet test tests/logcat.Tests/logcat.Tests.csproj`。
9. **`SelfContained` 约束乘二**：`logcat.csproj` 的注释说明该属性绝不能无条件写死——会沿 `ProjectReference` 传染，测试工程引用 exe 时报 NETSDK1151。拆分后测试要同时引**两个** exe 工程，这条约束的作用面翻倍。
10. **新增项目无 `Properties/` 冲突**：`Properties/` 下只有 `PublishProfiles/FolderProfile.pubxml`（VS 用，隐式绑 `logcat.csproj`，另有同名 `.user` 本机文件）与无 `AssemblyInfo.cs`；两 exe 同目录 publish 时 `*.deps.json` / `*.runtimeconfig.json` 按 exe 名区分，共享库 dll 只出一份。**自包含口径下两个 publish 输出合并到同一目录的运行时文件（hostfxr/coreclr 等）冲突面未核**，见 §四 D-S8。
11. **`InternalsVisibleTo("logcat.Tests")`**（`logcat.csproj`）要**复制**进共享库 csproj（不是挪——logcat.exe 侧 `frmMain` 等是否仍有被测 internal 成员，propose 时核实后再决定 exe 侧去留）；`Controls/DeviceFilePane.resx` 随控件走。
12. **`RecordDialog` 不进共享库**：只被 `frmMain.cs:1463` 调用（logcat 侧查看原始记录）；`DeviceOpsDialog.cs:195` 用的是 `ScreenRecordDialog`，两个类无关。但它的译文 `"记录 #{0}"` 在 `Services/LocTable.DeviceUi.cs:48` → 共享库会带 1 个 logcat 侧键，容忍即可（共享库两边都引）。
13. **OpenSpec 现状**：8 个 capability（`adb-capture`、`adb-server`、`chunked-file-load`、`device-file-transfer`、`file-browser`、`fileproxy`、`message-filter`、`time-column`）；`openspec/changes/` 无在途；无活动 worktree；主工程 `<Version>` = 0.1.7，TFM = `net10.0-windows`。
14. `DeviceOpsDialog` 本身就是注入式容器：构造 `(AdbManager, Func<string?> serialProvider, Func<RootMode> rootProvider)`（`Forms/DeviceOpsDialog.cs:27`），页签按需创建、命令页不要求选设备（`:79`）。→ 可直接升格为 andtools 主窗体内容，不需要重写。
15. **`Loc` 是混合体**（`Services/Loc.cs`，34 个公开成员）：约 10 个是纯译文/语言（`T`、`F`、`W`、`Current`、`SetLang`、`ParseLang`、`DetectLang`、`LangCode`、`En`、`SetLangGuard`），其余约 24 个是 WinForms/Drawing 辅助（`Bind`/`BindCol`/`BindTip`/`BindHint`、`ApplyTo*`、`PinFont`、`UiFont`、`SizedButton`、`FitWidth`/`FitHeight`/`FitWidths`、`LayoutButtonPair`、`AllControls`/`AllToolItems`）。调用面（2026-11-01 复测）：Bind 家族（`Loc.Bind`/`BindCol`/`BindTip`/`BindHint`）**187 处 / 19 个文件**，其余 UI 辅助 **38 处 / 11 个文件**（排除 tests；排除 `Loc.cs` 自身则 37/10）。`PinFont` 实测 0 个外部调用点（仅 `Loc.cs` 内定义与文档提及），搬迁时可确认是否删。`Loc.T`/`Loc.F` 则引擎侧与安卓侧都用（`LogDocument`、`FilterEngine`、`FileProxyService`、`Models/FilterSpec.cs` 等）。→ 本卡把 `Loc` 整体放进 `logcat.UiCore`（含 UI 辅助，`net10.0-windows` 成立）；**不要在本卡拆它**，187 处调用点属重构，会砸掉「纯搬迁、基线一致」的验收线。拆分是将来引擎切片的前置工作，已写进 `openspec/notes/BACKLOG-MCP-AND-FORMATS.md` 的 D1。
16. **UiCore 搬迁清单的支撑件（2026-11-01 依赖扫描补齐）**：对 §一 点名的每个文件扫描其对 Services 类的引用，除清单已列者外还实际依赖 5 个类，§一 结构图已补——`StartupLog`（AppSettings / Loc / FavoritesStore / CommandStore / FileBrowserDialog 的 catch 留痕都用它；N12 要求 andtools 写独立日志，故须共享且日志名可注入）、`DpiDiag` + `DpiFix`（DeviceOpsDialog:46-47、FileBrowserDialog）、`ClipboardHelper`（ApkInstall/Uninstall、Command 对话框、FilePane，logcat 侧 RecordDialog 也用 → 放 UiCore 两边可达）、`SimpleInputBox`（三个文件面板 + Command 对话框 + frmMain）、`AppInfo`（N6 统一版本号后两 exe 都要显示版本）。propose 的 `git mv` 清单以本条为准。
17. **语言读写两侧都在 `Loc`**：写侧 `SetLang`（`Loc.cs:83-84`，现为 `AppSettings.Default.Save()`）；读侧 `DetectLang`（`Loc.cs:126`，读 `AppSettings.Default.Language`）。拆分后**两侧都要改**指向 `language.json`，只改写侧会静默丢已存语言。

## 三、已定决策

| #   | 决策             | 结论 |
| --- | ---------------- | ---- |
| N1  | 共享代码组织     | 共享类库（命名见 N14），`git mv` 搬进子目录（不采用 csproj 链接源码） |
| N2  | 部署目录与发布   | 两个 exe 同目录，一个 zip 并列分发（自包含 + 框架依赖两套，沿用现发布口径） |
| N3  | 配置迁移         | schema 版本化自动拆分，见下文「迁移协议」 |
| N4  | logcat 入口      | 保留「设备操作」按钮，作为 andtools 启动器 |
| N5  | 单实例与唤醒     | andtools 独立互斥量 + 命名事件 + `AllowSetForegroundWindow` |
| N6  | 版本号           | 两 exe 统一版本号，抽 `Directory.Build.props` 单一来源 |
| N7  | 跨进程语言跟随   | 接受「重启生效」，不做实时通知 |
| N8  | capability       | 新建 `android-toolbox`，用 GIVEN/WHEN/THEN 写独立 exe、配置共享与语言跟随、单实例语义 |
| N9  | 命名与标识       | `logcat-andtools.exe`，沿用 `app.ico` |
| N10 | 测试工程         | 仍一个 `tests/logcat.Tests`，引共享库与两个 exe，同步改路径规则 |
| N11 | andtools 窗口几何 | `andtools.json` 新增 `WindowLocation/Size/State` 三键 |
| N12 | StartupLog       | andtools 写 `Log/startup-andtools.log` |
| N13 | 命名空间         | 保持 `namespace logcat`（只搬文件不改名），程序集名 `logcat.UiCore` |
| N14 | 共享库边界与命名 | 本卡只切一层：共享库定名 **`logcat.UiCore`**（`net10.0-windows`，装 AdbManager / Loc + LocTable / 三存储 / 设备 Forms / 文件面板 / 设备侧 Models），日志引擎留在 logcat.exe。**`logcat.Core` 这个名字空着**，留给将来自带跨平台目标的引擎切片（`BACKLOG-MCP-AND-FORMATS.md` 的 `core-extraction`，`net10.0`）。理由：本卡保持纯搬迁、验收线（测试基线完全一致）不被稀释；边界用命名固化——将来谁想把 WinForms 代码塞进 `logcat.Core` 会当场撞名。 |

**配置文件布局**：`settings.json` 归 logcat（窗口几何、显示选项、fileproxy），`andtools.json` 归 andtools（APK 选项、上次路径、列宽、andtools 窗口几何），`language.json` 只存语言、两 exe 共读共写。拆文件同时解决三件事：窗口几何不抢同一槽、退出全量 `Save()` 不覆盖对方改动、语言仍跟随 logcat。`Loc.SetLang` 改为只写 `language.json`，`DetectLang` 改为读 `language.json`（§二.17）。

**迁移协议**：各文件带 `SchemaVersion`，1 = 旧布局，2 = 拆分布局。迁移幂等，两 exe 反复启动不重搬；安卓侧键是**复制**不是移动（logcat 先跑、andtools 从未启动时也不丢）；`andtools.json` 已是版本 2 时**不回填** legacy（它可能更新，故 logcat 侧①有显式前提）。

```
andtools 启动:  andtools.json 版本=2 -> 用它（权威）
                否则 -> 从 settings.json 读 11 个安卓侧键 -> 写 andtools.json(版本2)
                        同批：language.json 缺失且 settings.json 有 Language
                              -> 抽出写 language.json

logcat  启动:  settings.json 版本=2 -> 直接用
                否则(版本1/缺失) ->
                   若 andtools.json 不存在或版本<2:
                       ① 11 个安卓侧键【复制】进 andtools.json(版本2)
                   ② settings.json 只留 logcat 侧键，存为版本2
                   ③ 同批：Language 键抽出写 language.json（幂等）

全新安装(三文件全缺): 不迁移；两 exe 各走 Loc.DetectLang 按系统语言兜底
                      （现行为，Loc.cs:126），首次切换语言才落 language.json
```

## 四、待拍板

（以下 7 条已在 propose 阶段按偏向逐条收口，结论落 change `split-android-tools-exe` 的 design.md；原文保留作决策依据。）

### D-S2 「设备选择」的复用形态

- **为什么必须定**：两 exe 都要「设备下拉 + 刷新 + DevicesChanged + adb server 自愈」，自愈逻辑必须只有一份（否则修一处漏一处）；但现形态是 `frmMain` 工具栏内嵌的 `ToolStripComboBox`（`frmMain.cs:198`），还带工具栏宽度联动布局（`:408-417`）和「换设备重建绑定设备页签」回调（`:200`）——直接抽成控件在 andtools 侧别扭。
- **方案**：甲 纯逻辑类（共享行为：持有 AdbManager、刷新、自愈接入、当前 serial），两 exe 各画各的 UI（约 30 行重复）；乙 `UserControl`（UI 也复用，logcat 侧要用 `ToolStripControlHost` 包一层）；丙 只抽自愈入口，下拉两边各写（共享面最小，但「刷新逻辑只有一份」只部分满足）。
- **偏向**：**甲**。`DeviceOpsDialog` 已经是 `(manager, serialProvider, rootProvider)` 注入范式（§二.14），逻辑类沿同一范式，不跨窗口类型抽象控件。

### D-S3 `AppSettings` 拆成几个类 + 迁移代码放哪

- **为什么必须定**：配置文件布局（§三）定了「谁拥有什么」，但没定「类怎么切」；这条决定 `InternalsVisibleTo` 那批 internal 构造（`AppSettings.cs:45`、`:134`、`:159`）怎么搬、迁移代码放哪、以及 `Loc.SetLang`/`DetectLang`（`Loc.cs:83-84`、`:126`）改指向谁。
- **类怎么切——方案**：甲 保留一个类、按 section 写不同文件（键定义仍一份，两进程各见全量 → 全量覆盖冲突回来了，与拆文件初衷矛盾）；乙 三个独立类（logcat 侧 / andtools 侧 / 语言），迁移期加一个只读 legacy DTO；丙 存储基类 + 三个薄壳（省序列化样板，多一层抽象）。
- **迁移代码放哪——方案**：甲 共享库只放迁移**原语**（legacy 读取、SchemaVersion 判定、原子写），各 exe 写自己的落盘分支；乙 迁移入口整体进共享库。
- **偏向**：类切**乙**（甲自相矛盾；丙在只有三类配置时收益不明显，legacy DTO 用完可在后续 change 里删）。迁移代码**甲**——乙有依赖方向矛盾：logcat 侧配置类按 N14 边界带 `System.Drawing` 键、应留 logcat.exe，共享库引用不到 exe，「同一份入口」写不出 logcat 侧落盘分支。

### D-S4 迁移的健壮性口径

- **为什么必须定**：§三 协议只讲了幂等，没讲两个会真丢数据的口子——① `AppSettings.SaveTo` 是非原子 `File.WriteAllText`（`AppSettings.cs:165`），「读 legacy 11 键 → 写 andtools.json」中途崩溃会留下存在但不完整的 `andtools.json`，下次启动按「版本 2 直接用」分支走 → **静默丢键**；② 老版本 exe（0.1.7）读到带 `SchemaVersion` 的文件能读（STJ 忽略未知属性），但回写会把 `SchemaVersion` 抹掉 → 新版再启动会**重新迁移一次**；language.json 同理（旧 exe 的 `SetLang` 把 Language 写回 settings.json，新版不再读那里）。
- **方案**：① 甲 tmp + rename 原子写，且「写全了才置版本 2」（约 5 行，三个 store 共用）；乙 维持现状，接受小概率丢键。② 甲 接受重迁移（不覆盖 `andtools.json`/`language.json`，最坏 logcat 侧一次改动回退），并写进 spec 场景；乙 把版本标记放到不会被抹掉的地方（独立 marker 文件 / 文件名）。
- **偏向**：① **甲**（成本低，且顺带修掉现有「配置文件写一半」的老问题）；② **甲**（一条场景就够，不必为降级加机制）。

### D-S5 `logcat-andtools.exe` 缺失时入口按钮的行为

- **为什么必须定**：N4「保留启动器」+ N2「同目录」有个必然缺口——用户只复制 logcat.exe、或包里漏发 andtools 时，按钮存在但点了没用。当前没有任何探测逻辑。
- **方案**：甲 启动时探测同目录 exe，缺失则禁用按钮并在悬停/点击时提示（需 2 条译文）；乙 保持可点，点击后报错；丙 不处理。
- **偏向**：**甲**。成本很低（一次 `File.Exists` + 一条 `Loc` 译文），乙把一个配置问题报成运行时异常，用户无从判断该补哪个文件。

### D-S6 唤醒协议的方向与兜底

- **为什么必须定**：N5 只拍了机制名。实际有顺序约束：`AllowSetForegroundWindow` 只能由**当前持有前台权限的进程**授权给目标进程——logcat 点按钮必须先授权再起/唤 andtools；而 andtools 已在运行时，授权对象是它的 PID，需要先经命名事件拿到那个 PID。
- **方案**：甲 完整版（命名事件双向握手 + PID 传递 + 超时重试）；乙 降级版（授权 → 拉起；已运行则请求前置，拿不到前台就退回提示「请手动切换窗口」）。
- **偏向**：**乙**。唤醒失败只是多点一次任务栏，不值得为它设计一套 IPC 握手；但要把「谁创建事件、事件名」在 design 里写清，留给甲的空间。

### D-S7 与另两张可开工卡的时序

- **为什么必须定**：`dlt-log-format`（`openspec/notes/DLT-INTEGRATION.md`，决策已收口）与 `doc-index-sidecar-cache`（`openspec/notes/SIDECAR-CACHE-INDEX.md`，仅剩 D13 待确认）都会往 `Services/` 加新文件。与本卡文件零重叠，但先后顺序决定要不要二次搬迁。
- **方案**：甲 本卡先做；乙 先做 DLT / sidecar，本卡后排。
- **偏向**：**甲**。新解码器、sidecar 读写器都必然落 `Services/`——先拆，新文件直接长在最终项目里；后拆则要为刚归档 change 引入的文件再 `git mv` 一遍，且 diff 追责变难。

### D-S8 自包含发布「两 exe 同目录」的合并口径

- **为什么必须定**：N2 拍了「自包含 + 框架依赖两套」。框架依赖口径下两 exe 同目录 publish 无冲突（§二.10 已核）；**自包含**口径下每个 publish 输出各带全套运行时文件（hostfxr、coreclr、clrjit 等），合并到同一目录是否安全未核——同 SDK 构建下文件应同名同版本、覆盖无害，但这只是推断，未实测，而发版踩坑成本高。
- **方案**：甲 分别 publish 到独立目录，再合并进同一 zip（运行时文件以最后一次拷贝为准，发布脚本不特判），BUILDING 写死步骤 + 发布冒烟（两 exe 均能启动、配置共享生效）；乙 改 publish 参数让两个 exe 发布到同一目录一次成型；丙 自包含套件改两 exe 各自子目录（放弃并列）。
- **偏向**：**甲**（先按此写 design 与 BUILDING，实施期用真实 publish 跑一次冒烟取证；乙若 SDK 原生支持更简，design 时顺手核一下 `dotnet publish -o` 同目录双 exe 的行为；丙破坏 N2 同目录前提，排除）。

## 五、下一步

可直接走 `/opsx:propose`（建议 change 名 `split-android-tools-exe`），specs delta 按 N8 新建 `android-toolbox` capability，design 需覆盖：`logcat.UiCore` 目录结构与 `git mv` 清单（**以 §二.16 依赖扫描为准，含 StartupLog / DpiDiag / DpiFix / ClipboardHelper / SimpleInputBox / AppInfo**）、`namespace logcat` 保持不变的 diff 策略、互斥量名可注入（§二.5）、设备选择复用形态（D-S2）、配置类拆分与迁移代码放置（D-S3）、迁移协议落地（§三，含 Language 分支与三种启动顺序）、迁移健壮性（D-S4）、语言读写两侧改指 `language.json`（§二.17）、唤醒与入口探测（D-S5/D-S6）、自包含发布合并口径（D-S8）、测试路径规则改动（含 §二.7 的 `LocTableTests` 排除规则指向 `UiCore`）、两个 exe 的 publish 流程与 BUILDING/README 同步。编码前不在探索阶段动手。
