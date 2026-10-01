# 发布到 GitHub / GitCode 操作清单

本文件记录 `logcat-viewer` 上架的现状与剩余步骤。

## 0. 当前状态（最近一次更新）

**已完成并已推送**（commit `1490b74`，`origin` 与 `gitcode` 两个远程都在同一提交）：

| 文件 | 改动 |
| --- | --- |
| `README.md` | 重写为**英文入口版**：定位、卖点表、功能、构建、快捷键、许可、免责 |
| `README.zh-CN.md` | **新增**，原 282 行中文 README 完整保留（23 个章节一条不少），项目地址改为 GitHub + GitCode 并列 |
| `logcat.csproj` | 输出/发布时一并复制 `README.zh-CN.md` |
| `docs/github-topics.txt` | GitHub topics 清单（单一来源，16 个） |

- 两个 README 顶部互相链接（`English | 简体中文`），GitHub 作为主仓库排在前面。
- 远程已配置：`origin` = GitHub，`gitcode` = GitCode 镜像。
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
git push origin master      # GitHub 主仓库
git push gitcode master     # GitCode 国内镜像（见下方 4.3 的限制）
```

两处都推送可保持镜像同步；也可以只推 `origin`。

### 3.1 ⚠️ GitCode 现在拒收推送（需在网页上处理）

**现象**：`git push gitcode master` 报

```
remote: <CH.00905403> This operation is not allowed because the repository is an image repository.
fatal: ... error: 403
```

**说明**：这不是 git 或证书问题，是 GitCode 把该仓库判定为**镜像仓库
（image repository）**并因此拒绝直接推送。注意改名前的第一次推送是成功的
（`cc120ea..1490b74`），说明这个状态是**改名过程中**变化的。

**处理**（二选一，都需在 GitCode 网页操作）：

1. 在仓库设置里把「镜像仓库」关掉 / 改为普通仓库，之后即可正常推送；
2. 或干脆把 GitCode 定位为**只读镜像**：在 GitCode 网页上配置成从 GitHub 定时同步，
   此后只推 GitHub，GitCode 自动跟随。

**当前状态**：GitHub 已是最新（`f5bfb7d`），GitCode 停在 `1490b74`，
落后一个文档提交。因为差的只是 `docs/PUBLISHING.md`，不影响使用者，不急于处理。

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

### 4.2 `dotnet test` 有 90 个失败（环境限制，非代码缺陷）

**现象**：`dotnet test` 报 90 个失败，全部是
`UnauthorizedAccessException: Access to the path 'C:\Users\...\Temp\logcat-tests\*.log' is denied`，
失败点集中在 `TempLogFile` 构造函数（`TestHelpers.cs:23`）。

**根因**：DSH 沙箱对**测试宿主子进程**限制了对 `%TEMP%` 的写入，
而普通 PowerShell 进程写同一路径是成功的。已用对照实验确认：

- 在测试宿主内写 `%TEMP%\logcat-tests\` → ❌ 失败
- 在测试宿主内写工作区 `obj\` → ✅ 成功
- 在 PowerShell 里写 `%TEMP%\logcat-tests\` → ✅ 成功

**结论**：**不是代码缺陷，也不是测试写错了**，是运行环境的沙箱边界。
`TempLogFile` 的写法（GUID 文件名 + `File.WriteAllBytes`）本身完全正确。

**建议**：需要跑全量测试时，在 DSH 之外的普通终端里执行即可：

```powershell
dotnet test tests/logcat.Tests/logcat.Tests.csproj
```

若希望在沙箱内也能跑，可把 `TestHelpers.cs` 的 `Root` 从 `%TEMP%` 改为
仓库内的 `.tmp-tests/`（记得加进 `.gitignore`）——但这是为环境妥协，非必要不改。
