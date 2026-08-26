#define MyAppName "Desktop Boxes"
#define MyAppExe "DesktopBoxesUI.exe"

; Version priority: build.ps1 forwards the nbgv-computed version (/DAppVersion=...) which carries
; prerelease labels like -beta.N; a bare iscc run falls back to the published exe's FileVersion.
#ifndef AppVersion
    #define MyAppVersion GetVersionNumbersString('src\DesktopBoxesUI\bin\Publish\' + MyAppExe)
#else
    #define MyAppVersion AppVersion
#endif

[Setup]
AppId={{6D63481B-105C-49A8-8C5E-52F761DD120B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppCopyright=Copyright (C) 2026 ezhassen.
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={commonpf}\Desktop Boxes
DefaultGroupName={#MyAppName}
OutputDir=..\..\..\..\Installer
OutputBaseFilename={#MyAppName} {#MyAppVersion}
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UsedUserAreasWarning=no
ShowLanguageDialog=auto
InternalCompressLevel=ultra64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
SourceDir=src\DesktopBoxesUI\bin\Publish

; Gracefully close a running instance before files are replaced (Windows Restart Manager sends the
; app a close request, which our app handles cleanly incl. a final state save), and never let the
; Restart Manager auto-relaunch it — reopening is handled explicitly below.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}";
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked;
Name: "runApplication"; Description: "Launch Desktop Boxes"; GroupDescription: "Post-Installation:";

[Files]
; Include everything from the publish folder
Source: "*.*"; Excludes: "*.pdb,*.xml,*.log, createdump.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Desktop Boxes"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\Desktop Boxes"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Desktop Boxes"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; Tasks: quicklaunchicon

[Run]
; Reopen silently when the app WAS running before the install and the launch task is unchecked.
Filename: "{app}\{#MyAppExe}"; \
    Flags: nowait runasoriginaluser; \
    Check: ShouldAutoReopen

; Post-install launch option (runs as the ORIGINAL user, not elevated admin).
Filename: "{app}\{#MyAppExe}"; \
    Description: "Launch Desktop Boxes"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser; \
    Tasks: runApplication

[UninstallRun]
; Make sure no running instance locks files during uninstall.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#MyAppExe}"" /F /T"; Flags: runhidden; RunOnceId: "CloseDesktopBoxes"

[Code]
var
  WasRunning: Boolean;

function IsDesktopBoxesRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // tasklist filtered by image name, piped into find: exit code 0 = process present.
  ResultCode := 1;
  Exec(
    ExpandConstant('{cmd}'),
    Format('/C tasklist /FI "IMAGENAME eq {#MyAppExe}" | find /I "{#MyAppExe}" >nul', []),
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Result := (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
begin
  // Remember BEFORE anything closes the app, so it can be reopened afterwards.
  WasRunning := IsDesktopBoxesRunning();
  Result := True;
end;

function ShouldAutoReopen(): Boolean;
begin
  // Reopen when it was open and the user did NOT also tick "Launch Desktop Boxes"
  // (otherwise the post-install entry launches it).
  Result := WasRunning and (not WizardIsTaskSelected('runApplication'));
end;

function InitializeUninstall(): Boolean;
begin
  Result := (MsgBox('Are you sure you want to uninstall Desktop Boxes?', mbConfirmation, MB_YESNO) = IDYES);
end;
