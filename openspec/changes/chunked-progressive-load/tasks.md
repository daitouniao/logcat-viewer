# Tasks

## 1. 前置与核实

- [ ] 1.1 本 change 在专属 git worktree 实施：分支 `wt/chunked-progressive-load`（路径以 `git worktree list` 定位；`git worktree add` 需当轮授权）
- [ ] 1.2 核实文档映射生命周期：确认解析推进期间消息读取映射是否已就绪、可取基指针；不成立则实现加载期提前建映射。测试：`LogDocumentTests.加载期可取得消息读取基指针`

## 2. 视图层：分段行号结构（design 决策 6/8）

- [ ] 2.1 视图行号模型改为分段块数组 + 段索引，覆盖 SetRows / AppendRows / CaptureAnchor / PinAnchor / ModelRowOf / DocRow 全部访问点。测试：`LogListViewTests.分段结构与整表结构_ModelRowOf_DocRow_往返一致`、`LogListViewTests.连续AppendRows与一次SetRows行号序列一致`
- [ ] 2.2 加载期发布上屏按时间片节流（约 100 ms 聚合一批），加载完成后做最终整批发布。测试：`LogListViewTests.节流窗口内多次追加只触发一次视图刷新`

## 3. 分块加载管线（design 决策 1/2/3/7）

- [ ] 3.1 分块加载编排：自适应两段式块长（首块 4 MB、稳态 32 MB，可调），区间扫描推进 + 增量并入消费 carry + 滞后一条发布 + EOF 无条件补发末条。测试：`LogDocumentTests.分块加载与整份构建逐元素等价_多块长`、`LogDocumentTests.跨块续行并入上批末条不产生多余记录`、`LogDocumentTests.EOF补发末条无滞留`
- [ ] 3.2 小文件统一走同一条流式管线（单块即整份，无独立路径）。测试：`LogDocumentTests.小文件单块加载与整份构建等价`
- [ ] 3.3 取消加载整份清空：worker 换代 + 块边界检查取消，清空文档、视图与过滤结果回到空状态。测试：`LogDocumentTests.取消加载后文档回到空状态`、`frmMainTests.取消加载后列表与过滤结果清空`

## 4. 加载期过滤（design 决策 4/8）

- [ ] 4.1 块扫描主流程拆出增量形态：会话（累计计数数组）+ 区间轮（只扫 `[上轮末尾, 本轮末尾)`，起点前移重叠量防跨轮漏配）+ 行段判定（只判新增候选行）。测试：`FilterEngineTests.增量区间轮与全量块扫描命中集合一致`、`FilterEngineTests.跨轮边界关键词命中不遗漏不重复`
- [ ] 4.2 增量过滤接入分派：满足可扫三条件走增量块扫描，否则回退逐条增量；加载期修改过滤条件对已发布前缀走既有全量路径后恢复增量。测试：`FilterEngineTests.加载期含Message关键词增量过滤与全量结论一致`、`FilterEngineTests.非ASCII关键词增量路径回退逐条`
- [ ] 4.3 加载期 UI 状态：过滤器与时间过滤增量生效，导出与崩溃面板置灰至完成，进度指示已发布量。测试：`frmMainTests.加载期导出与崩溃面板置灰加载完成后恢复`（Loc 断言按 LangScope.InZh/InEn 双语口径）

## 5. 等价性、性能与全量回归

- [ ] 5.1 等价性不变量进 CI：分块加载 vs 整份构建逐元素比对（行数、起始位置、长度、级别），多种块长参数化。测试：`LogDocumentTests.分块加载等价性不变量_参数化块长`
- [ ] 5.2 性能复测（1 GB 探针口径）：首屏 ≤ 0.5 s、总时长 ≤ 整份 × 1.05、内存峰值 ≤ 1.0 GB；验证分段行号结构下拼接开销不再随块数放大；结果与 docs/TESTING.md 数字联动
- [ ] 5.3 全量回归：dotnet test 全过并报告口径数字（相对基线无新增失败）；必要时 tstrun 交叉验证并声明跳过类别

## Workflow follow-up

- 验收由用户执行；「验收通过」后等明确归档指令。
- 归档时：`openspec archive chunked-progressive-load` + 同批将 `openspec/notes/CHUNKED-PROGRESSIVE-LOAD.md` 移入 `openspec/notes/archive/`（git mv + 日期前缀）+ 同批清除本 change 的 worktree（git worktree remove）。
- 发版按 AGENTS 阶段 8：版本号同步入库 5 处，需用户当轮明确指令。
