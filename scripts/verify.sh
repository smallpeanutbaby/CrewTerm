#!/usr/bin/env bash
# 提交前的完整检查（Linux / macOS）：Release 构建（警告即错误）、全部测试、空白字符检查。
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build CrewTerm.slnx -c Release
dotnet test CrewTerm.slnx -c Release --no-build
git diff --check
