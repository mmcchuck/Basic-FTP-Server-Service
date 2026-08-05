# macOS service

The macOS host uses the same FTP engine, virtual accounts, compatibility settings, and
transfer behavior as the Windows service. Its operating-system shell is deliberately native:
`launchd` starts it at boot and restarts it after a crash.

## Install the release

Download the archive for the Mac's processor, extract it, then open
**Basic FTP Server Installer.app**. The app explains the change and uses the standard macOS
administrator prompt to install and start the launch daemon. It then opens **Basic FTP Server
Settings**, where accounts, passwords, folders, permissions, and service restarts are managed
without Terminal.

The current release is not Apple-notarized. On first launch, Control-click the installer app,
choose **Open**, then confirm **Open**. This exception applies only to that downloaded app.

## Build and install from source

Requires macOS 13 or newer and the .NET 10 SDK.

```bash
./build-macos.sh
./macos/build-installer-app.sh publish/macos artifacts/installer
open "artifacts/installer/Basic FTP Server Installer.app"
```

The build detects Apple Silicon or Intel automatically and publishes a self-contained app,
so the target Mac does not need .NET installed after deployment.

## Add a copier account

```bash
sudo "/Library/Application Support/Basic FTP Server Service/app/basic-ftp-server" \
  add-user scanner 'choose-a-password' '/Users/Shared/Scans'
sudo launchctl kickstart -k system/com.basicftpserverservice.daemon
```

Accounts are upload-only by default. Add `--read` only when a device needs downloads, or
`--delete` when it must remove files. Account names are virtual FTP users, not macOS users.

Useful commands:

```bash
sudo "/Library/Application Support/Basic FTP Server Service/app/basic-ftp-server" list-users
sudo "/Library/Application Support/Basic FTP Server Service/app/basic-ftp-server" status
sudo "/Library/Application Support/Basic FTP Server Service/app/basic-ftp-server" show-config
```

## Files and security

- Configuration and logs: `/Library/Application Support/Basic FTP Server Service/`
- Service definition: `/Library/LaunchDaemons/com.basicftpserverservice.daemon.plist`
- Passwords: AES-GCM encrypted with a random, root-only key stored beside the config
- Config and key permissions: `0600`; data directory permissions: `0700`

The password must be recoverable because technicians need to enter it in copier settings.
The root-only machine key provides the macOS equivalent of the Windows service's machine
DPAPI protection. Copying only `config.json` to another Mac does not expose or recover its
passwords.

The installer does not modify the macOS application firewall. Incoming connections to a
command-line daemon are allowed by default; managed environments can restrict TCP port 21
and the configured passive range at the network firewall.

## Uninstall

```bash
sudo ./macos/uninstall.sh
```

This removes the launch daemon and app, but deliberately preserves configuration, credential
key, logs, and scan folders. Remove the data directory separately only when you intend to
destroy the saved accounts.
