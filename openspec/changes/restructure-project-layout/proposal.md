# Proposal：工程结构重组（三块同级收敛）

## Why

主 exe 的全部源码散在仓库根，工程目录就是仓库根，因此 `logcat.csproj` 必须靠三组 `Remove` 块（每组 `Compile` / `None` / `EmbeddedResource` / `Content` 四条，共 12 条）手工排除 `tests/**`、`logcat.UiCore/**`、`logcat.AndTools/**` 三棵兄弟子树；三个工程在磁盘上毫无边界，靠排除项维持「互不并入」。

这同时是 Rust 拆分的先行步骤：后续刀次要在 `logcat-core/`（Cargo workspace + C# interop）与 `logcat-mcp/`（Node 包）落实体工程，需要一个已经收敛、彼此同级、根目录只装基础设施的布局来安放它们。现在不搬，刀0 一开工就要在“根目录混装三工程源码”的地基上叠加第 4、第 5 个工程块。

## 范围

只搬现有三块，以及为搬动它们而必须同批修掉的路径耦合。

### 目标布局

- `logcat-viewer/`：主 exe（`logcat.csproj` 原样移入，工程文件名与程序集名都不变）
- `logcat-uicore/`：共享层（`logcat.UiCore.csproj` 移入，仅目录改名）
- `logcat-android-tools/`：安卓工具箱 exe（`logcat.AndTools.csproj` 移入，仅目录改名）
- `icon/`：仓库级图标资源目录，`app.ico` 从仓库根移入；两个 exe 各自向上引用
- 三块**工程目录**同级并列；`logcat-core/` 与 `logcat-mcp/` 将来在同一层补位（本次不建，见非目标）；顶层不加 `src/`
- `tests/`、`testdata/`、`publish/`、`scripts/`、`docs/`、`openspec/`、`sample/`、`icon/` 与四个根级文档留在仓库根

### 同批修掉的耦合

- 工程文件：根级文档 `None Include` 五项、`sample/**`、`docs/images/main.gif` 改 `..\` 前缀，并**全部补显式 `Link`**（不加 `Link` 会被拍平到输出根，`sample/` 与 `docs/images/` 层级静默丢失，实测已确认）
- 工程文件：四条 `ProjectReference`（viewer 1、android-tools 1、tests 2）；`app.ico` 移入根级 `icon/` 并改两个 `ApplicationIcon`
- 工程文件：**删掉**三组排除兄弟工程的 `Remove` 块（共 12 条；工程目录不再是仓库根，默认 glob 自然捞不到兄弟工程——这是搬迁的收益）
- 解决方案：`logcat.slnx` 从两个工程列全到四个
- 测试：`LocTableTests` 与 `CiLocaleAssertionTests` 里按旧路径断言的四处（共 7 条路径字面量，其中 `IsLocTablePath` 的搬迁前兜底 1 条删除；另有一处 `FindRepoRoot` doc 注释与一处 `IsLocTablePath` 注释需同批改正；改断言，不改被测实现）；源码扫描的排除项补 `.tmp-test/`
- 脚本：`scripts/mutation-test.py` 的 8 条现役路径加新前缀，顺手清 4 条已失效条目
- 文档：`docs/BUILDING.md` 四条 publish 命令的工程路径、警告块（`:76`）与排障表（`:194`）里的 `logcat.AndTools\publish\` 反例说法，以及两处非路径收敛——`## 四、发布` 下补一行工程目录说明（design D-21）、说明性版本号占位符化（design D-22）；双 README 的开发命令、「Project structure / 目录结构」章节与本地化章节里的 `logcat.UiCore/Services/LocTable.*.cs` 两处路径；三处指向 `logcat.csproj` 的 markdown 链接（双 README 版本徽章、第三方声明）与四处正文提及（双 README 的 `<Copyright>` 说明、`THIRD-PARTY-NOTICES.md` 的「输出目录自动复制」说明、`docs/TESTING.md` 的哨兵说明）；`docs/TESTING.md` 按「当前状态必改 / 历史记录不改 / `**` glob 不改」三层处理；`docs/UT-AUDIT.md` 的 7 处（4 条散文 + 2 条代码块命令 + 1 处裸 `frmMain.cs:713`）路径前缀；`openspec/config.yaml` 与 `Directory.Build.props` 各 1 处 `Services/` 前缀；`.github/workflows/ci.yml` 与 `scripts/run-tests-en-us.sh` 注释里的 `Services/Loc.cs` 各 1 处（design D-34）
- 仓库卫生：`.gitignore` 补泛化的 `target/` 与 `node_modules/`；本机残留（`logcat.csproj.user`、根级 `bin/` `obj/` `.vs/`）同批处理但不入库
- **前置动作**：先在 `master` 上 `git merge V0.1.9` 抹平分叉（它只领先 2 条 docs/版本号提交、零代码），否则搬迁后它的回流只能靠 cherry-pick 赌 rename 检测

## 非目标（out of scope）

- **不建 `logcat-core/`**：由刀0 建（其 `interop/` 是实体工程）；本 change 不建任何空目录
- **不建 `logcat-mcp/`**：由刀4 建
- **不抽 C# Core**：`Services/` 下的解析 / 索引 / 过滤 / 崩溃 / sidecar 各类随 viewer 一起搬，原地不动；`Loc` 不拆（187 处 / 19 文件）
- **不改行为**：不改任何界面、解析、索引、过滤、设置、设备侧逻辑；不改任何公开 API
- **不改标识**：不改 csproj 文件名、不改 `<AssemblyName>`、不改程序集名、不改输出产物文件名
- **不搬**：`tests/`（其 `RepoRoot` 与 5 级 bin 回溯都假设深度为 2）、`testdata/`（本机大样本）、`publish/`（仓库级产物，留根）
- **不修 `.tmp-test/realprobe*` 三条探针**：属本机 gitignored 资产，三种变体各 18 行 source-include 需按现目录重写，重建归刀1（它需要 C# oracle host，正好复用 realprobe 的 source-include 形状）。**例外（维护者 2026-11-08 定，D-36）**：`BenchLog` / `BlockScanBench` / `ChunkProbe` / `LoadProbe` 四个探针只挂一行 `ProjectReference` 到 `..\..\logcat.csproj`，同批改到 `logcat-viewer/`——否则构建直接报「找不到工程」
- **不加 CI 步骤**：不把 `dotnet build logcat.slnx` 加进 CI（15 分钟预算紧、slnx 变动极低频），改为本机验证
- **不发版**：不压 tag、不发任何包。本 change 自身不改版本号；但前置的 `V0.1.9` 合并会附带把 `<Version>` 从 0.1.7 带到 0.1.9（双 README 徽章与版本行同步），那是解该预发布分支的账，不是本 change 发版
- **不顺手修与路径无关的陈旧表述**：`AppInfo.cs` 注释「版本号以 logcat.csproj 的 `<Version>` 为唯一来源」与 `AGENTS.md`（不入库）里的 csproj 提及不在本 change 修。两处例外（维护者 2026-11-08 定）：`docs/BUILDING.md` 排障表「发出去的 zip 是旧版」一条（含必换的旧路径，改指 `Directory.Build.props`）与说明性版本号占位符化（D-22，改的是「文档跟随版本号」这条耦合本身）

## Capabilities

### New Capabilities

无。

### Modified Capabilities

无。

本 change 是纯结构重组：所有 13 个现有 capability 的行为契约一字不变，仅源码在磁盘上的位置变化。故在 change 的 `.openspec.yaml` 置 `skip_specs: true`，不产出 spec delta，也不为凑校验而虚构需求。

## Impact

### 构建与发布

- `logcat.exe` / `logcat-andtools.exe` / `logcat.UiCore.dll` 三个产物名与内容不变，publish 命令只改**工程路径**参数
- 落点写法一律不改：`docs/BUILDING.md` 的 `-p:PublishDir` 已是绝对路径、双 README 的 `-o publish\fd` 与文档里的 `Test-Path` 清单按当前工作目录解析；唯一按工程目录解析的落点是本机未入库的 `FolderProfile.pubxml`，它必须改成 `..\publish\Release\<版本>`，否则本机发布档案指向不存在的路径
- `-p:PublishDir` 的解析基准是各工程目录：若改写成相对路径，第二次 publish 会落到 `logcat-android-tools/publish/`、包里少 `logcat-andtools.exe`——即「重建该口径导致过断包」的那类事故，故本次明确不动它
- `Directory.Build.props` 留在仓库根，版本号单一来源继续被所有工程（含将来的 interop）继承
- 两个 exe 的 `ApplicationIcon` 改为向上引用 `icon/app.ico`；图标文件丢失时 SDK 只告警不报错，需在验收时看 exe 属性

### 测试与覆盖率

- 用例总数不变（**基线以 apply 第一步实测为准**；`docs/TESTING.md` 记 848，但 `V0.1.9-pre1` tag 消息实测 852，差 4 —— 不拿任一个当预期值），只有路径字面量型断言随之改写；**同批把用例数字改为实测量，共 9 处**：`docs/TESTING.md` 3 处 + 双 README 6 处（含 `README.md:194` 陈旧的 `791 tests`，与中文版的 848 已互相矛盾）（维护者 2026-11-08 定，§3.4 联动：数字一变即改，不等发版；逐处清单见 design D-25）
- 覆盖率口径靠程序集名过滤（`[logcat]*`），只搬目录不改名因此不受影响；但 `ExcludeByFile` 用的是 `**` 前缀 glob，移动后是否仍匹配需实跑一次覆盖率验证，掉档则修 runsettings、不下调标准。**基线同样现场复测**（apply 第一步采一次、完成后采一次互比，不预设 2026-11-01 的 97.31% / 88.65% / 95.61%，design D-24）

### 脚本与本机资产

- `scripts/` 下测试脚本与 `.github/workflows/ci.yml` 均按 `tests/` 定位，tests 不搬则不受影响；`mutation-test.py` 的变异路径清单必须同步
- `.tmp-test/realprobe*` 三个探针工程会因搬迁而断（每个 18 行 source-include 指旧路径），属已知且有意延后的代价；`.tmp-test/tstrun` 只引用未搬迁的 tests 工程，**不受影响**
- 本机残留的 `logcat.csproj.user`、根级 `bin/` `obj/` `.vs/` 同批清理，均不入库、不影响仓库

### 在途预发布

- `V0.1.9` 预发布分支与 master 分叉 2 条提交，但其中 BUILDING 的 34 行修正已由 `f6c2925` 回流到 master，因此**实际只剩 3 个文件 5 行（版本号）、零代码**（实测 `git diff master V0.1.9`），把“整树搬迁后回流需 cherry-pick”的推断风险直接消除：apply 前先合并回 master 即可（详见 design D-17；BUILDING 不受影响，唯一副作用是版本号从 0.1.7 变 0.1.9；**该合并不是 fast-forward**——master 独有 `f6c2925`，会生成 1 个 merge commit）

### 依赖

- 不新增 NuGet 依赖、不改 TFM、不改 csproj 的包引用
- `.gitignore` 新增的两条只影响将来 Rust / Node 产物，对现有 C# 构建无作用

### 后续刀次

- 本 change 合入后刀0 才有地方放 `logcat-core/interop/`；`logcat-core/` 与 `logcat-mcp/` 的实体、Cargo workspace、P/Invoke、koffi 接缝一律不在本 change 内
