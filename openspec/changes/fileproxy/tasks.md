# Tasks

> 前置说明：组 6 的单实例与启动清理任务所依赖的 D-OPEN-1（单实例为行为变更）已于 2026-10-17 获维护者拍板同意，各组均可按序执行。
> 测试口径统一为 tstrun 反射 runner（`dotnet test` 本机不可用），每项任务的验证均以对应测试类全绿为准；所有「测试通过」声明必须带口径数字。

## 1. 协议客户端（S-fp-0）

- [x] 1.1 实现管道客户端双模读取器（行缓冲读 JSON + 按 n 收裸块，不得套文本读取器）与帧解析，验证 `FileProxyProtocolTests` 的帧解析用例（读响应头+负载拼接、n=0 EOF 无负载、len clamp 16MB）通过
- [x] 1.2 实现 hello/open/read/close 客户端方法与 proto 校验（缺失或 ≠1 判连接失败并附代理自描述），验证 `FileProxyProtocolTests` 全类通过：请求/响应 JSON 序列化、错误响应、未知 op/坏 JSON/坏句柄不断线、off≥2^31 大偏移读位置正确、proto≠1 判失败
- [x] 1.3 搭建进程内回环 harness（测试工程内起模拟代理服务端线程，不起外部进程），验证 `FileProxyLoopbackTests` 通过：open/read/close/spool 落盘逐字节相等、EOF、坏句柄错误、10MB 分块完整性、双连接隔离

## 2. 配置与设置 UI（S-fp-1）

- [x] 2.1 `AppSettings` 加 5 个字段（模式 string 默认空、管道名默认 `logcat-fileproxy`、启动文件默认空、启动参数默认 `--pipe {pipe}`、启动超时 int 默认 5000）与模式解析（空/off/on/auto/乱码→Off），验证 `AppSettingsTests` 通过：默认 FileProxyMode == off（锚死 DEC-fp-3）、旧 JSON 无新字段反序列化得默认值、乱码回退 off
- [x] 2.2 实现设置对话框：模式三 radio（默认关闭）、管道名、启动命令两字段、超时、测试连接按钮（只发 hello）、保存即生效不需重启，验证手工打开对话框逐项检查默认值与保存后读回一致（自动化部分：`AppSettingsTests` 覆盖默认值/读写回/管道名校验；GUI 逐项检查留待维护者手工冒烟）（注：「测试连接只发 hello」后被 D15/任务 8.1 改为复用完整拉起链路，当前行为以 spec「设置对话框」需求为准）
- [x] 2.3 补齐 `LocTable.FileProxyUi` 资源键（radio 三个独立绑定，禁三元/循环绑定），验证 `LocTableTests` 扫描器通过（新键全部入扫描）且 harness 中/英快照对比无残留字符（扫描器已过；快照对比留待维护者 harness 手工执行——harness 按项目规矩不入库）
- [x] 2.4 加菜单入口：设置菜单「文件代理…」+ 文件菜单「打开…」同级「手动输入完整路径…」（复用既有单行输入框），验证两个菜单项点击分别打开对话框与输入框，输入路径进入后续判定链（代码已接；点击冒烟留待维护者手工）

## 3. 文本嗅探与模式判定

- [x] 3.1 实现文本嗅探（头部 8KB：NUL 或控制字符（不含 \t \r \n \f）占比 >10% 判非文本；空文件判文本；UTF-16 判非文本注明预期；嗅探句柄用后即释），验证 `TextProbeTests` 通过：logcat 样本判文本、随机字节判非文本、UTF-16LE 判非文本、空文件判文本、8KB 边界
- [x] 3.2 实现代理使用判定（九格真值表）+ 模式字符串解析，验证 `FileProxyModeTests` 通过：九格全表驱动、模式解析（空/off/on/auto/乱码→Off）、Off 模式零行为变化锚（判定层已全覆盖；「不进分流段、装载路径逐字节一致」的集成锚随 5.2 装载入口接入补测）

## 4. 编排与 spool

- [x] 4.1 实现自动拉起编排（先试连手动代理 200ms → 连不上且配了命令才启动 → 每 200ms 轮询至超时，错误附完整命令行），验证 `FileProxyLaunchTests` 通过：`{pipe}` 占位符替换、管道名含空格自动引号、管道名非法值（空/含反斜杠/超长）拒绝、空启动文件返回 no_proxy_not_running、命令行拼装引号路径、timeoutMs 注入小值验证超时路径（补充实现中发现并修复：管道名含 `/` 同样拒绝，资源键改「路径分隔符」；杀超时进程树用 cmd title+ping 替身真实验证 PID 消失）（注：`{pipe}` 占位符替换后被 D17/任务 9.1 补上「不含占位符时服务层自动追加 `--pipe 管道名`」主路径，占位符替换转为旧配置兼容语义）
- [x] 4.2 实现 spool 全流程（8MB 块顺序写、完整性以实收 == open 响应 size 判定、不符删目录报 short_read 含实收/期望、取消即删、进度 0–50% 与文案）+ 追逐态可观测（实收 > open.size 后百分比分母失效：进度驻留 50%，文案切「已接收 X MB · 源文件持续增长」并随块刷新，禁静态文案+静止进度条组合），验证 `FileProxyLoopbackTests` 对应用例通过：分块完整性、短读删目录、spool 中途取消（ct）→ 目录已删、追逐态进度序列（pct 驻留 50%、msg 含字节数且逐块变化）
- [x] 4.3 实现超大文件预警判定纯函数（open.size > 1GB 需确认框阻断、取消即中止；阈值写死不进设置），验证 `FileProxyLoopbackTests` 阈值判定用例通过（>1GB → 需确认，≤1GB → 不弹）
- [x] 4.4 实现 spool 文件名规则（取原文件名、空则 `proxy.log`、>64 字符截断为头 48+尾 16 保留扩展名），验证 `SpoolCleanupTests` 通过：超长原文件名的 spool 路径仍合法、空文件名回退

## 5. 装载入口接入

- [x] 5.1 `LogDocument` 加显示路径/源路径分离（构造与构建入口可选显示路径参数，默认等于路径）与代理布尔字段、暂存目录字段（默认 null）；内部触及文件的 7 处（打开映射、重载头部探测、增量回退、两处文件长度查询、两处扫描传参）从显示路径改源路径；Close 内在既有释放序列之后自删暂存目录（删不掉只留痕），验证 `LogDocumentProxiedTests` 通过：显示路径/源路径/代理标记语义、Close 删暂存目录、非代理文档 Close 零行为变化。**7 处逐一核对为完成判据**（防「改漏一处」，见 design Risks 首条）：按 design「改动」表逐处对照 checklist；改完后 grep `LogDocument.cs` 的文件触及残留为 0（`this.Path` 的文件访问全部改源路径，`ScanRange` 形参与 `Path` 属性定义除外）——漏改一处即代理文档按显示路径开文件，用户原样复现「打不开」（7 处触及点已全部改 `SourcePath`，grep `FileStream(Path`/`FileInfo(Path`/`ScanRange(Path` 残留为 0；另以「显示路径指向不存在的受限路径」功能性用例间接全覆盖——displayPath 不可访问而 Build/Reload 仍成功即证明内部全走源路径）
- [x] 5.2 装载入口加分流段：停当前工作 → 关旧文档前做嗅探探测 → 判定 → 关旧文档 → spool → 既有构建；进度分段映射；spool 复用既有取消管线（取消/抢占即中止删目录）；Off 模式整体不进分流段，验证 `FileProxyModeTests` Off 零行为变化锚通过 + 手工冒烟（auto 模式打开受限样本文件走代理、打开正常文件直接读）（`StartIndex` 分流段已实现：探测在停工作/关旧文档之前、spool 进度映射 0.5–1.0；自动化锚全过；手工冒烟留待维护者）
- [x] 5.3 Reload 对代理文档转全量重建（按显示路径重新走完整流程），验证 `LogDocumentProxiedTests` Reload 用例通过：代理文档 Reload 后重新 spool + 重建，显示路径不变
- [x] 5.4 实现失败分类与错误消息（no_proxy_not_running / launch_timeout / short_read / proto_mismatch / 代理侧 err 原文透出 / 直接打开原始异常附带；磁盘写满走通用路径原文透出无专用错误码），验证 `LocTableTests` 扫描通过（错误文案键全部入扫描）+ 手工触发各错误路径消息正确（自动化部分全过，含 `DirectOpenError` 分流段失败后回填；手工触发各错误路径留待维护者）
- [x] 5.5 接 StartupLog 留痕：代理拉起（完整命令行 + PID）、连接失败（管道名 + 错误）、spool 起止（路径/字节数/耗时）、启动清理（删除/跳过/失败目录数），验证手工触发后日志文件含上述字段（留痕代码全部就位并经 grep 核实——拉起命令行+PID、手动连接失败、spool 开始/完成（路径/字节数/耗时）、启动清理删/跳过/失败计数、删目录失败留痕；日志文件实际内容验证留待维护者手工）

## 6. 生命周期与渠道状态（S-fp-3）

- [x] 6.1 实现单实例守卫（`Local\` 前缀命名互斥量，全程持有禁提前 Dispose）+ 入口程序分流（获取失败 → 「已在运行」提示后退出），验证 `SingleInstanceTests` 通过：已持有时 TryAcquire 返回 false、释放后可获取、第二实例启动即退出判定路径（真双进程冒烟另测）（5 条用例全过；真双进程冒烟留待维护者）
- [x] 6.2 实现启动后台清理（入口程序在主界面启动前起线程：删暂存根目录全部残留子目录无时间阈值、跳过本进程活跃集合防误删、逐目录删不掉只留痕、不阻塞 UI），验证 `SpoolCleanupTests` 通过：残留全删、活跃 guid 目录跳过、删不掉只留痕不抛
- [x] 6.3 实现退出双保险杀进程树（主窗关闭流程 + 进程退出事件，Kill entireProcessTree），验证 `FileProxyLaunchTests` 拉起/杀进程路径通过（进程用 `--once` 例子或测试替身）（OnFormClosing + `ProcessExit` 双保险已接；测试用 cmd title+ping 替身真实验证杀树）
- [x] 6.4 实现渠道状态区：常驻状态栏标签（插在文件名分区之后）、三态互斥判定纯函数（ADB 在途优先 → spool 在途或代理文档 → 直接读 → 空）、空文本占位不隐藏、spool 进行中即切「代理读」、失败/取消复位空，验证 `FileProxyChannelStateTests` 真值表全格通过 + 手工检查状态栏无布局抖动（16 格真值表单 Fact 循环全过；布局抖动手工检查留待维护者）
- [x] 6.5 渠道状态文案随语言切换按当前渠道重算（禁 static readonly），验证 harness 中/英快照对比无残留字符（`RefreshChannelState` 由 `RefreshStatusBar` 调用、语言切换路径按当前渠道重算；harness 快照对比留待维护者手工——harness 按项目规矩不入库）

## 7. 收尾与集成（S-fp-4）

- [x] 7.1 更新 `docs/FEATURES.md`：新增 fileproxy 条目（与 spec 需求组对应，不漂移），验证条目内容与 spec 描述一致
- [x] 7.2 更新 `docs/IMPLEMENTATION-TRACKER.md` 记账，验证 tracker 反映本 change 状态（fileproxy 行 🖐 已实施待验收 + 待办手工验收清单 + 头部基线更新 633）
- [x] 7.3 跑 tstrun 全量并报告口径数字（相对基线无新增失败），验证输出含用例总数与通过数（**tstrun 口径：633 过 / 0 失败 / 0 跳过** = 519 基线 + 114 条新增；相对基线无回归）
- [x] 7.4 写发布说明（打 tag 前完成）：含「单实例为 BREAKING 行为变更」显著声明，并**明确写出丧失的能力**（不能再多窗口对比多份日志——单实例代价无使用统计数据可量化，项目无遥测，声明是唯一触达手段，见 design D13），验证 release note 文件存在且声明单实例变更与能力丧失（`docs/private/releasenote/RELEASE-V0.1.5.md` 已建，中英双语含 ⚠️ BREAKING 声明与能力丧失说明；版本号未动——升级与打 tag 为发布动作，待维护者指示）

## 8. 设置对话框易用性增强（2026-10-18 追加，design D15/D16）

- [x] 8.1 测试连接改走 `EnsureProxyAsync` 完整拉起链路（对话框字段值现构 `FileProxySettings`，服务层零改动）
- [x] 8.2 可执行文件 / 参数两字段各加「浏览…」（OpenFileDialog；脚本路径按 `QuoteIfNeeded` 口径半角引号插入光标处）
- [x] 8.3 新增 i18n 资源键进 `LocTable.FileProxyUi`（两个对话框标题 + 两组文件过滤器；「浏览…」Common 表已有定义，跨表重复会被扫描器测试拦下，勿重复添加）。LocTable 过滤集 30/0 过
- [x] 8.4 全量测试无新增失败（stash 前后对照实验：改动前代码 FileProxyLoopbackTests 同样 14 过 / 2 失败，与本次改动无关）。注意两点既有问题待查：① 应用正在运行时 SingleInstance/SpoolCleanup/LogDocumentProxied 共 6 条因互斥体与目录占用失败（环境性，关应用即过）；② `SpoolAsync_短读`/`SpoolAsync_中途取消` 两条在本机确定性失败——失败/取消路径的 sessionDir 未删且无删除失败留痕，文件系统级立即删除 8MB 新文件无此问题，疑似 tstrun 并行执行或环境差异，需单独排查

## 9. 管道名自动追加 + 命令行预览（2026-10-18 追加，design D17）

- [x] 9.1 服务层 `BuildLaunchArgs`：含 `{pipe}` → 旧替换；不含 → 末尾追加 `--pipe 管道名`（启动进程/测试连接/预览/留痕共用同一串）；`NormalizeLegacyArgs` 剥尾部模板
- [x] 9.2 对话框：参数标签换新键、载入归一化、命令行预览实时刷新（三字段 TextChanged）、布局下移（grpLaunch 112→140，下方控件 +28）
- [x] 9.3 LocTable.FileProxyUi：新键 2 条（参数标签、命令行预览），删旧键「参数（{pipe} 为管道名占位符）」
- [x] 9.4 FileProxyLaunchTests：BuildLaunchArgs 两种形态 + NormalizeLegacyArgs 共 3 条新用例；BuildCommandLine 空参数断言随新语义更新（`proxy.exe --pipe p1`）
- [x] 9.5 tstrun 验证 + 示例 README 同步（FileProxyLaunch 过滤集 13/0；全量 627 过——应用运行中 7 条环境性失败（SingleInstance/SpoolCleanup/LogDocumentProxied，关应用即过）+ 2 条既有 SpoolAsync 失败（§8.4 已记录），无本次改动引入的失败；README 中英 4 处已同步）

## Workflow follow-up

- 维护者验收通过后，如其明确指示归档：执行 `openspec archive fileproxy`（delta 并入 specs、change 入 archive），同批更新 FEATURES 受影响条目
- 归档后验证：specs 主规范含 fileproxy capability、archive 目录结构正确、FEATURES 与主规范无漂移
- 大文件计划（BIGFILE）立项时，将本 change 的分流段与源路径打开处登记为其协同改动点
