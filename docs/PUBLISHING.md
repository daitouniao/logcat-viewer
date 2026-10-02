# 发布到 GitHub / GitCode 操作清单

本文件记录 `logcat-viewer` 上架的现状与剩余步骤。

## 0. 当前状态（最近一次更新）

**已完成并已推送**（commit `1490b74`，当时两个远程都在同一提交）：

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
- 已执行验证：`dotnet build` **0 错误**（22 个既有警告），产物中同时生成
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
```

GitHub 镜像由 GitCode 侧自动同步（**Push 镜像**），无需手工推。若同步尚未生效、
需要临时手动补推，用下面的写法（注意要绕开会挂死的 credential-helper-selector）：

```powershell
$GCM = '!"C:/Users/Administrator/.workbuddy/binaries/PortableGit/versions/1.2.0/mingw64/bin/git-credential-manager.exe"'
git -c credential.helper= -c credential.helper=$GCM push github master
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

### 4.2 `dotnet test` 在沙箱内大面积失败（环境限制，非代码缺陷）

**现象**：在 DSH 沙箱里 `dotnet test` 会报大量失败（全量 340 个用例中，凡是经
`TempLogFile` / `TempStoreFile` 落临时文件的用例全挂，约 110 个），全部是
`UnauthorizedAccessException: Access to the path 'C:\Users\...\Temp\logcat-tests\*.log' is denied`，
失败点集中在 `TempLogFile` 构造函数的 `File.WriteAllBytes`（`TestHelpers.cs`）。

**根因**：DSH 沙箱对**测试宿主子进程**限制了对 `%TEMP%` 的写入，
而普通 PowerShell 进程写同一路径是成功的。已用对照实验确认：

- 在测试宿主内写 `%TEMP%\logcat-tests\` → ❌ 失败
- 在测试宿主内写工作区 `obj\` → ✅ 成功
- 在 PowerShell 里写 `%TEMP%\logcat-tests\` → ✅ 成功

**结论**：**不是代码缺陷，也不是测试写错了**，是运行环境的沙箱边界。
`TempLogFile` 的写法（GUID 文件名 + `File.WriteAllBytes`）本身完全正确。

**解法（已验证，无需改代码）**：把临时目录指到工作区内，`Path.GetTempPath()` 随之改变，
`TempLogFile` 不再触碰 `%TEMP%`，沙箱内可以全绿：

```powershell
$env:TMP  = "D:\01.0.Code\C#\logcat\.tmp-test"
$env:TEMP = $env:TMP
dotnet test tests/logcat.Tests/logcat.Tests.csproj
```

`.tmp-test/` 是临时产物，已加入 `.gitignore`。
在 DSH 之外的普通终端里跑测试则不需要这一步。
