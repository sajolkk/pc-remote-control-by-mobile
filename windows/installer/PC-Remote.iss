; PC-Remote installer (Inno Setup 6). Build it with build-installer.ps1 beside this file, which
; publishes both executables first and then compiles this script.
;
; What installing does, in order:
;   1. stops and removes any earlier PC-Remote service, so its files can be replaced;
;   2. copies RemoteAgent.Service.exe and RemoteAgent.Session.exe to Program Files\PC-Remote;
;   3. runs "RemoteAgent.Service.exe --install", which registers the service, adds the three
;      Private-network firewall rules and starts it (the service then starts the tray agent);
;   4. adds a "PC-Remote" Start-menu entry. Opening it shows the pairing window.
;
; Uninstalling runs "--uninstall" (service and firewall rules). Settings, keys and pairings in
; %ProgramData%\PCRemote are kept, so reinstalling does not force every phone to pair again.

#define AppName "PC-Remote"
#define AppVersion "0.2.0"
#define AppPublisher "PC-Remote"
#define ServiceName "PCRemoteAgent"
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\PC-Remote"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\release"
#endif

[Setup]
; Never change AppId: it is how an upgrade finds the installation it replaces.
AppId={{6B0C1D52-8E2F-4B7A-9C41-3F5D2A7E9B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=PC-Remote-Setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\RemoteAgent.Session.exe
; The service is stopped explicitly in PrepareToInstall; this also closes anything else holding
; the files, e.g. an Explorer window showing the folder.
CloseApplications=yes
RestartApplications=no

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nPC-Remote lets your phone see and control this PC over your own Wi-Fi. After installing, open PC-Remote on your phone and tap this PC to pair.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
; Offered only when a connected network is marked Public, which is the most common reason the
; phone cannot find the PC: the firewall rules deliberately apply to Private networks only.
Name: "privatenetwork"; Description: "Set my current network to &Private, so my phone can find this PC (recommended at home, not on public Wi-Fi)"; GroupDescription: "Network:"; Check: IsOnPublicNetwork

[Files]
Source: "{#SourceDir}\RemoteAgent.Service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\RemoteAgent.Session.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\RemoteAgent.Session.exe"; Comment: "Pair a phone with this PC"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\RemoteAgent.Session.exe"; Comment: "Pair a phone with this PC"; Tasks: desktopicon

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public' | Set-NetConnectionProfile -NetworkCategory Private"""; \
  StatusMsg: "Setting the network to Private..."; Flags: runhidden waituntilterminated; Tasks: privatenetwork
Filename: "{app}\RemoteAgent.Service.exe"; Parameters: "--install"; \
  StatusMsg: "Registering and starting the PC-Remote service..."; Flags: runhidden waituntilterminated
Filename: "{app}\RemoteAgent.Session.exe"; Parameters: "--after-install"; \
  Description: "Open PC-Remote now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\RemoteAgent.Service.exe"; Parameters: "--uninstall"; RunOnceId: "RemoveService"; Flags: runhidden waituntilterminated
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM RemoteAgent.Session.exe"; RunOnceId: "StopAgents"; Flags: runhidden waituntilterminated

[Code]
function IsOnPublicNetwork: Boolean;
var
  ResultCode: Integer;
begin
  // Exit code 1 means at least one connected network is Public.
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "if (Get-NetConnectionProfile | Where-Object NetworkCategory -eq ''Public'') { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 1);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  OldService: String;
begin
  Result := '';

  // Upgrade or repair: remove the old service (this also stops it) so its executable can be
  // replaced, then make sure no agent is left holding RemoteAgent.Session.exe.
  OldService := ExpandConstant('{app}\RemoteAgent.Service.exe');
  if FileExists(OldService) then
    Exec(OldService, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM RemoteAgent.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM RemoteAgent.Session.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Give the service control manager a moment to finish deleting, or "sc create" in --install
  // fails with "marked for deletion".
  Sleep(2000);
end;
