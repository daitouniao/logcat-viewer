# Tasks

> 前置说明：本 change 为缺陷修复（满足仓库「较大改动」清单：跨 ≥3 文件），按标准流程执行并携带 bug 报告（任务 3.2）。探索阶段实证结论见 design.md「已实证事实」；实施前先补跑两项待实证（任务 0.1），据此拍板监控策略。测试口径：`dotnet test` 本机可用（探索阶段已验证），每项任务的验证以对应测试类全绿为准；所有「测试通过」声明必须带口径数字。

## 0. 实施前补实证

- [x] 0.1 利用 `.tmp-test/adbprobe` 补跑 server 停止态与运行中被杀实验。已完成（2026-10-20）：`GetStatus` 停止态返回 `IsRunning=False` 不抛；`DeviceMonitor` 构造急切连接、停止态抛 `SocketException`；**监控僵尸态**（server 被杀后不 fault 也不再收事件）→ 自愈后须无条件重建监控；意外发现本机 `%TEMP%\adb.log` 达 3.7 GB 且 Permission denied 阻断一切 server 启动（原始问题本机真实根因），已删除恢复。结论全部回填 design.md「实证结论」，监控策略拍板为「无条件重建」
  - 注：会话内 dotnet 派生的 adb 无法创建 `%TEMP%\adb.log`（沙箱父进程相关），server 启停实验经 bash 侧编排完成

## 1. AdbManager 自愈

- [x] 1.1 新增 `public async Task<string?> EnsureServerRunningAsync()`：`GetStatus()` 探测（异常按未运行）→ 未运行时复用 `RunAdbAsync("start-server")` 拉起（exit ≠ 0 视为失败）→ 拉起后 `GetStatus()` 复核；失败统一返回提示串「未能启动 adb server（请确认 adb 已加入 PATH，或手动执行 adb start-server）：{原因}」；方法 SHALL NOT 触及监控。验证任务 3.1 用例通过
  - 已完成（2026-10-21）。注：返回形态按 1.2 与 design 拍板落为小结果对象 `AdbServerSelfHealResult(Prompt, Restarted)`——本行正文的 `Task<string?>` 签名由 1.2「实施时定形态，倾向小结果对象」覆盖（`Prompt` 为 null 即成功）；提示串格式与 design 逐字一致；单测覆盖见 3.1
- [x] 1.2 新增 internal 测试构造注入「状态探测 / 拉起」假实现，公共构造签名不变；自愈结果须携带「是否发生了拉起」信号供调用方决定是否重建监控（实施时定形态，倾向小结果对象）。验证任务 3.1 用例通过、单测无任何 5037 连接
  - 已完成（2026-10-21）：internal 构造注入四个 seam（状态探测 / 自愈拉起 / 构造时拉起 / 监控 socket 工厂），公共构造签名不变、生产行为逐字一致；`EnsureServerRunningAsync` 自身不触及监控；结果对象 `Models/AdbServerSelfHealResult(Prompt, Restarted)`；单测零触网（无 5037、不触真实 `AdbServer`、不派生 adb 进程）
- [x] 1.3 监控无条件重建：`AdbManager` 新增 `RestartMonitor()`（StopMonitor+StartMonitor）；`StartMonitor` 的 fire-and-forget 加 `Task.Run` 包裹防未观察异常（OCE 静默，其余 LogWarning）；`frmMain` 在自愈拉起成功后调 `RestartMonitor()`（僵尸态实证：不能以异常为重建触发器）。验证：单测（经注入）通过 + 手工场景验证
  - 已完成（2026-10-21）：`RestartMonitor()`=Stop+Start；包装层 OCE 静默、其余 LogWarning、**不置空 `_monitor`**（OCE 冒泡时置空会误杀刚重建的新监控，有单测）；另给 `StopMonitor` 的 `Dispose` 加 try/catch——实证监控循环已故障时 `DeviceMonitor.Dispose` 会把故障异常再抛一遍，不防护则 `RestartMonitor` 把异常抛进刷新流程（async void 链路会崩）。单测（经注入）+ adbprobe 库行为实证通过；手工场景验证随 3.3

## 2. frmMain 接入

- [x] 2.1 `AdbRefresh` 入口先 `EnsureServerRunning`：非 null → `ClearDeviceList()` + `ShowStatusRaw(提示串)` + 刷新按钮保持可用 + return；提取 `ClearDeviceList`（清缓存、Items、残留 Text）供空列表分支复用，`OnDevicesChanged` 不动。验证：手工启动一次，server 正常时刷新行为与改动前一致（spec 场景 3）
  - 已完成（2026-10-21）：入口先 `await EnsureServerRunningAsync()`；`Prompt` 非 null → `ClearDeviceList()` + `ShowStatusRaw(prompt)`（运行时数据消息，不走会炸花括号的 `Loc.F`）+ `SetAdbButtons(true)`（刷新按钮保持可用、可重试）+ return；`Restarted` → `RestartMonitor()`；提取 `ClearDeviceList` 供空列表分支复用，`OnDevicesChanged` 未动。server 正常时刷新路径与改动前逐字一致（spec 场景 3；探测段由单测「已运行零副作用」覆盖，端到端手工随 3.3）

## 3. 测试与文档收尾

- [x] 3.1 新增 `AdbManagerServerSelfHealTests`：拉起成功返 null、拉起异常返提示串（含引导词与原始消息）、构造失败不抛、（甲时）监控失败置空可重建与 OCE 不置空。验证 `dotnet test` 受影响类全绿 + 全量无新增失败，带口径数字
  - 已完成（2026-10-21）：9 条用例（design 测试设计 6 条，「exit≠0」与「复核仍不可达」拆为两条；+ 监控失败置空可重建、OCE 不置空）。经 internal 注入 + `FakeAdbSocket`（假 `IAdbSocket` 驱动 `DeviceMonitor` 循环）零触网。口径：**tstrun 全量 637 过 / 0 失败 / 0 跳过**（628 基线 + 9 新增，均为 `[Fact]`；连跑三遍稳定）。注：本机 `dotnet test` 不可用（testhost 0x80070005），口径走 tstrun（tasks 前置说明里「dotnet test 可用」的探索阶段结论已被项目 context 与 TESTING.md 否定，以 tstrun 为准）
- [x] 3.2 bug 报告 `docs/bugfixes/ADB-SERVER-SELF-HEAL.md`（§3.7 统一文档头，状态「已实施待验收」，关联提交实施提交后回填）。验证文档头四字段齐全、文件名大写连字符
  - 已完成（2026-10-21）：四字段头齐全（状态「已实施待验收」/ 日期 2026-10-21 / 关联提交待回填——本轮无提交授权 / 验收结论待验收）；文件名大写连字符 ✓；含现象、根因（三条）、修复、验证（自动化口径 637 + 实证 + 手工三项）
- [x] 3.3 手工验收清单登记至 `docs/IMPLEMENTATION-TRACKER.md`（design.md「手工验收清单」三条：真机自愈、不在 PATH 提示、正常态零变化）。验证 tracker 出现本 change 行
  - 已完成（2026-10-21）：第一节新增 change 行（🖐 已实施待验收 + 代码锚点 + 637 口径）；第三节第 6 项登记三条（真机自愈 / 不在 PATH 提示 / 正常态零变化）；头部「最近核对」同步基线 628 → 637

## Workflow follow-up

- 维护者验收通过后，如其明确指示归档：执行 `openspec archive fix-adb-server-self-heal`（delta 并入 `openspec/specs/adb-server/`、change 入 archive），同批更新 `docs/FEATURES.md` 设备刷新条目与 README（活文档打 tag 时联动口径）
- 归档后验证：主规范出现「刷新设备时 adb server 自愈」需求、archive 目录结构正确、FEATURES 与主规范无漂移
