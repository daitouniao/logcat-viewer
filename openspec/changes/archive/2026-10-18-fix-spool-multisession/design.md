# Design：跨会话并存的暂存目录活性保护（fix-spool-multisession）

动机与范围见 proposal.md，行为契约见 `specs/fileproxy/spec.md`（两条 MODIFIED 需求）。本文只写实现层面的决策、理由与接入点。

## Context

现状关键事实（均已代码核实；符号名为准）：

- 单实例互斥量名为 `Local\logcat-viewer-single-instance`（Services/SingleInstance.cs），`Local\` 前缀 = 每 Windows 会话范围。fileproxy change 的 design 已明确记录「`Local\` 前缀，规避跨会话 DACL 坑」——即跨会话互斥（`Global\`）当年评估过并否决，见本设计 D5。
- 启动清理 `CleanupStaleSpoolDirs`（Services/FileProxyService.cs）：枚举暂存根目录全部子目录，仅跳过 `activeDirs` 参数（本进程活跃集合），其余无时间阈值全删，删不掉只留痕。入口程序在启动守卫成功后于主界面启动前起后台线程调用（Program.cs），启动时本进程必然没有活跃目录，实际传参恒为 null——「跳过本进程活跃集合」这条判据在启动路径上从未真正生效过，只对测试与理论上的进程内二次清理有意义。
- 暂存根 = 应用所在目录下 `temp\logcat-proxy`（SpoolPaths.DefaultRoot）；每次获取用独立 guid 子目录（`NewSessionDir`，frmMain.cs spool 分流段创建），挂到 `LogDocument.SpoolDir`；`LogDocument.Close` 在既有释放序列之后删暂存目录并置 null（Services/LogDocument.cs）。
- spool 写盘期间暂存文件以排他共享打开（FileProxyService.SpoolCoreAsync）——写盘进行中的目录删不掉；**无保护窗口在「spool 完成 → 文档关闭」之间**，以及「目录已建、锁未建」的微秒窗口（见 D3）。
- 跨进程 / 跨会话互斥冒烟无法自动化：`SingleInstanceTests` 只覆盖同进程语义（互斥量语义等价），真双进程冒烟属维护者手工项——本 change 的活性锁同此惯例。

## Goals / Non-Goals

**Goals：**

- 暂存目录安全性与互斥量的覆盖范围解耦：无论两个实例因何并存（跨会话、未来的多进程多开），启动清理都不删对方活跃暂存目录
- 活性判定零协议、零心跳、零额外存储，崩溃 / 强杀后自动恢复「无主」语义
- 同会话内行为零变化；单一判据取代现有「参数传入的本进程活跃集合」双轨制

**Non-Goals：**

- 多实例功能全家桶（设置文件并发合并、每实例暂存根、`Global\` 互斥量）——proposal 已列 out of scope
- 同进程多窗口；代理双拉竞态

## Decisions

### D1 活性标记 = session 目录内锁文件 + 跨进程持锁句柄

每个暂存子目录在创建后立即获得一个锁文件，持有者以 `FileAccess.ReadWrite` + **`FileShare.Delete`** 保持句柄直到目录被删除；清理方对每个候选目录**尝试排他打开该锁文件**（读访问 + `FileShare.None`）：打不开 = 有主（跳过），打得开 = 无主（关闭探测句柄后删目录）。

共享模式必须是 `FileShare.Delete` 而非 `FileShare.None`：D4 要求 owner「先删目录、后释放锁」，而 `Directory.Delete` 递归删除时要以 DELETE 访问打开目录内文件——持锁句柄若为 `FileShare.None`，owner 删自己的目录同样共享冲突失败，每次 Close 都会留下残留。`FileShare.Delete` 的语义是「放行删除访问、拒绝一切读写访问」：探测方的排他打开必然失败，「打不开 = 有主」判据不变，owner 之外的建锁 / 读写也一律被拒。即「拒绝除删除以外的一切」，不是字面意义的完全排他。

备选与否决理由：

- **跨进程活跃登记表**（fileproxy D13 当年评估）：需要注册 / 心跳 / 崩溃清理 / 共享存储读写协调，复杂度显著更高，且 reintroduce 一个「登记表自身如何防并发写」的同类问题。否决。
- **时间阈值（如 24h）**：崩溃残留清理延迟一天；且他实例的长期活跃目录（拉取大文件可持续数小时）会被误判为残留删除——恰好把要修的场景修不干净。否决。
- **每目录持锁句柄（选定）**：Windows 文件共享语义天然跨进程；无协议无心跳；**进程崩溃 / 强杀时句柄被系统立即释放，目录自动变无主**——异常退出后**下次启动应用即可删除残留，不需要重启电脑**（进程终止时内核强制关闭其全部句柄，与退出路径无关：正常退出 / 崩溃 / 任务管理器强杀一律释放），与既有「崩溃残留全删」语义无缝衔接；同进程第二次排他打开自己的锁文件同样失败（Windows 共享校验按句柄、不按进程），因此「本实例活跃目录」与「他实例活跃目录」由同一判据覆盖，现有 `activeDirs` 参数可删除，消灭双轨制。

### D2 锁文件置于 session 目录内部，随目录同生共死

不放在暂存根做集中登记（那会退化成 D1 否决的登记表）。目录内锁文件随目录删除而消失，无孤儿登记项需要额外清理。探测逻辑只认这一个固定名。

### D3 无锁文件的目录按无主删除；建目录-建锁的微秒窗口可重试

无锁文件 = 从未持有或持有者已崩溃在持有之前 → 按无主删除。这同时覆盖：本 change 之前的存量残留（升级用户一次清理）、崩溃落在「目录已建、锁未建」之间。

残余竞态：owner 建完目录后、锁文件创建成功前，清理方恰好枚举到该目录 → 判无主 → 删。owner 随后创建锁文件失败（目录已不存在）→ 打开操作报 I/O 失败，用户重试即可。窗口微秒级，后果是**可重试的错误而非数据丢失**。实现侧用「创建即建锁、建锁失败重试一次」收窄（重试时重建目录），再失败才向上报错。

### D4 owner 删除顺序：先删目录、后释放锁

`LogDocument.Close` 保持现有「删目录 → 置 null」顺序，锁句柄释放挪到删目录之后。不变式：**目录存在 ⇒ 锁仍持有**。清理方不会看到「无锁 + 有目录」的假无主态超过 D3 的微秒窗口。

清理方探测与 owner 删除的交叉 race：清理方探测成功（无主）瞬间 owner 恰好开始删目录 → 清理方 `Directory.Delete` 失败 → 计 failed 留痕（现有语义）；或清理方探测句柄持有时 owner 删目录失败 → 留痕，残留由下次启动清理收走。两种交叉均只产生「一次失败计数 + 残留一个启动周期」，无数据损坏。接受，不引入额外协调。

### D5 互斥量保持 `Local\`，spec 措辞收敛为「本会话」

`Global\` 的否决理由（fileproxy design 已记录，本 change 维持）：一会话提权运行、另一会话普通权限时，第二实例对 `Global\` 互斥量访问被拒——`new Mutex(...)` 抛未处理异常，启动即崩，对「用户有时以管理员运行」的桌面工具不可接受。本 change 后暂存安全不再依赖互斥量覆盖范围，`Local\` + spec 收敛是自洽组合：同会话强单实例（2026-10-17 产品决策不变），跨会话安全并存（数据不再互删）。

### D6 计数与留痕口径不变

StartupLog 现有「删 X / 跳过 Y / 失败 Z」计数不变：「跳过」现含活性持有；「失败」现含删不掉与交叉 race。不新增计数维度（诊断留痕需求已覆盖「跳过」）。

## 接入点

**改动：**

| 位置 | 改动 |
| ---- | ---- |
| `Services/FileProxyService.cs`（`SpoolPaths`） | `NewSessionDir` 创建目录后立即建锁并返回 `SpoolDirLease` 租约（**公开 API 变更**：新增公开类型 `SpoolDirLease`；`NewSessionDir` 返回类型 `string` → `SpoolDirLease`）；句柄随目录生命周期；提供「排他打开探测」判据 `IsDirAlive`（打开成功 = 无主）；建锁失败重试一次（D3） |
| `Services/FileProxyService.cs`（`CleanupStaleSpoolDirs`） | 跳过判据从 `activeDirs` 参数改为活性探测；删除 `activeDirs` 参数（公开重载与 internal 重载同步），计数口径不变 |
| `Services/LogDocument.cs` | `SpoolDir` 属性类型 `string?` → `SpoolDirLease?`（**公开 API 变更**，随 `NewSessionDir` 返回值联动）；`Close` 先删目录、后释放锁（D4）；非代理文档路径零变化 |
| `frmMain.cs`（spool 分流段） | session 目录创建即持锁；取消 / 抢占 / 短读失败路径删目录后释放锁（与 `DeleteDirBestEffort` 同点） |
| `Program.cs` | 启动清理调用点适配新签名（行为不变） |

**测试：**

| 文件 | 改动 |
| ---- | ---- |
| `tests/logcat.Tests/SpoolLivenessTests.cs`（新增） | 锁语义：持有即探测失败、释放即探测成功、无锁文件判无主、本进程自持锁探测同样失败 |
| `tests/logcat.Tests/SpoolCleanupTests.cs` | 「活跃目录跳过」判据由参数传入改为测试内持锁（模拟他实例）；混合场景计数同步 |
| `tests/logcat.Tests/LogDocumentProxiedTests.cs` | 补「Close 后锁释放、目录可再清理」语义；非代理文档零行为变化锚不动 |
| `tests/logcat.Tests/FileProxyLoopbackTests.cs` | 取消 / 短读用例后断言无锁句柄泄漏（探测判定无主） |

## Risks / Trade-offs

- [建目录-建锁微秒窗口被清理命中] → 后果为可重试的 I/O 失败而非数据丢失；建锁失败重试一次收窄（D3）。
- [存量无锁残留目录被按无主删除] → 正是期望行为（升级一次清理）；且 fileproxy 之前的版本无 spool 功能，实际存量近零。
- [清理探测与 owner 删除交叉 race] → 仅一次失败计数 + 一个启动周期残留，无数据损坏（D4）。
- [活性锁只保护暂存目录，不保护 settings / favorites / commands 并发写] → 明确不在范围；跨会话并存下设置 last-writer-wins 为已知遗留，登记后续候选（proposal out of scope）。
- [真跨进程锁语义无法全自动化] → 同进程语义（含「自持锁打不开」这一 Windows 按句柄校验的关键性质）由单测覆盖；真双进程 / 双 RDP 会话冒烟留维护者手工（与 SingleInstanceTests 先例一致）。
- [API 面变更：内部 + 新增公开类型] → 删除 `activeDirs` 参数（调用点仅 Program.cs 与测试，同批改完，对外无暴露面）；新增公开类型 `SpoolDirLease`，`NewSessionDir` 返回类型与 `LogDocument.SpoolDir` 属性类型随改，调用点均限仓内（frmMain / 测试），无外部消费者。

## Migration / Rollback

- 数据迁移：无。设置零变化；锁文件只存于暂存目录内，随目录删除，无孤儿文件。
- 回滚：还原本 change 代码即可；回滚后跨会话互删风险复原（= 现状），无中间态残留需要清理。
- 发布说明：本 change 为缺陷修复（跨会话数据丢失风险），随版本 release note 记录即可，不需要 BREAKING 声明；同会话用户行为零变化。

## Open Questions

（无。活性锁方案、`Local\` 取舍、微秒窗口的处置均在本文闭环。）
