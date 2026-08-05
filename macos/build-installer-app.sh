#!/bin/bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <published-app-directory> <output-directory>" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
publish_dir="$(cd "$1" && pwd)"
output_dir="$2"
app_name="Basic FTP Server Installer.app"
app_path="${output_dir}/${app_name}"

if [[ ! -x "${publish_dir}/basic-ftp-server" ]]; then
  echo "Published service not found at ${publish_dir}/basic-ftp-server" >&2
  exit 1
fi

mkdir -p "${output_dir}"
osacompile -o "${app_path}" "${script_dir}/Installer.applescript"
mkdir -p "${app_path}/Contents/Resources/payload"
cp -R "${publish_dir}/." "${app_path}/Contents/Resources/payload/"
cp "${script_dir}/install.sh" "${script_dir}/com.basicftpserverservice.daemon.plist" \
  "${app_path}/Contents/Resources/"
chmod 755 "${app_path}/Contents/Resources/install.sh"

if file "${publish_dir}/basic-ftp-server" | grep -q 'arm64'; then
  settings_arch="arm64"
else
  settings_arch="x64"
fi
"${script_dir}/build-settings-app.sh" "${settings_arch}" "${app_path}/Contents/Resources/settings-app"

# An ad-hoc signature seals the bundle against accidental modification. A Developer ID
# signature and Apple notarization can replace this when signing credentials are available.
codesign --force --deep --sign - "${app_path}"
echo "Built ${app_path}"
