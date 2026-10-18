# CI 本地化断言失败：fileproxy 越界消息（en-US runner）

| 状态 | 已实施待验收 |
| 日期 | 2026-10-18 |
| 关联提交 | 随本报告同提交入库（`test(ut): 修复 fileproxy 越界消息断言未锁语言`） |
| 验收结论 | 待验收 |

## 现象

push `2aad4cd` 触发 GitHub CI（`.github/workflows/ci.yml`，windows-latest），全量 623 条挂 1 条：

- 用例：`FileProxyLoopbackTests.读响应负载超上限或为负_收负载前即报越界`（FileProxyLoopbackTests.cs:426）
- 失败：`Assert.Contains() Failure: Sub-string not found`，实际消息为 `"The file proxy returned an out-of-range p"…`（xUnit 截断显示），断言找的中文「越界」不在英文消息里
- 结果：622 过 / 1 失败；本地开发机（zh-CN）tstrun 全量 623/0/0 全绿——**同一份代码两种结论**

## 根因

异常消息由 `FileProxyClient.ReadAsync`（Services/FileProxyClient.cs:130-132）经 `Loc.F("文件代理返回的负载长度越界：{0}", n)` 生成；`Loc._lang` 在静态构造期按 `CultureInfo.CurrentUICulture` 探测（Services/Loc.cs:65、122-134），GitHub runner 为 en-US → 走英文译版（Services/LocTable.FileProxyUi.cs:54）。测试第 426/436 行硬编码中文子串，未按仓库既定约定锁语言。

同文件的 `SpoolProgress_追逐态…` 早有 `Loc.SetLangGuard(UiLang.ZhCn)` 锁中文的先例；本用例是二轮测试审计（UT-AUDIT N8）新增，漏挂语言作用域。TestHelpers.cs 的 LangScope 文档写明了两条要求：① 断言要跟语言走；② **生成动作必须在语言作用域内完成**——消息是抛出那一刻按当时语言算出来的，事后切语言拿到的是旧语言的串。

## 为什么不是产品缺陷

异常消息面向用户展示，本地化是设计行为。错误码（`Io`）、拒绝时机（收负载前）、越界值回显均不随语言变化；受影响面仅测试断言写法，产品代码零改动。

## 修复

`读响应负载超上限或为负_收负载前即报越界` 改为 `LangScope.InZh/InEn` 双语各「生成 + 断言」一遍：正超限（`MaxReadLen+1`）与负值（`-5`）各两种语言共 4 次生成；中文态断言「越界」，英文态断言 `"out-of-range"`，两种语言都回显越界值。双语都测才能防「被测代码退回硬编码中文」的回归（LangScope 文档原话）。测试总数不变（仍 1 条 `[Fact]`，全量口径 623）。

## 验证

- 本机：`dotnet build`（Debug）+ tstrun 反射 runner 全量，预期 623/0/0（tstrun 口径，与 README/TESTING 623 基线一致）。
- CI：修复推送后重跑 workflow，预期全绿。
