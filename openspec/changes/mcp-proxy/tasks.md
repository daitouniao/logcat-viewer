# Tasks

## 1. 前置与 worktree

- [ ] 1.1 在独立 git worktree 中实施本 change：worktree 标识 = 分支 `wt/mcp-proxy`（路径以 `git worktree list` 定位，禁写本机绝对路径），目标分支 `master`；验证：`git worktree list` 列出该分支，主工作树 `git status` 保持干净
- [ ] 1.2 复核已定口径与本 change 一致（Node 工程独立于 `mcp/`、SDK v2 双包、`server__tool` 前缀命名空间、路由走对应表、仅 stdio 上游、下游仅 stdio、页面可增删改上游、回环 + 令牌 + Host 校验、管理端口默认随机、下游名合规化而上游名不设门槛）；验证：`design.md` D1–D19 无「待拍」标记，Open Questions 无待决项

## 2. 工程骨架

- [ ] 2.1 新建 `mcp/proxy/` Node 工程：工程清单（包名 `logcat-mcp-proxy`、类型模块、脚本 `build` / `start` / `test`、可执行入口 `bin` = `logcat-mcp-proxy`）、`tsconfig.json`（沿用 `mcp/` 的编译口径）、源码与测试目录骨架；依赖 `@modelcontextprotocol/client@2.4.0`、`@modelcontextprotocol/server@2.4.0`、`zod@4.6.7`（与 `mcp/` 同版本线）、TypeScript 5.9.3；启动参数 `--config <路径>` 与 `--port <端口>`（管理端口**默认取随机空闲端口**）；验证：`npm ci` 与 `npm run build` 成功，锁文件入库，`node dist/src/index.js --help` 可用
- [ ] 2.2 忽略规则：`.gitignore` 覆盖 `mcp/proxy/node_modules/`、`mcp/proxy/dist/` 与 `mcp/proxy/coverage/`（仓库根已有的 `mcp/dist/` 规则锚定在根，不覆盖嵌套目录）；运行配置在用户配置目录（D6），不入仓库；验证：`git status` 看不到上述路径
- [ ] 2.3 CI：`.github/workflows/ci.yml` 新增代理工程作业，Node 版本矩阵与既有 `mcp` 作业一致（`['26','22']`），步骤为 `npm ci` → `npm run build` → `npm test`；验证：作业配置与既有 `mcp` 作业同形

## 3. 配置层

- [ ] 3.1 配置模型与校验：条目形状 `{ id, command, args[], env{}, cwd?, enabled }`；`id` 唯一、满足 `^[a-z0-9][a-z0-9_-]*$` **且不含连续下划线 `__`**，`command` 非空；`env` 值取 `"env:VAR"` 形式时表示从代理进程环境变量取值、不落盘；重复 id、非法 id、含 `__` 的 id、缺 command 均给出可归因错误且不静默覆盖；验证：`ConfigValidationTests.RejectsDuplicateId`、`RejectsIllegalId`、`RejectsIdWithDoubleUnderscore`、`RejectsMissingCommand`、`AcceptsMinimalEntry`、`AcceptsEnvReferenceValue`
- [ ] 3.2 配置读写与原子落盘：默认路径为用户配置目录（Windows `%APPDATA%\logcat\mcp-proxy\config.json`，其他平台 `~/.config/logcat/mcp-proxy/config.json`），`--config` 可覆盖；写入走「临时文件 + rename」；目录不存在时自动创建；验证：`ConfigStoreTests.WritesAtomically`、`CreatesMissingDirectory`、`PersistsAcrossReload`
- [ ] 3.3 示例配置：仓库内提交 `mcp/proxy/config.example.json` 说明默认形状（不含真实凭据，并演示 `"env:VAR"` 引用写法）；验证：示例可被配置层接受

## 4. 上游生命周期

- [ ] 4.1 上游实例：以 stdio 子进程拉起配置中的每个已启用条目，建立客户端会话并列举其工具；单个上游拉起失败不影响其他上游，失败原因被记录；验证：`UpstreamLifecycleTests.StartsEnabledUpstreams`、`FailedUpstreamDoesNotBlockOthers`、`RecordsFailureReason`
- [ ] 4.2 状态上报：每个上游维护标识、运行状态、工具数量、最近失败原因、最近诊断输出尾部（环形缓冲，默认 200 行）；验证：`UpstreamStateTests.ReportsToolCount`、`KeepsBoundedDiagnosticTail`
- [ ] 4.3 停止与重启：停止终止子进程并从工具集合中移除其工具；重启销毁并重建会话、重新列举工具；验证：`UpstreamLifecycleTests.RestartRefreshesToolSet`、`StopRemovesTools`
- [ ] 4.4 并发拉起：上游拉起并发执行，单个慢上游不阻塞代理进入可服务状态；验证：`UpstreamLifecycleTests.SlowUpstreamDoesNotDelayServiceStart`
- [ ] 4.5 通道纪律：代理只向上游 stdout 写协议帧，不混入任何非协议内容；上游 stderr 收进诊断缓冲；验证：`UpstreamChannelTests.NoNonProtocolBytesOnUpstreamStdout`

## 5. 聚合与路由

- [ ] 5.1 命名空间聚合：下游工具名 = `<id>__<tool>`；不同上游的同名工具可无歧义区分；工具描述中可辨识来源上游；上游工具的输入模式原样保留；验证：`AggregationTests.NamespacesToolNames`、`DisambiguatesSameNamedTools`、`KeepsUpstreamInputSchema`
- [ ] 5.2 路由走对应表：代理持有「下游工具名 → (上游 id, 上游工具名)」注册表，调用时按表路由并转发，**不按分隔符切分名称反解**，产出原样回填；两个上游拼出同名下游工具时先注册者保留、后注册者判不可用并记冲突原因；验证：`RoutingTests.UsesRegistryNotStringSplit`、`RoutesToMatchingUpstream`、`ReturnsUpstreamResultUnchanged`、`ResolveAmbiguousIdAndToolNames`、`LaterUpstreamMarkedUnavailableOnNameCollision`
- [ ] 5.3 下游名合规化：代理对外暴露的工具名须满足 `^[a-zA-Z0-9_-]+$`、总长 ≤ 64、不含 `__`、不与内置工具名撞名；**上游原始名不设门槛**，不合规则受控规范化（非法字符替换 `_`、连续双划线压为单下划线、超长按「id 段完整、工具名段截断」），原始名写入工具描述，调用仍路由到原始工具；验证：`AggregationTests.NormalizesIllegalUpstreamToolName`、`KeepsOriginalNameInDescription`、`CallsThroughToOriginalTool`、`TruncatesOverlongNameKeepingIdSegment`
- [ ] 5.4 错误语义：按 spec 锁定的四个码值返回带错误标记的结果——前缀无对应上游或上游不提供该工具 → `TOOL_NOT_FOUND`、入参不符 → `TOOL_FAILED`、上游处理失败 → `UPSTREAM_TOOL_FAILED`、上游不可用 → `UPSTREAM_UNAVAILABLE`；描述指出上游标识；参数不符时请求不转发到上游；验证：`RoutingTests.UnknownNamespaceReportsToolNotFound`、`InvalidArgumentsRejectedLocally`、`UpstreamFailureReportsUpstreamToolFailed`、`UnavailableUpstreamReportsUpstreamUnavailable`
- [ ] 5.5 内置管理工具：`proxy_servers`（列上游状态、工具数、最近错误）与 `proxy_restart`（重启指定上游）；两者不与任何上游命名空间撞名，且均出现在下游工具列举结果中；验证：`BuiltinToolsTests.ListUpstreams`、`RestartUpstreamById`、`DoNotCollideWithUpstreamNamespace`、`AppearInToolListing`、`RestartUnknownUpstreamReportsToolNotFound`

## 6. 管理面

- [ ] 6.1 HTTP 管理服务：基于 Node 内置 `http`，仅绑回环地址，**端口默认取随机空闲端口**（`--port` 可显式指定）；提供上游列表 / 新增 / 修改 / 删除 / 启动 / 停止 / 重启 / 诊断尾部读取接口；验证：`ManagementApiTests.ListCreateUpdateDelete`、`StartsStopsRestarts`、`ReturnsDiagnosticTail`、`UsesRandomPortByDefault`
- [ ] 6.2 访问控制：启动生成随机令牌写入 stderr，并把带令牌的页面地址一并写到 stderr；读写请求须携带令牌，否则拒绝且不改动任何状态；`Host` 头非本机地址的请求被拒；验证：`ManagementApiTests.RejectsMissingToken`、`RejectsInvalidToken`、`RejectsForeignHostHeader`、`PrintsTokenizedPageUrlToStderr`
- [ ] 6.3 凭据不回显：页面读取配置时敏感值统一掩码；保存修改时掩码值不覆盖原值；验证：`ManagementApiTests.MasksSecrets`、`PreservesMaskedValuesOnSave`
- [ ] 6.4 管理页面：单页 HTML（原生 JS，无构建步骤），列出上游状态与工具数，提供增删改与启停重启操作，**每 2 秒轮询刷新**；页面渲染上游名与工具名按文本转义，不拼 `innerHTML`；验证：`ManagementPageTests.ServesPageWithUpstreamList`、`EscapesUpstreamNames`

## 7. 下游服务端

- [ ] 7.1 代理对下游以 stdio 承载 MCP 服务端：握手、能力声明、工具列举、工具调用；未握手即调用被拒；**能力集不声明任何网络传输形态**；复用 `mcp/server-shell` 的信封与错误码族口径；验证：`DownstreamServerTests.HandshakeThenListTools`、`RejectsCallBeforeInitialize`、`DeclaresNoNetworkTransport`
- [ ] 7.2 通道纪律：代理诊断与页面令牌只走 stderr，stdout 每一行均为合法协议消息；验证：`DownstreamServerTests.DiagnosticsNeverReachStdout`
- [ ] 7.3 退出纪律：代理退出时终止全部上游进程，不残留孤儿；验证：`DownstreamServerTests.ExitTerminatesUpstreams`

## 8. 端到端验证

- [ ] 8.1 以仓库内既有 `logcat-mcp`（`mcp/` 工程）为真实上游做端到端：代理拉起它、聚合其 `info` 工具为 `<id>__info`、调用成功、停止后工具消失、重启后恢复；前置：先在 `mcp/` 执行 `npm ci && npm run build`（`dist/` 不入库，本地与 CI 都须先构建）；验证：`E2eTests.AggregatesRealUpstreamTool`、`CallSThroughProxy`、`DisappearsAfterStop`、`ReturnsAfterRestart`
- [ ] 8.2 全量：`npm test` 全过；报告用例数与既有 `mcp/` 工程的用例数口径（两者分别记录，不合并）；验证：无新增失败

## 9. 文档联动

- [ ] 9.1 `docs/TESTING.md` 新增 §8「MCP 代理（Node，`mcp/proxy/`）」，用例数**单独记录、不并入**既有合计口径（与 §7 `mcp/` 同款写法）；验证：文档数字与 8.2 实测一致
- [ ] 9.2 `docs/BUILDING.md` 增补代理工程的安装 / 构建 / 运行动作，并写明与 `mcp/` 既有 `npm run harness` 的分工：harness 是开发期调试单服务端，代理是运行期聚合多上游；验证：文档命令与实际可执行
- [ ] 9.3 探索笔记 `openspec/notes/MCP-PROXY-V0.md` 收口：按 D18 定案结论回填口径表与「已否决的选项与理由」一节；验证：笔记与 design D1–D19、spec 条款三者口径一致

## 10. 收口

- [ ] 10.1 wt 合入目标分支：`git merge wt/mcp-proxy` 合入 **`master`**；验证：`git log master` 出现该合并、`git status` 干净；worktree 清除随归档执行（本任务只记合入完成）
- [ ] 10.2 交付验收材料：汇总 8.2 的测试口径数字、页面与工具的手测结论，交用户验收；验证：AI 不自行判定「验收通过」，等用户明示
- [ ] 10.3 代理登记进本机 MCP profile（stdio，可执行名 `logcat-mcp-proxy`）：**属本机环境变更，须用户当轮明示后才执行**，本刀不自动登记；验证：用户明示后登记成功，且登记项可被客户端拉起

## Workflow follow-up

- 本 change 目录、探索笔记 `openspec/notes/MCP-PROXY-V0.md` 与 `mcp/proxy/` 新工程在 apply 期间为未跟踪文件，需用户当轮授权后才 `git add` + commit。
- 用户明确「验收通过」后追问是否归档；仅在用户明示「可以归档 mcp-proxy」时执行 `openspec archive`。
- 归档同批：把探索笔记 `openspec/notes/MCP-PROXY-V0.md` 以 `git mv` 移入 `openspec/notes/archive/`（加归档日期前缀），并 `git worktree remove` 清除 `wt/mcp-proxy`。
- 归档门禁：10.1 已关闭且 `wt/mcp-proxy` 已合入 `master`；tasks 存在未关闭项时不得归档。
- 本 change 不含发版动作（版本号 5 处同步与打 tag 需另行当轮授权）；代理工程不占公共包名、不发布。
