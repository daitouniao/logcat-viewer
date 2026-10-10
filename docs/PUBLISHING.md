# 发布到 GitHub / GitCode 操作清单

本文件记录 `logcat-viewer` 上架的现状与剩余步骤。

## 0. 当前状态（最近一次更新）

**已完成并已推送**（2026-10-05 核对：版本号升至 V0.1.1；此前 `1490b74` 之后又推了 `411189c` 测试工程临时目录修复、`55a8570` 清零编译告警、`f37d14d` 版本号升至 V0.1.0）：

| 文件 | 改动 |
| --- | --- |
| `README.md` | 重写为**英文入口版**：定位、卖点表、功能、构建、快捷键、许可、免责 |
| `README.zh-CN.md` | **新增**，原 282 行中文 README 完整保留（23 个章节一条不少），项目地址改为 GitHub + GitCode 并列 |
| `logcat.csproj` | 输出/发布时一并复制 `README.zh-CN.md` |
| `docs/github-topics.txt` | GitHub topics 清单（单一来源，16 个） |

- 两个 README 顶部互相链接（`English | 简体中文`）。
- 远程配置：`origin` = **GitCode 主仓库**，`github` = GitHub 镜像。`master` 上游为 `origin/master`，
  裸 `git push` 即发往 GitCode。
- GitHub 侧由 GitCode 的 **Push 镜像**自动同步；**Pull 镜像保持关闭**（启用会让仓库变只读，见 3.1）。
- 已执行验证：`dotnet build` **0 错误 0 警告**（22 条既有警告已在 `55a8570` 清零），产物中同时生成
  `README.md` 与 `README.zh-CN.md`。

## 1. ⚠️ 待办：GitHub 网页设置（git 改不了，必须手工做）

这两项是 GitHub 搜索的主要入口，**不做的话英文 README 基本白写**。

### 1.1 Description（必做）

仓库首页右上角 ⚙️ → Description：

```
Android logcat viewer for Windows — instant open of GB-scale log files with mmap + columnar indexing, live capture, ADB file transfer, APK install/uninstall. .NET WinForms.
```

### 1.2 Topics（必做）

同一处的 Topics，按 `docs/github-topics.txt` 逐个添加；或安装 gh CLI 后一次加完：

```powershell
gh repo edit daitouniao/logcat-viewer `
  --add-topic android --add-topic logcat --add-topic adb `
  --add-topic log-viewer --add-topic android-debugging --add-topic android-tools `
  --add-topic winforms --add-topic dotnet --add-topic csharp `
  --add-topic desktop-app --add-topic developer-tools --add-topic debugging `
  --add-topic windows --add-topic memory-mapped-file --add-topic log-analysis `
  --add-topic tool
```

### 1.3 确认 License 被识别

仓库首页右侧应显示 **Apache-2.0**。`LICENSE` 已在仓库根，正常会自动识别。

## 2. 待办：截图（明天做）

英文 README 第 13~14 行已留占位注释：

```markdown
<!-- TODO: replace with a real screenshot / GIF before release -->
<!-- ![logcat viewer](docs/screenshot.png) -->
```

截图准备好后：

1. 建 `docs/` 目录放入图片，建议 `screenshot.png`（主界面）+ `filter.png`（过滤窗口）
   + `file-browser.png`（双栏文件浏览）
2. 删掉那两行注释，换成：

```markdown
![logcat viewer](docs/screenshot.png)
```

3. **中文 README 顶部也加一张**（`README.zh-CN.md` 第 9 行附近）

优先做 **GIF** 而不是静态图：滚动千万行日志的流畅度、过滤的即时响应，
静态图完全表达不出来，而这恰恰是最值钱的卖点。

## 3. 日常提交与推送

```powershell
git add <files>
git commit -m "..."
git push                    # = git push origin master，发往 GitCode 主仓库
git push github master      # GitHub 镜像（Push 镜像已于 2026-10-10 确认失效，改直推）
```

> ⚠️ **两个远端都要推**。GitCode 的 Push 镜像已确认失效（用户手动触发同步也失败），
> 别再依赖它。两边的 master 应始终是同一个 SHA；不一致时以GitCode 为准补推。
>
> 推 GitHub 要绕开会挂死的 credential-helper-selector：
> ```powershell
> $GCM = '!"C:/Users/Administrator/.workbuddy/binaries/PortableGit/versions/1.2.0/mingw64/bin/git-credential-manager.exe"'
> git -c credential.helper= -c credential.helper=$GCM push github master
> ```
>
> **判断推送是否成功要看远端 SHA，不能看 exit code。** GitCode 的 post-receive
> 服务端钩子会在推送完成后返回 `接收端返回: 70`，此时推送其实已成功。

### 3.2 发布：必须先清空目标目录（2026-10-10 新增）

```powershell
Remove-Item -Recurse -Force publish\Release\V0.1.2   # 先删干净
dotnet publish logcat.csproj -c Release -p:PublishDir=publish\Release\V0.1.2\
Compress-Archive -Path publish\Release\V0.1.2\* -DestinationPath publish\Release\V0.1.2.zip -Force
```

**`dotnet publish` 不会清空目标目录**，历史文件会留在原地并被一起打进 zip。
实测踩到过：自包含改造后重发，目录里还留着上一次 `dotnet test` 期间
被复制进去的 `coverlet.collector.dll`、`Microsoft.TestPlatform.*.dll`
（时间戳差一个月），以及 `logcat.pdb`。

判断产物是否干净，别只看体积，要看**时间戳**和**关键字**：

```powershell
ls -la --time-style=+%m-%d_%H:%M publish\Release\V0.1.2\logcat.exe   # 本次生成的时间
ls publish\Release\V0.1.2\*.pdb2>$null; if ($?) { "有 pdb" } else { "无 pdb" }
Select-String -Path publish\Release\V0.1.2\*.* -Pattern 'xunit|coverlet|TestPlatform' -List
```

> `DebugType=none` / `DebugSymbols=false` **只管本项目产物**，
> 管不住依赖包以「库资产」路径复制进来的 pdb —— 与 Avalonia 那个
> `libSkiaSharp.pdb` 80MB 是同一类问题（见 MEMORY.md）。

### 3.3 发布形态：自包含（2026-10-10 改）

`logcat.csproj` 里 `<SelfContained>` 的默认值是 **false**，**由发布命令用
`-p:SelfContained=true -p:RuntimeIdentifier=win-x64` 打开**（见 3.2 的命令）。

> ⚠️ **`SelfContained=true` 绝不能无条件写进 csproj。**
> 它会沿 `ProjectReference` 传染：测试工程 `logcat.Tests.csproj` 是框架依赖的，
> 引用一个自包含的主工程会报
> `NETSDK1151: 自包含的可执行文件不能由非自包含的可执行文件引用`，
> **直接打断 CI**。本项目踩过一次：先是无条件写进 csproj，tstrun 立刻编不过；
> 改用 `Condition` 判断 `Configuration` 也不行 —— 出现了**build 时泄漏、
> publish 时不生效**的反向问题。
> 最终形态是「csproj 声明默认 false + 命令行显式打开」——
> **条件表达式不可靠，宁可多打一个参数。**
>
> 改完 csproj 必跑两个场景验证：
> ```powershell
> dotnet build tests\logcat.Tests\logcat.Tests.csproj -c Release
>   # 须 0 错误，且 bin\Release\net10.0-windows\win-x64\ 【无】coreclr.dll
> dotnet publish logcat.csproj -c Release -p:SelfContained=true -p:RuntimeIdentifier=win-x64 -p:PublishDir=<tmp>\
>   # 须【有】coreclr.dll，体积约 120 MB
> ```

| |旧（框架依赖） | 新（自包含） |
|---|---|---|
| 目录体积 | 1.8 MB | **120 MB**（zip 约 48 MB） |
| 目标机要求 | 必须预装 .NET 10 Desktop Runtime | **只需 Windows** |

旧版的 runtimeconfig 声明依赖 `Microsoft.NETCore.App` / `Microsoft.WindowsDesktop.App`
10.0.0，而目录内无 `coreclr.dll` —— 没装运行时的机器双击即报错，
而README 当时写着"运行已发布版本不需要 SDK"。**"下载即用"的前提不成立。**

- **不要用 `PublishSingleFile`**：实测单文件 exe 启动失败
  （`Failed to map file ... error 5` / `Couldn't memory map the bundle file`，
  非沙箱复测同样失败）。保持多文件形态。
- 验证是否真自包含：`runtimeconfig.json` 里 `frameworks` 字段应为**空**，
  且目录内有 `coreclr.dll` / `hostfxr.dll` / `hostpolicy.dll`。
- ⚠️ 本机 `hostfxr.dll` 加载会报 `0x80070005`（与 `dotnet test` 同一个问题，
  环境权限所致，**不代表产物有问题**）。所以本机无法用"能否启动"验证自包含，
  改用「runtimeconfig 无外部依赖 + coreclr.dll 在包内」这两个结构判据。

### 3.4 pubxml 需手动同步（不入库）

`Properties/PublishProfiles/FolderProfile.pubxml` 被 `.gitignore` 的 `*.pubxml` 排除，
**不入库**。它有两处必须与实际发布保持一致：

1. `<PublishDir>` 里硬编码了版本号（当前 `publish\Release\V0.1.2`）——
   改版本时**本机这份文件也要跟着改**，漏改会发到旧目录（本项目已踩过一次）。
2. `<SelfContained>` / `<RuntimeIdentifier>`：从 VS 发布面板发布会以本文件为准，
   覆盖 csproj 设置。保持与 3.3 一致。

换机器 / 重新 clone 后这份文件会消失，按此模板重建：

```xml
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <Platform>Any CPU</Platform>
    <PublishDir>publish\Release\V0.1.3</PublishDir>   <!-- 版本号跟着发布版本走 -->
    <PublishProtocol>FileSystem</PublishProtocol>
    <_TargetId>Folder</_TargetId>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
  </PropertyGroup>
</Project>
```

### 3.1 ✅ 已解决：GitCode 曾拒收推送（Pull 镜像导致仓库只读）

**现象**：`git push` 报

```
remote: <CH.00905403> This operation is not allowed because the repository is an image repository.
fatal: ... error: 403
```

**根因**：GitCode 仓库当时配置了**从 GitHub 单向 Pull 的镜像**。按 GitCode 官方文档，
镜像分 Pull（外部 → GitCode）与 Push（GitCode → 外部）两个方向，**配置了 Pull 镜像的仓库即只读**
（GitLab 血统的防镜像发散机制），这正是 `image repository` 报错的含义。

关键证据是本地与远端 ref 对不上：本地记的 `gitcode/master` 是 `1490b74`（从未成功推过），
而 GitCode 上的 `master` 却等于 GitHub 的 `2ba6c8e` —— 说明它在自行从 GitHub 同步。

**排除项**（都查过，均非原因）：

- 不是权限：`GET /api/v5/user` 返回的就是仓库属主 `gcw_WDXl5paK` 本人，token 有效；
- 不是网络：`info/refs` 与 API 都通，只有**写**被拦；
- 不是分支保护：推一个全新分支 `master:probe-push-test` 同样 403，属**仓库级**只读；
- 不是 remote 命名/URL：与 remote 叫什么、地址怎么写完全无关。

**解决**（已做，只能在网页操作）：GitCode 项目设置 → 仓库镜像 → **Pull 页签删掉条目**。
镜像的增删没有开放 API（`/mirror`、`/mirrors`、`/import`、`/sync`、`/settings` 全 404，
v5 仓库对象也不暴露 mirror 字段）。

删掉后推送即通：

```
2ba6c8e..4afc8b6  master -> master
```

**GitHub 自动跟随**：已在同一页面的 **Push 页签**配好（目标 = GitHub 仓库地址 + GitHub 账号 +
PAT，需 `repo` 权限）。此后往 GitCode 推的提交会在几分钟内自动同步到 GitHub，无需手工推 `github`。

#### ⚠️ 不要在这个仓库上启用 Pull 镜像

Pull 页签目前是空的，**这是有意为之**。再次启用 Pull（GitHub → GitCode）会有三个叠加风险：

1. **仓库会重新变为只读** —— 就是本文档 3.1 上面的现象，`CH.00905403 ... image repository`。
   之前删掉 Pull 条目才恢复可写，说明 GitCode 对「存在 Pull 镜像」的仓库直接禁写。
2. **可能回退 GitCode 的 master** —— Pull 的源头是 GitHub，而 GitHub 现在是 GitCode 的**下游镜像**
   （由 Push 镜像同步），它的 master 天然比 GitCode 旧。若勾选了「覆盖分叉分支 / Overwrite diverged
   branches」，下一次 Pull 会用 GitHub 的旧提交覆盖 GitCode，`4afc8b6`、`d2e941e` 直接从 origin 上消失。
   不勾选的话不会丢数据（GitLab 默认会因「分叉」而停止更新该分支），但镜像会静默卡住、且仓库仍是只读。
3. **Pull + Push 同时存在 = 双向镜像** —— GitLab 官方文档明说
   「不需要冲突的双向镜像支持并不存在」（*There is no bidirectional support without conflicts*），
   定期同步窗口里两边互相覆盖只是时间问题。

**结论**：本仓库的事实来源（source of truth）是 GitCode。万一确实需要临时从 GitHub 拉一次，
用本地一次性操作代替配置 Pull 镜像：

```powershell
git fetch github master          # 只取，不合并
git log --oneline HEAD..github/master   # 看看 GitHub 上有没有这边没有的提交
```

确认没有需要的提交后，记得在网页上把 Pull 镜像**再删掉**，否则仓库会一直处于只读状态。

## 4. 环境问题记录（已解决）

### 4.1 git 推送报 SSL 证书错误（已修复）

**现象**：`git push` 报
`SSL certificate problem: unable to get local issuer certificate`。

**根因**：git 默认用自带 OpenSSL 后端 + 内置 CA bundle，在这台机器上找不到本地签发者；
而系统证书库是好的（PowerShell 访问 github.com 返回 200）。

**修复**（已验证可用，且**保持证书校验开启**）：

```powershell
git config --global http.sslBackend schannel
```

`schannel` 是 Windows 原生 TLS 后端，直接使用系统证书库。
**不要**用 `http.sslVerify=false` 绕过——那会关闭校验，等于对中间人攻击不设防。

### 4.2 `dotnet test` 临时文件写入失败（已修复）

**现象**：在 DSH 沙箱里 `dotnet test` 会报大量失败（全量 345 个用例中，凡是经
`TempLogFile` / `TempStoreFile` 落临时文件的用例全挂，约 110 个），全部是
`UnauthorizedAccessException: Access to the path 'C:\Users\...\Temp\logcat-tests\*.log' is denied`，
失败点集中在 `TempLogFile` 构造函数的 `File.WriteAllBytes`（`TestHelpers.cs`）。

**根因**：DSH 沙箱对**测试宿主子进程**限制了对 `%TEMP%` 的写入，
而普通 PowerShell 进程写同一路径是成功的。已用对照实验确认：

- 在测试宿主内写 `%TEMP%\logcat-tests\` → ❌ 失败
- 在测试宿主内写工作区 `obj\` → ✅ 成功
- 在 PowerShell 里写 `%TEMP%\logcat-tests\` → ✅ 成功

**解法**：修改 `TempRoot` 使用项目目录而非 `%TEMP%`：

```csharp
// TestHelpers.cs
static readonly string s_tempRoot = System.IO.Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
    "tests", "logcat.Tests", "tmp");
public static readonly string Dir = System.IO.Path.GetFullPath(s_tempRoot);
```

临时文件会写入 `tests/logcat.Tests/tmp/`，无需改环境变量。

> 跑测试与出覆盖率的完整流程、统计口径、以及沙箱里用 tstrun 反射 runner 绕开
> testhost 的做法，统一见 [`TESTING.md`](TESTING.md)。

### 4.3 coverlet 覆盖率收集器失败（已修复）

**现象**：`dotnet test --collect:"XPlat Code Coverage"` 生成的覆盖率报告中所有行都是 `hits="0"`，
最终覆盖率显示 0%，但测试本身全部通过。

**根因**：coverlet 6.0.4 在 .NET 10 上存在兼容性问题，且测试宿主子进程无法在
`%TEMP%` 中创建锁文件（`.lock` 后缀），导致覆盖率数据无法正确收集。

**解法**：

1. 更新 coverlet.collector 到 10.1.0（支持 .NET 10）

```powershell
dotnet add tests/logcat.Tests/logcat.Tests.csproj package coverlet.collector --version 10.1.0
```

2. ~~设置 TEMP 环境变量到可写目录~~ —— 已不需要。`TempRoot` 已迁到项目目录（见 4.2），
   当前版本无需任何环境变量设置

**验证命令**（无需设置 TMP/TEMP）：

```powershell
dotnet test tests/logcat.Tests/logcat.Tests.csproj --collect:"XPlat Code Coverage" --settings tests/logcat.Tests/coverlet.runsettings
python tests/coverage-report.py
```

正常输出（2026-10-05 基线，已按行号去重）：`行覆盖率 98.17% (1,713/1,745)，分支覆盖率 88.04%`

> ⚠️ 若分母是几千（如 3,482），说明行号去重失效或排除规则失效 —— 见 [`TESTING.md`](TESTING.md) §5.1 / §5.2。

验证覆盖率报告生成：
```powershell
dotnet tool install -g dotnet-reportgenerator-globaltool  # 如未安装
reportgenerator -reports:"tests/logcat.Tests/TestResults/<guid>/coverage.cobertura.xml" -targetdir:"coverage-report" -reporttypes:"Html;TextSummary"
```
