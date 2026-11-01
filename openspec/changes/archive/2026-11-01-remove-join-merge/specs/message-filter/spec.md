# Spec Delta：message-filter

> 归档说明：本 delta 的「message 匹配的作用域」为整块重写——旧定义的场景
> 「多行记录的续行内容参与匹配」（多行合并口径）随续行合并机制删除而失效，
> 由「续行内容以其自身记录参与匹配 / 续行内容不并入上一条记录的正文 /
> 自带日志头的堆栈行按独立记录命中」三场景承接其反面契约。
> 因工具限制（MODIFIED 不允许丢弃现行场景、REMOVED+ADDED 同名被拒），
> 本次归档采用手工合并主规范 + `--skip-specs` 完成；规范侧改动与下方
> MODIFIED/ADDED 块语义一致，并已通过 `openspec validate --specs`。

## MODIFIED Requirements

### Requirement: message 匹配的作用域

message 过滤 SHALL 以物理行为作用域：每条记录的正文即该物理行的日志正文部分；行首时间戳、进程号、线程号、级别、tag 等头部信息 SHALL NOT 参与 message 匹配。

除空行与横幅行外的每一物理行各自构成一条记录；任何物理行的内容 SHALL NOT 并入另一条记录的正文参与匹配。续行（缩进的、不自带日志头的行）的正文只属于其自身那条记录。

#### Scenario: 正文命中

- **WHEN** 记录正文含 `started`
- **THEN** 以 `started` 过滤时该记录命中

#### Scenario: 头部信息不参与匹配

- **WHEN** 记录的 tag 含 `ActivityManager`、时间戳含 `18:00:00`
- **THEN** 以 `ActivityManager` 或 `18:00:00` 过滤时该记录均不命中

#### Scenario: 续行内容以其自身记录参与匹配

- **GIVEN** 文件中一行缩进续行的正文含 `Foo.bar`，其上方一条记录的正文不含 `Foo.bar`
- **WHEN** 以 `Foo.bar` 过滤
- **THEN** 命中的是该续行自身的那条记录，而非其上方的任何记录

#### Scenario: 续行内容不并入上一条记录的正文

- **GIVEN** 某条记录首行正文为 `crash`，其后的缩进续行正文含 `Foo.bar`
- **WHEN** 以 `Foo.bar` 过滤
- **THEN** 首行那条 `crash` 记录不因此命中

#### Scenario: 自带日志头的堆栈行按独立记录命中

- **GIVEN** 一行自带时间戳、级别与 tag 的堆栈行（如崩溃堆栈的 `at …` 行），其正文含 `Foo.bar`
- **WHEN** 以 `Foo.bar` 过滤
- **THEN** 该行作为独立记录命中，其头部元数据完整可用

## ADDED Requirements

### Requirement: 元数据类过滤下的记录可见性

当用户显式启用某类元数据过滤（如按进程、线程、tag、时间列或级别过滤）时，不具备该类元数据的记录 SHALL 不命中该类过滤；排除模式下，该类记录视为反向命中而被保留。

用户未启用元数据过滤时，不具备元数据的记录 SHALL 照常展示，其正文照常参与 message 匹配。

#### Scenario: 启用元数据过滤时无元数据记录不命中

- **GIVEN** 一条不带日志头的续行记录（无进程号、线程号、tag、级别与时间戳）
- **WHEN** 用户启用任一类元数据过滤（如按 tag 过滤）
- **THEN** 该记录不命中该类过滤，从过滤结果中消失

#### Scenario: 排除模式下无元数据记录保留

- **GIVEN** 同上一条无元数据记录
- **WHEN** 用户启用同类元数据过滤并开启排除
- **THEN** 该记录视为未命中而被保留

#### Scenario: 未启用元数据过滤时照常可见

- **GIVEN** 同上一条无元数据记录，正文含 `Foo.bar`
- **WHEN** 用户未启用任何元数据过滤，仅以 `Foo.bar` 做 message 过滤
- **THEN** 该记录照常参与匹配并命中
