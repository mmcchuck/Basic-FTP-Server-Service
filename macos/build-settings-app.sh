#!/bin/bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <arm64|x64> <output-directory>" >&2
  exit 2
fi

architecture="$1"
output_dir="$2"
script_dir="$(cd "$(dirname "$0")" && pwd)"
app_path="${output_dir}/Basic FTP Server Settings.app"

case "${architecture}" in
  arm64) target="arm64-apple-macos13.0" ;;
  x64) target="x86_64-apple-macos13.0" ;;
  *) echo "Architecture must be arm64 or x64." >&2; exit 2 ;;
esac

mkdir -p "${app_path}/Contents/MacOS" "${app_path}/Contents/Resources"
cp "${script_dir}/settings/Info.plist" "${app_path}/Contents/Info.plist"
case "${architecture}" in
  arm64) clang_arch="arm64" ;;
  x64) clang_arch="x86_64" ;;
esac
clang -fobjc-arc -arch "${clang_arch}" -mmacosx-version-min=13.0 -framework Cocoa \
  "${script_dir}/settings/SettingsApp.m" \
  -o "${app_path}/Contents/MacOS/BasicFtpServerSettings"
codesign --force --deep --sign - "${app_path}"
echo "Built ${app_path}"
