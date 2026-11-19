# Design

## Context

动机见 `proposal.md`。现状（HEAD 核对）：

- `logcore` 仍是单箱 crate；plan 在 `source.rs` 内按模式分支：`Off` / `Auto` 先 `File::open` 再 `probe::probe_open_file(...).unwrap_or(false)`，`On` 完全不打开原件；路由由 `route::should_use_proxy` 决定；身份摘要与缓存查找发生在路由之后。
- 代理路径在 `load_proxy` 中先 `proxy::spool` 落盘到暂存目录，再把副本整体 `fs::read` 进内存解析；`run_loader` 已把 `Err` 统一转成 `State::Failed` + `Event::Error`。
- 错误码 `UNSUPPORTED_FORMAT = 14` 已在错误表中，但无生产者；文本嗅探判据（白名单、严格 >10%、空文件判文本、宽字符判非文本）与 C# `TextProbe` 逐条一致。
- C# 桌面 UI 不走 Rust plan，本 change 不改 C#。

## Goals / Non-Goals

**Goals：**

- 在解析前把非文本与 DLT 输入识别为「不支持的格式」，按获取阶段走同步返回或错误事件。
- 判定只在真正会被读取的副本上进行，不增加「可能挂住的打开」。

**Non-Goals：**

- 不做行级格式匹配（文本但不是 logcat 不拒）。
- 不做 DLT 解码、不加新渠道。
- 不改 Reload / 追加路径、不加回退开关、不改控制字符白名单。
- 不改 C# UI（覆盖随 cabi 接线自动生效）。

## Decisions

### D1 两个判定点，只对可读副本判

- **plan 直读**（`Off` / `Auto`，`File::open` 成功）：读窗口 → 魔数 → 嗅探；非文本直接 `Err`。现状本来就在这个分支里探测，判定只是复用同一次打开。
- **commit 暂存副本**（`On`，或 `Auto` 直开失败回退）：`load_proxy` 在 `proxy::spool` 完成后、`fs::read` 前读副本头部判定；非文本返回 `Err`，由 `run_loader` 的既有错误路径转成失败态 + 错误事件。
- **`On` 的 plan 期不探原件**：`On` 的语义就是跳过直读，避免在不可达路径上多一次可能挂住的打开。备选「plan 期 best-effort 探原件、省一次全量暂存」被否：把挂住风险重新引入 `On`，收益仅限罕见的大体积非文本。

### D2 一次读窗口，魔数先于嗅探

`probe` 增加 `read_window(file, window) -> io::Result<Vec<u8>>`，plan 与 `load_proxy` 都只读一次头部样本，再依次调 `format::detect_unsupported_format(&sample)` 与 `probe::sample_is_text(&sample, threshold)`。魔数先于嗅探是 DLT 笔记已定的顺序（DLT 头本身就是二进制，嗅探只能给出 `Binary`，先判魔数才能给出 `Dlt` 标签）。

### D3 魔数判定独立成 `format.rs`

新增模块只含 `detect_unsupported_format(sample) -> Option<&'static str>`（当前只认 `44 4C 54 01` → `"Dlt"`）。备选「塞进 `probe.rs`」被否：后续 DLT change 要在同一位置长「识别后走解码」的路由，独立模块是已规划好的接缝，`probe.rs` 只留文本判据。

### D4 路由真值表不动

`route::should_use_proxy` 保持原样。非文本拒绝实现为 plan 内的提前返回；Auto 的「非文本」触发在进入路由前已被拒绝消化，真值表的现实触发只剩「直读打开失败 / 读窗口 I/O 失败 → 回退代理」，函数语义不变。备选「把拒绝做成真值表第四种输出」被否：会把路由与判定耦合成三值以上，且 `Off` 根本没有代理参与。

### D5 读窗口 I/O 失败不算非文本证据

保持现状：`Auto` 视同直开失败转代理，`Off` 继续直读。读失败是环境问题，不是内容证据；且 `Off` 无代理可回退。commit 期读副本窗口失败同样不构成拒绝证据：放行到整体读取，由整体读取的失败路径报错。备选「直接以 I/O 错误失败」被否：改变了与内容无关的既有行为，收益为零。

### D6 拒绝早于身份与缓存

plan 的判定位于身份摘要与缓存查找之前（现状探测位置不变）；`load_proxy` 的判定位于 `fs::read` 与写缓存之前。历史上被解析并缓存过的垃圾文件可能留有 `.lcs`，该条目不会复活（plan 直接拒绝，不进入缓存查找），由容量回收自然淘汰。

### D7 错误载荷

`CoreError::new(UNSUPPORTED_FORMAT).with("format", "Dlt" | "Binary")`。不新增事件类型、不加 `path` 参数（调用方持有路径）；宽字符不细分 `WideText`（UI 文案固定，细分价值留给编码判定 change）。

### D8 失败态描述不随拒绝改变格式轴

描述里的 `format` 轴保持当前取值 `Logcat`，检出值只经错误参数的 `format`（`Dlt` / `Binary`）报出。理由：`format` 的取值集合归「下一个触及列契约的 change」（DLT 笔记 K6 已排期），提前扩集合会把描述轴与拒绝标签绑死；且 `Binary` 不是一种格式，而是「没认出来」，长期挂在 `format` 轴会污染语义。备选「失败态描述报检出值」被否。

### D9 测试形态

复用 `source_tests.rs` 的既有工具：`local_plan` / `proxy_plan` / `TestProxyServer`。plan 矩阵用真实临时文件；commit 期用 `TestProxyServer` 服务一个二进制文件、以 `On` 模式走通「spool 完成 → 副本非文本 → 失败态 + 14 事件」。不引入新依赖、不改测试替身协议。

## Risks / Trade-offs

- [`adb logcat -v color` 等 ANSI 彩色日志可能因 ESC 占比超阈值被拒] → 已知接受：此类文件罕见，错误信息明确；判据与 C# 一致，若日后需要豁免，随编码判定 change 统一改两侧。
- [UTF-16 日志被拒（嗅探判非文本）] → 与今天 Auto 模式的嗅探结论一致，只是从「转代理」变成「拒绝」；文案提示在 UI 接线时落地。
- [超大非文本经 `On` / 回退路径要先付一次全量暂存] → 接受；不为此引入首块即判或 spool 前探测（见 D1）。
- [plan 期拒绝让调用方失去「先打开看看」的机会] → 契约即如此设计（同步拒绝、无句柄），与「plan 失败不经事件」既有原则一致。
- [判定之后内容被替换成二进制不在覆盖范围] → 判定锚定获取入口：plan 判原件、commit 判暂存副本；`load_local` 装载开始复核身份发现内容已变时按现状直接解析，不重判；本地直读的 Reload / 追加同属此类。任何「先判后读」都有同一道 TOCTOU 缝，再判一次只能缩小窗口不能根除（根除需让判定与解析读同一份字节，另案处理）。

## Migration Plan

无数据迁移：sidecar / 缓存字节格式不变，历史缓存条目无需清理（拒绝先于查找）。回滚 = 版本回退；本 change 不产生不可逆的持久化副作用。