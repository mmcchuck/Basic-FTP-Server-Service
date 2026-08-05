#!/bin/bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this uninstaller with sudo." >&2
  exit 1
fi

label="com.basicftpserverservice.daemon"
plist="/Library/LaunchDaemons/${label}.plist"
app_dir="/Library/Application Support/Basic FTP Server Service/app"
settings_app="/Applications/Basic FTP Server Settings.app"

launchctl bootout system "${plist}" 2>/dev/null || true
rm -f "${plist}"
rm -rf "${app_dir}"
rm -rf "${settings_app}"

echo "The service and settings application were removed."
echo "Configuration, credential key, logs, and scan folders were preserved."
