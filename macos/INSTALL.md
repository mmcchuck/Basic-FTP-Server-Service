# Basic FTP Server Service — macOS installation

## 1. Choose the correct download

- Apple Silicon Mac (M1, M2, M3, M4, or newer): download the `arm64` ZIP.
- Intel Mac: download the `x64` ZIP.

To check, choose Apple menu → **About This Mac** and look for **Chip** or **Processor**.

## 2. Open the installer

1. Double-click the downloaded ZIP to extract it.
2. Open the extracted folder.
3. Control-click (or right-click) **Basic FTP Server Installer.app**.
4. Choose **Open**.
5. Confirm **Open** in the security dialog.
6. Click **Install** and enter the Mac administrator password.

The Control-click step is needed because this independent release is not yet notarized through
Apple. The installer is open source and its SHA-256 checksum is published on the GitHub release.

The service starts immediately, starts automatically after reboot, and continues running when
no user is signed in.

## 3. Add the first scanner account

Open Terminal and run this command, replacing the username, password, and destination folder:

```bash
sudo "/Library/Application Support/Basic FTP Server Service/app/basic-ftp-server" \
  add-user scanner 'choose-a-password' '/Users/Shared/Scans'
```

Then restart the service so it loads the new account:

```bash
sudo launchctl kickstart -k system/com.basicftpserverservice.daemon
```

Configure the copier with:

- Server: the Mac's IP address from System Settings → Network
- Port: `21`
- Username and password: the values entered above
- Directory: `/`
- Transfer mode: passive (recommended)

## Check status

```bash
sudo "/Library/Application Support/Basic FTP Server Service/app/basic-ftp-server" status
```

Configuration and logs are stored in:

```text
/Library/Application Support/Basic FTP Server Service/
```

## Uninstall

From the extracted release folder, run:

```bash
sudo bash ./macos/uninstall.sh
```

Uninstalling preserves accounts, logs, and scan folders so an accidental uninstall does not
destroy data.
