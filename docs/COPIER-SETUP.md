# Copier setup and troubleshooting

Configuring scan-to-FTP on a multifunction printer, and what to change when it doesn't work.

---

## Before you touch the copier

Three things on the server side cause most of the failures that look like copier problems.

**Give the PC a static IP or a DHCP reservation.** The copier stores a fixed address. If this
machine's IP changes, every device stops scanning at once and nothing reports why.

**Stop the PC from sleeping.** *Settings → System → Power → Screen and sleep → Sleep: Never.*
A sleeping machine cannot accept a scan.

**Confirm the firewall rules exist.** The installer creates two, and you need both — one for
the control port and one for the whole passive data range. Only having the first produces the
classic symptom of a device that logs in successfully and then times out on every transfer.

```powershell
netsh advfirewall firewall show rule name="Basic FTP Server Service (Control)"
netsh advfirewall firewall show rule name="Basic FTP Server Service (Passive Data)"
```

---

## Creating the account

In the tray icon → **Settings → Users → Add**:

| Field | Guidance |
| --- | --- |
| Account name | One per device, e.g. `copier-reception`. No spaces or colons — some devices mangle them. |
| Password | Use **Generate**. It avoids characters that copier keypads and web forms mishandle. |
| Scan folder | A local path such as `C:\Scans\Reception`. See the note on network folders below. |
| Permissions | Leave **Upload** on and everything else off unless you have a reason. |

Upload-only matters. FTP sends the password in the clear, and copiers store it readably in
their own web interface — anyone who can reach the device can read the credential. If the
account cannot download or delete, a leaked password cannot be used to read back or destroy
what has already been scanned.

### Network scan folders

The service runs as **LocalSystem**, which authenticates to other machines as the computer
account and will normally be denied on a UNC path. To scan to a share, set the service to run
as a user with rights to it:

*services.msc → Basic FTP Server Service → Properties → Log On → This account.*

---

## Per-brand notes

Firmware varies a lot even within a brand; treat these as starting points, and use the tray's
**Live Log** to see what your specific device actually does.

### Ricoh (also Savin, Gestetner, Lanier)

*Address Book → Register → Folder → Protocol: FTP.*

- Server Name: the PC's IP address
- Path: the subfolder, or blank for the account's root
- User Name / Password: the account you created
- Port: 21

Ricoh devices are commonly configured for **active** mode. That is fully supported. If
transfers fail, check the Live Log for a `PORT` command — if the address it advertises differs
from the address it connected from, the **Ignore the address a device sends in PORT/EPRT**
setting (on by default) is what fixes it.

### Kyocera

*Command Center RX → Address Book → Add Contact → FTP.*

Kyocera's own documentation instructs you to leave the **Path** field blank. That makes the
device send a bare filename with no directory, which this server handles — files land in the
account's scan folder.

### Canon (imageRUNNER)

*Remote UI → Address Book → Register New Destination → File → Protocol: FTP.*

- Host Name: `ftp://<ip>` or just the IP depending on model
- File Path: leave blank for the account root
- Set "Use Passive Mode" to **On** first; fall back to Off if transfers stall.

### Konica Minolta (bizhub)

*Web Connection → Store Address → New Registration → FTP.*

Set **PASV** to On. If the device is on a different subnet and passive fails, turn PASV off
to use active mode instead.

### Xerox (WorkCentre / AltaLink)

*Properties → Services → Workflow Scanning → File Repositories → FTP.*

Xerox controllers are usually strict about the login banner and directory listing format.
Both defaults here (Unix listings, `SYST` reporting `UNIX Type: L8`) are what they expect.

### Sharp / Brother / HP

Generally straightforward. Enter the IP, account, password, port 21, and leave the directory
blank. If a device offers both, prefer passive.

---

## Troubleshooting

Open the tray icon → **Live Log** and reproduce the scan. The exchange tells you which stage
failed. Then find the symptom below.

| Symptom | Likely cause | What to change |
| --- | --- | --- |
| Device cannot connect at all | Firewall, wrong IP, or service stopped | Check the tray shows **Running**; verify both firewall rules; confirm the PC's IP matches what the copier has |
| Logs in, then every transfer times out | Passive data ports blocked or reserved | Check the passive rule exists. If the tray shows a red passive-range warning, change the range (Settings → Server) |
| `227` reply shows an address the copier can't reach | Multiple network adapters (Hyper-V, VPN, Docker) | Set **Advertise this IP** to the PC's real LAN address |
| Active mode fails, `PORT` shows an odd address | Device advertises an unroutable address | Leave **Ignore the address a device sends in PORT/EPRT** on |
| Device disconnects immediately after connecting | Firmware chokes on the feature list | Turn on **Send a minimal FEAT reply**; if that doesn't help, turn off **Enable EPSV** and **Enable EPRT** |
| "Directory not found" or upload rejected | Device uploads into a folder it never creates | Leave **Create missing directories automatically** on |
| Device connects but shows an empty or garbled folder | Listing format | Keep **Directory listing style** on `unix` |
| Filenames with accents come out as mojibake | Old device sending a legacy codepage | Set **Fallback text encoding** to `windows-1252` (default) or `iso-8859-1` |
| Upload fails on files with `:` or `?` in the name | Illegal Windows filename characters | Leave **Replace characters that are illegal in Windows filenames** on |
| Downstream process grabs half-written scans | No staging file | Leave **Upload to a .part file and rename when complete** on |
| Second scan of the same name overwrites the first | Duplicate policy | Set **If the file already exists** to `rename` (default) |
| Device tries TLS and fails | FTPS is not supported | The server replies `534` so devices fall back to plaintext. Turn off FTP-SSL on the device |
| Everything works until the PC reboots | Service not set to start automatically | `sc qc BasicFtpServerService` should show `AUTO_START` |

### The passive port range

This is the failure that wastes the most time. Windows reserves blocks of ports for Hyper-V,
WSL and WinNAT, and those blocks commonly sit inside the range FTP servers traditionally use.
Binding fails with an unhelpful error and only passive transfers break.

Check what is reserved:

```powershell
netsh int ipv4 show excludedportrange protocol=tcp
```

The default range here is **55000–55100**, chosen to sit clear of the usual reservations. If
your machine reserves part of it, pick another range in Settings → Server, then click
**Update Windows Firewall Rules** so the inbound rule matches.

### Port 21 already in use

Usually the IIS FTP service or a leftover FTP server. Find the owner:

```powershell
Get-Process -Id (Get-NetTCPConnection -LocalPort 21 -State Listen).OwningProcess
```

Stop it, or change the control port in Settings — but note that most copiers assume 21, so
stopping the other server is usually the better answer.

### Useful commands

```powershell
BasicFtpServer.exe --status
```

```powershell
Get-Content "$env:ProgramData\BasicFtpServerService\logs\ftpserver-*.log" -Tail 50
```
