; Inno Setup script for Basic FTP Server Service.
;
; Build with:  powershell -File build.ps1
; (build.ps1 publishes the app first, then invokes ISCC on this script.)

#define AppName        "Basic FTP Server Service"
#define AppPublisher   "Basic FTP Server Service"
#define AppExe         "BasicFtpServer.exe"
#define AppExeSource   "..\publish\" + AppExe

; Read the version out of the executable we are about to package, so it can never disagree
; with the assembly version set in Directory.Build.props.
#define AppVersion     GetStringFileInfo(AppExeSource, "ProductVersion")
#define ServiceName    "BasicFtpServerService"
#define TrayTaskName   "BasicFtpServerServiceTray"

[Setup]
AppId={{7F3C1A54-9D2E-4B18-A6C7-2E5B9F0D4A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=BasicFtpServerService-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}

; The installer registers a service, writes firewall rules and creates a scheduled task,
; all of which require a full administrator token.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\COPIER-SETUP.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Goes via the scheduled task rather than the executable directly. The tray is manifested
; requireAdministrator, so launching it from a shortcut always raises a UAC prompt; the task
; is registered to run with highest privileges and starts it elevated, silently. It also
; behaves correctly either way round — if no tray is running the task starts one, and if one
; already is, the second instance asks it to show its settings window and exits.
Name: "{group}\{#AppName}"; Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#TrayTaskName}"""; IconFilename: "{app}\{#AppExe}"; Comment: "Open the Basic FTP Server Service settings"; Flags: runminimized
Name: "{group}\Copier Setup Guide"; Filename: "{app}\docs\COPIER-SETUP.md"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--install-service"; StatusMsg: "Registering the Windows service..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--add-firewall-rules"; StatusMsg: "Adding Windows Firewall rules..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--register-tray ""{code:GetOriginalUser}"""; StatusMsg: "Registering the tray icon to start at logon..."; Flags: runhidden waituntilterminated
; runascurrentuser is required here. Postinstall entries default to runasoriginaluser —
; i.e. de-elevated — and the tray is manifested requireAdministrator, so without this flag
; the finish-page checkbox triggers a UAC prompt. Inheriting Setup's already-elevated token
; starts the tray silently, matching how the logon task starts it from then on.
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Description: "Open the settings tray now"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-tray"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTrayTask"
Filename: "{app}\{#AppExe}"; Parameters: "--remove-firewall-rules"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
// Configuration and logs live in %ProgramData% and are intentionally left in place on
// uninstall, so reinstalling does not lose the accounts and scan folders.

var
  CachedOriginalUser: String;

// The account that will actually be logged in and needs the tray.
//
// {username} is the account that answered the UAC prompt, which is NOT the same person
// when a technician elevates with a separate admin account — the usual case in a domain.
// Registering the logon task against that admin would mean the real user never gets a
// tray. ExecAsOriginalUser drops back to the pre-elevation context to find out who that is.
function GetOriginalUser(Param: String): String;
var
  TempFile: String;
  Lines: TArrayOfString;
  ResultCode: Integer;
begin
  if CachedOriginalUser <> '' then
  begin
    Result := CachedOriginalUser;
    exit;
  end;

  CachedOriginalUser := ExpandConstant('{username}');

  TempFile := ExpandConstant('{tmp}\originaluser.txt');
  if ExecAsOriginalUser(ExpandConstant('{cmd}'),
                        '/C echo %USERDOMAIN%\%USERNAME%>"' + TempFile + '"',
                        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TempFile, Lines) then
      if GetArrayLength(Lines) > 0 then
        if Trim(Lines[0]) <> '' then
          CachedOriginalUser := Trim(Lines[0]);
    DeleteFile(TempFile);
  end;

  Result := CachedOriginalUser;
end;

procedure StopRunningComponents;
var
  ResultCode: Integer;
begin
  // The tray holds the executable open, which would block file replacement on upgrade.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningComponents;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningComponents;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MsgBox(
      'Two things to check before pointing copiers at this machine:' + #13#10 + #13#10 +
      '1. Give this PC a static IP address or a DHCP reservation.' + #13#10 +
      '    Copiers are configured with a fixed address; if this machine''s IP moves,' + #13#10 +
      '    every device stops scanning at once.' + #13#10 + #13#10 +
      '2. Stop this PC from sleeping.' + #13#10 +
      '    Settings > System > Power > Screen and sleep > Sleep: Never.' + #13#10 +
      '    A sleeping machine cannot accept scans.' + #13#10 + #13#10 +
      'Open the tray icon to add an account and choose a scan folder.',
      mbInformation, MB_OK);
  end;
end;
