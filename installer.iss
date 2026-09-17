#define MyAppName "Desktop Boxes"
#define MyAppExe "DesktopBoxesUI.exe"

; Version is provided by build.ps1 via BuildVersion.inc (written next to this script before the
; compile). It carries MinVer labels, e.g. '#define AppVersion "1.0.2-beta"'.
#include "BuildVersion.inc"

; Strictly numeric variant for directives that reject prerelease labels (VersionInfoVersion):
; cut at first '-' or '+'. "1.0.4-beta+abc" -> "1.0.4".
#define TmpVer MyAppVersion
#define PlusPos Pos('+', TmpVer)
#if PlusPos > 0
    #define TmpVer Copy(TmpVer, 1, PlusPos - 1)
#endif
#define DashPos Pos('-', TmpVer)
#if DashPos > 0
    #define TmpVer Copy(TmpVer, 1, DashPos - 1)
#endif
#define MyNumericVersion TmpVer

[Setup]
AppId={{6D63481B-105C-49A8-8C5E-52F761DD120B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Ezz Hassan
AppCopyright=Copyright (C) 2026 Ezz Hassan.
AppVerName={#MyAppName} {#MyAppVersion}
; Full version-resource identity: heuristic scanners and SmartScreen weigh missing
; Company/Product metadata as a (weak) malware signal. Names must match the assembly
; metadata in Directory.Build.targets (Company "Ezz Hassan") — one consistent publisher
; everywhere builds reputation instead of splitting it.
VersionInfoVersion={#MyNumericVersion}
VersionInfoCompany=Ezz Hassan
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} setup
VersionInfoCopyright=Copyright (C) 2026 Ezz Hassan.
VersionInfoTextVersion={#MyAppVersion}
; Optional Authenticode signing (the single biggest false-positive reducer — an unsigned,
; admin-privileged, freshly-built setup with no reputation is exactly what generic
; heuristics fire on). Configure once, then uncomment:
;   SignTool=signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a $f
;   SignedUninstaller=yes
; (SignTool itself is registered with: iscc /Ssigntool="signtool sign ... $f" setup.iss,
; or via Tools > Configure Sign Tools in Inno Setup.)
DefaultDirName={commonpf}\Desktop Boxes
DefaultGroupName={#MyAppName}
OutputDir=..\..\..\..\Installer
OutputBaseFilename={#MyAppName} {#MyAppVersion}
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UsedUserAreasWarning=no
ShowLanguageDialog=auto
; lzma2/max, not ultra64: ultra-grade solid compression raises the output's entropy into
; the range packer heuristics look at, for little size gain — and it slows every build.
InternalCompressLevel=max
Compression=lzma2/max
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
