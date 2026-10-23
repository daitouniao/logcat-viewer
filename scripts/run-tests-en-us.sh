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
# 本脚本把 CI 的语言环境搬到本地：设 LOGCAT_FORCE_UI_CULTURE=en-US，由测试程序集
# TestCultureBootstrap（ModuleInitializer）在**测试进程内**把 CurrentCulture /
# CurrentUICulture 钉成 en-US，再跑 dotnet test。凡是「本地绿、CI 红」的语言类问题，
# 在这里就会当场红。
#
# ── 为什么不用 LANG / LC_ALL ──
# 2026-10-24 实测：Windows 上 LANG / LC_ALL / DOTNET_CLI_UI_LANGUAGE 都**不改变**
# .NET 的 CurrentUICulture（不设 zh-CN，设了仍 zh-CN）；这些变量只在 Unix 生效。
# 沿用环境变量方案会让 Windows 上的「英文复现」实际跑中文却显示全绿——正是本
# change 要消灭的假绿。脚本保留它们仅供 Unix runner 使用。
#
# 硬断言：CiLocaleAssertionTests.强制界面语言已生效 校验测试进程实际区域；
# 初始化器失效或变量未生效时立即红，不会再静默退回中文。
#
# 与 tests/logcat.Tests/CiLocaleAssertionTests.cs 的分工：
#   - 那条**扫描器测试**在提交前就拦住「硬编码中文断言本地化文案」的写法（预防）；
#   - 本脚本**在英文环境真跑一遍**全量（兜底），抓扫描器漏网的形态。

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
cd "$REPO_DIR"

# 真正生效的开关：测试程序集在进程内设置区域（LANG 在 Windows 上是无效的）。
export LOGCAT_FORCE_UI_CULTURE=en-US
export LANG=en_US.UTF-8
export LC_ALL=en_US.UTF-8
export DOTNET_CLI_UI_LANGUAGE=en-US

echo "=== 环境 ==="
echo "LOGCAT_FORCE_UI_CULTURE=$LOGCAT_FORCE_UI_CULTURE （测试进程内强制 en-US）"
echo ""

echo "=== 重建（Release，对齐 CI）==="
dotnet build tests/logcat.Tests/logcat.Tests.csproj -c Release -v minimal

echo ""
echo "=== 跑全量（en-US 区域）==="
dotnet test tests/logcat.Tests/logcat.Tests.csproj -c Release --no-build -v normal

echo ""
echo "=== 完成 ==="
