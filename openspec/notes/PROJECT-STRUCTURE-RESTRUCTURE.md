# 工程结构重组探索（log 解析 / android 工具 / core / MCP）

- 状态：**待评审**（探索记录，未编码、未 propose）
- 日期：2026-11-07
- 关联提交：无（未编码）
- 验收结论：不适用（未实施）

## 0. 一句话结论

目标形态是四个同级块：log 解析、android 工具、core、MCP；第一步「不拆 core」，log 解析界面与引擎留在同一目录。真正阻塞 propose 的是两件事：**`logcat.UiCore` 这个第五块放在哪**、**Node.js MCP 与 core 之间用什么边界**。core 的语言（C# / Rust）仍按 `RUST-CORE-EVALUATION.md` 停在阶段 0 探针，本轮不改变该结论。

## 1. 诉求（维护者原话，2026-11-07）

1. 调整工程结构为四块：log 解析界面、android 工具、core、MCP。
2. 第一步：log 解析先放一起，不拆 core，放一个目录。
3. 同级放 android 工具、core、mcp。
4. MCP 计划用 Node.js 实现，调用 core 来完成读取和过滤。

## 2. 现状事实（HEAD 核对）

| 事实 | 依据 |
| --- | --- |
| 已是三工程 + 一个测试工程，不是单工程 | `logcat.csproj`、`logcat.UiCore/logcat.UiCore.csproj`、`logcat.AndTools/logcat.AndTools.csproj`、`tests/logcat.Tests/logcat.Tests.csproj` |
| log 解析界面与引擎同在主 exe 工程 | `frmMain*.cs`、`Forms/`、`Controls/`、`Services/`、`Models/` 均在仓库根 |
| `logcat.UiCore` 是两 exe 的共享层 | 主 exe 与 AndTools 均 `ProjectReference` 它；承载 `AdbManager`、`Loc` + `LocTable.*`、设备对话框、设备侧 Models |
| 解决方案未列全 | `logcat.slnx` 只列 `logcat.csproj` 与 `tests/logcat.Tests`；UiCore / AndTools 靠引用带入 |
| 引擎已无直接 UI 引用，但经 `Loc` 传递依赖 | `Services/LogParser.cs`、`LogDocument.cs`、`FilterEngine.cs`、`CrashExtractor.cs`、`CrashAggregator.cs`、`CrashTime.cs`、`SidecarCache*.cs`、`Models/CrashEvent.cs`、`Models/FilterSpec.cs` 不含 WinForms/Drawing；进度与描述文案走 `Loc`，而 `Loc` 在 UiCore |
| `logcat.Core` 尚不存在 | 全仓库无该工程 |
| core 语言未定 | `RUST-CORE-EVALUATION.md` 的阶段 0 探针未做 |
| 既有切片顺序 | 拆分卡（已归档）→ `core-extraction` → `mcp-server`（`BACKLOG-MCP-AND-FORMATS.md` D2 / D7） |
| 在途 change 只剩收口任务 | `fix-devices-changed-race` 3/4，仅 4.1（wt 合入）未勾选；`wt/sidecar-cache-index` 相对 `master` 0 条独有提交（已合入，勾选滞后） |
| 分支分叉 | `master` 与 `origin/master` 同步；`master...V0.1.8` 为 24 / 6（master 领先 24，V0.1.8 独有 6） |

现状目录：

```
<repo>/
├─ logcat.csproj            主 exe：log 解析界面 + 引擎
│  ├─ frmMain*.cs
│  ├─ Forms/  Controls/     界面
│  ├─ Services/             解析 / 索引 / 过滤 / 崩溃 / sidecar / FileProxy
│  └─ Models/
├─ logcat.UiCore/           共享库（net10.0-windows）
├─ logcat.AndTools/         android 工具 exe
├─ tests/logcat.Tests/      单一测试工程
├─ logcat.slnx
├─ Directory.Build.props    <Version> 唯一来源
└─ NuGet.config
```

## 3. 依赖关系

`UiCore` 被两个 exe 同时依赖，因此不能简单按功能块只留四个目录：

```
logcat-viewer ──> logcat.UiCore <── logcat-android-tools
       │                                  │
       └──────────── tests ───────────────┘

logcat-mcp (Node.js) ──> core（将来）
```

若把 `UiCore` 塞进 android 工具目录，依赖方向会变成 viewer → android 工具，语义倒置；若把它塞进 viewer 目录，则 android 工具 → viewer，同样倒置。故推荐保留独立共享块。

## 4. 目标布局候选

四个选项，均按「第一步只做目录搬迁、不改行为」为前提：

### 方案 A：五工程同级（推荐）

```
<repo>/
├─ logcat-viewer/           log 解析界面 + 引擎（第一步不拆 core）
├─ logcat-android-tools/    android 工具
├─ logcat-uicore/           共享层（两 exe 依赖）
├─ logcat-core/             core（等 core-extraction 再建）
└─ logcat-mcp/              Node.js MCP（等 mcp-server 再建）
```

第一步只搬前三块；`logcat-core/` 与 `logcat-mcp/` 等各自 change 落地时再建。理由：不改依赖方向；git 不跟踪空目录，提前建空 `core/` 要么加占位 README（无意义文件），要么根本留不下来。

### 方案 B：四工程同级，UiCore 并入 android 工具

目录数贴合原话，但 viewer 需要 `ProjectReference` 指向 android 工具目录内的共享工程，形成 viewer → android 工具的反向依赖。不推荐。

### 方案 C：四工程同级，UiCore 暂留仓库根

只搬 viewer 与 android 工具，UiCore 原地不动，core / mcp 目录后加。过渡期根目录最不整齐，且后续还要再搬一次。仅作兜底。

### 方案 D：只搬 log 解析界面

第一步只把主 exe 收进一个目录，android 工具与 core / mcp 的搬迁留到 `core-extraction` 一起做。搬迁次数最少，但与「同级放 android 工具」的原话不符。

**当前偏向：A。** 若维护者更在意目录数与清单一致，B / C 也能落地，只是依赖方向或过渡整洁度有代价。

## 5. 搬迁的路径耦合清单

搬迁不是纯 `git mv`，以下点位会直接打断构建或测试：

| 位置 | 耦合内容 |
| --- | --- |
| `logcat.csproj` | `None Include` 指向仓库根的 `README.md`、`README.zh-CN.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`DISCLAIMER.md`、`sample/**`、`docs/images/main.gif`；工程目录下移后全部要改成 `..\` 相对路径。`ApplicationIcon`、`Properties/` 随工程移动 |
| `tests/logcat.Tests/logcat.Tests.csproj` | 对主 exe 与 AndTools 的 `ProjectReference` 相对路径；`AssemblyMetadata("RepoRoot")` 以测试目录 `..\..` 计算仓库根（测试不搬则不变） |
| `tests/logcat.Tests/LocTableTests.cs` | `InlineData("logcat.UiCore/Forms/…")`；`IsLocTablePath` 的前缀判定；`FindRepoRoot` 断言仓库根存在 `logcat.csproj` |
| `tests/logcat.Tests/CiLocaleAssertionTests.cs` | 同样断言仓库根存在 `logcat.csproj` |
| `.github/workflows/ci.yml` | `dotnet build/test tests/logcat.Tests/...`（测试不搬则不变）；仓库根执行以读取 `NuGet.config` 的前提不变 |
| `logcat.slnx` | 只列了两个工程；重组后应把全部工程列全，测试工程一并列入 |
| `README.md` / `README.zh-CN.md` | 结构章节、构建 / 测试命令路径 |
| `docs/BUILDING.md` | 两条 `dotnet publish` 命令、产物验证路径 |
| `docs/TESTING.md` / `docs/UT-AUDIT.md` | 测试命令、覆盖率命令、源码扫描路径说明 |
| `scripts/run-tests.sh`、`scripts/run-tests-en-us.sh` | 测试工程与 `coverlet.runsettings`、`tests/coverage-report.py` 路径 |
| `tests/coverage-report.py`、`scripts/mutation-test.py` | 测试工程路径与报告输出路径 |
| `.gitignore` | 目前无 `node_modules/` 等 Node 产物模式；MCP 落地时需补 |

## 6. MCP（Node.js）与 core 的接缝

Node.js 这一定位与既有 `BACKLOG-MCP-AND-FORMATS.md` D6「推荐 NuGet dotnet tool」相反，属于**结论被推翻**，需要在 `mcp-server` propose 前重新拍板，并同步修订相关笔记口径。

Node 调 C# core 的候选边界：

| 方案 | 形态 | 代价 |
| --- | --- | --- |
| **甲（推荐）** | core 暴露长驻 stdio JSON / JSON-RPC 子进程，Node MCP 拉起并复用会话 | 多一层进程；但语言无关，未来 core 换 Rust 时 MCP 不动；索引生命周期跟子进程走 |
| 乙 | `node-api-dotnet` 进程内托管 .NET | 少一次 IPC；引入原生桥、Node 版本与 .NET 运行时耦合 |
| 丙 | core 做成 native 库 + N-API | 与 core 语言决策（Rust）绑定，语言未定前无法落地 |
| 丁 | 本地 HTTP / socket 服务 | 多一个端口与生命周期，v1 不需要 |

**当前偏向：甲。** 关键约束：Node 若每次调用都新起进程，索引会反复重建；MCP 会话必须持有长驻子进程（或 core 侧缓存），否则大文件查询不可用。

需一并确认的既有口径：输出固定英文 JSON（D3）；只读、显式绝对路径、单次输出上限（D9）。

## 7. 待澄清

### C1 第一步的目录落点（阻塞 propose）

- **为什么需要澄清**：`core` 作为同级目录，与「第一步不拆 core」互相排斥；这一步决定 tasks 里的路径和验收线。
- **有哪些方案**：方案 A / B / C / D（见 §4）。
- **当前偏向**：A。

### C2 `logcat.UiCore` 的归属与命名

- **为什么需要澄清**：它是两 exe 的共享层，不在原话的四块清单里，但搬迁时必须有落点。
- **有哪些方案**：独立第五块；并入 android 工具；暂留仓库根。
- **当前偏向**：独立第五块，目录名 `logcat-uicore`。

### C3 是否引入 `src/` 与目录 / 工程命名

- **为什么需要澄清**：影响所有构建、测试、文档路径；也会决定 MCP 的 Node 工程怎么摆。
- **有哪些方案**：顶层同级（贴合原话、路径改动最小）；统一放 `src/`（根目录更干净，后续加 `logcat-mcp` 不脏根）。
- **当前偏向**：顶层同级；若维护者希望根目录只留基础设施，再改 `src/`。

### C4 MCP 与 core 的调用边界

- **为什么需要澄清**：决定 core 对外契约的形状；在 `core-extraction` 定契约前必须先定。
- **有哪些方案**：甲 / 乙 / 丙 / 丁（见 §6）。
- **当前偏向**：甲（长驻 stdio 子进程）。

### C5 时序

- **为什么需要澄清**：`master` 与 `V0.1.8` 已分叉（24 / 6），大目录搬迁会加大 V0.1.8 修 bug 后回流的冲突面。
- **有哪些方案**：等 V0.1.8 稳定版发布后再动；或确认 V0.1.8 不再加修后立即动；或在独立 change / worktree 中并行。
- **当前偏向**：等 V0.1.8 稳定版发布后再动；本重组单独建 change，不与 `core-extraction` 合并（沿用 D7「工程结构 + 对外 API + 解析抽象同批改评审面过大」的理由）。

## 8. 显式猜测

| 内容 | 标注 |
| --- | --- |
| 目录搬迁对构建时长无实质影响 | **推断**，未实测 |
| 长驻子进程下 Node MCP 查询延迟与进程内接近 | **推断**，未实测 |
| 采用方案甲后，core 换语言不需要改 MCP 侧 | **推断**，前提是契约只走 stdio JSON |
| core-extraction 是否仍按 1a/1b 两步走 | **未定**，本笔记不改变 D7 原结论 |

## 9. 关联

- `BACKLOG-MCP-AND-FORMATS.md`：D1 双库边界、D2 顺序、D6 发布形态（本笔记提出 Node.js 推翻该推荐）、D7 切片。
- `MCP-FUNCTIONALITY.md`：MCP 范围与代码事实复核；其「进程内直接调用 core」的形态需按 §6 重新定。
- `MCP-LOG-ANALYSIS-API.md`：分析型工具面，与 D5 的关系仍未拍板。
- `RUST-CORE-EVALUATION.md`：core 语言决策与阶段 0 探针。
- `openspec/changes/archive/` 下的拆分卡：UiCore / AndTools 现状的来源，仅作状态说明。

## 10. 下一步

1. 维护者拍板 C1 / C2 / C3。
2. 走 `/opsx-propose` 建独立 change（建议名 `restructure-project-layout`），tasks 首条记录 worktree 标识，并把 §5 的路径耦合点逐项列为验证任务。
3. C4 / C5 在 `core-extraction` / `mcp-server` 的 propose 前补齐；本重组不实现任何 MCP 或 core 功能。
