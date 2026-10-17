; Inno Setup script for Cmdflip. Built by build.ps1 -Installer, which passes the version.
; Per-user install by default (no admin prompt); the user can switch to all users.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0.0"
#endif

[Setup]
AppId={{6F2B9C4E-3D1A-4E8B-9C7F-2A5D8E1B4C30}
AppName=Cmdflip
AppVersion={#AppVersion}
AppVerName=Cmdflip {#AppVersion}
AppPublisher=Marek Nereca
AppPublisherURL=https://github.com/marek-nereca/cmdflip
AppSupportURL=https://github.com/marek-nereca/cmdflip/issues
AppUpdatesURL=https://github.com/marek-nereca/cmdflip/releases
AppCopyright=Copyright (c) Marek Nereca
VersionInfoVersion={#AppNumericVersion}
DefaultDirName={autopf}\Cmdflip
DefaultGroupName=Cmdflip
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
OutputBaseFilename=Cmdflip-{#AppVersion}-setup
UninstallDisplayIcon={app}\Cmdflip.exe
SetupIconFile=..\assets\Cmdflip.ico
UninstallDisplayName=Cmdflip
; A running copy is closed through its quit event (see [Code]); Restart Manager can't close tray apps.
CloseApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763

[Tasks]
Name: "startup"; Description: "Start Cmdflip when I sign in"

[Files]
Source: "..\bin\Cmdflip.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Cmdflip"; Filename: "{app}\Cmdflip.exe"

[Registry]
; Same value the tray menu's "Start at login" toggles, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Cmdflip"; \
    ValueData: """{app}\Cmdflip.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Cmdflip"; \
    Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\Cmdflip.exe"; Description: "Start Cmdflip now"; Flags: nowait postinstall skipifsilent
; Silent installs and upgrades (e.g. winget) start the app straight away.
Filename: "{app}\Cmdflip.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
; Remove the "start as administrator" task if the user created one (fails quietly otherwise).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN Cmdflip /F"; Flags: runhidden; RunOnceId: "DeleteTask"

[Code]
const
  AppMutexName = 'Local\Cmdflip.SingleInstance';
  QuitEventName = 'Local\Cmdflip.Quit';
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(Access: DWORD; Inherit: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

// Ask a running Cmdflip to exit and wait for it. Returns True when it is no longer running.
function CloseRunningApp(): Boolean;
var
  Handle: THandle;
  I: Integer;
begin
  Result := not CheckForMutexes(AppMutexName);
  if Result then Exit;
  Handle := OpenEvent(EVENT_MODIFY_STATE, False, QuitEventName);
  if Handle <> 0 then
  begin
    SetEvent(Handle);
    CloseHandle(Handle);
  end;
  for I := 1 to 50 do
  begin
    if not CheckForMutexes(AppMutexName) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(100);
  end;
end;

function EnsureAppClosed(): Boolean;
begin
  Result := True;
  while not CloseRunningApp() do
    if SuppressibleMsgBox('Cmdflip is still running. Please exit it from the tray icon, then click Retry.',
                          mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

function InitializeSetup(): Boolean;
begin
  Result := EnsureAppClosed();
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureAppClosed();
end;
