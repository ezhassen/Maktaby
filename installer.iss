#define MyAppName "Maktaby"
#define MyAppExe "Maktaby.exe"

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
AppId={{EAF11F66-B8AF-48F3-A785-CE0394AE912E}
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
DefaultDirName={commonpf}\Maktaby
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
SourceDir=src\Maktaby\bin\Publish

; Gracefully close a running instance before files are replaced (Windows Restart Manager sends the
; app a close request, which our app handles cleanly incl. a final state save), and never let the
; Restart Manager auto-relaunch it — reopening is handled explicitly below.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}";
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked;
Name: "runApplication"; Description: "Launch Maktaby"; GroupDescription: "Post-Installation:";
Name: "startup"; Description: "Launch Maktaby on Windows startup"; GroupDescription: "Post-Installation:"; Flags: checked;

[Files]
; Include everything from the publish folder
Source: "*.*"; Excludes: "*.pdb,*.xml,*.log, createdump.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Maktaby"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\Maktaby"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Maktaby"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; Tasks: quicklaunchicon

[Run]
; Reopen silently when the app WAS running before the install and the launch task is unchecked.
Filename: "{app}\{#MyAppExe}"; \
    Flags: nowait runasoriginaluser; \
    Check: ShouldAutoReopen

; Post-install launch option (runs as the ORIGINAL user, not elevated admin).
Filename: "{app}\\{#MyAppExe}"; \
    Description: "Launch Maktaby"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser; \
    Tasks: runApplication

; Offer launch-on-startup via the app's own Settings toggle. The app owns the actual
; HKCU Run-key write (StartupManager.Enable/Disable); the installer merely starts the app
; with a flag so the write happens in the correct (original, non-elevated) user context
; right after install. A running instance will pick the flag up and re-enable on next start.
Filename: "{app}\\{#MyAppExe}"; \
    Description: "Configure Maktaby to launch on Windows startup"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser; \
    Tasks: startup

[UninstallRun]
; Make sure no running instance locks files during uninstall.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#MyAppExe}"" /F /T"; Flags: runhidden; RunOnceId: "CloseMaktaby"

[Code]
var
  WasRunning: Boolean;

function ReadRegStr(Key: String; Name: String): String;
var
  R: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKEY_CURRENT_USER, Key, Name, Result) then
    Result := '';
end;

function IsMaktabyStartupEnabled(): Boolean;
begin
  Result := ReadRegStr('Software\Microsoft\Windows\CurrentVersion\Run', 'Maktaby') <> '';
end;

function SetMaktabyStartup(On: Boolean): Boolean;
var
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\\{#MyAppExe}');
  if On then
    Result := RegWriteStringValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Maktaby', '"' + ExePath + '"')
  else
    Result := RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Maktaby');
end;

function IsMaktabyRunning(): Boolean;
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
  WasRunning := IsMaktabyRunning();
  // If the user asked for startup and the Run key is absent, write it now (elevated
  // installer writing to HKCU is fine — it lands in the installing user's hive, which
  // is the user who will run the app). Skip when already present so a user toggle isn't
  // clobbered by a reinstall.
  if WizardIsTaskSelected('startup') and not IsMaktabyStartupEnabled() then
    SetMaktabyStartup(True);
  Result := True;
end;

function ShouldAutoReopen(): Boolean;
begin
  // Reopen when it was open and the user did NOT also tick "Launch Maktaby"
  // (otherwise the post-install entry launches it).
  Result := WasRunning and (not WizardIsTaskSelected('runApplication'));
end;

function InitializeUninstall(): Boolean;
begin
  // Clear the Run-key entry we may have written (either by the installer task above or by
  // the app's Settings toggle). Best-effort: a manually-added value with a different path
  // is not touched, and deleting an absent value is a no-op.
  SetMaktabyStartup(False);
  Result := (MsgBox('Are you sure you want to uninstall Maktaby?', mbConfirmation, MB_YESNO) = IDYES);
end;
