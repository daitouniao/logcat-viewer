# Spec Delta

## Purpose

定义 message 正文过滤的判定契约：匹配的作用域、大小写等价规则、多关键词组合语义与排除语义。本能力只描述「哪些记录命中过滤条件」这一可观测结论，不约束达成结论的实现方式。

## ADDED Requirements

### Requirement: message 匹配的作用域

message 过滤 SHALL 只在记录的正文内容内做子串匹配。行首时间戳、进程号、线程号、级别、tag 等头部信息 SHALL NOT 参与 message 匹配。

多行记录 SHALL 以合并后的完整正文参与匹配，即续行内容与记录首行正文同等对待。

#### Scenario: 正文命中

- **WHEN** 记录正文含 `started`
- **THEN** 以 `started` 过滤时该记录命中

#### Scenario: 头部信息不参与匹配

- **WHEN** 记录的 tag 含 `ActivityManager`、时间戳含 `18:00:00`
- **THEN** 以 `ActivityManager` 或 `18:00:00` 过滤时该记录均不命中

#### Scenario: 多行记录的续行内容参与匹配

- **WHEN** 记录首行正文为 `crash`，续行正文含 `Foo.bar`
- **THEN** 以 `Foo.bar` 过滤时该记录命中

### Requirement: message 的大小写等价规则

message 过滤 SHALL 提供区分大小写与忽略大小写两种模式，模式由用户显式选择；默认 SHALL 为忽略大小写。

忽略大小写模式下，大小写等价关系 SHALL 与 Unicode 小写化规则一致：比较双方各自小写化后逐字节相同即视为相等。ASCII 字母的大小写互为等价；非 ASCII 字符 SHALL 遵循 Unicode 小写化规则，因此带重音字符与不带重音字符 SHALL NOT 互为等价，德语 ß(U+00DF) 与大写 ẞ(U+1E9E) SHALL 互为等价，土耳其无点 i(U+0131) SHALL NOT 与 ASCII 字母 i 互为等价。

区分大小写模式下 SHALL 按原始字节精确比较。

#### Scenario: ASCII 大小写双向等价

- **WHEN** 记录正文含 `plain ascii lower`，以 `plain`、`PLAIN`、`PlAiN` 任一过滤
- **THEN** 三者命中同一批记录

#### Scenario: 区分大小写时严格比较

- **WHEN** 记录正文含 `PLAIN ASCII UPPER`
- **THEN** 以 `PLAIN` 过滤命中该记录，以 `plain` 过滤不命中该记录

#### Scenario: 重音字符不与无重音字符等价

- **WHEN** 一条记录正文含 `CAFÉ`，另一条正文含 `cafe`
- **THEN** 以 `CAFÉ` 过滤只命中前者，以 `cafe` 过滤只命中后者

#### Scenario: 德语 eszett 大小写等价

- **WHEN** 记录正文含 `Straße`、`ß` 或 `ẞ`
- **THEN** 以 `ß` 或 `ẞ` 过滤命中这三类记录

#### Scenario: 土耳其无点 i 不等于 ASCII i

- **WHEN** 某条记录正文只含 `ı` 而不含任何 ASCII 的 i 或 I
- **THEN** 以 `i` 或 `I` 过滤不命中该记录

#### Scenario: 非 ASCII 过滤词按 Unicode 规则参与匹配

- **WHEN** 记录正文含 `中文Mixed大小写`
- **THEN** 以 `中文mixed大小写` 过滤命中该记录

### Requirement: message 多关键词的组合语义

多条 message 过滤词 SHALL 支持「全部命中」与「任一命中」两种组合方式，由用户显式选择。

空过滤词 SHALL 视为命中：在「任一命中」模式下空词立即成立；在「全部命中」模式下空词 SHALL 跳过、不构成否决条件。

#### Scenario: 任一命中模式

- **WHEN** 过滤词为 `start` 与 `timeout`，组合方式为「任一命中」
- **THEN** 含 `start` 或含 `timeout` 的记录均命中

#### Scenario: 全部命中模式

- **WHEN** 过滤词为 `start` 与 `failed`，组合方式为「全部命中」
- **THEN** 仅同时含两者的记录命中

#### Scenario: 空词在任一命中模式下立即成立

- **WHEN** 过滤词含空串与一个不存在的词，组合方式为「任一命中」
- **THEN** 全部记录命中

#### Scenario: 空词在全部命中模式下不构成否决

- **WHEN** 过滤词含空串与 `timeout`，组合方式为「全部命中」
- **THEN** 结果与仅以 `timeout` 过滤相同

### Requirement: message 的排除语义

message 过滤 SHALL 支持排除模式：排除模式下保留未命中的记录、剔除命中的记录。排除模式下，缺失正文的记录 SHALL 一律视为未命中并被保留。

#### Scenario: 排除命中记录

- **WHEN** 过滤词为 `start`，开启排除
- **THEN** 含 `start` 的记录被剔除，其余记录保留

#### Scenario: 排除时无正文的记录仍保留

- **WHEN** 某条记录正文为空，以 `start` 过滤并开启排除
- **THEN** 该记录被保留

### Requirement: 增量过滤与全量过滤的结论一致

实时采集场景下的增量过滤与全量过滤 SHALL 对同一份过滤条件得出相同的命中结论；同一份过滤条件下，增量过滤在既有记录上的命中结果 SHALL 与全量过滤相同。

#### Scenario: 增量路径与全量路径结论一致

- **WHEN** 以同样的过滤条件分别执行增量过滤与全量过滤
- **THEN** 两者在共同覆盖的记录上命中集合相同

#### Scenario: 增量路径的忽略大小写结论与全量一致

- **WHEN** 以含 ASCII、中文、重音字符、德语 eszett、土耳其无点 i 的过滤词分别执行增量过滤与全量过滤
- **THEN** 两者命中集合相同
