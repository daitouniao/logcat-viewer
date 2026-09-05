# 第三方许可声明 / Third-Party Notices

本文件列明 logcat viewer 在**构建**与**分发**过程中用到的第三方组件、它们的许可与版权，以及分发时需要保留的声明。
需与仓库根目录的 [LICENSE](LICENSE)（Apache License 2.0）一并阅读；本文件只是补充说明，不改变任何组件自身的许可。

## 目录

- [总览](#总览)
- [1. NuGet 依赖](#1-nuget-依赖)
  - [直接依赖](#直接依赖)
  - [传递依赖](#传递依赖)
  - [AdvancedSharpAdbClient 的上游](#advancedsharpadbclient-的上游)
- [2. 编译工具链与运行时](#2-编译工具链与运行时)
- [3. 运行时外部程序（不由本仓库分发）](#3-运行时外部程序不由本仓库分发)
- [4. 分发时的合规清单](#4-分发时的合规清单)
- [5. 许可全文](#5-许可全文)
  - [Apache License 2.0](#apache-license-20)
  - [MIT License](#mit-license)

## 总览

| 组件                                                  | 版本     | 许可                       | 是否随程序分发   |
| ----------------------------------------------------- | -------- | -------------------------- | ---------------- |
| 本项目 logcat viewer                                  | —        | Apache-2.0                 | —                |
| AdvancedSharpAdbClient                                | 3.6.16   | Apache-2.0                 | 是（DLL）        |
| Microsoft.Extensions.\*（Logging 及其依赖，共 13 个） | 10.0.11  | MIT                        | 是（DLL）        |
| .NET SDK（含 C# 编译器、MSBuild、NuGet）             | 10.0.400 | 源码 MIT / 二进制 MS LTSD  | 否（仅构建期）   |
| .NET Runtime + Windows Desktop（WinForms）            | 10.0.11  | 源码 MIT / 二进制 MS LTSD  | 视发布方式而定   |
| adb.exe（Android platform-tools）                     | 用户环境 | Apache-2.0                 | 否（外部程序）   |

结论：所有依赖（Apache-2.0 与 MIT）都与本项目采用的 Apache-2.0 相容，无需额外授权即可随程序分发。

## 1. NuGet 依赖

### 直接依赖

声明于 [logcat.csproj](logcat.csproj)：

| 包                                     | 版本     | 许可      | 版权                                | 用途                    |
| -------------------------------------- | -------- | --------- | ----------------------------------- | ----------------------- |
| AdvancedSharpAdbClient                 | 3.6.16   | Apache-2.0 | Copyright © 2021 - 2026 SharpAdb    | 与设备端 adb server 通信 |
| Microsoft.Extensions.Logging           | 10.0.11  | MIT       | Copyright © Microsoft Corporation   | 日志抽象与工厂          |
| Microsoft.Extensions.Logging.Console   | 10.0.11  | MIT       | Copyright © Microsoft Corporation   | 控制台日志输出          |
| Microsoft.Extensions.Logging.Debug     | 10.0.11  | MIT       | Copyright © Microsoft Corporation   | 调试器日志输出          |

### 传递依赖

由上述包间接引入，随构建产物一起出现在输出目录，全部为 **MIT**、版权 **Microsoft Corporation**，版本统一 `10.0.11`：

- Microsoft.Extensions.Logging.Abstractions
- Microsoft.Extensions.Logging.Configuration
- Microsoft.Extensions.Options
- Microsoft.Extensions.Options.ConfigurationExtensions
- Microsoft.Extensions.Primitives
- Microsoft.Extensions.Configuration
- Microsoft.Extensions.Configuration.Abstractions
- Microsoft.Extensions.Configuration.Binder
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.DependencyInjection.Abstractions

### AdvancedSharpAdbClient 的上游

该库是其 fork 链的产物，版权归属见其包声明：
`The Android Open Source Project, Ryan Conrad, Quamotion and improved by yungd1plomat and wherewhere`，
整体以 Apache-2.0 授权（其实现源自 Android 的 ddmlib 与 SharpAdbClient / madb，同为 Apache-2.0）。
仓库：<https://github.com/SharpAdb/AdvancedSharpAdbClient>

## 2. 编译工具链与运行时

| 工具                                            | 许可                                                               | 说明                                                                                  |
| ----------------------------------------------- | ------------------------------------------------------------------ | ------------------------------------------------------------------------------------- |
| .NET SDK（`dotnet build` / `dotnet publish`）  | 源代码 MIT；安装的二进制受《Microsoft 软件许可条款 — .NET 库》约束 | 构建期使用；其条款允许免费构建并分发应用程序                                          |
| C# 编译器 / Roslyn、MSBuild、NuGet client      | 源代码 MIT，二进制同上                                             | 均随 .NET SDK 安装                                                                     |
| Microsoft.NETCore.App（.NET 运行时）           | 源码 MIT / 二进制 MS 许可条款                                      | 框架依赖发布时由用户机器提供；自包含发布时按其条款随程序再分发                         |
| Microsoft.WindowsDesktop.App（WinForms、Drawing） | 源码 MIT（dotnet/winforms）/ 二进制 MS 许可条款                    | 本项目的 UI 框架                                                                       |
| Windows SDK 头文件与引用程序集                  | Windows SDK 许可协议                                               | 仅编译期引用，不复制其内容                                                             |

已安装版本（本机验证）：`dotnet --list-sdks` → `10.0.400`；`dotnet --list-runtimes` → `Microsoft.WindowsDesktop.App 10.0.11`。
SDK 自带的第三方声明见 `C:\Program Files\dotnet\ThirdPartyNotices.txt` 与各组件包内的 `THIRD-PARTY-NOTICES.TXT`。
上述 Microsoft 组件的许可标识取自其包内 `.nuspec` 的 `<license type="expression">MIT</license>`（即 <https://licenses.nuget.org/MIT>）。

## 3. 运行时外部程序（不由本仓库分发）

- **adb.exe**：属于 Android SDK Platform-Tools，由 Google 以 Apache-2.0 发布（adb 协议实现含 The Android Open Source Project 与 Brian Swetland 的版权）。本程序只调用用户环境里已有的 `adb.exe` 或连接其启动的 adb server，不复制、不打包、不修改它，因此无需在其之外附加声明；若你把 `platform-tools` 一起打包分发，则须保留其中的 `LICENSE` 与 `NOTICE` 文件。
- **设备端输出**（logcat 文本、截图、录屏、文件内容）：属于用户与设备的数据，不在任何代码许可范围内；其版权归数据来源方所有。

## 4. 分发时的合规清单

发布二进制（`dotnet publish` 产物或源码包）时，请确保：

1. 保留根目录 `LICENSE`（Apache-2.0 全文），或在压缩包/安装目录中放入同等副本；输出目录已通过 `logcat.csproj` 自动复制 `LICENSE` 与 `THIRD-PARTY-NOTICES.md`。
2. 一并分发本文件，或把其中的第三方声明放进「关于」对话框、`about.txt` 等第三方声明通常出现的位置（Apache-2.0 第 4(d) 条允许的形式）。
3. 不要移除或修改依赖 DLL 内嵌的版权信息（`AdvancedSharpAdbClient.dll` 等保留原始版本资源）。
4. 若修改了本项目的源文件，按 Apache-2.0 第 4(b) 条在改动处说明你修改过该文件。
5. 产品名称不使用 "AdvancedSharpAdbClient"、".NET"、"Android"、"Windows" 等商标作为自有品牌（Apache-2.0 第 6 条、Microsoft 与 Google 的商标条款）。
6. 依赖升级后，复核 `obj/project.assets.json` 的 `libraries` 节与各包 `.nuspec` 中的 `<license>` 字段，确认包名、版本与许可标识仍与本文件一致。

## 5. 许可全文

### Apache License 2.0

本项目与 AdvancedSharpAdbClient 采用。全文见 [LICENSE](LICENSE)，或官方原文：<http://www.apache.org/licenses/LICENSE-2.0>

### MIT License

Microsoft.Extensions.\* 与 .NET 各组件源码采用（各包内的具体版权行见上文表格；下例为 dotnet 仓库使用的版权行）：

```
MIT License

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
