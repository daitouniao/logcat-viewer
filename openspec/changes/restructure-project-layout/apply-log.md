# Apply Log：工程结构重组

> 本文件是 design **D-37** 定义的「完成记录」载体。tasks 里所有「写入完成记录」都指这里。
> 只记**实测值与来源命令**，不写推测；测不出来写「未验证」并注明原因。
> 归档时随 change 进 `openspec/changes/archive/`；**不进** `openspec/specs/`。

## 1. 基线（改任何文件之前 · tasks 1.4）

| 项 | 命令 | 实测值 |
| --- | --- | --- |
| 用例数（Debug） | `dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Debug` | **852** 总计 / 852 通过 / 0 失败 / 0 跳过（= `V0.1.9-pre1` tag 记录值，等于 848 之外的另一个证据） |
| 行覆盖率 | 见下方「覆盖率三步」 | **94.26%**（2,677 / 2,840 行） |
| 分支覆盖率 | 同上 | **88.64%**（1,460 / 1,647 分支） |
| 方法覆盖率 | 同上 + 下方「方法覆盖率口径」 | **92.77%**（295 / 318 方法） |

覆盖率三步：`dotnet test tests/logcat.Tests/logcat.Tests.csproj --collect:"XPlat Code Coverage" --settings tests/logcat.Tests/coverlet.runsettings` → `python tests/coverage-report.py` → 抄行 / 分支 / 方法三项（含分母）。

- 采集日期：**2026-11-08**（worktree 首跑，改任何文件之前）；cobertura 文件 `tests/logcat.Tests/TestResults/cf4962cf-f14f-4e5e-b72a-f361ecb40725/coverage.cobertura.xml`
- 行 / 分支：取 cobertura 根节点 `line-rate` / `lines-covered` / `lines-valid` 与 `branch-rate` / `branches-covered` / `branches-valid`
- **方法覆盖率口径**（本次记录用，7.3 必须同法复算）：`//class/methods/method` 共 N 条，`line-rate > 0` 计为已覆盖；本次 `295/318`
- 与 `docs/TESTING.md:13-15` 的 2026-11-01 采值（97.31% / 88.65% / 95.61%，分母 3,934 / 1,833 / 433）**分母均不同**——基线已先期漂移，7.3 只与本表比（见 §3）

## 2. 阶段记录

| 任务 | 要记录的东西 | 实测值 / 结论 |
| --- | --- | --- |
| 1.1 | `git rev-list --count master..V0.1.9`；`git rev-parse V0.1.9`（D-35 冻结基准）；合并带入的文件与行数（应 3 文件 5 行）；`docs/BUILDING.md` 是否零差异 | `master..V0.1.9` = **0**；`V0.1.9` = **354c301**；merge commit **778829a**，`3 files changed, 5 insertions(+), 5 deletions(-)`（`Directory.Build.props` / `README.md` / `README.zh-CN.md`）；`docs/BUILDING.md` 差异 **0 行**。⚠️ `<Version>` 实测为 **`0.1.9-pre1`**（tasks 预估 `0.1.9`）：V0.1.9 分支本就是预发布号，非本 change 改号 |
| 2.5 | `git status --short` 的 rename / 新增 / 删除条数 | **R = 90 / ?? = 0 / D = 0**（M = 2，为 `apply-log.md` / `tasks.md` 自身）；90 = viewer 32 + uicore 51 + andtools 6 + `app.ico` 1，与搬迁前计数逐项相等；**90 条 R 的文件名主干全部未变**（脚本比对 `BASENAME_CHANGED=0`）——csproj 的 R 条目只是目录段变化（`logcat.csproj -> logcat-viewer/logcat.csproj`），文件名未改，符合 D-23 |
| 3.1 | 根级 `None` 改 `..\` 后的本地 build：`bin\Debug\net10.0-windows\` 下 `sample\fileproxy\...` 与 `docs\images\main.gif` 的层级是否保留（D-11 拍平坑，D-53） | **层级全部保留**：`sample/fileproxy/fileproxy.py` True、`sample/fileproxy/fileproxy.js` True、`docs/images/main.gif` True、`README.md` True、`LICENSE` True；输出根下**零** `fileproxy.py` / `main.gif` / `fileproxy.js` 裸文件（无拍平）。同时 `logcat-viewer/bin/.../logcat.UiCore.dll` True（3.3 的 ProjectReference 生效）。**另做一次 `dotnet publish logcat-viewer/logcat.csproj -c Release -o <临时目录>`**：`sample\fileproxy\{fileproxy.py,fileproxy.js,README.md,README.zh-CN.md}`、`docs\images\main.gif`、`README.md`、`README.zh-CN.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`DISCLAIMER.md`、`logcat.UiCore.dll`、`logcat.exe` **均 True**；输出根下无 `fileproxy.py` / `main.gif` / `fileproxy.js` 裸文件，`sample\` 子树结构与仓库内一致 |
| 4.4 | 两条哨兵用例的 `scanned` 计数（应不变） | **`scanned` 实计（LocTableTests）**：`本地化调用点的中文键都有译文`（哨兵 `>300`）= **461**；`本地化格式串的占位符数与实参个数一致`（哨兵 `>50`）= **180**；另记两条扫描同类：`绑定资源键不得是已译文本` = **168**、`列头BindCol必须挂进Columns集合` = **23**。取数方式：临时 `Console.WriteLine` 插桩一次运行后立即还原（已复核 `SCAN_PROBE` 残留 0 行）。`.tmp-test/` 影响面：主树 `.tmp-test` 下 **82 个 `.cs`**、`Loc.(T\|F\|Bind)` 命中 **0 处** → 补排除后计数理论不变。⚠️ 1.4 基线**当时未采**这两个计数，本次为**首次记录**，无法逐值对比；「不变」的依据是上述 0 命中，非历史同值 |
| 4.5 | 全量用例数与 1.4 基线逐项比对 | **852 总计 / 852 通过 / 0 失败 / 0 跳过**；行 `dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Debug`。与 1.4 基线（852 / 852 / 0 / 0）**逐项一致**，无新增失败、无用例增减。构建期 warning 仍为既有 2 条（`DeviceSelectorTests.cs:133` xUnit2013、`FilterEngineTests.cs:689` xUnit2029） |
| 5.1 | 8 条现役变异路径的 `Test-Path`；stale 条目数 | **全部 True（8/8，去重后 8 个文件）**，缺失 **0**；`SUBSET_OVERRIDES` 7 个键均指向存在文件。清理后：`TEXT_MUTATIONS` **75 条** / `LINE_MUTATIONS` **7 条**：删掉 17 条已失效条目（CommandStore 5 文本 + 2 行号、FavoritesStore 7、AppSettings 3、AdbManager 3），删掉 `SUBSET_OVERRIDES` 的 `AppSettings.cs` 键。**锚点健康度（额外采证）**：75 条文本锚点全部精确命中 1 次（无 SKIP-匹配数噪声）；行号型 7 条中 **6 条命中、1 条失配**（见§3） |
| 5.2 | `git check-ignore -v logcat-core/target/debug/x` / `logcat-mcp/node_modules/a.js` | 均命中新规则：`.gitignore:17:target/`、`.gitignore:18:node_modules/` |
| 5.3 | `testdata/big_test.log` 与 `logcat_core.dll` 的忽略来源 | 未变：`*.log`（`.gitignore:68`）、`*.dll`（`.gitignore:48`）均仍命中；本 change **未开**任何新例外 |
| 6.9 | 全仓字面量扫描的分类结果（路径命中 / 命名空间命中 / 工程文件名命中） | 待填 |
| 7.1 | `dotnet build logcat.slnx -c Debug` 的 error 数；4 个工程是否都出现（D-53） | 待填 |
| 7.2 | Release / Debug / en-US 三次的用例数字 | 待填 |
| 7.3 | 三项覆盖率与基线对比：分母是否一致、比率差值、是否落在 D-31 容差内 | 待填 |
| 7.4 | 双包 `Test-Path` 清单逐条 True / False | 待填 |
| 7.5 | 四个探针 `ProjectReference` 目标的 `Test-Path`；三条 realprobe 的断链清单 | 待填 |
| 8.2 | master 上 `dotnet build logcat.slnx` 与全量 `dotnet test` 的数字 | 待填 |

## 3. 未验证 / 异常

| 项 | 原因 | 处置 |
| --- | --- | --- |
| 覆盖率基线分母与 `docs/TESTING.md` 的 2026-11-01 采值不同（行 2,840 vs 3,934、分支 1,647 vs 1,833、方法 318 vs 433） | 旧采值早已漂移（工程拆分后统计口径变化，文档 `:16` 也写着「另行重采」从未执行），**非搬迁造成**；1.4 是在改任何文件之前采的 | 7.3 的「持平」只对 1.4 基线比较；按 7.3 后段要求，同批把 `docs/TESTING.md:13-15`、双 README 覆盖率徽章与 `README.md:194` 更新为本次实测值 |
| **图标目视**（3.3 / 3.4 要求的「exe 属性-详细信息里可见图标」） | 无图形化核对手段，未做目视 | 替代证据：① 构建全文**无图标缺失告警**（如 MSB3552），只有 2 条 CS9191 + 2 条 xUnit 分析器 warning；② `Shell.Application` 读 `System.Copyright` 两 exe 均可读到正确版权串；③ `ExtractAssociatedIcon` 两 exe 均能取到 32×32 图标且哈希一致。真图标与默认图标的区分留待 7.4 双包产物核对时目视 |
| 5.1 删掉的 4 条 stale 条目里，**15 个锚点在新位置仍可命中**（探针实测：AdbManager 3/3、CommandStore 5 文本 + 2 行号、FavoritesStore 7/7），仅 `AppSettings` 的 3 条彻底失效（`AppSettings` 类已不存在，现为 `LogcatSettings`） | 这些条目是 UiCore 拆分时路径没跟着改留下的，在本仓库从未生效 | **按 design D-7 删除**（本 change 不动变异覆盖范围）。若要复活这 15 个锚点，需把路径改指 `logcat-uicore/Services/…` 并跑一轮变异验证，属独立改动，不在本 change 做 |
| `LINE_MUTATIONS` 的 `LogDocument.cs:240`（`TagOf：去掉上界检查`）锚点失配：实际代码在 **:309**（脚本注释说「TagOf 从 253 漂到 240」） | 既有漂移，发生在本 change 之前（搬迁不改文件内容） | 未改（不属 5.1 范围）。后果仅是该点静默 SKIP（脚本设计上的安全失败）；修正只需改行号，可并入刀 1 的脚本维护 |

> 本表**就是交付说明**（design **D-44**）：交付时整段摘出随验收请求给出，不另写第二份。确无内容时写一行 `（无）`，不留空白表（同 D-41）。
