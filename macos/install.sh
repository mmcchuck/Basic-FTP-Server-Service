#!/bin/bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
artifact_dir="$(cd "${script_dir}/.." && pwd)"
if [[ -x "${artifact_dir}/basic-ftp-server" ]]; then
  default_publish_dir="${artifact_dir}"
else
  default_publish_dir="${artifact_dir}/publish/macos"
fi
publish_dir="${1:-${default_publish_dir}}"
data_dir="/Library/Application Support/Basic FTP Server Service"
app_dir="${data_dir}/app"
label="com.basicftpserverservice.daemon"
plist="/Library/LaunchDaemons/${label}.plist"

if [[ ! -x "${publish_dir}/basic-ftp-server" ]]; then
  echo "Published app not found at ${publish_dir}/basic-ftp-server" >&2
  echo "Run ./build-macos.sh first, or pass the publish directory as argument 1." >&2
  exit 1
fi

launchctl bootout system "${plist}" 2>/dev/null || true
install -d -m 700 "${data_dir}" "${data_dir}/logs"
install -d -m 755 "${app_dir}"
find "${app_dir}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp -R "${publish_dir}/." "${app_dir}/"
chmod -R go-w "${app_dir}"
chmod 755 "${app_dir}/basic-ftp-server"
install -m 644 "${script_dir}/${label}.plist" "${plist}"

"${app_dir}/basic-ftp-server" init
launchctl bootstrap system "${plist}"
launchctl enable "system/${label}"
launchctl kickstart -k "system/${label}"

echo "Basic FTP Server Service is installed and running."
echo "Add an account with:"
echo "  sudo '${app_dir}/basic-ftp-server' add-user scanner 'password' '/Users/Shared/Scans'"
