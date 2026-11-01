# Proposal：mutation-test-accel（变异测试加速）

## Why

变异测试（`scripts/mutation-test.py`，105 个变异点）全程约 35 分钟，其中 ~75% 花在「每个变异都跑全量测试」（实测单轮全量 14.7s，而子集只需 ~2s）。每个变异历史上只被 1~3 条用例抓住、KILLED 率 ~97%，全量重跑是纯浪费。加速后审计迭代更轻，覆盖率/有效性审计更愿意跑。

## What Changes

- **A 两级测试选择**：每个变异先跑与被变异文件相关的测试类子集（约定推导 + 显式覆盖表），子集有失败 → KILLED；子集全过 → 回退跑全量再判 SURVIVED。
- **B 失败即停**：反射 runner 增加失败即停开关（仅变异模式启用），首个失败即退出，汇总行格式不变。
- **脚本默认两级模式**，保留 `--full` 开关强制全量口径；输出各级命中占比，便于事后校准映射表。
- **文档同步**：runner 模板更新进 `docs/TESTING.md` §3.1；A+B 流程与双口径说明进 `docs/UT-AUDIT.md`。
- 一致性校验：落地后安排一轮「全量口径 × 全部变异」复跑，与两级模式结果对照，作为验收标准。

正确性约束（不可妥协）：KILLED 判定只会提前、不会失真（子集失败 = 有用例咬住）；SURVIVED 必须全量确认，否则引入假 SURVIVED；映射缺失/漂移时安全失败、直接全量。

## Capabilities

### New Capabilities

（无——本 change 是纯开发工具链改动，不触及产品行为契约。）

### Modified Capabilities

（无。已设 `skip_specs: true`：变异测试脚本与反射 runner 是测试基础设施，不属于 `openspec/specs/` 描述的系统能力。）

## Impact

- `scripts/mutation-test.py`（入库）：新增两级调度、文件→测试类映射、`--full` 开关、占比统计。
- `.tmp-test/tstrun` Runner.cs（git-ignored）：失败即停开关；其源码模板在 `docs/TESTING.md` §3.1 必须同步更新，否则 runner 重建后改动丢失；改后必做 756 条交叉验证。
- `docs/TESTING.md`（入库）：§3.1 runner 模板更新。
- `docs/UT-AUDIT.md`（入库）：A+B 流程、双口径（KILLED=子集口径、SURVIVED=全量口径）声明方式。
- 不触及产品代码（`Services/`、`Models/`、`Forms/`）、不改变测试用例本身、不改变 tstrun 日常用法默认行为。

预期收益：全程 35 → 约 15 分钟（KILLED ~7s/轮、回退 ~22s/轮，按 90% KILLED 在子集内命中估算；该比例未实测，落地后以脚本占比输出校准）。
