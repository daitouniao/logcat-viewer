#!/usr/bin/env bash
# run-tests-en-us.sh — 以 **en-US 区域**跑全量单测，复刻 GitHub CI 的语言环境
#
# 用法: bash scripts/run-tests-en-us.sh
#
# ── 为什么需要这个脚本 ──
# 界面语言由 Services/Loc.cs 的静态构造期按 CultureInfo.CurrentUICulture 探测；
# 仓库根没有 settings.json 时完全由**系统区域**决定。后果：
#
#   本地开发机（zh-CN）→ 界面中文 → 某些用例绿
#   GitHub runner（en-US）→ 界面英文 → 同一批用例红
#
# 历史上有 4 次 CI 因此翻车（2026-10-09 ×2、10-10、10-18），最近一次是
# fileproxy 越界消息断言硬编码中文。**本地全绿、推送就红**，且本地反复跑也复现不出。
#
# 本脚本把 CI 的语言环境搬到本地：把当前线程与默认线程的区域都钉成 en-US，
# 再跑 dotnet test。凡是「本地绿、CI 红」的语言类问题，在这里就会当场红。
#
# 与 tests/logcat.Tests/CiLocaleAssertionTests.cs 的分工：
#   - 那条**扫描器测试**在提交前就拦住「硬编码中文断言本地化文案」的写法（预防）；
#   - 本脚本**在英文环境真跑一遍**全量（兜底），抓扫描器漏网的形态。

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
cd "$REPO_DIR"

# dotnet test 派生的 testhost 是独立进程，改父进程 shell 的 locale 无效；
# 但 .NET 运行时在**进程启动时**会读 LANG / LC_ALL 来定默认区域，
# 环境变量会被 testhost 继承，故设这三个即可。
#
# 已实测（2026-10-22，临时探针打印实际区域）：
#   不设 → CurrentUICulture=zh-CN  Loc.Current=ZhCn
#   设了 → CurrentUICulture=en-US  Loc.Current=En
# 即 testhost 确实被切成了英文界面，与 GitHub runner 同环境。
export LANG=en_US.UTF-8
export LC_ALL=en_US.UTF-8
export DOTNET_CLI_UI_LANGUAGE=en-US

echo "=== 环境 ==="
echo "LANG=$LANG"
echo ""

echo "=== 重建（Release，对齐 CI）==="
dotnet build tests/logcat.Tests/logcat.Tests.csproj -c Release -v minimal

echo ""
echo "=== 跑全量（en-US 区域）==="
dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Release --no-build -v normal

echo ""
echo "=== 完成 ==="
