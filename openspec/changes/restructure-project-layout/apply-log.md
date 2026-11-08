# Apply Log：工程结构重组

> 本文件是 design **D-37** 定义的「完成记录」载体。tasks 里所有「写入完成记录」都指这里。
> 只记**实测值与来源命令**，不写推测；测不出来写「未验证」并注明原因。
> 归档时随 change 进 `openspec/changes/archive/`；**不进** `openspec/specs/`。

## 1. 基线（改任何文件之前 · tasks 1.4）

| 项 | 命令 | 实测值 |
| --- | --- | --- |
| 用例数（Debug） | `dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Debug` | 待填（不得预设 848 / 852） |
| 行覆盖率 | 见下方「覆盖率三步」 | 待填（不得预设 97.31%） |
| 分支覆盖率 | 同上 | 待填（不得预设 88.65%） |
| 方法覆盖率 | 同上 | 待填（不得预设 95.61%） |

覆盖率三步：`dotnet test tests/logcat.Tests/logcat.Tests.csproj --collect:"XPlat Code Coverage" --settings tests/logcat.Tests/coverlet.runsettings` → `python tests/coverage-report.py` → 抄行 / 分支 / 方法三项（含分母）。

## 2. 阶段记录

| 任务 | 要记录的东西 | 实测值 / 结论 |
| --- | --- | --- |
| 1.1 | `git rev-list --count master..V0.1.9`；`git rev-parse V0.1.9`（D-35 冻结基准）；合并带入的文件与行数（应 3 文件 5 行）；`docs/BUILDING.md` 是否零差异 | 待填 |
| 2.5 | `git status --short` 的 rename / 新增 / 删除条数 | 待填 |
| 3.1 | 根级 `None` 改 `..\` 后的本地 build：`bin\Debug\net10.0-windows\` 下 `sample\fileproxy\...` 与 `docs\images\main.gif` 的层级是否保留（D-11 拍平坑，D-53） | 待填 |
| 4.4 | 两条哨兵用例的 `scanned` 计数（应不变） | 待填 |
| 4.5 | 全量用例数与 1.4 基线逐项比对 | 待填 |
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

> 本表**就是交付说明**（design **D-44**）：交付时整段摘出随验收请求给出，不另写第二份。确无内容时写一行 `（无）`，不留空白表（同 D-41）。
