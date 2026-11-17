; Overtone setup for Windows (Inno Setup 6).
;
; Per-user install, no admin rights: %LOCALAPPDATA%\Programs\Overtone, a Start menu
; shortcut, optional start at sign-in (the same Run entry the tray menu's
; "Start with Windows" toggles), and an entry in Settings > Apps that uninstalls.
; Settings in %LOCALAPPDATA%\Overtone are kept on uninstall.
;
; Build (after dotnet publish into SourceDir):
;   iscc /DAppVersion=0.1.0 /DNumVersion=0.1.0 /DArch=x64 /DSourceDir=..\..\..\publish\win-x64 /DOutputDir=..\..\..\dist Overtone.iss
; Arch: x64 or arm64.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumVersion
  #define NumVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<dotnet publish output>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{6A1C3E52-7D0B-4E8A-9C2F-0B7F3D41A9E6}
AppName=Overtone
AppVersion={#AppVersion}
AppVerName=Overtone {#AppVersion}
VersionInfoVersion={#NumVersion}
AppPublisher=BUNK
AppPublisherURL=https://github.com/bunkhq/overtone
AppSupportURL=https://github.com/bunkhq/overtone/issues
DefaultDirName={localappdata}\Programs\Overtone
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Overtone-{#AppVersion}-Windows-{#Arch}-Setup
SetupIconFile=..\Overtone\Assets\overtone.ico
UninstallDisplayIcon={app}\Overtone.exe
UninstallDisplayName=Overtone
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Tasks]
Name: startup; Description: "Start Overtone when I sign in"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files dropped from a newer build don't linger from an older one.
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{userprograms}\Overtone"; Filename: "{app}\Overtone.exe"; Comment: "Now playing and synced lyrics, floating over your desktop"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Overtone"; ValueData: """{app}\Overtone.exe"" --startup"; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueName: "Overtone"; Flags: deletevalue uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Overtone.exe"; Description: "Open Overtone"; Flags: nowait postinstall

[Code]
// Overtone lives in the tray with no window to close, so stop it before
// replacing or removing its files.
procedure StopOvertone();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Overtone.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopOvertone();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopOvertone();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The tray menu can turn startup on after install; don't leave a dangling entry.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Overtone');
end;
