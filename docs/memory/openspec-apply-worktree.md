# OpenSpec change 在 worktree 中实施的工作流经验

> 最后更新：2026-10-24
> 出处：change `minute-byte-column` apply（2026-10-24，16/16 任务）
> 相关：[`../UT-AUDIT.md`](../UT-AUDIT.md)（变异测试审计）、[`../TESTING.md`](../TESTING.md)（跑测口径）

## 1. worktree 约定

- 每个 change 一个 worktree：**兄弟目录 + `wt/<change-name>` 分支**，从 master 拉出。
- `tasks.md` 首条记录分支标识（只记分支名，不写绝对路径）。
- apply 期间**不做任何 git commit**，除非当轮获得明确授权；全部改动留在工作树供走查。
- 多个 change 并行时，各 worktree 相互独立，但**共享同一个 `.git`**——在某个 worktree 里
  执行 git 写操作前，先确认没有其他 apply 正在进行。

## 2. 主树与 worktree 的 artifact 同步

change 四件套（proposal / specs / design / tasks）可能在主树被继续修订而未提交。
apply 前**必须比对两边版本**：本次主树 tasks.md / design.md 比 worktree 分支版新
（测试口径改为 dotnet test、删除 7.4、补字段名细节），以主树为权威 `cp` 同步进 worktree。

- 只同步属于本 change 的文件，避开主树上**其它 change 的在途改动**。
- 同步后把已完成任务的复选框在 worktree 副本里勾上，保持 tasks.md 是唯一的进度真相。

## 3. 换行符铁律（本次连踩三次）

仓库 `.gitattributes` 规定 `* text=auto eol=crlf`，配合 `core.autocrlf=true`：
**仓库存 LF、工作树检出 CRLF**。由此：

- 新建 `.cs` 文件若以 LF 落盘，`git status` 会刷整文件改写噪音——先转 CRLF 再收工。
- python 脚本读写源码必须「**读入归一化 LF → 替换 → 写回转 CRLF**」；
  直接 `open(...).read()` 后原样写回，会把 CRLF 文件写成 LF。
- 判断文件真实行尾要**当场验**（数 `\r\n` 与 `\n`），别凭早期检查结论下判断——
  本次曾据一次错误检查断言「HEAD 版是纯 LF」，导致重放脚本第一遍全部断言失败。

## 4. 大批量编辑：优先 Edit 工具，重放脚本是最后手段

单文件编辑量过大时（本次 LogDocument.cs 约 33 处）可用「python 脚本按序重放全部编辑」。
以下纪律是本次实际事故换来的：

1. **每组替换带 `count(old) == 1` 断言**。同构代码有 2 处匹配时文本替换会静默漏改，
   断言把歧义变成显式失败。
2. **删除区间前先 `sed -n '首,尾p'` 验证区间内容**。本次事故：意图删「时间戳终算」
   区间，实际该区间之后还含 `Reload` / `Append` / `TrimPartial` / `NearestPos` /
   `TsToText` / `TsFromParts`，一刀切掉后编译雪崩（CS1501 / CS0103 / CS1061 连环）。
   恢复用 `git checkout -- <file>`（该文件当时无脚本外的手工编辑，checkout 是安全的）。
3. 脚本对同一文件**非幂等**：失败重跑前必须还原到干净状态，否则第二遍会因 old 串
   已被替换而全部断言失败。**若该文件还有脚本外的手工编辑，先 `tar` 备份再还原**——
   未提交树上的 `git checkout` 会打回 HEAD，抹掉那个文件的全部改动（详见
   [mutation-testing.md](mutation-testing.md) 第 2 条）。
4. 重放成功后**立刻编译 + 跑测试**，通过后再删脚本；脚本是临时资产，不进提交。

## 5. 跑测前的环境确认

- **`logcat.exe` 未运行**——单实例互斥体是进程级的，会让 `SingleInstanceTests` 假红。
  免重建判据：源码 mtime ≤ 二进制 mtime。
- 口径 **`dotnet test`**：tstrun 反射 runner **不 await `async Task` 用例**，
  仓库既有 async 用例在 tstrun 下从未真跑过（假绿）；本机 `dotnet test` 已可直接运行，优先。
- 任何「测试通过」的声明必须带口径数字（如 650/650），不接受无数字的「全绿」。

## 6. 删机制的通用安全网

本次删除整套年份机制（`FinalizeTs` / `GuessYear` / `YearTables` / `TsToText` 等）
前先写下了**等价性论证**（分钟过滤 `(ts/60000)%60 ≡ (within/60000)%60`），
删除动作因此有了正确性基石，测试也只删机制专属用例、不动行为契约用例。
删任何机制前：先证明替代路径与原路径在输入空间上等价，再动手；删不掉的等价性就别删。
