#!/usr/bin/env bash
# 运行单元测试并生成覆盖率报告
# 用法: bash scripts/run-tests.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"

# 设置 TEMP 环境变量（coverlet 需要可写目录）
export TEMP="$REPO_DIR/tests/logcat.Tests/tmp"
export TMP="$TEMP"

cd "$REPO_DIR"

echo "=== 运行单元测试 ==="
dotnet test tests/logcat.Tests/logcat.Tests.csproj \
    --collect:"XPlat Code Coverage" \
    --settings tests/logcat.Tests/coverlet.runsettings

echo ""
echo "=== 生成覆盖率报告 ==="
python tests/coverage-report.py

echo ""
echo "=== 完成 ==="
echo "覆盖率报告: tests/coverage-report.html"
