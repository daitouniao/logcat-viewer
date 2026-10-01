# 发布到 GitHub 操作清单

本文件是 `logcat-viewer` 上架 GitHub 的操作步骤。README 的双语改造已完成，剩余的
命令需要你在本机终端执行（当前会话的 shell 无法启动，见文末说明）。

## 0. 已完成的改动

| 文件 | 改动 |
| --- | --- |
| `README.md` | 重写为**英文入口版**（约 160 行）：定位、卖点表、功能、构建、快捷键、许可、免责 |
| `README.zh-CN.md` | **新增**，原 282 行中文 README 完整保留（23 个章节一条不少），仅项目地址改为 GitHub + GitCode 并列 |
| `logcat.csproj` | 输出/发布时一并复制 `README.zh-CN.md` |
| `docs/github-topics.txt` | 新增，GitHub topics 清单（单一来源） |

两个 README 顶部互相链接（`English | 简体中文`），GitHub 作为主仓库排在前面。

## 1. 检查产物（先做这一步）

```powershell
dotnet build
```

- 确认 **0 错误**。
- 确认 `bin/Debug/net10.0-windows/` 下同时出现 `README.md` 与 `README.zh-CN.md`。

> 我没有跑过 build（shell 起不来），这一步请务必自己确认。
> 如果你有测试，顺带跑一下：`dotnet test tests/logcat.Tests/logcat.Tests.csproj`

## 2. 确认远程与分支

```powershell
git remote -v
git status
git branch
```

- 期望看到 `origin  https://github.com/daitouniao/logcat-viewer.git`（或 SSH 形式）。
- 如果 GitCode 也在，按 `origin` = GitHub、`gitcode` = 镜像 来命名，避免后面 push 错地方。
  **本仓库已配置好**：`origin` = GitHub，`gitcode` = GitCode 镜像。
- 若远程还没配：`git remote add origin https://github.com/daitouniao/logcat-viewer.git`

## 3. 提交

```powershell
git add README.md README.zh-CN.md logcat.csproj docs/github-topics.txt
git status
git commit -m "docs: 英文 README 入口 + 中文文档分离，关联 GitHub 地址"
```

`git status` 里留意**不要误提交** `bin/`、`obj/`、`publish/`（已在 `.gitignore` 里，正常不会被 add）。
另外 `docs/PUBLISHING.md`（本文件）你可以选择一起提交，也可以留在本地。

## 4. 推送

```powershell
git push -u origin master
```

如果你的默认分支是 `main`，把 `master` 换成 `main`。
首次 push 可能会要求登录；推荐用 `gh auth login` 或配置 PAT，别把密码写进 remote URL。

## 5. 推送后在 GitHub 网页上补齐（这些改不了仓库文件）

### 5.1 Description（**必做**，GitHub 搜索的主要入口）

仓库首页右上角齿轮 → Description，填入：

```
Android logcat viewer for Windows — instant open of GB-scale log files with mmap + columnar indexing, live capture, ADB file transfer, APK install/uninstall. .NET WinForms.
```

### 5.2 Topics（**必做**）

同一处的 Topics，按 `docs/github-topics.txt` 里的 16 个逐个添加；
或用 gh CLI 一次加完：

```powershell
gh repo edit daitouniao/logcat-viewer `
  --add-topic android --add-topic logcat --add-topic adb `
  --add-topic log-viewer --add-topic android-debugging --add-topic android-tools `
  --add-topic winforms --add-topic dotnet --add-topic csharp `
  --add-topic desktop-app --add-topic developer-tools --add-topic debugging `
  --add-topic windows --add-topic memory-mapped-file --add-topic log-analysis `
  --add-topic tool
```

### 5.3 确认 License 被识别

仓库首页右侧应显示 **Apache-2.0**。若显示 "View license" 或未识别，
检查 `LICENSE` 是否在仓库根、文件名是否全大写。

## 6. 明天的截图（README 里已预留位置）

英文 README 第 13~14 行有占位注释：

```markdown
<!-- TODO: replace with a real screenshot / GIF before release -->
<!-- ![logcat viewer](docs/screenshot.png) -->
```

截图准备好后：

1. 建 `docs/` 目录，放入 `screenshot.png`（主界面）、建议再加一张 `filter.png`（过滤窗口）
   和一张 `file-browser.png`（双栏文件浏览）
2. 删掉那两行注释，换成：

```markdown
![logcat viewer](docs/screenshot.png)
```

3. **中文 README 顶部也加一张**（`README.zh-CN.md` 第 9 行附近）

建议优先做**动图（GIF）**而不是静态图：滚动千万行日志的流畅度、过滤的即时响应，
这两点静态图完全表达不出来，而这恰恰是你最想让人看到的。

## 7. 后续（可选但建议）

- **打赏入口**：等 star 有起色再加。原生位置只有三处——`帮助 → 关于` 对话框
  （`frmMain.cs:291` 的 `ShowAbout`）、仓库根 `FUNDING.yml`、README 底部一节。
  不要做启动弹窗。
- **镜像同步**：GitCode 仓库已改名对齐为 `logcat-viewer`
  （<https://gitcode.com/gcw_WDXl5paK/logcat-viewer>），两个仓库名与产品名现已一致。

## 8. 关于本会话的限制

这个会话的 PowerShell **完全无法执行**（每次调用都以 `3221225794` = `0xC0000142`
DLL 初始化失败退出），所以：

- 我**没有**执行任何 git 命令，上面第 1~4 步全部需要你自己跑
- 我**没有**验证 build 是否通过
- 文件改动是通过文件工具直接写入的，内容可信，但未经编译校验

如果你希望我以后能直接跑命令，需要先排查这台机器上 pwsh 的启动问题
（通常是 PATH 里有损坏的 DLL 或侧加载劫持，`where.exe pwsh` 和事件查看器可以先看）。
