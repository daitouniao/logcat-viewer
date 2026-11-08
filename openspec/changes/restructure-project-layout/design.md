# Design：工程结构重组

## Context

动机见 `proposal.md` 的 Why。以下是决定「怎么搬」的现状约束（均在 HEAD 上核对过）：

| 约束 | 事实 |
| --- | --- |
| 依赖方向 | 主工程 `logcat.csproj` 与 `logcat.AndTools/logcat.AndTools.csproj` 同时依赖 `logcat.UiCore/logcat.UiCore.csproj`；tests 依赖前两个工程（下表路径均为 HEAD 上的搬迁前路径） |
| 主工程无显式 `<AssemblyName>` | 程序集名默认取 csproj 文件名；`logcat.AndTools.csproj` 则有显式 `<AssemblyName>logcat-andtools</AssemblyName>` |
| 工程目录 = 仓库根 | 故需三组 `Remove` 块（每组 4 条，共 12 条）排除 `tests/**`、`logcat.UiCore/**`、`logcat.AndTools/**` |
| 测试侧假设 tests 深度为 2 | csproj 里 `RepoRoot` 用 `$(MSBuildProjectDirectory)\..\..` 计算；`TestHelpers` 还有 5 级 bin 回溯 |
| 覆盖率按程序集名过滤 | `coverlet.runsettings` 的 `<Include>[logcat]*</Include>`；`ExcludeByFile` 用 `**` 前缀 glob |
| 版本号单一来源 | 仓库根 `Directory.Build.props`，其下所有 csproj 继承 |
| publish 落点 | 仓库根 `publish/Release/`；`-p:PublishDir` 相对路径按**各工程目录**解析 |
| 发布断言 | `docs/BUILDING.md` 按 `logcat.exe` / `logcat-andtools.exe` / `logcat.UiCore.dll` 三个产物名验证 |
| 本机 gitignored 资产 | `.tmp-test/realprobe*` 三个探针用 18 行 source-include 引用根级 `Services/` / `Models/` / `Controls/` / `Forms/` 源码；`.tmp-test/tstrun` 则只用 `ProjectReference` 指向 `tests/logcat.Tests/logcat.Tests.csproj`（实测） |
| 文档里的旧路径分布 | `docs/TESTING.md` 10 处（含历史记录段与 `**` glob，**仅 3 处必改**）、`docs/UT-AUDIT.md` 4 处、`openspec/config.yaml` 1 处（`Services/Loc.cs`，实际属 UiCore）；`openspec/specs/` **零处** |
| 在途 `V0.1.9` | `git log master..V0.1.9` = 2 条，但 `f6c2925` 已把其中 BUILDING 的 34 行修正**回流**到 master；真正会被 merge 带进来的只有 `git diff --stat master V0.1.9` 的 **3 个文件 5 行**（`Directory.Build.props` / 双 README），**零代码、BUILDING 零差异** |

## Goals / Non-Goals

**Goals：**

- 三个工程在磁盘上各有自己的目录，仓库根只装基础设施（解决方案、版本号 props、NuGet 配置、根级文档、`icon/`、脚本、openspec、tests、testdata、publish）
- 搬迁后：构建绿、用例数不变（**以 1.4 实测为基线，不预设 848**）、覆盖率三项持平、两条 publish 产物清单不变
- 为后续刀次留出同级空位：`logcat-core/`、`logcat-mcp/` 将来各占一块，不挤进现有工程

**Non-Goals（设计层面的额外边界，范围本身见 proposal.md）：**

- 不追求「根目录只剩五个目录」的美感：`tests/` `testdata/` `publish/` `scripts/` `docs/` `openspec/` `sample/` 继续留根
- 不引入 `Directory.Build.targets`、不改 `Directory.Build.props` 的继承链、不新增 MSBuild 属性面
- 不做「一个工程目录一个 publish」的重构：publish 是仓库级资产，与工程目录解耦

## Decisions

### D-1 方案 A：三块工程同级，UiCore 独立成块

`logcat-viewer/`、`logcat-uicore/`、`logcat-android-tools/` 三块工程同级；将来 `logcat-core/`、`logcat-mcp/` 在同一层补位，`icon/` 作为仓库级资源目录也占同层一席。

**为什么不选两块（把 UiCore 塞进 android 工具）**：依赖方向会变成 `viewer → android 工具`，语义倒置；塞进 viewer 则 `android 工具 → viewer`，同样倒置。UiCore 被两个 exe 同时依赖，必须独立。

**为什么不选「只搬 viewer」**：搬迁次数最少，但与「同级放 android 工具」的诉求不符，后续还要再搬一次。

**为什么顶层不加 `src/`**：`scripts/` `docs/` `openspec/` `tests/` 已在根，加 `src/` 只包工程会造成一半在里一半在外，且所有相对路径改动量翻倍。

### D-2 只搬目录，不改工程文件名与程序集名

`logcat.csproj` 移入 `logcat-viewer/` 后仍叫 `logcat.csproj`。

**理由**：主工程无显式 `<AssemblyName>`，改名会**静默**改程序集名 → `coverlet.runsettings` 的 `[logcat]*` 匹配空、覆盖率归零；且 `logcat.exe` 变名，打断 `docs/BUILDING.md` 的产物断言与双 README 的产物名。

**替代方案（未取）**：改名 `logcat.Viewer.csproj` 并同批显式钉 `<AssemblyName>logcat</AssemblyName>` + `<RootNamespace>logcat</RootNamespace>`——额外收益只有命名整齐，代价是多一个配置出错面。

**与 D-23 的关系**：D-23 是同一决定在三工程上的逐项对照（目录 / csproj 文件名 / AssemblyName / 产物名），不重复决策，只补证据。

配套：`logcat.UiCore/` → `logcat-uicore/`、`logcat.AndTools/` → `logcat-android-tools/` 只改目录（`logcat.UiCore.csproj` / `logcat.AndTools.csproj` 文件名不动），换取长期一致的连字符命名；代价是 `LocTableTests` 的 4 处路径字面量同批改。

### D-3 `tests/` 不搬

`tests/logcat.Tests.csproj` 的 `RepoRoot` 用 `$(MSBuildProjectDirectory)\..\..` 算仓库根，`TestHelpers` 还有 5 级 bin 回溯到 `tests/logcat.Tests/tmp`。两处都假设 tests 深度为 2，搬了要改两处、收益为零。`testdata/` 同理（本机大样本，与工程无关）。

### D-4 删掉三组 `Remove` 块，而不是改写路径

`logcat.csproj` 里排除兄弟工程的是**三个 ItemGroup 块**，每组四条 `Remove`（`Compile` / `None` / `EmbeddedResource` / `Content`），共 12 条，分别对应 `tests/**`、`logcat.UiCore/**`、`logcat.AndTools/**`。三组全部删除：工程目录不再是仓库根，默认 glob 自然捞不到兄弟工程。这是搬迁的**收益**，不是成本。

反向验证点是「隐性漏删」：只要漏删一组（四条必须同时删），构建会因重复类型或重复输出文件而失败——错误信息明显，不会静默通过。

### D-5 publish 落点留仓库根，不跟工程目录

`-p:PublishDir` 的相对路径按**各工程目录**解析。工程下移一层后：

- 跟工程走 → 落进 `logcat-viewer/publish/` 与 `logcat-android-tools/publish/`，两个 exe 不再同目录，`logcat-andtools.exe` 从包里消失，正是 `docs/BUILDING.md` 排障表记录过的那类事故
- 留仓库根 → `docs/BUILDING.md` 已有的绝对路径口径（`<仓库根>\publish\Release\...`）搬迁后原样可用

**搬迁后命令里只有工程路径参数变，落点写法一律不变**（两种口径都已实测对得上）：

| 位置 | 落点写法 | 解析基准 | 搬迁后是否要改 |
| --- | --- | --- | --- |
| `docs/BUILDING.md` 四条 publish | `-p:PublishDir=<仓库根>\publish\Release\...` | 已是绝对路径（该文档明令禁止相对写法） | **不改** |
| 双 README 两条 publish | `-o publish\fd` | `-o` 由 CLI 按**当前工作目录**转绝对路径 | **不改**（命令仍在仓库根执行） |
| `Compress-Archive` / `Test-Path` 清单 | `publish\Release\...` | 相对 CWD | **不改** |
| 本机未入库 `FolderProfile.pubxml` | 相对路径 | 按**工程目录** | **必改**为 `..\publish\Release\<版本>` |

**决定留根**，并沿用「目录名里的版本号是文档明示的占位示例、发版时手改」的既有口径，**不引入**用 MSBuild 属性自动带版本的做法（会与「版本号单一来源 + 发版同步 5 处」的既有口径重叠，新增一个属性面）。

`Properties/PublishProfiles/FolderProfile.pubxml` 未入库（`.gitignore` 的 `*.pubxml`），随工程移入 `logcat-viewer/Properties/` 后由**本机手改**成 `..\publish\Release\<版本>`；该文件不在仓库内，因此不进验收项，但 apply 时一并处理（维护者 2026-11-08 定），否则本机发布档案会指向已不存在的路径。

### D-6 测试侧只改断言，不改被测实现

四处断言点（共 7 条路径字面量，其中 1 条为待删兜底）：

1. `LocTableTests.FindRepoRoot()` 与 `CiLocaleAssertionTests.FindRepoRoot()` 用 `Path.Combine(root, "logcat.csproj")` 判定注入的 RepoRoot 是否正确 → 改为按新路径判定（`logcat-viewer/logcat.csproj`）。doc 注释实测**只有一处**该改：`LocTableTests.cs:1055` 的 `FindRepoRoot` 注释写着「那样找不到 `logcat.csproj`」（`CiLocaleAssertionTests` 的同名方法注释只写「仓库根目录（编译期由 AssemblyMetadata 注入）」，不含该句），不改正会误导后来人往回找根目录
2. `LocTableTests.IsLocTablePath` 的前缀判定含 `"logcat.UiCore/Services/LocTable."` → 改为 `"logcat-uicore/Services/LocTable."`（旧前缀 `"Services/LocTable."` 是搬迁前的兜底，一并清掉）；其 `<summary>`（`:1047`）写着「共享库搬迁后落在 logcat.UiCore/Services/」，同批改成 `logcat-uicore`
3. `LocTableTests` 三条 `[InlineData("logcat.UiCore/Forms/…")]` → 改前缀

**为什么 `EnumerateSourceFiles` 只需补一条排除项**：它遍历整个仓库根，现有排除项已有 `StartsWith("obj/")`、`StartsWith("bin/")` **以及** `Contains("/obj/")`、`Contains("/bin/")`——后两条是按任意深度匹配，所以搬迁后 `logcat-viewer/obj/`、`logcat-uicore/bin/` 里的生成物**自动被排除**，不需为下移一层再加条目。真正缺的只有 `.tmp-test/`（它既不在 `tests/` 下也不含 `obj/` `bin/`）。

**补排除项不会改变 `scanned` 计数（实测）**：`.tmp-test/` 下现有 40 个手写 `.cs`（不含 `obj/`、`bin/`）中 `Loc.T(` / `Loc.F(` / `Loc.Bind` 调用为 **0** 处，所以两条哨兵用例的 `scanned > 300` / `scanned > 50` 不受影响。仍要补的理由是防将来：新增探针若直接 include UiCore 的 UI 文件，就会把未翻译的控件字面量带进扫描面。

**为什么不改成「搜索定位」**：`FindRepoRoot` 的 `File.Exists` 是防「注入的 RepoRoot 不对」的哨兵，换成模糊搜索会削弱它；直接指到新路径即可。

### D-7 `mutation-test.py` 变异路径清单同步清旧

8 条现役路径（`Services/LogParser.cs`、`Services/LogDocument.cs`、`Services/FilterEngine.cs`、`Services/CrashAggregator.cs`、`Services/CrashExtractor.cs`、`Services/FileProxyService.cs`、`Services/FileProxyClient.cs`、`Models/FilterSpec.cs`）加 `logcat-viewer/` 前缀；另有 4 条已 stale 的条目（`Services/AdbManager.cs`、`Services/AppSettings.cs`、`Services/CommandStore.cs`、`Services/FavoritesStore.cs`——这些类早已在 UiCore）顺手清理，连同顶部的「类名提示」字典一起按现目录重写。

**理由**：加了前缀但不清 stale，`SKIP-匹配数=0` 的噪声会掩盖真失效；清理属同一处改动的顺带收益，不夹带新任务。

### D-8 `.gitignore` 补泛化模式，不写限定路径

新增 `target/` 与 `node_modules/`（**不用** `logcat-core/target/` 这种限定写法），与既有「模式覆盖、不写枚举型黑白名单」的口径一致。

不动的现有条目：`*.dll` 全局忽略与「预编译 dll 不入库、CI 产出」的口径一致，不为将来的 `logcat_core.dll` 开例外；`*.log` 全局忽略会挡住将来的 golden 小样本，但那是刀1 的事，不在本 change 开例外。

### D-9 CI 不动

`ci.yml` 的 build/test 都按 `tests/logcat.Tests/...` 定位，tests 不搬则不变；`Get-ChildItem -Path tests` 同理。不把 `dotnet build logcat.slnx` 加进 CI（15 分钟预算紧、slnx 变动极低频），改为本机验证一条。

`logcat.slnx` 本身列全四个工程（viewer / uicore / android-tools / tests），刀0 再加 interop。

### D-10 一次搬完，不分批

三块同批搬，引用修正是一次性动作；分批只会多一轮改写与多一轮全量验收。

### D-11 根级 `None Include` 改成 `..\` 时必须同时补显式 `Link`（实测结论）

根级 `None Include="README.md"` 一类条目在工程目录等于仓库根时，`%(Link)` 默认就是文件名，输出位置恰好对。工程下移一层、路径改成 `..\README.md` 后，`%(Link)` 会退化成「相对工程目录的路径」，MSBuild 把其中的 `..` 段折叠掉——**文件被拍平到输出根，层级丢失**。

用最小探针工程实测（`.tmp-test/linkprobe`，已删）：

| 写法 | publish 结果 |
| --- | --- |
| `..\..\sample\**`（无 `Link`） | `fileproxy.py` 等落在**输出根**，`sample/` 层级丢失 |
| `..\..\docs\images\main.gif`（无 `Link`） | `main.gif` 落在**输出根**，`docs/images/` 分层丢失 |
| `..\..\README.md`（无 `Link`） | `README.md` 恰好在根，碰巧正确 |
| `..\..\sample\**` + `Link="sample\%(RecursiveDir)%(Filename)%(Extension)"` | `sample/fileproxy/fileproxy.py` ✅ |
| `..\..\docs\images\main.gif` + `Link="docs\images\main.gif"` | `docs/images/main.gif` ✅ |

**决定**：**七条**根级 `None` 条目全部补显式 `Link`（五项文档 + `sample/**` + `main.gif`）。这不只是风格：`docs/BUILDING.md` 的产物断言按 `sample\fileproxy\fileproxy.py` 与 `docs\images\main.gif` 两个路径 `Test-Path`，不加 `Link` 就会断——且**构建不会报错**，只在验收清单上翻车。

`sample/**` 用 `%(RecursiveDir)` 而不是固定文件名，是因为该目录下不止一个文件（`fileproxy.js` / `fileproxy.py` / 双 README），将来加文件不应再改 csproj。

### D-12 `app.ico` 移入根级 `icon/`，两个 exe 都跨目录引用

探索笔记在此处有一处内部不一致：目标布局图把 `app.ico` 列在 `logcat-viewer/` 内，同时又在耦合清单里给 android-tools 写 `..\..\app.ico`（隐含图标留根）。两者不能同时成立。

**决定（维护者 2026-11-08 定）**：新建仓库根级 `icon/` 目录，`app.ico` 移入 `icon/app.ico`。两个 `ApplicationIcon` 搬迁后**都写 `..\icon\app.ico`**：

| 工程 | 搬迁前 | 搬迁后 | 层级变化 |
| --- | --- | --- | --- |
| `logcat.csproj` | `app.ico`（= `<仓库根>/app.ico`） | `..\icon\app.ico` | 工程**下移一级**，所以从 `../` 起 |
| `logcat.AndTools.csproj` | `..\app.ico`（= `<仓库根>/app.ico`） | `..\icon\app.ico` | 目录只**改名**（`logcat.AndTools/` → `logcat-android-tools/`，仍在根下一级），**层级未变，仍只有一个 `..`** |

⚠️ 这里容易错："下移一层所以多一个 `..`"只对 viewer 成立；android-tools 深度没变，写成 `..\..\icon\app.ico` 会指到仓库根的**上一级**（父目录），而 `ApplicationIcon` 指向不存在的文件时 SDK **只告警不报错**，构建照样绿。

理由（按权重）：

1. 图标被**两个 exe 同时**使用，归任一方都会让另一方跳进别人的工程目录取图；放中立目录后两个工程都只向上指，依赖方向干净。
2. 将来要加其它尺寸 / 其它格式的图标（多分辨率 ico、logo、安装包图）有现成落点，不必再动布局。
3. 不被任何单个工程的发布生命周期绑架：viewer 将来若拆或改名，`icon/` 不受影响。

代价：根目录多一个条目。与「根目录只装基础设施」的口径不冲突——`icon/` 就是仓库级资源目录，与 `docs/images/` 同一性质。

**替代方案（未取）**：随 viewer 走（android-tools 写 `..\logcat-viewer\app.ico`）——依赖方向变成「工具箱跳进 viewer 的工程目录」，且 viewer 改名就断；留根——根目录为一张图留孤儿文件，与「根目录只装基础设施」直接矛盾。

### D-13 验收口径（沿用探索笔记 D4，不放宽）

| 维度 | 口径 |
| --- | --- |
| 用例 | **基线以 1.4 实测为准**（文档记 848、`V0.1.9-pre1` tag 记 852，两者都不预设）；`LocTableTests` 四处断言点路径字面量**改断言、不改实现**，用例数不增不减 |
| 覆盖率 | 三项**持平**：基线同样在 1.4 现场采集、7.3 再采一次互比（D-24），**不预设** 2026-11-01 的 97.31% / 88.65% / 95.61%。若因 `ExcludeByFile` 的 `**` glob 失配掉档，**修 runsettings**，不下调标准 |
| 发布 | 只跑两条 `dotnet publish` + 产物 `Test-Path` 清单，**不压 tag、不发版** |
| slnx | 不进 CI；改为 tasks 里一条本机验证「`dotnet build logcat.slnx` 通过」 |

### D-14 文档里对 csproj 的提及全部改到新路径

搬迁后主工程文件不再在仓库根，但它在文档里被引用了 **13 处**（实测 `git grep -c`，不含 `openspec/**`：README 4 / README.zh-CN 3 / THIRD-PARTY-NOTICES 3 个匹配分布在 2 行 / docs/BUILDING 3 / docs/TESTING 1），其中三处是 **markdown 链接**（双 README 的版本徽章、第三方声明里的 csproj 声明）。

| 类别 | 位置 | 性质 |
| --- | --- | --- |
| markdown 链接 | 双 README 第 3 行版本徽章、`THIRD-PARTY-NOTICES.md` 声明链接 | 搬迁后直接断链，必改 |
| 文件链接/命令 | `docs/BUILDING.md` 两条 publish 命令、双 README 两条 publish 命令与「VS 直接打开」的说法 | 必改 |
| 正文提及 | 双 README 的「改 `<Copyright>` 在 logcat.csproj」说明（`:348` / `:351`）、`THIRD-PARTY-NOTICES.md:91` 的「输出目录已通过 logcat.csproj 自动复制…」 | 改，否则读者找不到文件 |
| 已陈旧、且含旧路径 | `docs/BUILDING.md:190` 排障表的「版本号没同步（logcat.csproj + 双 README 的 badge）」 | **改指 `Directory.Build.props`**（维护者 2026-11-08 定，见 D-15） |

**决定（维护者 2026-11-08 定）**：全部改到新路径（含 markdown 链接与正文提及）。

理由：三处 markdown 链接会直接断链（徽章点不开、声明引不到文件）；剩下的是同一批字面量的顺带收敛，改动量很小，但漏一处就是一个假引用。

### D-15 陈旧表述的边界：只修路径相关的

明确不动两处（已陈旧但与本改变无关），另有一处例外改为修：

| 位置 | 为何不动 |
| --- | --- |
| `logcat-uicore/Services/AppInfo.cs` 注释「版本号以 logcat.csproj 的 `<Version>` 为唯一来源」 | 版本号早已迁到 `Directory.Build.props`，这是既有陈旧；本次只搬文件、不改语义 |
| `docs/BUILDING.md:58` 的「约 2 MB」与 `README.md:147` 的「~1.8 MB」 | 同一个包体的两个体量口径不一致，但与路径无关；按 proposal 非目标「不顺手修无关的陈旧表述」处理，留给单独的 docs 修正（维护者 2026-11-08 定） |
| ~~`docs/BUILDING.md` 排障表版本号同步口径~~ | **例外：维护者 2026-11-08 定改为修**——该条同时含必换的旧路径，改成指向 `Directory.Build.props`（只改来源，不重采版本号） |
| `AGENTS.md` 里的 csproj 提及 | 该文件不入库，且其自身规定修订需用户批准，属独立事务 |

**`docs/UT-AUDIT.md` 例外**：它实测 **6 处**旧路径——4 条散文引用（`:418` `Services/LogDocument.cs:447`、`:482` `Models/FilterSpec.cs:44`、`:491` `Services/LogParser.cs:415`、`:619` `Services/LogDocument.cs`）与 2 条代码块命令（`:627` `git diff --stat Services/ Models/`、`:629` `grep -n "if (false)" Services/*.cs Models/*.cs`）。散文均指向 viewer 的 `Services/` `Models/`，只补目录前缀 `logcat-viewer/`，**不改历史行号**（行号是当次审计快照，已在后续改动中漂移，强行更新会造成假精度）；两条命令同批补同一前缀，否则照抄会扫不到文件。

**`docs/TESTING.md` 一处例外**：「不要靠 `AppContext.BaseDirectory` 往上找 csproj」这句必改——哨兵判定的位置已变，原句会误导人往回找仓库根。

### D-16 本机残留物同批处理，但不进入仓库

三个未入库对象会因搬迁成为孤儿：不影响 `git`、不影响构建，但会让人误以为「旧路径还在」。

| 残留物 | 性质 | 处置 |
| --- | --- | --- |
| 根级 `logcat.csproj.user` | 被 `*.user` 忽略的 VS 每用户设置（启动工程、调试参数） | 移入 `logcat-viewer/`；其实只需改 `_LastSelectedProfileId` 一行绝对路径——其余 `Compile Update="Controls\…"` 是 **相对内容路径**，文件跟着工程目录走仍成立（已读文件内容确认） |
| 根级 `bin/` `obj/` | 主工程旧构建产物（`git ls-files` 记 0 条跟踪） | **全部删除**（维护者 2026-11-08 定，含 `bin/`）：旧 `obj/` 里的 `*.assets.json` 仍写旧路径，排障时易误判 |
| 根级 `.vs/` | VS 缓存（含 `logcat.slnx` 的旧路径索引） | 关闭 VS 后删除，重开自动重建 |

不做：不新增 `.gitignore` 条目（`*.user`、`[Bb]in/`、`[Oo]bj/`、`.vs/` 已全在）、不写清理脚本、不在验收清单里为它们单列验证项。

### D-17 apply 前先把 `V0.1.9` 的两条提交合回 `master`（维护者 2026-11-08 定）

实测（2026-11-08 重核，与初稿结论不同）：`master..V0.1.9` 仍列两条提交，但 master 已在 `f6c2925` 把其中 BUILDING 的 34 行 PublishDir 修正**回流**过。所以真正会被 merge 带进来的只有 `git diff master V0.1.9` 的 **3 个文件 5 行**：

| 文件 | 变化 |
| --- | --- |
| `Directory.Build.props` | `<Version>` 0.1.7 → 0.1.9 |
| `README.md` | 徽章版本文字 + 当前版本行 |
| `README.zh-CN.md` | 同上 |

**`docs/BUILDING.md` 零差异**（master 与 `V0.1.9` 两版内容一致），**零代码改动**。

**决定**：apply 开始前先 `git merge V0.1.9` 到 `master`（git 写操作，需当轮授权），把分叉抹平后再生 worktree。这样已推迟的「回流 cherry-pick」风险直接消失，也不需赌 rename 检测能不能跟上。

**这次合并不是 fast-forward**：实测 `git rev-list --left-right --count master...V0.1.9` = **1 / 2**（master 独有 `f6c2925` 的 BUILDING 回流），故 merge 会生成 **1 个 merge commit**。tasks 1.1 的验证只要求 `master..V0.1.9` 为 0（V0.1.9 已全部并入），`V0.1.9..master` 不为 0 属正常。

三处必须意识到的副作用：

1. **master 的 `<Version>` 会从 0.1.7 变成 0.1.9**（双 README 徽章与版本行同步变）。这不违反「本 change 不改版本号」——那 5 处是 §2.5 的发版动作，由解 `V0.1.9` 的账带来，不是本 change 擅自发版。

   **版本号停在 0.1.9，既不回退到 0.1.7 也不升到 0.1.10**（维护者 2026-11-08 定）：`V0.1.9` 分支存在的意义就是让这个号段持续到稳定版，master 提前持有它只意味着「代码已超过 0.1.9-pre1」，而 §2.5 本就规定版本号只在阶段 8 同步 5 处。回退或跳号反而制造 master 与分支不一致，且下次发版还要再改一次。
2. **`docs/BUILDING.md` 不受合并影响**：`f6c2925` 已把 V0.1.9 的 34 行修正回流到 master，两版内容一致（实测 `git diff master V0.1.9 -- docs/BUILDING.md` 为空），所以**行号不变**，tasks 6.1 里记的 `:74-77` / `:133-144` 一类位置在合并后仍可用。（初稿曾以为合并会让行号全部失效——那是没注意到 `f6c2925` 的结果。）
3. 验收时 `git log` 上 `V0.1.9` 的两条提交与本 change 的 rename 混在一起，靠 commit 边界区分，不靠 diff 整体审阅。

### D-18 基线在 apply 第一步当场复测（维护者 2026-11-08 定）

`848` 来自 `docs/TESTING.md` 的 2026-11-06 记录。但 2026-11-08 核出一个矛盾证据：**`V0.1.9-pre1` 的 tag 消息写「dotnet test 852/852 全过 / 0 跳过」**，而该 tag 的起点正是 master 的 `713d472`。从 `713d472` 到当前 HEAD 只有 `8cfc9c2`（UI 修复，可能带配套用例）与 `f6c2925`（纯文档），所以**真实用例数很可能是 852，TESTING 的 848 落后 4 条**。

**决定**：**不拿 848 当预期值**。worktree 建好后、改任何文件之前，先跑一次 `dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Debug` 并记录真实数字，**以该数字作为本 change 的基线**；后续 tasks 7.2 / 8.2 的「持平」均对它比较。若实测既不是 848 也不是 852，先查清原因（新增用例 / 既有失败）再动搬迁。

**附带事实与决定**：`docs/TESTING.md` 的 848 与双 README 的用例数字目前**落后于实际**，成因是上一次增用例时未按 §3.4 同步。**维护者 2026-11-08 定：本 change 同批修正**，范围与逐处清单见 **D-25**（双 README 共 6 处 + `docs/TESTING.md` 3 处；`>` 历史记录段里的旧数字不改，与 D-20「历史记录不改」同口径）。

### D-19 配置文件里的旧路径：`openspec/config.yaml` 与 `Directory.Build.props`

两处**非文档**但入 git 的文件含旧路径：

| 文件 | 旧内容 | 改为 | 性质 |
| --- | --- | --- | --- |
| `openspec/config.yaml` | `context` 段的 `Services/Loc.cs` | **`logcat-uicore/Services/Loc.cs`**（实测 `Loc.cs` 只在 UiCore） | 规范类文件，是后续所有 openspec 产物的上下文来源；不改会让后续 change 继承旧路径 |
| `Directory.Build.props` | 第 3 行注释里的 `Services/AppInfo.cs` | `logcat-uicore/Services/AppInfo.cs` | 该文件唯一存在于 UiCore（实测），注释指向它 |

**边界**：`config.yaml` 里的 `scripts/run-tests-en-us.sh`、`docs/TESTING.md` 等路径不随搬迁变化，不动；`Directory.Build.props` 的 `<Version>` 不动；同一文件第 4 行的工程列举（`logcat / logcat.UiCore / logcat.AndTools / tests`）是**工程 / 程序集名而非路径**，**不改**（D-26）。**`openspec/specs/` 实测零处旧路径，不动。**

### D-20 `docs/TESTING.md` 按性质分三层改，不整篇替换

`docs/TESTING.md` 实测 12 行命中旧路径（`Select-String 'logcat.UiCore|logcat.csproj|Services/|Models/'`，含 `**` glob 与 `>` 历史记录），但**只有 3 行该改**：

| 性质 | 位置 | 处置 |
| --- | --- | --- |
| **当前状态描述**（正文 / 表格） | `:16` 表格「统计口径」行的 `logcat.UiCore/` → `logcat-uicore/`；`:446` `logcat.csproj` → `logcat-viewer/logcat.csproj`；`:458` `Services/Loc.cs` → `logcat-uicore/Services/Loc.cs` | **改**，按文件实际所在工程补前缀 |
| **历史记录**（`>` 引用块） | `:33-34`、`:76-77`（`Services/StartDirResolver.cs` 等）、`:150`/`:153`（`Services/Loc.cs` 排除说明） | **不改** |
| **`**` glob** | `:252-273` 的 `--exclude-byfile "**/Forms/*.cs"` 等八条 | **不改** |

不改历史记录的理由：它们记录的是「当时重采了什么」，改写等于伪造；且 `:77` 里的 `Services/AppSettings.cs` 今天已**不存在**，补前缀会让一条历史记录变成一个假引用。

不改 `**` glob 的理由：`**` 前缀按任意深度匹配，搬迁后仍成立（这也是 D-13 判定覆盖率不会因搬迁掉档的依据）。

⚠️ **不能一律补 `logcat-viewer/`**：「`Services/`」在搬迁后对应**两个**目录——viewer 的 `logcat-viewer/Services/` 与 UiCore 的 `logcat-uicore/Services/`。逐处改之前必须先按文件名 `Get-ChildItem -Recurse -Filter <文件名>` 确认它到底在哪个工程。实测结果：`Loc.cs` / `AppInfo.cs` / `StartDirResolver.cs` / `DeviceLastDir.cs` 在 **UiCore**；`LogDocument.cs` / `LogParser.cs` / `FilterSpec.cs` 在 **viewer**。

**搬迁不改统计口径，但基线仍要复测（D-24）**：`:16` 原文写着「排除规则随三工程结构调整后**另行重采**」，它给的理由（排除规则要跟着改）实测不成立——`--exclude-byfile` 全是 `**` 前缀 glob，搬迁后一条都不用改，分母不可能变，所以这句必须改写；而 D-24 又要求 apply 前后各采一次互比。两者不冲突：**搬迁不改口径**（判据是分母一致，D-31），**复测是为确认基线本身没有先期漂移**。`:16` 因此改两处：`logcat.UiCore/` → `logcat-uicore/`，以及把「另行重采」按 1.4 实测结果二选一改写（见下一段）——**不能预先写死「仍为 2026-11-01 采值」**。

**采样日期以文件为准是 2026-11-01，不是 11-06**：`:13`–`:15` 三行明写「2026-11-01 采样」，`:49`–`:51` 记录该次重采的来源；`2026-11-06` 只是 `:18` / `:24` 两条**用例数**变更记录的日期，从未重采覆盖率。把日期写成 11-06 会让后来人以为覆盖率随那次用例变更重采过。用环境抖动硬造一组新数字写进权威源，比不重采更坏。

**`:16` 的日期表述要与 D-24 的复测结果对齐**：若 1.4 采集值等于 2026-11-01 那组，写「仍为 2026-11-01 采值」；若不相等（说明基线已漂移），改写为「随结构重组于 <apply 日期> 重新采样」，并同批把 `:13`–`:15` 更新为实测值。两句话不能同时成立，开工前先看 1.4 结果再定。

**不在本轮动 `:252-265` 的 15 条 `--exclude`**（维护者 2026-11-08 定）：其中 `AdbManager` / `ClipboardHelper` / `StartupLog` / `DpiFix` / `DpiDiag` 已不在 viewer（迁走或删除），确实陈腐；但 `:281` 特意强调过「15 个 `--exclude` + 8 个 `--exclude-byfile`，这不是啰嗦，是**必须**这么写」，删条目会让那句强调失效并需连带重采，属**另一个话题**，应单独走一次 docs 修正。**该项 2026-11-08 复核确认延后**：本 change 不触碰，也不再列为待决策项。

### D-21 `docs/BUILDING.md` 加一句工程目录说明

**决定**（维护者 2026-11-08 定）：除按 D-5 换掉四条 publish 的工程路径外，在 **`## 四、发布`** 标题下、「以 V0.1.4 为例」那句**之前**补一行：

> 工程目录自 2026-11 重组：主 exe 在 `logcat-viewer/`，共享库在 `logcat-uicore/`，工具 exe 在 `logcat-android-tools/`。旧笔记或 issue 里写的 `logcat.csproj` / `logcat.UiCore\…` / `logcat.AndTools\…` 一并按此替换。

**为什么加这一句**：`docs/BUILDING.md:74-77` 已有一句「相对路径会被 SDK 按**各工程目录**解析」的警告——搬迁后“各工程目录”具体指哪几个，读者无从得知；且旧命令（含 `-p:PublishDir` 反例里的 `logcat.AndTools\publish\Release\...`）仍在别人笔记里流通。一句目录说明比在每个命令旁加注更省。

**反例路径同批改**：`:76` 那句「第二条命令的产物会落进 `logcat.AndTools\publish\Release\...`」与排障表 `:194` 的「产物落进 `logcat.AndTools\publish\`」都是**搬迁后的反例**，必须写成 `logcat-android-tools\publish\...`——留着旧目录名会让读者以为那个路径今天还可能存在。

**不做的**：不在 `## 二、编译` / `## 三、跑测试` 旁重复（`logcat.slnx` 与 `tests/…` 两条命令路径没变）；不写目录结构树（那是 README 的职责）。

### D-22 `docs/BUILDING.md` 的版本号示例占位符化（维护者 2026-11-08 定）

`docs/BUILDING.md` 里有 25 处具体版本号（`0.1.7` 一处、`V0.1.4` 二十四五处）。其中**说明性**的那几处读起来像“当前版本”，每次发版都过期，却从未跟上过（`V0.1.9-pre1` 那次动过这个文件 34 行，也没改它们）。

**决定**：**把“说明性”的版本号换成占位符，让 BUILDING 从此不随版本号更新**（占位符统一写成 `V<版本>`，与 `:72` 既有的包名写法一致）：

| 位置 | 现文 | 改为 |
| --- | --- | --- |
| `## 一、改版本号` 表格第 1 行 | `` `<Version>0.1.7</Version>` `` | `` `<Version>` 的值 `` |
| 同上第 3 行 | `"Current version **V0.1.4**"` | `"Current version **V<版本>**"` |
| 同上第 5 行 | `"当前版本 **V0.1.4**"` | `"当前版本 **V<版本>**"` |
| `## 四、发布` 包体积表 | `logcat-V0.1.4-win-x64.zip` | `logcat-V<版本>-win-x64.zip` |

**边界：命令块里的 `V0.1.4` 保留不动**（`Remove-Item` / `-p:PublishDir` / `Compress-Archive` / `Test-Path` 那一批）。它们是**可复制执行**的，占位符化会让命令不能直接跑；`:51` 已声明「以 V0.1.4 为例」，读者知道要替换。这就是「说明性内容去数字、可执行内容留示例」的分界。

**效果**：以后发版，`docs/BUILDING.md` 一个字都不用改。

### D-23 改目录名，但不改 csproj 文件名、不改 AssemblyName

搬迁有个「顺手统一命名」的诱饵：`logcat.UiCore/` → `logcat-uicore/` 时把 `logcat.UiCore.csproj` 也想改成 `logcat-uicore.csproj`；`logcat.AndTools.csproj` 同理。**一律不做。**

| 工程 | 目录 | csproj 文件名 | AssemblyName | 产物名 |
| --- | --- | --- | --- | --- |
| viewer | `logcat/` → `logcat-viewer/` | **保持 `logcat.csproj`** | 无显式（取文件名）→ `logcat` | `logcat.exe` / `logcat.dll` |
| UiCore | `logcat.UiCore/` → `logcat-uicore/` | **保持 `logcat.UiCore.csproj`** | 无显式（取文件名）→ `logcat.UiCore` | `logcat.UiCore.dll` |
| AndTools | `logcat.AndTools/` → `logcat-android-tools/` | **保持 `logcat.AndTools.csproj`** | 显式 `logcat-andtools` | `logcat-andtools.exe` |

三条理由：

1. **`logcat.UiCore.dll` 这个产物名被写死在多处断言里**：`docs/BUILDING.md` 的 `Test-Path ...\logcat.UiCore.dll`、本 change 的 publish 产物清单。改 csproj 文件名会让默认 AssemblyName 从 `logcat.UiCore` 变成 `logcat-uicore`，产物名随之变，断言全断。
2. **coverlet 的 `[logcat]*` 虽然仍匹配**（以 `logcat` 开头），但产物名一变会让历史覆盖率报告与 issue 里的文件名对不上。
3. **`tests` 的两条 `ProjectReference` 与 `logcat.slnx`** 都按文件名引用，改名要三处同改，收益为零。

**验证**：搬迁后 `git status` 里不应出现 `*.csproj` 的 **rename（R）**，只应出现内容修改（M）。

### D-24 覆盖率基线也现场复测（维护者 2026-11-08 定，第二轮评审）

`docs/TESTING.md:13`–`:15` 的三项覆盖率是 **2026-11-01** `chunked-progressive-load` 那次重采的值（`:49`–`:51` 记录了来源），此后 `sidecar-cache-index`（+21 用例）、`fix-devices-changed-race`（+2 用例）与 `8cfc9c2`（UI 修复）都改过代码；`README.md:8` / `README.zh-CN.md:8` 的覆盖率徽章与 `README.md:194` 也带着这组数字。

**决定**：与用例数同口径——**不把 97.31% / 88.65% / 95.61% 当预期值**：

- 1.4 同批（改任何文件之前）多采一次覆盖率，作为本 change 基线
- 7.3 搬迁后再采一次，两次互比判「持平」
- 掉档先查 `ExcludeByFile` 的 `**` glob 是否失配（修 runsettings），不下调标准

**为什么不能沿用旧数字**：D-13 的判据是「持平」，而用例基线的判据是「与 1.4 实测一致」。两套口径混用会让「搬迁是否掉档」与「基线本身已漂移」无法区分；复测成本几分钟，比事后争论便宜。

**副作用**：两次采集若有差异，`docs/TESTING.md:13`–`:15` 与双 README 覆盖率徽章（`README.md:8` / `README.zh-CN.md:8`）、`README.md:194`、`README.zh-CN.md:196` 的数字按 §3.4 同批更新（不预设具体值）。

### D-25 用例数字一次同步 9 处，含修 `README.md:194` 的 791（维护者 2026-11-08 定）

实测扫描（2026-11-08，`Select-String '848'`）：

| 文件:行 | 现文 | 性质 |
| --- | --- | --- |
| `README.md:7` | 徽章 `tests-848 passing` | 徽章 |
| `README.md:194` | `Current status: **791 tests, all passing**` | **当前状态，且是陈旧值**——中文版同期写 848，两份 README 已互相矛盾 |
| `README.md:306` | 结构章节 `xUnit test project (848 tests: …)` | 结构章节 |
| `README.zh-CN.md:7` | 徽章 | 徽章 |
| `README.zh-CN.md:196` | 「当前状态：**848 个用例全部通过**」 | 当前状态 |
| `README.zh-CN.md:309` | 结构章节 | 结构章节 |
| `docs/TESTING.md:12` / `:171` / `:429` | 848 | 基线 / 交叉验证基线 |
| `docs/TESTING.md:18` / `:22` | 848 | `>` 历史记录，**不改** |

**决定**：双 README 6 处 + TESTING 3 处一次改成 1.4 实测量。

- `README.md:194` 的 **791** 是既存漂移，顺手修正——同属「让当前状态描述为真」，不夹带新任务
- 结构章节里的用例数**保留**（那里同时列了覆盖范围，删数字反而让读者失去量级感）
- 不改的地方：`docs/TESTING.md:18` / `:22` 的 `>` 历史记录（D-20 同口径）

**验证口径**：`Select-String '848|791'` 在双 README **零命中**；在 `docs/TESTING.md` 只剩 `:18` / `:22` 两处 `>` 引用块。

### D-26 `Directory.Build.props:4` 的工程列举不改（维护者 2026-11-08 定）

第 4 行注释「仓库内所有工程（`logcat` / `logcat.UiCore` / `logcat.AndTools` / `tests`）」列举的是**工程 / 程序集名**，不是磁盘路径：`logcat` 与 `logcat.UiCore` 是程序集名、`logcat.AndTools` 是 `logcat.AndTools.csproj` 的文件名主干，搬迁后三者都不变（D-23）。

**决定**：不改，D-19 只处理同一文件第 3 行那句真正含路径的 `Services/AppInfo.cs`。tasks 6.9 的全仓扫描据此把该行归为「工程名命中，保留」。

**替代方案（未取）**：改成目录名 `logcat-viewer / logcat-uicore / logcat-android-tools / tests`——收益只是字面整齐，却把「工程名」与「目录名」两个概念搅在一起（两者搬迁后已不同名，例如 `logcat-android-tools/` 里的程序集叫 `logcat-andtools`）。

### D-27 探索笔记同批修订（维护者 2026-11-08 定）

笔记 `openspec/notes/PROJECT-STRUCTURE-RESTRUCTURE.md` 有 11 处与 design 冲突或事实有误，均为后续决策改判后未回写（已逐条修正）：

| 笔记位置 | 原现文 | 实际 |
| --- | --- | --- |
| 头部日期行 | 「第四轮修订」 | 改记「第五轮修订：与 change 的 design / tasks 对齐」，免得读者以为笔记仍停在 propos 前的状态 |
| §4.1 布局图 `:91` | `app.ico` 列在 `logcat-viewer/` 内 | 已改归根级 `icon/`（D-12），图里移出 viewer 并新增 `icon/` 一行 |
| `:156`–`:157` | `<None Include>` 「全部改 `..\` 相对路径」 | **漏了必须补显式 `Link`**（D-11 探针实测：不补会把 `sample/` 与 `docs/images/` 层级拍平到输出根） |
| `:158` | 「图标随工程移动，不用改」 | 图标已移出 viewer，两个 `ApplicationIcon` 都要改 |
| `:160` | 「四个 `<Compile Remove>` / None / EmbeddedResource / Content 块」 | **三组 × 4 条 = 12 条**（分别排除 `tests/**`、`logcat.UiCore/**`、`logcat.AndTools/**`） |
| `:161` | `logcat.AndTools.csproj:10` → `..\..\app.ico` | 该目录只改名、深度未变 → 应为 `..\icon\app.ico`（D-12 对照表） |
| `:162` | ProjectReference → `..\..\logcat-uicore\...` | 同上，应为 `..\logcat-uicore\...` |
| §5.2 `:177` | 排除项「只有 `tests/`、`obj/`、`bin/`、`publish/`」 | 另有 `Contains("/obj/")` / `Contains("/bin/")` 按任意深度匹配（D-6 实测） |
| §7 D1 `:361`、§10 `:397` | 「多一轮 848 验收」、「删四个 Compile Remove 块」 | 验收轮次按实测基线（D-18）；Remove 为三组共 12 条（D-4） |
| §7 D4、§8、§10 | 按「848 用例不变 / 覆盖率不重采」 | 改为「用例数与覆盖率基线均现场复测」（D-18 / D-24） |
| §10 下一步 `:409`–`:410` | 「可直接走 `/opsx:propose` 建独立 change」「tasks 首条记录 worktree 标识」 | change 已建（`skip_specs: true`）；worktree 标识现记在 tasks **表头**（配置规则与 AGENTS §2.3 的「首条」要求） |

**决定**：apply 前已同批修订笔记（笔记仍在途，归档时才冻结）。否则实现时两份文档对读会打架，而 tasks 3.4 / 4.x 正是照 design 写的。

### D-28 apply 前 git 写操作逐条授权（维护者 2026-11-08 定）

`tasks 1.1`（`git merge V0.1.9`）、`1.2`（commit 笔记 + change 四件套）、`1.3`（`git worktree add`）、`3.8`（删根级 `bin/` `obj/` `.vs/`）都是写操作，AGENTS §一.7 要求「一次一动作」。

**决定**：不把四项打包成「apply 一并授权」。

- 收到 apply 指令后，先列 1.1 / 1.2 / 1.3 三项请维护者一次确认——三者是同一条准备链（合分支 → 落档 → 建 worktree），拆开无意义
- 3.8 属破坏性本机操作（删除），留到执行到那一步再单独确认
- tasks 措辞由「随 apply 指令一并授权，不单独请示」改为「需当轮授权（D-28）」

### D-29 `docs/TESTING.md` 的「最后更新」同批改（维护者 2026-11-08 定）

`:3` 现写「最后更新：2026-11-01」，但 `:18` / `:24` 已有 2026-11-06 的两条记录——头部早已落后。本 change 还要改 `:12`（用例数）、`:16`（统计口径）、`:446`、`:458`，按 §3.4「用例数 / 覆盖率数字一变即更新」，头部必须跟进。

**决定**：`:3` 改为 **apply 执行当天**的日期；同时把 `:8` 的 §0 标题「当前基线（2026-11-02）」一并改为同一日期。

**为什么两处一起改**：两行语义相同——「本文的基线何时成立」。只改 `:3` 会留下一个更早的 `:8`，读者反而更分不清哪一个是基线日。

### D-30 覆盖率徽章保留数字形态（维护者 2026-11-08 定）

`README.md:8` / `README.zh-CN.md:8` 现为 `Coverage: 97.31% lines`。D-24 复测后数字可能变。

**决定**：保留数字徽章，按 D-24 的实测值更新，与 `tests-*` 徽章同口径（§3.4 联动）。

**理由**：数字是给用户看的量级信号；去掉数字会让徽章退化成装饰，而覆盖率是 README 里唯一可量化的质量指标。

**替代方案（未取）**：改成不带数字的说明型徽章——从此不用随重采更新，但失去信息量。

### D-31 「持平」的判定容差（维护者 2026-11-08 定）

D-13 / 7.3 只写「三项持平」，没说差多少算持平。

**决定**：同时满足两条即判持平——

1. 分母（行 / 分支 / 方法的总数）与 1.4 基线一致
2. 比率差 ≤ **0.1 个百分点**

差值本身（含零差异）都写入完成记录；超出容差先查 `ExcludeByFile` 的 `**` glob 是否失配，修 runsettings 后复跑，**不下调标准、不改判据**。

**为什么不能要求严格相等**：比率由命中数/总数算出，同一份代码两次采集理论上一致，但工具并行度会带来末位抖动；把 0.01pp 当掉档会让复跑变成无用功。**分母一致**这一条是关键的：分母变了说明统计口径真变了，不是抖动。

### D-32 归档只归档本 change 的笔记（维护者 2026-11-08 定）

`openspec/notes/` 根下实测 **12 份在册笔记**（另有 `archive/` 下 19 份已归档）。与本 change 相关的是 `PROJECT-STRUCTURE-RESTRUCTURE.md`；`RUST-CORE-EVALUATION.md` 与 `RUST-CORE-REWRITE-SLICING.md` 是刀0–刀6 的来源（描述尚未开始的工作）；其余 9 份是各主题 backlog / 机制笔记，与本 change 无关。

**决定**：归档时**只**把 `PROJECT-STRUCTURE-RESTRUCTURE.md` 移入 `openspec/notes/archive/`（`git mv` + 日期前缀）；其余 11 份留原位（`RUST-CORE-EVALUATION.md` / `RUST-CORE-REWRITE-SLICING.md` 等各自刀次的 change 归档时再各自归档，主题 backlog / 机制笔记属常驻参考，不归档）。

**理由**：AGENTS §3.2「常驻参考不归档」。把刀次来源笔记提前归档，会让刀0 开工时要引用一份「已归档、不再维护」的文档。

### D-33 `docs/UT-AUDIT.md:533` 的 `frmMain.cs:713` 同批补前缀（维护者 2026-11-08 定，第四轮评审）

`docs/UT-AUDIT.md` 的旧路径不是 6 处而是 **7 处**：除 D-15 列的 4 条散文 + 2 条命令，`:533` 还有一处**裸文件名**——「（`frmMain.cs:713` 传 `_filterDocRows`）」。它是 viewer 的根级文件（搬迁后为 `logcat-viewer/frmMain.cs`），而 tasks 6.7 的扫描口径只认 `Services/` / `Models/` 起首，扫不到它。

**决定**：6.7 一并改为 `logcat-viewer/frmMain.cs:713`，**行号 713 不动**（与「不改历史行号」同口径）；6.7 的验证句扩成「不再出现 `Services/` / `Models/` 起首的裸路径，也不出现裸 `frmMain.cs`」。

**为什么不能留在快照里**：UT-AUDIT 是**活文档**（§3.1 的「其他」类，随内容演进更新），不是归档快照；它里面的路径是照着扫的入口，写错就扫不到。

### D-34 `ci.yml` 与 `run-tests-en-us.sh` 注释里的 `Services/Loc.cs`（维护者 2026-11-08 定）

`.github/workflows/ci.yml:40` 与 `scripts/run-tests-en-us.sh:7` 的注释都写「`Services/Loc.cs` 的静态字段在按 `CurrentUICulture` 探测」。`Loc.cs` 早已在 UiCore，这两处**现在就不准**，搬迁后应为 `logcat-uicore/Services/Loc.cs`；而它们既不在 tasks 6.x 的范围，也不在 6.9 的扫描字符集（扫的是 `logcat.UiCore` / `logcat.AndTools`），会静默留下。

**决定**：两处注释各补 `logcat-uicore/` 前缀，**可执行路径一行不动**（脚本靠 `tests/` 定位，与搬迁无关）。

**为什么不归 6.9**：6.9 是全仓**字面量**扫描（`logcat.UiCore` 等），这两处写的是 `Services/Loc.cs`——口径不同，必须显式点名。

### D-35 apply 窗口内冻结 `V0.1.9` 分支（维护者 2026-11-08 定）

`git worktree list` 实测：`logcat-wt-V0.1.9` 在 `354c301 [V0.1.9]`，master 在 `f6c2925`。1.1 把 `V0.1.9` 合进 master 后再有提交就又分叉；而那时 master 已是新布局（`logcat-viewer/` 等），第二次 merge 会在旧路径上撞改，cherry-pick 也要赌 rename 跟随。

**决定**：从 1.1 执行起、到 8.2 关闭为止，**`V0.1.9` 分支冻结**（不提交、不发 `pre2`）。窗口内若必须修 bug，**在新布局上改并留在 master**，窗口结束后再按 §2.5 处理分支；tasks 原「按单文件 cherry-pick 回流」的说法**作废**。

**边界**：冻结只约束这段时间的人为提交；`V0.1.9-pre1` 已发布，用户侧不受影响。

### D-36 `.tmp-test/` 四个只挂 `ProjectReference` 的探针同批修（维护者 2026-11-08 定）

`.tmp-test/` 的 11 个探针里，`BenchLog:11` / `BlockScanBench:14` / `ChunkProbe:22` / `LoadProbe:23` 各只有一行 `<ProjectReference Include="..\..\logcat.csproj" />`；搬迁后构建直接报「找不到工程」。另三条 `realprobe*` 各 18 行 source-include，属刀1 重建范围。

**决定**：非目标从「不修 `.tmp-test/` 探针工程」**收窄**为「不修 `realprobe*` 三条」；四个单引用探针同批改到 `..\..\logcat-viewer\logcat.csproj`。

**理由**：4 行改动 vs 54 行重写，不是一个量级；且「找不到工程」的报错比「指向不存在文件」更难自诊断。**不进发布的产物验证清单**（gitignored 本机资产，只记一条路径 `Test-Path` 为 True）。

### D-37 「完成记录」的载体 = `apply-log.md`（维护者 2026-11-08 定，第五轮评审）

tasks 的 1.4 / 2.5 / 4.4 / 4.5 / 6.9 / 7.2 / 7.3 / 7.4 / 7.5 / 8.2 与 design 的 D-13 / D-18 / D-31 都要求「把数字写入完成记录」，但**全篇没有一处定义记录落在哪**。验收时拿不到一份可对照的实测表，各项「持平」的判据也就无从复核。

**决定**：在 change 目录内建 `apply-log.md`，用「任务 → 命令 → 实测值」的表格集中记录；tasks 里的「写入完成记录」一律指这个文件。

- 单独成文而**不写进 tasks**：tasks 已有 78 行、单行很长，塞数字会把待办清单变成流水账
- **不写进 `docs/TESTING.md`**：那里的数字是「当前基线」，本文件记的是「这次搬迁前后的对照」，两者随归档去向不同（§3.4 vs change archive）
- 归档时随 change 一起进 `openspec/changes/archive/`，**不进 `openspec/specs/`**（specs 只写行为契约）
- 只记**实测值与来源命令**，不写推测；测不出来就写「未验证」并注明原因（§一.4 口径）

### D-38 `openspec/config.yaml` 的 `context` 不补工程布局（维护者 2026-11-08 定）

`context` 现有 10 段（语言 / 项目 / 测试口径 / CI 语言环境 / 文档红线 / 规范定位 / 文档口径 / 工作流门禁 / 探索规范 / 易错规则），**没有布局描述**；搬迁后后续 change 读它时不会知道源码在 `logcat-viewer/`。

**决定**：不补。6.8 只改 `:7` 那处 `Services/Loc.cs` 的路径，**不追加布局段**。

**理由**：`context` 的每一条都是**稳定约束**，而布局在刀0（`logcat-core/`）、刀4（`logcat-mcp/`）还要再变；写进去等于每次搬目录都要改 config.yaml，与本 change 降耦合的方向相反（同 D-22「说明性内容不跟版本走」）。

**替代方案（未取）**：补三行目录说明。收益是后续 change 少查一次仓库；代价是这三行会随刀0 / 刀4 立刻过期，而过期后没人会来改它——这类「会烂的镜像」正是 D-22 要消掉的东西。

### D-39 README 散文里的 `Forms/` 不改（维护者 2026-11-08 定）

`README.md:118` / `README.zh-CN.md:122` 写「Every hand-written dialog under `Forms/` declares `AutoScaleMode.Dpi`…」。仓库里有**两个** `Forms/`（viewer 的与 `logcat-uicore/Forms/`），这句今天就已经靠上下文隐含指向。

**决定**：不改。它描述的是**编码约定**（所有手写窗体都声明 DPI 模式），不是可点的路径，也不在 6.x 任何一条的字面量清单里；补前缀会把一句功能说明降格成路径断言，而读者真正需要的信息（约定本身）没有增加。

**替代方案（未取）**：改成 `logcat-viewer/Forms/`。若将来这句话要明确指「哪一侧的窗体」，再改不迟。

### D-40 提交粒度：按 tasks 的 8 个 section 分批（维护者 2026-11-08 定）

`1.2` 已定「笔记 + change 四件套 = 单个 commit」，其余任务未定。本 change 会动约 40 个文件、含 3 处大目录 rename。

**决定**：按 tasks 的 1…8 分节分批 commit，每批过 `dotnet build` + 相关测试后才提交：

| 批次 | 内容 | 提交时机 |
| --- | --- | --- |
| 前置（1.2） | 笔记 ×3 + change 四件套（含 `apply-log.md`） | worktree 建立前，单独 1 个 commit |
| 2 | 目录搬迁 + `icon/`（**只 rename，不改内容**） | `git status` 显示成对 `R` 之后 |
| 3 | csproj / slnx / 本机发布档案 / 清理 | 构建通过后 |
| 4 | 测试侧字面量 + 源码扫描 | 测试通过后 |
| 5 | 脚本 / `.gitignore` | `git check-ignore` 验证后 |
| 6 | 文档同步 | 按 6.1–6.11 逐条验证后 |
| 7–8 | 验收与 wt 合入 | 验收通过后 |

**为什么分批**：rename 与内容修改天然落在不同批次，`git log --follow` 与 review 都清楚；2.5 那条「先 `git mv`、提交前不改被移动文件内容」也才有意义。**单 commit 的风险**是 40 个文件的 diff 无法分段审阅，且相似度被内容改动拉低后 rename 识别可能失败。

**授权口径**：每次 commit 仍需当轮授权（§一.7，`git add` 随同次 commit 授权一并覆盖）；分 8 批不等于 8 次预先授权。

### D-41 交付门槛：`apply-log.md` 不允许留「待填」（维护者 2026-11-08 定，第六轮评审）

D-37 允许写「未验证 + 原因」，但没定**交付时能不能留着空格**。7.x 里有带主观性的条目（6.3 的「两份 README 条目一一对应」、7.4 的目视核对），一旦允许留白，验收时就分不清「没做」和「做了但没法量化」。

**决定**：交给维护者验收前，`apply-log.md` 的每个格子必须**有内容**：要么是实测值 + 来源命令，要么是「未验证 + 原因 + 替代证据」。校验方式是全文搜「待填」零命中（tasks **7.6**）。

**理由**：§一.4 禁止声称未验证的通过；「待填」既不是通过也不是失败，留在文件里等于把判断成本转嫁给验收人。**替代证据**放宽到「可复核的间接证据」（`Test-Path` 清单、目视要点、构建日志行），不要求每项都有可量化数字。

**替代方案（未取）**：允许留白、验收时边看边补。代价是验收会变成第二遍排查。

### D-42 回滚分两段：合入前丢分支，合入后 `revert -m 1`（维护者 2026-11-08 定）

Migration Plan 第 6 条原写「回滚 = 丢弃该 worktree 分支；主工作树从未被改动」。但 **1.1 已在 master 上产生 merge commit、1.2 产生 docs commit**，8.1 又会把 rename 合进 master——「主工作树从未被改动」在 8.1 之后不再成立，这条预案会把人引到「直接 reset」这种破坏共享历史的路子上。

**决定**：

| 时点 | 回滚动作 | 说明 |
| --- | --- | --- |
| 2.x–7.x（未合入） | 丢弃 `wt/restructure-project-layout` 分支 + `git worktree remove` | master 保留 1.1 的 merge commit 与 1.2 的 docs commit——两者对所有路径零影响，**不还原** |
| 8.1 之后（已合入，验收不过） | `git revert -m 1 <8.1 的 merge commit>` | 撤销整块 rename；**不得 `reset --hard` / `push --force`**（master 是共享历史，§一.7） |
| 已打 tag / 已发布 | 不在本 change 处理 | 本 change 不发版（非目标） |

**为什么不用 reset**：8.1 之后 master 上可能已叠加别人的提交，reset 会连带抹掉它们；revert 只撤销这一次合并。

**残留风险**：revert 不会删除 worktree 目录、也不会清掉 `bin/` / `obj/` 里的旧路径产物——回滚后需重跑 `dotnet build`（3.8 的根级 `bin/` / `obj/` / `.vs/` 清理清单同样适用于回滚场景）。

### D-43 `publish/Release/` 的历史版本目录不动（维护者 2026-11-08 定）

实测该目录下有 **20 个子目录**（`Use` + `V0.0.7`…`V0.1.8`，含一个 `V0.1.5-zip-backup`），合计约 **2,350 个文件**，全部 gitignored。3.7 把 pubxml 的目标从 `V0.1.4` 改到 `V0.1.9` 之后，旧目录既不会被再次写入，也不会被自动清理；7.4 的双包验证还会再添 `V0.1.9-selfcontained` / `V0.1.9-fdd`（约 320 个）。

**决定**：**不动**。D-16 清理的是「会让人误判旧路径仍然有效」的本机残留（`bin/` / `obj/` / `.vs/`），历史发布产物不属于这一类——它们是按版本号归档的成品，删不删与搬迁无关。

**替代方案（未取）**：顺手清掉 `≤ V0.1.4` 的 11 个目录（约 1,100 个文件）。收益是省几百 MB 磁盘；代价是把一次磁盘清理混进搬迁验收，且一旦要复查旧包就没副本了。

**留痕**：磁盘清理若要做，作为独立动作另行提出，不挂在任何 change 下。

### D-44 「交付说明」= `apply-log.md` §3，不另起文（维护者 2026-11-08 定，第七轮评审）

tasks **7.6** 原写「§3 有内容的，逐条写进交付说明」，但没定义交付说明的载体；若另写，就会与 §3 形成两份可能不一致的清单。

**决定**：`apply-log.md` §3「未验证 / 异常」表**就是**交付说明——交付时把该表整段摘出、随验收请求一并给出，不另写第二份。§3 无内容时也要写一行 `（无）`，不留空白表（与 D-41「不留待填」同一口径）。

**理由**：单一来源。两处并存时「改了一处忘另一处」是必然的，与本 change 反复消的「会烂的镜像」（D-22 / D-38）同类。

### D-45 两条 merge 都用手写语义式 message（维护者 2026-11-08 定）

实测仓库两种先例并存：`7c54752 merge(release): 并入 V0.1.8 分支修复…` 与 `7438bd6 Merge branch 'wt/sidecar-cache-index'`。§一.7 只规定 `type(scope): 中文描述`，git 默认的 `Merge branch '…'` 不满足该形式。本 change 有两条 merge：1.1 的 `git merge V0.1.9` 与 8.1 的 `git merge wt/restructure-project-layout`。

**决定**：两条均用 `git merge <branch> -m "…"` 手写——

- **1.1**：`merge(release): 并入 V0.1.9（零代码改动，仅版本号与双 README 徽章 3 文件 5 行）`
- **8.1**：`merge(restructure): 三块同级布局落定（logcat-viewer / logcat-uicore / logcat-android-tools）`

**理由**：`git log --oneline` 是回看搬迁历史的第一入口，D-42 的回滚也要按 commit 定位；默认 message 只说「合了哪个分支」，不说「合了什么」。

### D-46 `docs/memory/` 的 3 个已跟踪文件另行取消跟踪（维护者 2026-11-08 定）

实测 `git ls-files docs/memory` 仍有 **3 个文件**在库：`README.md`、`mutation-testing.md`、`openspec-apply-worktree.md`。目录已 gitignore，但「忽略 ≠ 取消跟踪」，与 §一.1（非项目内容不入 git）、§3.8（已入库存量用 `git rm --cached`）冲突。

**决定**：**不并入本 change**。另起一次 `docs(memory): 取消跟踪 docs/memory 存量`（`git rm --cached`，3 个文件保留工作区副本），按 §一.7 单独当轮授权。

**理由**：`git rm --cached` 与 rename 无因果关系，混进搬迁 diff 会让 40+ 文件的 review 多出无关条目；它随时可做，不阻塞本 change。

**留痕**：写进 tasks 的 Workflow follow-up，避免「知道要改但没人做」。

### D-47 `docs/BUILDING.md` 里 4 处 `<None Include>` 字面量同批改（维护者 2026-11-08 定，第八轮评审）

3.1 之后根级 `None` 两项变成 `..\sample\**` + `Link="sample\%(RecursiveDir)%(Filename)%(Extension)"`、`..\docs\images\main.gif` + `Link="docs\images\main.gif"`（D-11），但 `docs/BUILDING.md` 有 4 处仍在讲旧写法：

| 位置 | 现文 | 改为 |
| --- | --- | --- |
| `:132`（验证命令注释） | 「由 csproj 的 `<None Include="sample/**">` 负责，别删该项」 | 「由 csproj 里指向 `sample/` 的 `<None Include>` 项（`..\sample\**` + `Link="sample\…"`）负责，别删该项」 |
| `:136`（验证命令注释） | 「`<None Include="docs/images/main.gif">`，路径须与 README 内相对链接一致」 | 同上，指 `..\docs\images\main.gif` + `Link="docs\images\main.gif"` |
| `:191`（踩坑速查） | 「csproj 的 `<None Include="sample/**">` 被删或改坏，恢复该 ItemGroup 后重打」 | 恢复时**必须带 `Link`** |
| `:192`（踩坑速查） | 同上的 `main.gif` 行 | 恢复时**必须带 `Link`** |

**为什么必须改**：这两条的排障建议就是「恢复该 ItemGroup」——照旧写法恢复会丢掉 `Link`，包内 `sample/` 与 `docs/images/` 层级被拍平到输出根，**构建不报错**，只在 `Test-Path` 清单上翻车（D-11 实测）。

**边界**：只改这 4 处对 ItemGroup 的引用；`:133-134` / `:137-138` 的 `Test-Path` 清单落点是 `publish\Release\...`（相对 CWD），按 D-5 **不改**。

### D-48 tasks 6.1 的「产物结构段落」在文件里不存在：删该从句（维护者 2026-11-08 定，第八轮评审）

`docs/BUILDING.md` 没有「产物结构」一节；`logcat.UiCore.dll` 只出现在 `:17`（版本号继承段：三个产物名一并继承 `<Version>`）、`:143`（`Test-Path` 清单）与 `:196`（`.pdb` 排障行）。三处都只是**产物名**，搬迁后不变（D-23），**不该改**。6.1 原句在文件里找不到落点，执行时只会卡住或误改。

**决定**：删掉该从句；6.1 显式写「产物名 `logcat.exe` / `logcat-andtools.exe` / `logcat.UiCore.dll` 与 `.pdb` 提法不是路径，`:17` / `:143` / `:196` 一律不改」。

### D-49 README 结构章节不写 `logcat-core/` / `logcat-mcp/` 预留（维护者 2026-11-08 定，第八轮评审）

6.3 原要求「注明 `logcat-core/` 与 `logcat-mcp/` 为后续刀次预留」，与 **D-38** 冲突：D-38 刚以「布局随后续刀次还会变，写进去就是会烂的镜像」为理由，拒绝往 `openspec/config.yaml` 补三行布局说明；README 结构章节是同一性质的内容，只是读者不同。

**决定**：README 只列**现有**三块工程 + 根级基础设施（含 `icon/`）；`logcat-core/` / `logcat-mcp/` 建实体时（刀0 / 刀4）各补一行。

**替代方案（未取）**：写「预留」说明——读者能预期后续结构，但两处一旦与刀次实际落点不一致（例如 `logcat-core/` 下 `interop/` 与 `crates/` 的最终形状），README 立刻变成错镜像，而这类错没人会回来改。

**边界**：不禁止在「Roadmap / 后续计划」类叙述里提 Rust core 与 MCP 的**意图**，禁的只是把它们写进**目录树**。

### D-51 master 停留 `0.1.9` 的窗口期只登记、不提前升号（维护者 2026-11-08 定，第八轮评审）

D-17 已定「停在 0.1.9，不回退不升号」。副作用：master 与已发布的 `V0.1.9-pre1`（以及将来的 `V0.1.9` 稳定版）**同号不同内容**——期间从 master 构建的产物资产写着 0.1.9，代码已含重组。

**决定**：本 change 不动版本号，只在 tasks 的 Workflow follow-up 登记时序：`V0.1.9` 稳定版按 §2.5 收尾（打 tag 后删分支与 worktree）；master 下一次发版（`V0.1.10`）时按阶段 8 同步 5 处。

**理由**：§2.5 规定版本号只在发版时同步 5 处；本 change 提前改号会制造「master 领先预发布分支」的第二处不一致，下次发版还得再改一次。

**边界**：发版归口（稳定版从哪条分支打 tag）**不属本 change 的关注面**，由维护者在发版时按 §2.5 处理（第八轮评审的 D-50 已明确不关注，不写入本 change）。

### D-52 `docs/memory/` 取消跟踪排在 apply 前、独立提交（维护者 2026-11-08 定，第八轮评审）

D-46 只定了「另起一次 `docs(memory): 取消跟踪 docs/memory 存量`」，没定何时。

**决定**：在 1.2（笔记 + change 四件套入库）之后、1.3（建 worktree）之前，在 master 上单独提交（tasks **1.2b**）。

**理由**：① 那时主工作树正好干净，插一条不打断 apply 的节奏；② 该 commit 与 rename 无因果关系，提前做掉就不会在 40+ 文件的 review 里多出无关条目（D-46 的原意）；③ 放进 worktree 会落在 `wt/` 分支上，合入时平白多一条历史。

**授权**：`git rm --cached` 属 git 写操作，按 §一.7 当轮授权（与 1.1 / 1.2 / 1.3 一并请确认，但**动作与 commit 独立**）。

### D-53 `apply-log.md` §2 补 7.1 与 3.1 两行（维护者 2026-11-08 定，第九轮评审）

§2 原有 10 行（1.1 / 2.5 / 4.4 / 4.5 / 6.9 / 7.2 / 7.3 / 7.4 / 7.5 / 8.2），漏了两类「没测出来也以为过了」的项：

- **7.1**（`dotnet build logcat.slnx -c Debug`）：CI 不动（D-9），本机构建是**唯一**能发现「4 条新 `ProjectReference` 写错 / slnx 少一个工程」的验证；
- **3.1**（根级 `None` 改 `..\` + `Link`）：D-11 实测表明这类错**构建不报错**，只在产物层级上翻车，是本 change 最隐蔽的一类失败。

**决定**：补两行——7.1 记「构建 error 数 + 4 个工程是否都出现」；3.1 记「本地 build 后 `bin\Debug\net10.0-windows\` 下 `sample\fileproxy\...` 与 `docs\images\main.gif` 的层级是否保留」。

### D-54 探索笔记头部状态改「已定稿」，在 planning 阶段就改（维护者 2026-11-08 定，第九轮评审）

`openspec/notes/PROJECT-STRUCTURE-RESTRUCTURE.md:3` 原写「状态：**待评审**（探索记录，未编码、未 propose）」，与事实不符：change 已建、评审到第九轮、`openspec status` 为 `isPlanningComplete: true`。1.2 会把这份笔记提交入库并公开到归档为止，读者看到「未 propose」会误判进度。

**决定**：**现在**（planning 阶段）就改为「状态：**已定稿**（change `restructure-project-layout` 已建、planning 完成，实施见其 `tasks.md`）」，日期行追加「第九轮修订（状态行 + D-53~D-56 落盘）」。

**为什么不排进 tasks**：这是**规划产物自身的状态**，与实施内容无关；排成任务只会让 apply 多一步「改一个已经改过的文件」。

**边界**：只改状态与日期两行，不动正文结论；不引入 `docs/` 那套统一文档头（OpenSpec 产物不带文档头）。

### D-55 tasks 6.9 的扫描字符集加 `app.ico`（维护者 2026-11-08 定，第九轮评审）

6.9 原扫三类（`logcat.UiCore` / `logcat.AndTools` / `logcat.csproj`），**不含 `app.ico`**。它本轮从根级挪到 `icon/app.ico`，改动点分散在 `logcat.csproj:5`（3.3）、`logcat.AndTools.csproj:10`（3.4）、双 README 结构树（6.3）。`git grep -n "app\.ico"` 实测今天全仓只有 4 处命中，全部落在这四个点上。

**决定**：6.9 加一条规则——**裸 `app.ico` 命中为 0**，合法形态只有 `icon/app.ico`（README、`Link`）与 `..\icon\app.ico`（两 csproj 的 `ApplicationIcon`）。

**为什么值得单独加**：漏改这条 `dotnet build` 会直接报错（不像目录引用那样隐蔽），但**验收时没有任何机械抓手**；一行规则与已有三类扫描同批做，零额外成本。

### D-56 tasks 7.4 显式写「命令里的 `V0.1.4` 手改为 `V0.1.9`」（维护者 2026-11-08 定，第九轮评审）

D-22 明确**保留命令块里的 `V0.1.4`**（可复制执行优先，`:51` 已声明「以 V0.1.4 为例」）。于是 7.4「按 `docs/BUILDING.md` 跑四条 publish」实际需要执行者自己替换落点，而原文只用「本次只新增 `V0.1.9-selfcontained` 与 `V0.1.9-fdd` 两个目录」隐含了这件事——照抄会写进历史目录 `V0.1.4`，与 D-43（历史目录不动）直接冲突。

**决定**：7.4 正文补一句「**命令里的 `V0.1.4` 逐条手改为 `V0.1.9`**（四条命令各有一处）」；**不**把 BUILDING 的命令块改成 `V<版本>` 占位（那会与 6.10 的分工冲突）。

### D-57 section 6 加「行号只作提示」通则（维护者 2026-11-08 定，第十轮评审）

6.x 里同一文件被多条任务改，行号必然漂移，已可预见的至少两处：

| 漂移源 | 受影响的引用 |
| --- | --- |
| 6.1 在 `## 四、发布` 标题下补一行（D-21） | 6.10 依赖的 `docs/BUILDING.md:51`（「以 V0.1.4 为例」）后移 1 行 |
| 6.3 重写双 README 结构章节（原 `:234-310` / `:237-313`） | 6.5 的 `README.md:348` / `README.zh-CN.md:351`；6.11 的 `:306` / `:309` |

D-17 的「行号仍有效」只承诺 **1.1 合并后那一刻**（`docs/BUILDING.md` 零差异），不是整个 section 6 的全程。

**决定**：section 6 开头加一条通则——行号只作定位提示，**一律用唯一文案串 `Select-String` 定位，文案与行号冲突时以文案为准**；每改完一条任务按文案重扫一次再进下一条。各条目里的行号保留（仍是有用的第一跳），不逐条改写。

### D-58 7.4 的 `Test-Path` 清单同样要手改版本号，否则验出假绿（维护者 2026-11-08 定，第十轮评审）

`docs/BUILDING.md:137-144` 的 `Test-Path` 清单**全部指向 `V0.1.4-*`**，而 `publish/Release/V0.1.4-selfcontained` 与 `V0.1.4-fdd` **实测仍在**（现存 19 个版本目录 + `Use/`）。照抄清单，这 7 行都会返回 **True——验的是历史旧包**，整包验收失效。D-56 只覆盖了四条 publish 命令，没覆盖这份清单。

**决定**：7.4 明确「清单里的 `V0.1.4` 同样逐条手改为 `V0.1.9`」，并补一条**时间戳判据**：`publish/Release/V0.1.9-selfcontained\logcat.exe` 与 `V0.1.9-fdd\logcat.exe` 的 `LastWriteTime` 必须是本次构建时间。

**为什么加时间戳**：`Test-Path` 只回答「路径存在」，而历史目录的存在正是为了让「存在」永远为真；时间戳是唯一能机械区分「这次打的包」与「去年的包」的判据。

### D-59 7.3 的覆盖率命令内联写全（维护者 2026-11-08 定，第十轮评审）

7.3 原写「`dotnet test --collect:"XPlat Code Coverage"` + `python tests/coverage-report.py`」——**既无工程路径也无 `--settings`**。在仓库根照抄会跑 `logcat.slnx` 里的测试工程，且**不带 `coverlet.runsettings`**（排除规则全丢、分母完全不同），与 1.4 基线的对比必然「不平」；而 `apply-log.md` §1 的三步命令是完整的，同一 change 内两条命令不一致。

**决定**：7.3 内联写全三条（`dotnet test tests/logcat.Tests/logcat.Tests.csproj --collect:"XPlat Code Coverage" --settings tests/logcat.Tests/coverlet.runsettings` → `python tests/coverage-report.py` → 抄三项与分母），与 7.2「命令写全」的风格一致。

**替代方案（未取）**：写「命令同 `apply-log.md` §1」——省重复，但要求执行者跨文件对照，tasks 失去独立可执行性。

### D-60 搬迁前先清两个改名工程的 `bin/` `obj/`（维护者 2026-11-08 定，第十一轮评审）

`git mv` 只移动**跟踪文件**。实测 `logcat.UiCore/obj`、`logcat.UiCore/bin`、`logcat.AndTools/obj`、`logcat.AndTools/bin` 四个目录都存在（均 gitignored、untracked）。于是 `git mv logcat.UiCore logcat-uicore` 之后，**`logcat.UiCore/` 会以「只剩 obj + bin 的空壳」继续存在**——而 2.1 / 2.2 原来的验证写的是「旧目录不存在」，执行者会看到一个必然为 False 的断言，第一反应是「mv 失败了」，进而可能去动 `git reset` 这类高风险命令。

**决定**：新增 **tasks 2.0**，在 2.1 之前删掉这四个目录（本机、gitignored、破坏性操作，按 D-28 单独确认）；2.1 / 2.2 的验证改为「`Test-Path <旧目录>` 为 False（依赖 2.0 已清）」，并显式写「未清则旧目录留空壳，别误判为 mv 失败」；3.8 的清理范围标注为「只管根级，两个改名工程的已在 2.0」。

**副作用（正向）**：旧 `obj/project.assets.json` 里存的是改名前的绝对路径，虽然会被重新 restore 覆盖，但清掉可让 3.8 的「从零重建」验证更干净。

### D-61 apply 只在三个判据点停靠（维护者 2026-11-08 定，第十二轮评审）

D-40 只定了「commit 粒度按 8 个 section」，没定「什么时候停下来向维护者回报」。46 条任务里有三个「一旦不平就得回头查」的判据点：**1.4 基线**（用例数与三项覆盖率）、**4.5 全量测试**、**7.3 覆盖率对账**。

**决定**：只在**这三个台阶停**，其余连续执行——

| 停靠点 | 位置 | 回报内容 |
| --- | --- | --- |
| ① | 1.x 完成（含 1.4） | 环境建成（worktree / 合入 / 基线 commit）+ 1.4 基线数字 |
| ② | 4.5 完成 | 3.x 构建结果（含 3.1 的 `Link` 拍平验证）+ 4.5 全量用例数字 |
| ③ | 7.3 完成 | 三项覆盖率与基线的逐项对比结论（含分母） |

**为什么不停 8 次**：D-40 的 commit 粒度已经保证每个 section 可单独回看；每 section 都停会把你打断 8 次，而真正需要你拍板的只有「数字对不对」。

**为什么不能不停**：若 7.3 掉档才发现问题，前面 section 6 的文档改动要一起回头（属 D-42 的第一段回滚）。

### D-62 7.4 产出的两个 `V0.1.9-*` 目录保留（维护者 2026-11-08 定，第十二轮评审）

D-43 只说了「`publish/Release/` 下既有 19 个历史版本目录不动」，没说本次新产出的 `V0.1.9-selfcontained`（约 120 MB）与 `V0.1.9-fdd`（约 2 MB）算证据还是算垃圾；两者都在 `.gitignore` 的 `[Pp]ublish/` 内，留或删都不影响仓库。

**决定**：**保留**——它们是 D-58「全 True + 时间戳」判据的物证，交付后维护者可以自己点开看；发版时会重新 publish 到同名目录、自然覆盖。

### D-63 7.1 / 8.2 写「构建前确认无进程占用」（维护者 2026-11-08 定，第十二轮评审）

本仓库有反复踩到的坑：`dotnet build` 因输出文件被占用失败（`MSB3021` / 文件被另一进程锁），典型触发是刚跑过测试或 VS 开着。7.1 / 8.2 都是「构建 + 立刻测试」的时序，正好最容易撞上；tasks 原本没有任何提示，执行者会把它当成搬迁引入的错误去排查。

**决定**：7.1 与 8.2 各加一句——构建前先 `Get-Process dotnet,devenv -ErrorAction SilentlyContinue` 确认无占用，有则先关 VS / 等测试进程退出。

### D-64 环境自检前移到动 git 之前（新增 tasks 1.0）（维护者 2026-11-08 定，第十三轮评审）

1.4 是第一次真正跑测试，但它排在 **1.1 merge → 1.2 入库 → 1.2b `git rm --cached` → 1.3 建 worktree** 之后——仓库状态已改动四次，才第一次验证「环境能不能跑」。若此时发现 `dotnet` 不可用、Python 缺失或测试本来就红，前四步 git 操作就白做一遍。

**决定**：新增 **tasks 1.0「环境自检（只读）」**：`dotnet --version` / `python --version` / `git worktree list` + 一条最短子集测试（`--filter FullyQualifiedName~LocTableTests`），确认能启动并出结果；任一不可用就停下报告，不得带环境问题进 1.1。

**边界**：1.0 不产生任何**跟踪文件**变更（`git status --short` 与开工前一致），因此不需要 git 写授权；它会在 `bin/` `obj/` 里产生本机产物，这些随后由 3.8 清理。

**实测（2026-11-08）**：`dotnet 10.0.401`、`Python 3.13.2` 均可用——2.0 / 3.8 的清理与 7.3 的 `python tests/coverage-report.py` 都有落脚点。

### D-65 7.2 的 en-US 复现改为 PowerShell 优先（维护者 2026-11-08 定，第十四轮评审）

7.2 原本写「`bash scripts/run-tests-en-us.sh` 或 `$env:LOGCAT_FORCE_UI_CULTURE='en-US'`」。实测（2026-11-08）：本机 `bash` 在 PATH 里命中的是 **Windows 自带的 WSL 启动器**，WSL 未安装时 `bash <cmd>` 直接报错退出（退出码 1、无正常输出）；Git Bash 虽已安装但不在 `bash` 的解析结果里。照抄第一种会得到一个与搬迁完全无关的失败——与 D-63 的 MSB3021 属同一类误判。

**决定**：7.2 改为**优先**用 PowerShell 方式（`$env:LOGCAT_FORCE_UI_CULTURE='en-US'`，即 `docs/TESTING.md:514` 的等价命令）；`bash scripts/run-tests-en-us.sh` 标注为「仅供 CI（Linux runner）参考」。

**边界（重要）**：**不改** `docs/TESTING.md:507` 及其所在段的 CI 说明，也**不改** `openspec/config.yaml:7`「本地复现 CI 环境用 bash 脚本」——它们面向通用环境（CI 与克隆者），本机不可用不等于规则错；tasks 的执行指引优先。

**未取的方案**：把 TESTING / config 里的 bash 说明一起改——会让 CI 文档与本机环境耦合，属越界。

## Risks / Trade-offs

- **[搬迁后 `ExcludeByFile` 的 `**` glob 可能失配，覆盖率掉档]** → 重组验收必须实跑一次覆盖率并与 **1.4 实测基线**三项对比；掉档时修 `coverlet.runsettings`（必要时把 glob 改成能匹配新深度的写法），**不下调标准**
- **[一次 `git mv` 三块，`git log --follow` 局部失效]** → 接受：同日为每个移动目录看一次 `git log --diff-filter=R --summary`，确认 rename 成对落库；不做历史清洗
- **[在途 `V0.1.9` 的后续 bug 修复回流冲突]** → 已由 D-17 消除：apply 前先把它的提交合回 master（实测只剩 **3 个文件 5 行**、零代码，BUILDING 已由 `f6c2925` 回流），分叉不存在就不存在回流
- **[`.tmp-test/realprobe*` 三个探针工程搬后即断，但 tstrun 不受影响（实测）]** → `realprobe` / `realprobe-main` / `realprobe-wt` 各 18 行 `..\..\Services\*.cs` 一类 source-include 会指向不存在的文件，属已知且有意延后的重建工作（归刀1）；**`tstrun` 只用 `ProjectReference` 指向未搬迁的 `tests` 工程，搬迁后仍可跑，可当 1.4 基线验证的第二条退路**
- **[文档路径漂移（BUILDING / TESTING / 双 README / THIRD-PARTY-NOTICES）]** → 验收含一次全仓 `logcat.csproj` / `logcat.UiCore` / `logcat.AndTools` 字面量扫描，除 `openspec/changes/**`、`openspec/notes/**` 与 gitignored 目录外应为零命中（`docs/FEATURES.md` 实测 0 处引用，不在清单内）
- **[根级 `None Include` 只改成 `..\` 会被静默拍平，发布包丢 `sample/` 与 `docs/images/` 层级]** → 见 D-11：七条全部补显式 `Link`；再由 publish 产物 `Test-Path` 清单兜住（`README.md` / `LICENSE` / `sample/fileproxy/fileproxy.py` / `docs/images/main.gif`）
- **[文档里的 markdown 链接会静默断链]** → 双 README 版本徽章与 `THIRD-PARTY-NOTICES.md` 声明链接都指向 `logcat.csproj`；按 D-14 全改，验收时对每处 `](` 目标 `Test-Path` 一次
- **[`app.ico` 移入 `icon/` 后两个 exe 的图标丢失且构建仍能成功]** → 两个 `ApplicationIcon` 都应是 `..\icon\app.ico`（android-tools 深度未变，不要写成 `..\..\`）；验收时查 `logcat.exe` / `logcat-andtools.exe` 的「属性-详细信息」里图标存在，并抽查 exe 图标未回落为默认图标
- **[`dotnet run --project .` 一类开发命令失效]** → 双 README 的 Build and run 段落同批更新为 `--project logcat-viewer`；验收时实跑一次
- **[根级 `bin/` `obj/` `.vs/` 与 `logcat.csproj.user` 成为孤儿]** → 均不入库、构建不依赖；但旧 `obj/*.assets.json` 与旧 `.vs/` 索引会让人误判「旧路径仍有效」，按 D-16 删除或迁移

## Migration Plan

1. **前置（在 master 上）**：`git merge V0.1.9` 抹平分叉（D-17），并把 `openspec/notes/` 的三份未提交改动入库（否则归档时 `git mv` 移不动）
2. 在独立 worktree（分支 `wt/restructure-project-layout`）中执行，主工作树保持干净；**先跑一次全量测试定基线**（D-18）
3. 按 `tasks.md` 顺序：新建 `icon/` 并移入图标 → `git mv` 三块目录 → 改 csproj / slnx → 改测试字面量 → 改脚本与文档（含 `openspec/config.yaml`） → 补 `.gitignore` → 清本机残留（D-16）
4. 每批后跑 `dotnet build logcat.slnx` 与 `dotnet test`，把口径数字记录下来
5. 全绿后跑覆盖率对账与两条 publish 产物验证
6. **回滚**（分两段，D-42）：**未合入时**丢弃 `wt/restructure-project-layout` 分支 + `git worktree remove`，master 上 1.1 的 merge commit 与 1.2 的 docs commit 对路径零影响、**不还原**；**8.1 合入后验收不过**用 `git revert -m 1 <8.1 的 merge commit>`，**不 `reset --hard`**；回滚后需重跑 `dotnet build` 重建 `bin/` / `obj/`

## Open Questions

无。（搬迁时机、目录命名、工程文件名、interop 归属、publish 落点、验收口径均已在探索笔记中定案，见 `openspec/notes/PROJECT-STRUCTURE-RESTRUCTURE.md` 的 C1–C7 / D1–D9；2026-11-08 第二轮评审的 5 项已按推荐全部定案——覆盖率基线复测 D-24、用例数字 9 处同步 D-25、`Directory.Build.props:4` 不改 D-26、探索笔记同批修订 D-27、git 写操作逐条授权 D-28；第三轮评审的 4 项——`TESTING.md:3` / `:8` 日期同批改 D-29、覆盖率徽章保留数字 D-30、持平容差 D-31、归档只归档本 change 笔记 D-32；第四轮评审的 4 项——`UT-AUDIT.md:533` 补前缀 D-33、`ci.yml` / `run-tests-en-us.sh` 注释补前缀 D-34、apply 窗口冻结 `V0.1.9` D-35、四个单引用探针同批修 D-36；第五轮评审的 4 项——完成记录载体 = `apply-log.md` D-37、`config.yaml` 不补布局 D-38、README 散文 `Forms/` 不改 D-39、提交粒度按 8 个 section 分批 D-40；第六轮评审的 3 项——交付门槛 = `apply-log.md` 不留「待填」D-41、回滚分两段（未合入丢分支 / 已合入 revert）D-42、`publish/Release/` 历史目录不动 D-43；第七轮评审的 3 项——交付说明 = `apply-log.md` §3 D-44、两条 merge 手写语义式 message D-45、`docs/memory/` 3 个存量文件另行取消跟踪 D-46；第八轮评审的 5 项——`docs/BUILDING.md` 4 处 `<None Include>` 字面量同批改 D-47、删 6.1 的「产物结构段落」从句 D-48、README 不写 `logcat-core/` / `logcat-mcp/` 预留 D-49、master 停留 `0.1.9` 只登记窗口期 D-51、`docs/memory/` 取消跟踪排在 apply 前单独做 D-52（D-50 稳定版发版归口维护者明确不关注，不写入本 change）；第九轮评审的 4 项——`apply-log.md` §2 补 7.1 / 3.1 两行 D-53、探索笔记状态行即改 D-54、6.9 扫描加裸 `app.ico` 规则 D-55、7.4 显式写 `V0.1.4` → `V0.1.9` 手改 D-56；第十轮评审的 3 项——section 6 加「行号只作提示」通则 D-57、7.4 的 `Test-Path` 清单同步手改版本号 + 时间戳判据 D-58、7.3 覆盖率命令内联写全 D-59；第十一轮评审的 1 项——搬迁前先清两个改名工程的 `bin/` / `obj/`（新增 tasks 2.0）D-60；第十二轮评审的 3 项——apply 只在三个判据点停靠（1.4 / 4.5 / 7.3）D-61、7.4 产出的两个 `V0.1.9-*` 目录保留 D-62、7.1 / 8.2 加「构建前确认无进程占用」D-63；第十三轮评审的 1 项——环境自检前移到动 git 之前（新增 tasks 1.0）D-64；第十四轮评审的 1 项——7.2 的 en-US 复现改为 PowerShell 优先（不改 TESTING / config 的 CI 说明）D-65；刀0 起的事项不属本 change。）
