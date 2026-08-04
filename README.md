# Basic FTP Server Service

A small Windows and macOS FTP server for receiving scan-to-FTP jobs from copiers and
multifunction printers. It runs as an operating-system service, so scanning survives reboots
and works with nobody logged in. On Windows, the system tray icon is UI only — closing it
does not stop the server. On macOS, `launchd` owns the headless service and a small CLI manages
accounts and status.

Built as a replacement for Pablo's Quick 'n Easy FTP Server, which does the job well but is a
desktop application: it only runs while someone is signed in, and a logoff or reboot silently
stops scanning until somebody notices.

[![build](https://github.com/mmcchuck/Basic-FTP-Server-Service/actions/workflows/build.yml/badge.svg)](https://github.com/mmcchuck/Basic-FTP-Server-Service/actions/workflows/build.yml)

---

## What it does

- Runs as a Windows service or macOS launch daemon, starts at boot, restarts on failure
- Virtual user accounts — **not** Windows accounts — each with its own scan folder and permissions
- Active (`PORT`/`EPRT`) and passive (`PASV`/`EPSV`) transfers, because copiers are split between them
- Live session log in the tray showing the exact exchange with the device
- Compatibility switches for the handful of quirks that account for most copier failures
- Uploads staged to a `.part` file and renamed on completion, so folder watchers never see a partial scan
- Optional client IP allow-list
- Installer that registers the service, adds both firewall rules, and sets the tray to start at logon

Not supported: FTPS, per-user bandwidth limits. Copier support for FTPS is inconsistent enough
that plaintext on a restricted LAN is the realistic deployment; see [Security](#security).

## Install

### Windows

Download the installer from [Releases](https://github.com/mmcchuck/Basic-FTP-Server-Service/releases),
run it, then open the tray icon and add an account.

Then read **[docs/COPIER-SETUP.md](docs/COPIER-SETUP.md)** — it covers per-brand device
configuration and a symptom-to-setting troubleshooting table.

### Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and, for the
installer, [Inno Setup](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
.\build.ps1
```

That builds, runs the tests, publishes self-contained to `publish\`, and produces an installer
in `installer\Output\`. Use `-SkipInstaller` to publish only.

### Installing without the installer

From an **elevated** prompt in the publish folder:

```powershell
.\BasicFtpServer.exe --install-service; .\BasicFtpServer.exe --add-firewall-rules; .\BasicFtpServer.exe --register-tray
```

### macOS

The Mac host shares the same FTP protocol engine and configuration model, with a native
`launchd` wrapper and machine-local encrypted credentials. See **[docs/MACOS.md](docs/MACOS.md)**
for build, install, account, status, and uninstall commands.

```bash
./build-macos.sh
sudo ./macos/install.sh
```

## How it works

Windows Session 0 isolation means a service cannot draw its own UI. The tray therefore has to
be a separate process in the user's session. Both roles ship in one executable selected by
command line, which keeps deployment, signing and versioning to a single artifact.

```
BasicFtpServer.exe --service      Windows service (Session 0, LocalSystem)
BasicFtpServer.exe --tray         Tray icon and settings (user session, elevated)
```

```
┌──────────────────────┐    named pipe     ┌─────────────────────┐
│ Service (Session 0)  │  BasicFtpSvc      │ Tray (user session) │
│  FTP listener :21    │ ◄───────────────► │  status and IPs     │
│  passive 55000-55100 │  status, config,  │  settings, users    │
│  virtual users       │  live log         │  live log window    │
└──────────────────────┘                   └─────────────────────┘
           │                                          │
           └──── %ProgramData%\BasicFtpServerService ─┘
                 config.json  (DPAPI secrets, ACL: SYSTEM + Administrators)
                 logs\ftpserver-YYYYMMDD.log
```

The tray is manifested `requireAdministrator` and registered as a logon scheduled task with
highest privileges, so it starts elevated without prompting at every sign-in. A non-elevated
process would have the Administrators SID filtered out of its token and be rejected by the
pipe's ACL.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/BasicFtpServer.Core` | Protocol engine, virtual filesystem, config, auth. No service or UI dependency, so it is testable headlessly |
| `src/BasicFtpServer.App` | Service host, control pipe, tray UI, setup helpers |
| `src/BasicFtpServer.Mac` | macOS launch-daemon host, encrypted credential store, and management CLI |
| `tests/BasicFtpServer.Tests` | xUnit — raw wire-level protocol tests plus end-to-end transfers driven by FluentFTP |
| `installer/` | Inno Setup script |
| `macos/` | launchd definition and install/uninstall scripts |
| `docs/COPIER-SETUP.md` | Device configuration and troubleshooting |

The FTP protocol is implemented directly rather than on a library. The command surface needed
here is about 25 verbs, and the entire value of the project is controlling the wire format to
accommodate device quirks — precisely what a framework abstracts away.

## Security

FTP is a cleartext protocol. The password crosses the network in the open and copiers store it
readably in their own web interface. This server is intended for a trusted LAN segment, and is
built accordingly:

- **Accounts are virtual, not Windows accounts.** A compromised copier cannot yield an OS credential.
- **Upload-only by default.** A leaked password cannot read back or delete existing scans.
- **Optional IP allow-list** restricts which devices may even reach the login prompt.
- **Passwords are machine-protected:** Windows uses DPAPI and an Administrators-only ACL;
  macOS uses AES-GCM with a random root-only key and `0600` files. They remain recoverable
  because a technician has to enter them in a copier — a one-way hash would be the wrong trade.
- **No default account.** First run writes a config with no users rather than a blank-password default.

## Testing

```shell
dotnet test
```

The suite drives a real server instance on an ephemeral port: login handling, passive and
active uploads, home-directory containment, auto-created directories, `.part` staging,
duplicate policies, Unix listing parsing, non-ASCII filenames, and concurrent transfers.

## Licence

MIT — see [LICENSE](LICENSE).
