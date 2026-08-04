#!/bin/bash
set -euo pipefail

repo_dir="$(cd "$(dirname "$0")" && pwd)"
case "$(uname -m)" in
  arm64) runtime="osx-arm64" ;;
  x86_64) runtime="osx-x64" ;;
  *) echo "Unsupported Mac architecture: $(uname -m)" >&2; exit 1 ;;
esac

dotnet test "${repo_dir}/tests/BasicFtpServer.Tests/BasicFtpServer.Tests.csproj" -c Release --nologo
dotnet publish "${repo_dir}/src/BasicFtpServer.Mac/BasicFtpServer.Mac.csproj" \
  -c Release -r "${runtime}" --self-contained true -o "${repo_dir}/publish/macos" --nologo

echo "Published ${runtime} app to ${repo_dir}/publish/macos"
echo "Install with: sudo ${repo_dir}/macos/install.sh"
