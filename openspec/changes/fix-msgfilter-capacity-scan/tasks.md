# Tasks

- [x] 0.1 建独立 worktree：分支 `wt/fix-msgfilter-capacity-scan`（路径以 `git worktree list` 定位，禁写绝对路径）；后续任务均在其中实施

## 1. G′ 对照实验（Q1-B，修复前基线）

- [x] 1.1 在探针工程（`.tmp-test/realprobe/`，git-ignored；csproj 需显式 `<Compile Include>` 新增源文件）加 G′ 实验：保留全容量扫描、只统计扫描字节（跳过回退或只计数），与既有 G（现状复刻）/ H（切片折叠）组成三点对照；验证：同一测试文件上 G′ 耗时与「容量扫描 ≈ 16 s」量级吻合，扫描字节数 ≈ 行数 × 缓冲容量
- [x] 1.2 将 G′ 实测数字回填本 change 的 design.md「Context」一处（主因由反算升级为实测）；若探针跑不了则改记「反算口径」并注明原因

## 2. 修复本体（Q3 方案 1 最小切片）

- [x] 2.1 `Services/FilterEngine.cs`：折叠函数新增 `Span<byte>` 重载，原 `byte[]` 版本改为对切片重载的转发（逻辑单源）；函数从 private 放宽为 internal（`InternalsVisibleTo` 已就绪）。验证：编译零警告，既有过滤测试全过
- [x] 2.2 调用点改为按 `msgBuf.Length` 切片折叠（`scratch.AsSpan(0, msgBuf.Length)`），匹配切片维持现有按 `msgBuf.Length` 的用法不变。验证：2.3 的行为等价测试全绿
- [x] 2.3 行为等价回归：新增测试覆盖「先长中文行污染缓冲容量、再短 ASCII 行」场景（用例名建议 `MsgFilter_ResidualBytes_AsciiLineStillMatches`），断言命中结果与大小写敏感路径/Unicode 回退路径全等；验证：dotnet test 该用例通过

## 3. 回归测试钉不变式（Q2 ②为主 + ①附带）

- [x] 3.1 折叠函数直测（internal 直测，含纯 ASCII 大小写折叠、含非 ASCII 返回 false、空串、单字节边界 0x7F/0x80；用例名建议 `FoldLowerAscii_*` 系列）。验证：dotnet test 通过
- [x] 3.2 扫描字节计数器（internal 只读，仅测试观测）：断言一次多行过滤的「扫描总字节 == Σ 各行正文长度」（用例名建议 `MsgFilter_ScanBytes_EqualSumOfLineBytes`）。验证：dotnet test 通过；人为把切片改回 `scratch.Length` 时该用例必须变红（防钉失效）
- [x] 3.3 全量测试 + 口径声明：跑 dotnet test 出总数（基线 655/655，相对基线无新增失败）；如触发 docs/TESTING.md 更新时机（用例数变化）同批更新其数字，并按 §3.3 联动 README 测试数字/徽章。验证：口径数字写入交付说明

## Workflow follow-up

- 交付用户验收：附 G/G′/H 对照数字、dotnet test 口径数字、命中数不变声明；验收通过后停在原状，等归档指示（AGENTS.md §2.2）。
- 归档（需用户明确指示）：`openspec archive`，同批将 `openspec/notes/NETWORK-FILE-FILTER-PERF.md` 移入 `openspec/notes/archive/`（git mv + 归档日期前缀）、清除 worktree `wt/fix-msgfilter-capacity-scan`。
