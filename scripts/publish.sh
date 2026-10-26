#!/usr/bin/env bash
# 生成自带 .NET 运行时的独立发布包，输出到 artifacts/<RID>（Linux / macOS）。
# 用法：./scripts/publish.sh [linux-x64|linux-arm64|osx-x64|osx-arm64]
# 不指定 RID 时按当前系统与处理器架构选择。Windows 请使用 scripts/publish.ps1。
set -euo pipefail
cd "$(dirname "$0")/.."

runtime="${1:-}"
if [ -z "$runtime" ]; then
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    *) echo "Unsupported system: $(uname -s). Use scripts/publish.ps1 on Windows." >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    arm64 | aarch64) arch=arm64 ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
  esac
  runtime="$os-$arch"
fi
output="artifacts/$runtime"

dotnet publish src/CrewTerm.App -c Release -r "$runtime" --self-contained true -o "$output"
for file in README.md LICENSE CHANGELOG.md; do
  if [ -f "$file" ]; then cp "$file" "$output/$file"; fi
done
mkdir -p "$output/docs" "$output/examples/plugins"
cp docs/*.md "$output/docs/"
cp -R examples/plugins/workspace-notes "$output/examples/plugins/"
echo "Ready: $output/crewterm"
