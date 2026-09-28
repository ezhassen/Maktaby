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

[Files]
; Include everything from the publish folder
Source: "*.*"; Excludes: "*.pdb,*.xml,*.log, createdump.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Maktaby"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\Maktaby"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Maktaby"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; Tasks: quicklaunchicon

[Run]
; Post-install: launch the app. No Description / postinstall on purpose — a postinstall
; [Run] entry with a Description is rendered as a checkbox on the Select Additional Tasks
; page ("Post-Installation:" group). The checkbox lives on the Completing page instead
; (see LaunchCheckBox in [Code]); this entry just runs the action that Check gates.
Filename: "{app}\{#MyAppExe}"; \
    Flags: nowait runasoriginaluser; \
    Check: ShouldLaunchApp

; Post-install: enable Windows startup. Same no-Description trick as above, for the same
; reason. The app owns the HKCU Run-key write via --enable-startup, running as the original
; user (not the elevated installer), so the value lands in the correct hive.
Filename: "{app}\{#MyAppExe}"; \
    Parameters: "--enable-startup"; \
    Flags: nowait runasoriginaluser; \
    Check: ShouldConfigureStartup

[UninstallRun]
; Make sure no running instance locks files during uninstall.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#MyAppExe}"" /F /T"; Flags: runhidden; RunOnceId: "CloseMaktaby"

[Code]
var
  WasRunning: Boolean;
  // Checkboxes shown on the Completing wizard page, launch first. Created in
  // InitializeWizard, which Inno Setup does NOT call for silent installs (/SILENT,
  // /VERYSILENT) — every access here must nil-guard, or the Check function raises
  // "Could not call proc." on the object.
  LaunchCheckBox: TCheckBox;
  StartupCheckBox: TCheckBox;

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

// Own checkboxes on the Completing page rather than [Tasks] entries, so neither option
// shows up on the Select Additional Tasks page. Launch sits above the startup option.
procedure InitializeWizard();
var
  Top: Integer;
begin
  Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);

  LaunchCheckBox := TCheckBox.Create(WizardForm);
  with LaunchCheckBox do
  begin
    Caption := 'Launch the app';
    Parent := WizardForm.FinishedPage;
    Left := WizardForm.FinishedLabel.Left;
    Top := Top;
    Width := WizardForm.FinishedLabel.Width;
    Checked := True;
  end;
  Top := Top + ScaleY(24);

  StartupCheckBox := TCheckBox.Create(WizardForm);
  with StartupCheckBox do
  begin
    Caption := 'Configure Maktaby to launch on Windows startup';
    Parent := WizardForm.FinishedPage;
    Left := WizardForm.FinishedLabel.Left;
    Top := Top;
    Width := WizardForm.FinishedLabel.Width;
    // Default on: this is the behavior most installs want, and the write is
    // idempotent (StartupManager.Enable overwrites the same Run-key value).
    Checked := True;
  end;
end;

// Launch the app after install: explicit user choice, or reopening an instance that
// was already running before the install. Unchecked in a silent install => only reopen.
function ShouldLaunchApp(): Boolean;
begin
  if LaunchCheckBox = nil then
    Result := WasRunning
  else
    Result := LaunchCheckBox.Checked or WasRunning;
end;

// Gate the --enable-startup post-install run on our Completing-page checkbox.
// Nil (silent install, no InitializeWizard) => leave the Run key alone.
function ShouldConfigureStartup(): Boolean;
begin
  if StartupCheckBox = nil then
    Result := False
  else
    Result := StartupCheckBox.Checked;
end;

function InitializeSetup(): Boolean;
begin
  // Remember BEFORE anything closes the app, so it can be reopened afterwards
  // (see ShouldLaunchApp, which launches when the user ticks the box or the app was open).
  WasRunning := IsMaktabyRunning();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  // Inno Setup shows its own uninstall confirmation prompt by default; don't duplicate it.
  // The [UninstallRun] taskkill still runs to close any live instance before files are removed.
  Result := True;
end;
