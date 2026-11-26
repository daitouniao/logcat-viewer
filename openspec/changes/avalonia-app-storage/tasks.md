# Tasks

## 1. 准备与持久化根

- [x] 1.1 在独立 worktree 中实施：分支 `wt/avalonia-app-storage`（路径以 `git worktree list` 定位，不写本机绝对路径）；前置：`avalonia-ui-shell` 的 7.1 已合入 `master`，若未合入则先请求合并授权、不自行合入；验证：`git worktree list` 可见该分支，且 worktree 内测试工程能定位到 `logcore.dll`（必要时在 worktree 内构建 core）
- [x] 1.2 在 `Next.App/Storage` 实现持久化根解析：候选目录（应用目录、用户应用数据目录）可注入、可写探测可注入、结果为不可变对象；验证：`StorageRootTests.PrefersExecutableDirectoryWhenWritable`、`StorageRootTests.FallsBackToUserDirectoryWhenExecutableReadOnly`、`StorageRootTests.SessionOnlyWhenNoCandidateWritable`
- [x] 1.3 在应用启动早期解析一次并写入应用级入口，供设置、日志与请求构造共用；验证：启动日志出现根目录与「是否仅会话内」，`dotnet build` 通过
- [x] 1.4 源码扫描约束：`AppContext.BaseDirectory` 只允许出现在存储模块；验证：`StorageBoundaryTests.BaseDirectoryOnlyInStorage`

## 2. 设置层

- [x] 2.1 设置模型补齐 `SchemaVersion = 1` 与容错读取（缺失 / 损坏 / 版本不识别按无设置，未知键忽略）；验证：`SettingsStoreTests.RoundTripsKnownKeys`、`SettingsStoreTests.CorruptFileYieldsDefaults`、`SettingsStoreTests.UnknownVersionYieldsDefaults`、`SettingsStoreTests.IgnoresUnknownKeys`
- [x] 2.2 改为原子写并在保存失败时降级；验证：`SettingsStoreTests.InterruptedWriteLeavesPreviousFile`、`SettingsStoreTests.SaveFailureDoesNotThrow`
- [x] 2.3 设置改为单一写入入口，合并语言与主题两条「读-改-写」路径（保留 `INextSettingsStore` 作为测试缝）；验证：`SettingsStoreTests.ThemeAndLanguagePersistIndependently`，既有 `LocalizationTests` 全过
- [x] 2.4 启动装配改用新的设置入口，删除旧设置的静态路径助手；验证：`dotnet build` 通过，手测切换语言与主题后重启保持

## 3. 诊断日志

- [x] 3.1 实现追加式诊断日志（启动信息、持久化失败），行含本地时间、进程/线程前缀与级别，异常带完整信息；达到 20 MB 时轮转保留一份旧档，写入失败不抛；验证：`StartupLogTests.AppendsEntries`、`StartupLogTests.WriteFailureDoesNotThrow`、`StartupLogTests.RotatesAtSizeLimit`（测试注入较小上限）、`StartupLogTests.IncludesProcessAndThreadPrefix`
- [x] 3.2 在设置保存失败与缓存目录不可用的分支接入日志；验证：`SettingsStoreTests.SaveFailureWritesLog`（日志可写时），日志不可写时不抛

## 4. 缓存目录与临时目录

- [x] 4.1 请求构造把缓存目录改为持久化根下的缓存目录；仅会话内时退回系统临时目录下的应用子目录；验证：`CacheDirTests.RequestUsesPersistentCacheDir`、`CacheDirTests.FallsBackToTempWhenSessionOnly`
- [x] 4.2 临时目录保持系统临时目录下的应用子目录，不跟持久化根；验证：`CacheDirTests.TempDirStaysUnderSystemTemp`
- [x] 4.3 手测：重开同一大文件命中缓存（耗时或启动日志可辨）；删除缓存目录后重开正常且无提示（2026-11-26 用户手测通过：重开命中、删除后重开正常无提示）

## 5. 集成验证与文档

- [x] 5.1 跑 `dotnet test logcat-next/Next.Tests/Next.Tests.csproj` 全量，报告口径数字；验证：新增用例全过、无新增失败
- [x] 5.2 用例数变化时按测试文档既有口径同步数字（含 `logcat-next` 测试工程的口径登记）；验证：文档数字与实测输出一致
- [x] 5.3 手测矩阵：应用目录可写 / 只读两种部署下，设置、日志、缓存各自表现；验证：逐项记录结果，失败项不得声称通过（2026-11-26 用户手测通过：可写与只读两部署均正常）

## 6. 合入

- [ ] 6.1 将 `wt/avalonia-app-storage` 合入目标分支 `master`；验证：合入后 `dotnet build` 与 `dotnet test` 全过，并报告用例口径数字

## Workflow follow-up

- 用户验收通过后按门禁停在原状，等归档指令。
- 归档时同批把 `openspec/notes/APP-STORAGE-AND-CACHE.md` 移入 `openspec/notes/archive/`，并清除该 change 的 worktree。
