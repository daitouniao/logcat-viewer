# 工程结构重组探索（log 解析 / android 工具 / core / MCP）

- 状态：**已定稿**（change `restructure-project-layout` 已建、planning 完成，实施见其 `tasks.md`；C2–C7 与 D1–D9 已定案）
- 日期：2026-11-08（第五轮修订：与 change 的 design / tasks 对齐——`icon/` 落点、Remove 块条数、ProjectReference 深度、基线口径；第四轮为 D1–D9 定案；第九轮修订：状态行改为已定稿 + D-53~D-56 落盘）
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

## 0. 一句话结论

本轮定位改为 **Rust 拆分的先行步骤**：重组只搬现有三块（viewer / uicore / android-tools），把根目录收敛为基础设施；`logcat-core/` 与 `logcat-mcp/` 由后续刀次建实体工程，不提前建空目录。目标布局仍是五块同级（方案 A），但 `logcat-core/` 的内涵已变——**Cargo workspace + 一个 C# interop 工程**，不再是「等 core-extraction 再建的 C# Core」。

原四件阻塞项本轮全部拍板：**搬迁时机**（C4 取乙——先回流 V0.1.9 的 docs 修正再重组）、**工程文件名**（C5 取甲——只搬目录不改名）、**interop 是否独立工程**（C6 已展开，推荐甲——独立成工程）、**BUILDING 修正回流**（并入 C4 乙的第一动作）。另定案 **publish 落点 = 仓库根**（§5.4.1）。MCP 与 core 的边界已由 Rust 侧决策定案，不再是待决项。

**无未闭合项**：C1（第一步目录落点）已被维护者确认按推定执行——C2 取甲 + C3 取甲已唯一确定方案 A（§7 C1）。D1–D9 执行级决策同样已定案（§7 D 系列），其中 D2（回流）与 D9（遗留分支清理）已执行。

## 1. 诉求与本轮定位

### 1.1 维护者原话（2026-11-07）

1. 调整工程结构为四块：log 解析界面、android 工具、core、MCP。
2. 第一步：log 解析先放一起，不拆 core，放一个目录。
3. 同级放 android 工具、core、mcp。
4. MCP 计划用 Node.js 实现，调用 core 来完成读取和过滤。

### 1.2 本轮定位（2026-11-08）

维护者明确「这个笔记是 rust 拆分的先行」。据此本轮修订三条口径：

- **core-extraction 作废**：`RUST-CORE-REWRITE-SLICING.md` 已把顺序改为刀0–刀6，不再先抽 C# Core。原话第 2 条「不拆 core」仍然成立，且理由更强——C# 引擎留在 viewer 内直到迁移完成后整批删。
- **MCP 接缝已定**：Rust 编译一个 C ABI cdylib，C# 用 P/Invoke、Node.js 用 koffi 调**同一个 dll**（该笔记 D3/D4）。原 §6 的「长驻 stdio 子进程（方案甲）」被取代，但 CLI 作为对拍与退路保留。
- **`logcat-core/` 由刀0 建**：刀0 要在 C# 侧立分页契约，该契约与 DTO 落在 `logcat-core/interop/`——有实体工程，原「git 不跟踪空目录、提前建要加无意义占位 README」的顾虑自动消失。

## 2. 现状事实（HEAD 核对，2026-11-08）

### 2.1 工程与目录

| 事实 | 依据 |
| --- | --- |
| 三工程 + 一个测试工程 | `logcat.csproj`、`logcat.UiCore/logcat.UiCore.csproj`、`logcat.AndTools/logcat.AndTools.csproj`、`tests/logcat.Tests/logcat.Tests.csproj` |
| **主 exe 的全部源码散在仓库根** | 根目录有 `Controls/`、`Forms/`、`Models/`、`Services/`、`Program.cs`、`frmMain.cs` / `.Load.cs` / `.Sidecar.cs` / `.Designer.cs` / `.resx`、`app.ico`、`Properties/` |
| 工程目录 = 仓库根，故需手工排除兄弟工程 | `logcat.csproj:79`–`:98` **三组共 12 条**（每组 `Compile` / `None` / `EmbeddedResource` / `Content` 四条），分别排除 `tests/**`、`logcat.UiCore/**`、`logcat.AndTools/**` |
| **主工程无显式 `<AssemblyName>`** | `logcat.csproj` 全篇无该项 → 程序集名默认取 csproj 文件名；`logcat.AndTools.csproj:8` 则有显式 `<AssemblyName>logcat-andtools</AssemblyName>` |
| 解决方案未列全 | `logcat.slnx` 只列 `logcat.csproj` 与 `tests/logcat.Tests`；UiCore / AndTools 靠引用带入 |
| `logcat.UiCore` 是两 exe 的共享层 | 承载 `AdbManager`、`Loc` + `LocTable.*`、设备对话框、设备侧 Models；TFM `net10.0-windows` |
| 引擎无直接 UI 引用，但经 `Loc` 传递依赖 | `Services/` 下解析/索引/过滤/崩溃/sidecar 各类不含 WinForms/Drawing，进度与描述文案走 `Loc`（在 UiCore） |
| `logcat.Core` 尚不存在 | 全仓库无该工程 |
| 在途 change 为 0 | `openspec list --json` 返回 `changes: []`；主规范 13 个 capability |
| 版本号唯一来源 | `Directory.Build.props` 的 `<Version>`，master 上为 `0.1.7`；根目录 props 会被其下所有 csproj 继承（含将来 interop 工程） |
| `testdata/` 只有本机大样本 | `big_test.log` 1,073,855,630 B、`m_test.log` 38,968,063 B；`git ls-files testdata` 为空，靠 `.gitignore:64` 的 `*.log` 忽略（**目录本身未被忽略**）；无任何测试引用它 |
| `.gitignore` 缺 native / Node 模式 | 有 `:17 [Ll]og/`、`:29 *.user`、`:44 *.dll`、`:64 *.log`、`:126 .tmp-test/`；**无 `target/`、无 `node_modules/`** |
| CI 无 Rust 工具链 | `.github/workflows/ci.yml` 只有 `windows-latest` + `setup-dotnet` + build/test；无其他 workflow |

### 2.2 分支 / worktree / tag（本轮刷新，原表已过时）

| 事实 | 依据 |
| --- | --- |
| 主工作树在 master | HEAD `f6c2925`（本轮回流提交） |
| **V0.1.8 已完全合入 master** | `git rev-list --left-right --count master...V0.1.8` = **28 / 0**；tag `V0.1.8` 发布于 2026-11-07 19:59 |
| **活跃预发布分支是 V0.1.9** | `master...V0.1.9` = **0 / 2**（V0.1.9 = master + 2 条）；tag `V0.1.9-pre1` 发布于 2026-11-07 22:16 |
| V0.1.9 的两条独有提交 | `95c6b2b` 版本号同步 5 处（`Directory.Build.props` + 双 README）；`354c301` **BUILDING 发布命令修正**，`git show --stat` 确认只碰 `docs/BUILDING.md` 一个文件 |
| **该修正已回流 master**（2026-11-08） | `git checkout 354c301 -- docs/BUILDING.md` → 提交 `f6c2925`；`git diff origin/V0.1.9 master -- docs/BUILDING.md` 为空（不含 `95c6b2b` 版本号） |
| 共三棵 worktree | 主工作树（master）；V0.1.9 的独立 worktree（仓库同级目录 `logcat-wt-V0.1.9`，**仍在用，不动**）；WorkBuddy 工具目录下的一棵（`workbuddy/origin-v0-1-8-*` 分支，不归本项目清理） |
| 遗留分支已收尾（2026-11-08） | `V0.1.8` 分支**无 worktree**（原记录有误），已 `git branch -d` 删除（was `c7ed6e2`，tag 保留）；`wt/sidecar-cache-index` 同日删除（was `4bd3de7`，`master...` = **17 / 0** 已合入） |

## 3. 依赖关系

`UiCore` 被两个 exe 同时依赖，因此不能简单按功能块只留四个目录：

```
logcat-viewer ──> logcat.UiCore <── logcat-android-tools
       │                                  │
       └──────────── tests ───────────────┘

logcat-viewer ──> logcat-core/interop ──> logcat_core.dll (Rust)
                                              ^
logcat-mcp (Node.js + koffi) ─────────────────┘
```

若把 `UiCore` 塞进 android 工具目录，依赖方向会变成 viewer → android 工具，语义倒置；塞进 viewer 目录则 android 工具 → viewer，同样倒置。故保留独立共享块。

**interop 的依赖方向必须单向**，详见 §4.4——这是本轮新增的关键约束。

## 4. 目标布局

### 4.1 方案 A：五块同级（推荐，本轮细化）

```
<仓库根>/
├─ logcat-viewer/           主 exe（工程文件名与程序集名都不变）
│  ├─ logcat.csproj         -> logcat.exe
│  ├─ Program.cs  frmMain*.cs  frmMain.resx  Properties/
│  └─ Controls/  Forms/  Services/  Models/
├─ logcat-uicore/           logcat.UiCore.csproj（共享层，两 exe 依赖）
├─ logcat-android-tools/    logcat.AndTools.csproj -> logcat-andtools.exe
├─ icon/app.ico             仓库级图标资源（两个 exe 都向上引用 `..\icon\app.ico`，D-12）
├─ logcat-core/             <- 刀0 建（有实体工程，不是空目录）
│  ├─ interop/              logcat.Interop.csproj：刀0 只有契约 + DTO，刀2 加 P/Invoke（C6）
│  ├─ Cargo.toml            <- 刀1 加（workspace）
│  ├─ crates/{core,cabi,cli}
│  └─ golden/               入库小样本（*.log 全局忽略，需开例外）
├─ logcat-mcp/              <- 刀4 建
│  ├─ package.json  src/
│  └─ prebuilds/            CI 产出的 dll，不入库
├─ tests/logcat.Tests/      **不搬**
├─ testdata/                本机大样本，不搬
├─ publish/Release/         发布产物，**留仓库根**（不入库，§5.4.1）
├─ scripts/  docs/  openspec/  sample/
├─ Directory.Build.props  NuGet.config  logcat.slnx
└─ README.md  README.zh-CN.md  LICENSE  DISCLAIMER.md  THIRD-PARTY-NOTICES.md
```

重组这一步**只搬前三块**；`logcat-core/` 由刀0 建（interop 工程是实体），`logcat-mcp/` 由刀4 建。理由：不改依赖方向；不建无实体的空目录。`publish/` 在根、不在任何工程目录内——重组对它**不产生新动作**（理由见 §5.4.1）。

### 4.2 其余候选（原评估保留）

- **方案 B：四块同级，UiCore 并入 android 工具**——目录数贴合原话，但形成 viewer → android 工具的反向依赖。不推荐。
- **方案 C：四块同级，UiCore 暂留仓库根**——只搬 viewer 与 android 工具，过渡期根目录最不整齐，后续还要再搬一次。仅作兜底。
- **方案 D：只搬 log 解析界面**——搬迁次数最少，但与「同级放 android 工具」的原话不符。

### 4.3 五条布局决策

| 决策 | 内容 | 理由 |
| --- | --- | --- |
| **只搬目录，不改工程文件名** | `logcat.csproj` 移入 `logcat-viewer/` 后仍叫 `logcat.csproj` | 主工程无显式 `<AssemblyName>`，程序集名默认取文件名。改名会静默改程序集名 → `coverlet.runsettings:10` 的 `[logcat]*` 匹配空、**覆盖率归零**，且 `logcat.exe` 变名（`docs/BUILDING.md` 产物断言、README 均按此名）。要改必须同批显式钉 `<AssemblyName>logcat</AssemblyName>`，属配置变更，见 C5 |
| **`tests/` 不搬** | 测试工程留在 `tests/logcat.Tests/` | `logcat.Tests.csproj:37` 的 `AssemblyMetadata("RepoRoot")` 用 `$(MSBuildProjectDirectory)\..\..` 算根；`TestHelpers.cs:127`–`:129` 还有 5 级 bin 回溯到 `tests/logcat.Tests/tmp`。两处都假设 tests 深度为 2，搬了要改两处、收益为零 |
| **顶层同级，不加 `src/`** | 五块直接放仓库根 | scripts/docs/openspec/tests 已在根，加 `src/` 只包工程会造成一半在里一半在外，且所有相对路径改动量翻倍 |
| **`logcat-core/` 由刀0 建** | 重组不建 core 目录 | 刀0 的 interop 工程是实体，空目录顾虑消失；重组与刀0 的职责边界也因此清楚 |
| **C# 引擎留在 viewer，不做 core-extraction** | `Services/` 下解析/索引/过滤/崩溃/sidecar 各类随 viewer 搬，迁移完成后整批删 | 抽出去要先拆 `Loc`（187 处 / 19 文件），而 Rust 接管后该前置自动消失；留在 viewer 里将来一样删得掉 |

### 4.4 interop 工程的边界约束（关键，决定会不会成环）

`logcat-core/interop/` **只装分页契约 + DTO + P/Invoke 签名 + native 加载，绝不装 C# 引擎适配**：

```
刀0:  viewer ──> interop（接口 / DTO）
        └─────> viewer 内的进程内适配 ──> LogDocument / FilterEngine
                                          （临时实现，刀2 删）

刀2:  viewer ──> interop（接口 + native 实现）──> logcat_core.dll
      进程内适配删除，viewer 一行不动
```

若把刀0 的进程内适配也放进 interop，它就要反向引用 viewer 的引擎 → **循环依赖** → 逼着把引擎搬出 viewer → 就是 core-extraction → 又把 `Loc` 拆分拽回来。这条边界必须守住。

顺带一个正面效应：interop 不引用 UiCore、只回错误码与事件码，**天然不拖 WinForms**；码 → 文案的映射留在 viewer，正好兑现 `RUST-CORE-REWRITE-SLICING.md` 里「`Loc` 拆分前置随引擎外迁而消失」。

是否独立成工程（第 6 个）见 C6。

## 5. 搬迁的路径耦合清单

搬迁不是纯 `git mv`。以下点位会直接打断构建、测试或发布，逐项都要进 tasks。

### 5.1 工程与解决方案

| 位置 | 耦合内容 | 处理 |
| --- | --- | --- |
| `logcat.csproj:23`–`:27` | `<None Include>` 指向仓库根的 `README.md`、`README.zh-CN.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`DISCLAIMER.md`（各带 CopyToOutput / CopyToPublish） | 全部改 `..\` 相对路径，并**补显式 `Link`**（只改 `..\` 会被拍平到输出根，层级静默丢失——design D-11 探针实测） |
| `logcat.csproj:29` / `:31` | `<None Include="sample/**">`、`<None Include="docs/images/main.gif">` | 同上并补 `Link`（`sample\%(RecursiveDir)…` / `docs\images\main.gif`）；**这两项决定发布包内容，删改即断包** |
| `logcat.csproj:5` | `<ApplicationIcon>app.ico</ApplicationIcon>` | 图标移入根级 `icon/`，改为 `..\icon\app.ico`（design D-12） |
| `logcat.csproj:74` | `<ProjectReference Include="logcat.UiCore\logcat.UiCore.csproj">` | 改 `..\logcat-uicore\...` |
| `logcat.csproj:79`–`:98` | **三组共 12 条**（每组 `Compile` / `None` / `EmbeddedResource` / `Content` 四条，分别排除 `tests/**`、`logcat.UiCore/**`、`logcat.AndTools/**`） | **全删**（每组四条必须同时删）——工程目录不再是仓库根，默认 glob 不会捞到兄弟工程。这是搬迁的**收益**，不是成本 |
| `logcat.AndTools.csproj:10` | `<ApplicationIcon>..\app.ico</ApplicationIcon>` | 改 `..\icon\app.ico`——该目录只改名、**深度未变，仍是一个 `..`**（写成 `..\..\` 会指向仓库根上一级，且 SDK 只告警不报错，D-12） |
| `logcat.AndTools.csproj:33` | `<ProjectReference Include="..\logcat.UiCore\...">` | 改 `..\logcat-uicore\...`（深度未变） |
| `logcat.UiCore.csproj` | 无外部路径、无图标、无构建事件 | 只改目录名 |
| `tests/logcat.Tests.csproj:25`–`:26` | 两条 ProjectReference 指向 `..\..\logcat.csproj` 与 `..\..\logcat.AndTools\...` | 改新路径；`:37` 的 RepoRoot 计算因 tests 不搬而不变 |
| `InternalsVisibleTo` | `logcat.csproj:103`、`logcat.UiCore.csproj:23`、`logcat.AndTools.csproj:38` 均为 `Include="logcat.Tests"` | 目标是测试程序集名，**不受搬迁影响** |
| `logcat.slnx` | 只列两个工程 | 列全：viewer / uicore / android-tools / tests，刀0 后加 interop |
| `Properties/PublishProfiles/FolderProfile.pubxml:7` | `<PublishDir>publish\Release\V0.1.4</PublishDir>`（工程相对） | 该文件**未入库**（`.gitignore:88 *.pubxml`；`git ls-files Properties` 为空）；搬进 `logcat-viewer/Properties/` 后改 `..\publish\Release\V0.1.9`——**按合并后的当前版本落具体值**，可执行配置里不留占位符（tasks 3.7），与 §5.4.1「产物留仓库根」一致 |
| 三个 csproj 的构建事件 | `PreBuildEvent` / `PostBuildEvent` / `<Target>` / `BeforeBuild` / `AfterBuild` | **均不存在**，无需处理；将来拷 dll 的步骤不要放成会沿 ProjectReference 传染的属性 |

### 5.2 测试侧（最容易红的一批）

| 位置 | 耦合内容 | 处理 |
| --- | --- | --- |
| `LocTableTests.cs:1065`、`CiLocaleAssertionTests.cs:330` | 同一句硬断言：`Assert.True(File.Exists(Path.Combine(root!, "logcat.csproj")), ...)` | **移动后必红**；改为按新路径判定或搜索定位 |
| `LocTableTests.cs:1048`–`:1049` | `IsLocTablePath` 前缀判定含 `"logcat.UiCore/Services/LocTable."` | 改 `"logcat-uicore/Services/LocTable."` |
| `LocTableTests.cs:166`–`:168` | 三条 `[InlineData("logcat.UiCore/Forms/CommandEditDialog.cs", "HINT")]` 等 | 改目录前缀 |
| **`LocTableTests.cs:1034`–`:1044`** | `EnumerateSourceFiles` 走**整个仓库根**，排除项只有 `tests/`、`obj/`、`bin/`、`publish/`（`:1039`–`:1040`，另有 `Contains("/obj/")` / `Contains("/bin/")` 按任意深度匹配，故搬迁后 `logcat-viewer/obj/` 一类自动被排除），**不排除 `.tmp-test/`、`sample/`、`docs/`** | 本轮新增风险：`logcat-core/interop/*.cs` 与将来 `logcat-mcp/` 下的源文件会被当本地化调用点扫描。**D6 定**：只补 `.tmp-test/`（11 个探针工程有 `.cs`，今天就在被扫，只是暂时没翻车）；`sample/`、`docs/` 无 `.cs` 不补；interop 侧确认无 `Loc` 调用即可 |
| `CiLocaleAssertionTests.cs:151` | `Path.Combine(repoRoot, "tests", "logcat.Tests")` | tests 不搬则不变 |
| `CiLocaleAssertionTests.cs:61`/`:63`/`:65` | 白名单键为字面量 `"tests/logcat.Tests/CommandStoreTests.cs: …"` | tests 不搬则不变 |
| `TestHelpers.cs:127`–`:129` | 5 级 bin 回溯 + `"tests", "logcat.Tests", "tmp"` | tests 不搬则不变 |
| `coverlet.runsettings:10`/`:30` | `<Include>[logcat]*</Include>`、`[logcat]logcat.Forms.*` 等按**程序集名**过滤 | 只搬目录不改名则不变；改名则覆盖率归零（C5） |
| `coverlet.runsettings:33` | `ExcludeByFile` 用 `**/Forms/*.cs`、`**/frmMain.cs`、`**/Services/Loc.cs` 等 `**` 前缀 glob | **推断**移动后仍匹配（未跑覆盖率验证，列 §8） |

### 5.3 脚本、CI 与文档

| 位置 | 耦合内容 |
| --- | --- |
| `.github/workflows/ci.yml:34`/`:58`/`:67` | `dotnet build/test tests/logcat.Tests/...`、`-Path tests`——tests 不搬则不变；但 CI 必须在仓库根执行才读得到 `NuGet.config`（已用 `<clear/>` 覆盖全局源），这条前提不变 |
| `scripts/run-tests.sh:11`/`:17`/`:19`/`:23` | `TEMP` 指向 `tests/logcat.Tests/tmp`、测试工程路径、runsettings、`tests/coverage-report.py`——tests 不搬则不变 |
| `scripts/run-tests-en-us.sh:51`/`:55` | 测试工程路径，同上 |
| `tests/coverage-report.py:19`–`:21`/`:29`/`:31`/`:181` | 以自身位置算 REPO，再拼 `tests/logcat.Tests/...`，同上 |
| **`scripts/mutation-test.py:649`/`:670`/`:695`** | 变异元组用 `"Services/LogParser.cs"`、`"Services/LogDocument.cs"`、`"Services/FilterEngine.cs"`、`"Services/CrashAggregator.cs"`、`"Services/CrashExtractor.cs"`、`"Services/FileProxyService.cs"`、`"Services/FileProxyClient.cs"`、`"Models/FilterSpec.cs"` 共 8 条现役路径 → 移动后全部要加 `logcat-viewer/` 前缀。**另有 4 条已 stale**（`Services/AdbManager.cs`、`AppSettings.cs`、`CommandStore.cs`、`FavoritesStore.cs` 早已在 UiCore），顺手清理 |
| `scripts/mutation-test.py:53` | `RUNNER = .tmp-test/tstrun`——不入库资产，见 §5.5 |
| `scripts/gen_test_log.py:248` | 默认输出 `testdata/big_test.log`，按 CWD 解析 → 不受搬迁影响 |
| `docs/BUILDING.md:75`/`:79`/`:95`/`:99` | 两条 publish 命令的工程路径；产物验证清单（`logcat.exe` / `logcat-andtools.exe` / `logcat.UiCore.dll` / `docs/images/main.gif` / `sample/fileproxy/`） |
| `README.md:163` / `README.zh-CN.md:167`、`README.md:278` | publish 命令与产物名；两份 README 的结构章节 |
| `docs/TESTING.md`、`docs/UT-AUDIT.md` | 测试命令、覆盖率命令、源码扫描路径说明 |
| `.gitignore` | **重组同批**补**泛化** `target/` 与 `node_modules/`（**D7 定**：不用 `logcat-core/target/` 限定写法）；`*.log` 全局忽略会挡住 golden 小样本入库，需开 `!` 例外或换扩展名（刀1 处理）；`*.dll` 全局忽略与「预编译 dll 不入库、CI 产出」的口径一致，不用开例外 |

### 5.4 PublishDir 口径与 publish 落点（前置项 + 落点定案）

`-p:PublishDir` 的相对路径由 SDK 按**各工程目录**解析，不是按 CWD。V0.1.9 的 `354c301` 把 `docs/BUILDING.md` 改成绝对路径口径（`<仓库根>\publish\Release\...`）并加 `-p:DebugType=none -p:DebugSymbols=false`。

**前置已闭合（2026-11-08）**：该修正已回流 master（提交 `f6c2925`，仅 `docs/BUILDING.md`，不含 `95c6b2b` 版本号），`git diff origin/V0.1.9 master -- docs/BUILDING.md` 为空。搬迁后工程目录普遍下移一层，这条口径只会更要紧，但重组**不再需要二次改写文档**。

#### 5.4.1 落点定案：publish 留仓库根（C7）

**结论：可以，且应当留根**——发布目录跟仓库，不跟工程目录。

| 依据 | 事实 |
| --- | --- |
| 入库安全与位置无关 | `.gitignore:84` `[Pp]ublish/` 不带前导斜杠，任意深度都忽略 |
| 现有命令本就按 CWD 在仓库根执行 | `docs/BUILDING.md` 的清目录 / `Compress-Archive` / `Test-Path` 全是 `publish\Release\...` 相对写法，前提就是 CWD = 仓库根 |
| README 的开发命令天然落根 | `README.md:163` / `README.zh-CN.md:167` 用 `-o publish\fd`（`-o` 相对 CWD）→ 重组只改工程路径，产物路径不动 |
| 产物是仓库级资产 | 两个 exe **先后 publish 到同一目录**再压 zip（`docs/BUILDING.md`），单个工程目录承载不了 |
| 跟工程走会直接断包 | 相对 `-p:PublishDir` 按工程目录解析 → 分别落进 `logcat-viewer/publish/` 与 `logcat-android-tools/publish/`，`logcat-andtools.exe` 从包里消失，正是 `354c301` 修的那类事故 |
| 跟工程走还有构建噪声 | publish 落在工程目录内会被 SDK 默认 `None **/*` glob 整个收编（`DefaultItemExcludes` 不含 `publish/`）**（推断，未实测）** |

做法（三项，重组不新增长任务）：

1. `docs/BUILDING.md` 沿用回流后的绝对路径口径（`<仓库根>\publish\Release\...`）——重组后原样可用，**不为重组再改一次**。
2. `Properties/PublishProfiles/FolderProfile.pubxml` 未入库（`.gitignore:88 *.pubxml`），搬进 `logcat-viewer/` 后改成 `..\publish\Release\V0.1.9`（按当前版本落具体值）；tasks 3.7 只验证它不被 git 跟踪，**值对不对靠发布实跑兜住**。
3. 目录名里的版本号是**文档明示的占位示例**（`docs/BUILDING.md:51`「以 V0.1.4 为例，先把版本号换掉」），发版时手改；**不引入**用 MSBuild 属性自动带版本的做法——会新增一个属性面，与「版本号单一来源 + 发版同步 5 处」的既有口径重叠。

### 5.5 不入库但会断的本机资产

| 资产 | 现状 | 处理 |
| --- | --- | --- |
| `.tmp-test/` 11 个探针工程 | 4 个用 `<ProjectReference Include="..\..\logcat.csproj">`（BenchLog / BlockScanBench / ChunkProbe / LoadProbe）；`tstrun` 用**绝对路径**引用测试工程；3 个 realprobe 变体各有 18 条 `<Compile Include="..\..\Services\...">`；生成器 `.tmp-test/gen_probe_csproj.py` 按字符串替换重写路径 | 全部会断，但分两类处理：**4 个单引用探针同批改到 `..\..\logcat-viewer\logcat.csproj`**（一行 × 4，design D-36）；**3 个 realprobe 变体留刀1**（各 18 行 source-include 需按现目录重写，它需要 C# oracle host，形状正好复用）。`tstrun` 引用的是未搬迁的 tests 工程，不受影响 |
| realprobe 系列已 stale | `realprobe` / `realprobe-main` 各 5 个目标缺失（`Loc.cs`、`LocTable.*.cs`、`StartupLog.cs`、`AppSettings.cs`、`AppInfo.cs` 已迁 UiCore）；`realprobe-wt` 指向的整棵 worktree 已不存在；探针调用的 `LogDocument.Build(path, join: true)` 签名早已移除 | 刀1 重建时按现签名与现目录重写，不沿用旧 csproj |
| `.tmp-test/BenchLog/Program.cs` | 硬编码本机绝对路径指向 `testdata/big_test.log` | 不入库，本机自用；重建时改相对定位 |
| `logcat.csproj.user` | 含本机绝对路径的发布 profile id | `.gitignore:29 *.user` 已忽略，不入库 |

## 6. MCP 与 core 的接缝（已定，不再是待决项）

原 §6 的甲/乙/丙/丁四方案由 `RUST-CORE-REWRITE-SLICING.md` 的 D3/D4 定案取代：

| 项 | 定案 |
| --- | --- |
| 形态 | Rust 编译**一个 C ABI cdylib**（`crates/cabi` → `logcat_core.dll`） |
| C# 宿主 | P/Invoke；仓库已有先例（`Controls/LogListView.cs:200`、`logcat.AndTools/WakeUp.cs:30` 均 DllImport user32） |
| Node 宿主 | **koffi 调同一个 dll**（方案甲），不引 napi-rs 的 Node ABI × 平台构建矩阵 |
| 长任务 | Node 侧必须走 koffi async 或 worker_threads，否则堵死事件循环 |
| 会话生命周期 | **MCP 必须长驻**（索引生命周期跟进程走）——原 §6 的这条约束原样保留 |
| 退路 | `crates/cli` 与 core 同源；koffi 若不可用，Node 改拉 stdio 子进程，core 一行不改 |
| panic 边界 | 每个 `extern "C"` 薄包装 `catch_unwind`；koffi 无法替宿主捕获 Rust panic |

工程侧后果：`logcat-mcp/` 是 npm 包（`package.json` + `src/` + CI 产出的 `prebuilds/`），需要 `.gitignore` 补 `node_modules/`；dll 不入库、由 CI 构建后拷入发布目录与 npm 包。分发实账（CI 加 Rust 工具链、双包各补产物断言、fdd 体积翻倍到 3 倍、版本号第 6 处、覆盖率口径）见 `RUST-CORE-REWRITE-SLICING.md` §7，本笔记不重复。

原 §6 需一并确认的既有口径不变：输出固定英文 JSON、只读、显式绝对路径、单次输出上限。

## 7. 决策记录与待澄清

> 编号变更说明：原 C4（MCP 与 core 的调用边界）已由 D3/D4 定案，移入 §6；原 C5（时序）本轮改为 C4。
> 2026-11-08 维护者拍板：C1 确认按方案 A；C2 / C3 / C4 / C5 同意原偏向（C4 指定「可以先回流」），C6 要求展开后记录（本版已展开），C7 为 publish 落点，随本轮提问定案；D1–D9 执行级决策另行定案（见本节末尾）。

### C1 第一步的目录落点（**已确认：方案 A**，2026-11-08）

- **为什么需要澄清**：决定 tasks 里的路径与验收线；本轮已细化为「只搬三块、core 由刀0 建」。
- **有哪些方案**：方案 A（§4.1 细化版）/ B / C / D（§4.2）。
- **推定结论（已确认）**：A。依据是 C2 取甲（UiCore 独立第五块）与 C3 取甲（顶层同级、不加 `src/`）只能与方案 A 组合——B 倒置依赖（§3），C 会把 UiCore 留在根，D 根本不搬 android 工具，三者都与 C2 甲冲突。维护者 2026-11-08 确认按此执行。

### C2 `logcat.UiCore` 的归属与目录命名（**已定：甲**，2026-11-08）

- **为什么需要澄清**：它是两 exe 的共享层，不在原话的四块清单里但必须有落点；目录命名风格决定要改多少处测试字面量。
- **有哪些方案**：甲 独立第五块、统一连字符目录名 `logcat-uicore/`（要改 `IsLocTablePath` 前缀 + 3 条 InlineData）；乙 独立第五块但 UiCore / AndTools 目录名不动，只把根级 exe 收进 `logcat-viewer/`（最小 diff，根目录风格不统一）；丙 并入某一 exe（依赖倒置，§3 已否）。
- **定**：甲（维护者 2026-11-08 同意）。四处字面量改动量很小，换来长期一致的命名。
- **结论**：UiCore 独立第五块，目录统一连字符名 `logcat-uicore/`；`logcat.AndTools/` 同步改 `logcat-android-tools/`（目录改名、工程文件名不变，见 C5）。
- **已知代价**：`LocTableTests` 4 处字面量（`IsLocTablePath` 前缀 + 3 条 `[InlineData]`）同批改，已列 §5.2。

### C3 是否引入 `src/`（**已定：甲**，2026-11-08）

- **为什么需要澄清**：影响所有构建、测试、文档路径，也决定 Node 工程怎么摆。
- **有哪些方案**：甲 顶层同级；乙 工程统一放 `src/`。
- **定**：甲（维护者 2026-11-08 同意）。理由见 §4.3。

### C4 搬迁时机与前置回流（**已定：乙**，2026-11-08「可以先回流」）

- **为什么需要澄清**：V0.1.9 预发布在途（`V0.1.9-pre1` 于 2026-11-07 22:16 打 tag），其独立 worktree 承载后续 bug 修复；整树搬迁会加大修复回流的冲突面。另有一条 BUILDING 修正只在 V0.1.9 上、未回流 master（§5.4）。
- **有哪些方案**：甲 等 V0.1.9 转稳定（tag `V0.1.9`）后再重组；乙 先回流 `354c301` 的 docs 修正到 master（不含版本号提交），随即重组，后续 V0.1.9 若有代码修复按单文件 cherry-pick 回流；丙 确认 V0.1.9 不再加修后立即重组。
- **定**：乙。V0.1.9 只领先 2 条且都是 docs/版本号，代码修复概率与冲突面都有限；但「rename 检测能跟上 cherry-pick」属推断（§8）——首个 cherry-pick 即验证点，失败则把单文件修复手工重放到新路径（量级可接受）。
- **回流动作（已完成 2026-11-08）**：`git checkout 354c301 -- docs/BUILDING.md` → 提交 `f6c2925`；只含该文件，**不含 `95c6b2b` 的版本号提交**。校验：`git diff origin/V0.1.9 master -- docs/BUILDING.md` 为空。
- **附带清理（已完成 2026-11-08）**：`git branch -d V0.1.8`（was `c7ed6e2`，tag 保留；**该分支本无 worktree**，原记录有误）、`git branch -d wt/sidecar-cache-index`（was `4bd3de7`，相对 master 17/0 已合入）。`logcat-wt-V0.1.9` 属在途预发布，**不动**。

### C5 工程文件名是否改（**已定：甲**，2026-11-08；属配置变更）

- **为什么需要澄清**：主工程无显式 `<AssemblyName>`，程序集名默认取 csproj 文件名。改名会连带改程序集名，打断覆盖率过滤与产物名断言。
- **有哪些方案**：甲 只搬目录、文件名保持 `logcat.csproj`（程序集与 exe 名不变）；乙 改名 `logcat.Viewer.csproj` 并同批显式加 `<AssemblyName>logcat</AssemblyName>` + `<RootNamespace>logcat</RootNamespace>`；丙 改名且接受程序集名变化，同批改 coverlet 过滤与全部产物断言。
- **定**：甲（维护者 2026-11-08 同意）。乙 的额外收益只有命名整齐，代价是一处配置变更多加一个出错面；丙 不取。
- **因此不动的东西**：`coverlet.runsettings` 的 `[logcat]*` 与 `[logcat]logcat.Forms.*` 等过滤、`docs/BUILDING.md` 的 `logcat.exe` / `logcat-andtools.exe` 产物断言、README×2 的产物名。

### C6 interop 是否独立成工程（**已展开**；阻塞刀0，不阻塞重组）

#### C6.1 这个容器到底装什么（决定「工程」是否值当）

| 内容 | 刀0 | 刀2 起 | 说明 |
| --- | --- | --- | --- |
| 分页契约（`open` / `page` / `locate` / `full_text` / `export` / `cancel` + `appended` 事件） | 有 | 不变 | 纯接口，无实现体 |
| DTO / 结构：页缓冲布局、tag 字典、错误码表、事件码 | 有 | 不变 | 定长数值列 + 变长字符串区（`RUST-CORE-REWRITE-SLICING.md` §4.4） |
| P/Invoke 签名 + native 加载（`NativeLibrary` 解析路径） | 无 | 有 | 刀2 才有实体 |
| **进程内适配**（把 `LogDocument` / `FilterEngine` 包成契约实现） | **有** | **删** | **必须落 viewer 内**，见 §4.4 |
| 码 → 文案映射 | 无 | 无 | 留 viewer（`Loc`） |

关键量：刀0 的 interop 只有「接口 + record + 一张错误码表」，**没有任何可执行逻辑**；刀2 才长出 P/Invoke。所以「独立工程」买到的是**边界容器**，不是复用单元——消费者只有 `logcat-viewer` 与（可能的）`logcat.Tests`：`logcat-andtools` 不碰，Node 侧走 koffi 直连 dll 也不碰。这一点必须诚实列出，它是乙的立论基础。

#### C6.2 四个方案

| 方案 | 落点 |
| --- | --- |
| 甲（原偏向） | 独立工程 `logcat-core/interop/` |
| 乙 | 同工程子目录 `logcat-viewer/Services/Core/`，刀2 再拆 |
| 丙 | 不拆，契约与 native 调用长期留 viewer |
| **丁（本轮新增）** | 独立工程，但放根级 `logcat-interop/`，与五个块同级；`logcat-core/` 保持纯 Rust |

#### C6.3 判定维度对比

| 维度 | 甲 | 乙 | 丙 | 丁 |
| --- | --- | --- | --- | --- |
| **边界能否在编译期强制** | **能**：新工程可设 `net10.0`（不带 `-windows`、不 `UseWindowsForms`），`using System.Windows.Forms` 或引用 `Loc` 直接编译失败 | 不能：`logcat.csproj:6`/`:8` 是 `net10.0-windows` + `UseWindowsForms=true`，随手引用就穿 | 不能 | 同甲 |
| 刀2 的 diff 面 | interop 内换实现 + viewer 改调用；UI 代码不动 | 「先搬家再换实现」两段，与 UI 改动混在同一 csproj | 无边界可换 | 同甲 |
| 覆盖口径 | 程序集名以 `logcat` 开头（`logcat.Interop`）会被 `[logcat]*` 纳入；但 interop 内是接口 + record + extern：**extern 声明无 IL 体、不进分母**，record 自动属性会被计入（`SkipAutoProps=false`）→ 影响≈0（**推断**，刀0 实测） | 不新增程序集，口径不变 | 同乙 | 同甲 |
| `LocTableTests` 源码扫描 | `EnumerateSourceFiles` 扫全仓（排除项仅 `tests/ obj/ bin/ publish/`），interop 的 `.cs` 会被当本地化调用点扫；无 `Loc` 调用即无影响，但要确认（§5.2 已列） | 本就在扫描范围 | 同乙 | 同甲 |
| 工程数 / 解决方案 | 4 → 5，slnx 须列全（§5.1） | 4 不变 | 4 不变 | 4 → 5 |
| `InternalsVisibleTo` | 按需补 `logcat.Tests`（与现有三处同口径） | 沿用 viewer 的 | 同乙 | 同甲 |
| 目录语义 | `logcat-core/` = core 边界，C# 契约宿主与 `crates/` 同住 | — | — | 语言分开：C# 工程全在根同级，`logcat-core/` 到刀1 才出现且纯 Rust |
| native dll 落点 | 天然归属（拷 dll / `SetDllImportResolver` 有家），但拷 dll 不能写成沿 ProjectReference 传染的属性（§5.1 末行） | 混在 viewer 的 csproj 与产物断言里 | 同乙 | 同甲 |
| 依赖成环风险（§4.4） | 四方案相同，靠「适配不进契约容器」这条纪律守 | 同 | 同 | 同 |

#### C6.4 推荐：甲

理由（按权重）：

1. **只有独立工程能把「interop 不拖 WinForms、不反向引用引擎」变成编译错误**；乙丙都只是口头纪律，而 §4.4 的边界正是整套刀0–刀2 能否成立的前提。
2. 刀2 的验收「UI 不动、行为不变」（`RUST-CORE-REWRITE-SLICING.md`）要求「viewer 一行不动」这句话物理成立，只有甲/丁能给。
3. `logcat-core/` 因此有实体，§4.1 不必留空目录。

甲成立需在刀0 tasks 里钉死四个子项：

- **工程文件名 `logcat.Interop.csproj`**（目录 `interop/`）。不能叫 `interop.csproj`——程序集名默认取文件名，`interop.dll` 既不匹配 `[logcat]*` 覆盖率口径，宿主里也看不出归属；`logcat.Core.Interop` 亦可，但 `logcat.Interop` 更短且已表达归属。
- **TFM `net10.0`**（不带 `-windows`、不 `UseWindowsForms`）——这是边界本身，不是风格。
- 其余属性与兄弟工程同口径：`Nullable` / `ImplicitUsings` 开、`IncludeSourceRevisionInInformationalVersion=false`、版本号继承根 `Directory.Build.props`。
- 进程内适配落 `logcat-viewer/Services/Core/`，**不进 interop**（§4.4）。

不取丁：它比甲多开一块根级目录，只换来「`logcat-core/` 不混装 C#」；而 `logcat-core/` 的语义本来就是 core 边界（刀1 起里面还有 `crates/`、`golden/`），契约宿主放进去是自洽的。若将来判定混装不整洁，改目录是一次 `git mv` + 两处引用，代价极低——**丁保留为甲的后备，现在不必选**。

不取乙丙：达不到上面第 1、2 条。

**注意（四方案通用）**：进程内适配都不能与契约同放一个会被引擎反向引用的位置，否则成环（§4.4）。

### C7 publish 落点（**已定：仓库根**，2026-11-08）

- **结论**：`publish/Release/` 留仓库根，不跟工程目录走；依据与做法见 §5.4.1。
- **为什么需要澄清**：工程目录下移一层后，相对 `-p:PublishDir` 的解析结果会变，落点不定就会重演 `354c301` 的断包事故。
- **重组成本**：零——回流后的 `docs/BUILDING.md` 绝对路径口径原样可用，`publish/` 已在 `.gitignore:84` 覆盖内。

### D1–D9 执行级决策（**2026-11-08 全部按推荐定案**）

| # | 决策点 | 结论 | 落在哪 |
| --- | --- | --- | --- |
| **D1** | 搬迁批次 | **一次搬完三块**（viewer / uicore / android-tools 同批），不分两批。理由：§5.1 的引用修正是一次性动作，分批只会多一轮改写与多一轮基线验收 | 重组 tasks 面 |
| **D2** | C4 回流 | **已执行**：提交 `f6c2925`，只含 `docs/BUILDING.md`（§2.2、§5.4） | 已入库 |
| **D3** | change 标识 | 名称 `restructure-project-layout`；worktree 分支 `wt/restructure-project-layout`；合入目标 **master**；**worktree 在 apply 时建**（建时需当轮授权） | tasks 首条 |
| **D4** | 验收口径 | a) 用例数**以 apply 第一步实测为基线**（文档记 848、`V0.1.9-pre1` tag 记 852，**不预设**），`LocTableTests` 4 处路径字面量**改断言**、不改实现<br>b) 覆盖率三项**持平**：基线同样在 apply 第一步现场采集（第五轮修订，不预设 2026-11-01 的 97.31% / 88.65% / 95.61%）；若因 `ExcludeByFile` 的 `**` glob 失配掉档，**修 `coverlet.runsettings`**，不下调标准<br>c) 发布验证只跑两条 `dotnet publish` + 产物 `Test-Path` 清单，**不压 tag、不发版** | 重组验收 |
| **D5** | CI 加不加 `dotnet build logcat.slnx` | **不加进 CI**；改为 tasks 里一条本机验证「`dotnet build logcat.slnx` 通过」（CI 15 min 预算紧、slnx 变动极低频） | tasks |
| **D6** | 源码扫描排除项 | 只补 `.tmp-test/`；`sample/`、`docs/` 无 `.cs` 不补；interop 侧确认无 `Loc` 调用（§5.2） | tasks |
| **D7** | `.gitignore` | **重组同批**补**泛化** `target/` 与 `node_modules/`（不用 `logcat-core/target/` 限定写法） | 重组 |
| **D8** | interop 两个子项 | 程序集名 `logcat.Interop`；**不显式排除**覆盖率（extern 无 IL 体、影响≈0，实测后再定） | 刀0 |
| **D9** | 遗留分支清理 | **已执行**：删除 `V0.1.8`（was `c7ed6e2`）与 `wt/sidecar-cache-index`（was `4bd3de7`）；`logcat-wt-V0.1.9` 不动 | 已入库 |

## 8. 显式猜测

| 内容 | 标注 |
| --- | --- |
| 目录搬迁对构建时长无实质影响 | **推断**，未实测 |
| 搬迁后 `coverlet.runsettings` 的 `**/Forms/*.cs` 等 `ExcludeByFile` glob 仍匹配 | **推断**，未跑覆盖率验证；重组验收必须实跑一次覆盖率对比 |
| git rename 检测能让 V0.1.9 后续单文件修复 cherry-pick 跟上整树搬迁 | **推断**，未实测；C4 取乙的主要风险点 |
| interop 独立工程能让刀2 时 viewer 一行不动 | **推断**，前提是分页契约在刀0 定死且进程内适配不进 interop |
| 只搬目录不改名则覆盖率与产物断言全不受影响 | **推断**，未实测；重组验收须跑 apply 第一步实测基线用例 + 一次双包发布验证 |
| 重组与刀0 分两个 change 不会互相冲突 | **未定**，取决于 C4 时机与刀0 是否等重组合入 |
| publish 目录落在工程目录内会被 SDK 默认 `None **/*` glob 收编 | **推断**，未实测；§5.4.1 的次要理由，不是选「留根」的主因 |
| interop 独立工程对覆盖率的影响≈0（extern 无 IL 体、record 自动属性计入） | **推断**，未跑覆盖率验证；刀0 验收时实测 |

## 9. 关联

- `RUST-CORE-REWRITE-SLICING.md`：本轮定位的来源；D1–D5 决策、刀0–刀6 顺序、interop 边界、分发实账。本笔记是其先行步骤。
- `RUST-CORE-EVALUATION.md`：core 语言决策与阶段 0 探针；其 §4 范围、§5 接缝、§6 内存峰值口径已被切片笔记修订。
- `BACKLOG-MCP-AND-FORMATS.md`：D1 双库边界、D2 顺序（已推翻）、D6 发布形态（已推翻）、D7「工程结构 + 对外 API + 解析抽象同批改评审面过大」——**本笔记沿用该理由，重组单独建 change，不与刀0 合并**。
- `MCP-FUNCTIONALITY.md`、`MCP-LOG-ANALYSIS-API.md`：MCP 工具面两套口径仍未拍板，属刀4 的待决项，不影响本重组。
- `docs/TESTING.md`、`docs/BUILDING.md`：测试与覆盖率基线（**以 apply 第一步实测为准**，不预设 848）、发布步骤；§5.4 的 PublishDir 口径以后者为准，且需先回流 V0.1.9 的修正。

## 10. 下一步（时序）

```
前置  [已完成] 回流 V0.1.9 的 BUILDING 修正到 master   <- f6c2925（仅 docs/BUILDING.md）
  |
重组  一次搬完三块（D1）+ §5 路径耦合逐项 + 删三组共 12 条 Compile/None/EmbeddedResource/Content Remove
  |   + slnx 列全 + CI/脚本/README×2/BUILDING/TESTING 同批
  |   + publish 留根：无新动作（§5.4.1）+ .gitignore 补 target/ 与 node_modules/（D7）
  |   验收（D4）：用例数 / 覆盖率三项均与 apply 第一步实测基线对比（不预设 848、97.31% 等历史值）
  |              路径字面量只改断言；掉档先修 runsettings
  |              + 两条 publish + 产物 Test-Path 清单（不压 tag、不发版）
  v
刀0   建 logcat-core/interop（logcat.Interop.csproj，net10.0；独立工程见 C6）+ viewer 进程内适配 + LogListView 改造
刀1   logcat-core/ 加 Cargo workspace + crates/{core,cli}（并修 .tmp-test oracle host）
刀2   crates/cabi + interop 加 P/Invoke
刀4   建 logcat-mcp/（npm + koffi + CI 产出 prebuilds）
```

1. C1–C7 与 D1–D9 全部定案（C1 经维护者确认）：change **已建**于 `openspec/changes/restructure-project-layout/`（proposal / design / tasks，`skip_specs: true`）；D2（回流 `f6c2925`）与 D9（遗留分支清理）已执行完毕；第二轮评审的 **D-24–D-28** 见该 change 的 `design.md`。
2. change 名 `restructure-project-layout`：tasks **表头**记录 worktree 标识（`wt/restructure-project-layout`，**apply 时建**），把 §5 的耦合点逐项列为验证任务，含「wt 合入 master」任务，并落 D4（验收口径）、D5（本机 `dotnet build logcat.slnx`）、D6（`.tmp-test/` 排除项）。
3. 本重组不实现任何 MCP / Rust / 分页功能，不改任何行为；`logcat-core/` 与 `logcat-mcp/` 由后续刀次建。