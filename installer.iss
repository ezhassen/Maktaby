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
; Post-install: launch the app. No Description on purpose — a [Run] entry with a
; Description is rendered as an auto checkbox in the RunList. The checkbox lives on the
; Completing page instead (see LaunchCheckBox in [Code]); this entry just runs the
; action that Check gates. `postinstall' defers execution until the user clicks Finish
; (without it the entry runs as soon as files are installed, before the Completing
; page is even shown). No `skipifsilent': silent installs are gated by the Check
; functions alone (nil checkboxes => reopen-if-was-running, leave startup alone).
; NOTE: a Description-less postinstall entry is NOT hidden — Inno auto-lists it in the
; RunList with a default "Run <exe>" caption. CurPageChanged below hides that auto
; RunList on the Finished page (hidden items still run when their Check passes), so
; only our two checkboxes show.
Filename: "{app}\{#MyAppExe}"; \
    Flags: nowait runasoriginaluser postinstall; \
    Check: ShouldLaunchApp

; Post-install: enable Windows startup. Same no-Description trick as above, for the same
; reason. The app owns the HKCU Run-key write via --enable-startup, running as the original
; user (not the elevated installer), so the value lands in the correct hive.
Filename: "{app}\{#MyAppExe}"; \
    Parameters: "--enable-startup"; \
    Flags: nowait runasoriginaluser postinstall; \
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
  LaunchCheckBox: TNewCheckBox;
  StartupCheckBox: TNewCheckBox;

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

// Proactively close a running instance when the user clicks Install, before Inno's own
// Restart Manager pass runs. RM cannot gracefully shut a plain WPF app down, which used
// to surface the "files in use / close applications" prompt instead of just closing it.
// A plain taskkill (no /F) delivers WM_CLOSE so the app exits through its normal path
// (OnExit state save), with a forced tree-kill only as a backstop (also reaps WebView2
// child processes). WasRunning (snapshotted in InitializeSetup) still drives the
// reopen after Finish, so a running install stays seamless: closed, updated, relaunched.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  WaitedMs: Integer;
begin
  NeedsRestart := False;
  Result := '';

  if not WasRunning then
    Exit;
  if not IsMaktabyRunning() then
    Exit;

  // Graceful first: WM_CLOSE, no /F.
  Exec(ExpandConstant('{cmd}'),
    '/C taskkill /IM "{#MyAppExe}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Wait up to ~10 s for the normal close path (tray teardown, state save).
  WaitedMs := 0;
  while IsMaktabyRunning() and (WaitedMs < 10000) do
  begin
    Sleep(500);
    WaitedMs := WaitedMs + 500;
  end;

  // Backstop: force-kill the whole tree.
  if IsMaktabyRunning() then
    Exec(ExpandConstant('{cmd}'),
      '/C taskkill /F /T /IM "{#MyAppExe}"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Own checkboxes on the Completing page rather than [Tasks] entries, so neither option
// shows up on the Select Additional Tasks page. Launch sits above the startup option.
// NOTE: no `with' blocks here on purpose — inside `with CheckBox do', an unqualified
// `Top' resolves to the control's own property, so `Top := Top' was a silent self-assign
// that left both boxes at Top=0, overlapping each other and the heading.
procedure InitializeWizard();
var
  CheckTop: Integer;
begin
  CheckTop := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);

  LaunchCheckBox := TNewCheckBox.Create(WizardForm);
  LaunchCheckBox.Parent := WizardForm.FinishedPage;
  LaunchCheckBox.Caption := 'Launch Maktaby';
  LaunchCheckBox.Left := WizardForm.FinishedLabel.Left;
  LaunchCheckBox.Top := CheckTop;
  LaunchCheckBox.Width := WizardForm.FinishedPage.ClientWidth - LaunchCheckBox.Left - ScaleX(16);
  LaunchCheckBox.Checked := True;

  CheckTop := LaunchCheckBox.Top + LaunchCheckBox.Height + ScaleY(8);

  StartupCheckBox := TNewCheckBox.Create(WizardForm);
  StartupCheckBox.Parent := WizardForm.FinishedPage;
  StartupCheckBox.Caption := 'Start Maktaby on Windows startup';
  StartupCheckBox.Left := LaunchCheckBox.Left;
  StartupCheckBox.Top := CheckTop;
  StartupCheckBox.Width := LaunchCheckBox.Width;
  // Default on: this is the behavior most installs want, and the write is
  // idempotent (StartupManager.Enable overwrites the same Run-key value).
  StartupCheckBox.Checked := True;
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

// Hide Inno's auto RunList when the Completing page shows: both postinstall [Run]
// entries above would otherwise appear there with default "Run Maktaby.exe" captions,
// duplicating (and overlapping) our own checkboxes. A hidden RunList item keeps its
// default checked state and still executes after Finish when its Check passes, so
// hiding changes only what the user sees. Re-seat our boxes here too, since the
// FinishedLabel metrics are final at this point.
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (LaunchCheckBox <> nil) and (StartupCheckBox <> nil) then
  begin
    WizardForm.RunList.Visible := False;
    LaunchCheckBox.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
    StartupCheckBox.Top := LaunchCheckBox.Top + LaunchCheckBox.Height + ScaleY(8);
  end;
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

// Best-effort removal of one Run-key startup value. Only deletes when the value points
// at this install's exe (unquoted before comparing, since StartupManager quotes the
// path), so a foreign value that happens to share the name is never touched.
procedure DeleteRunValue(const Name: String);
var
  Value: String;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run', Name, Value) then
  begin
    StringChangeEx(Value, '"', '', True);
    if CompareText(Trim(Value), ExpandConstant('{app}\{#MyAppExe}')) = 0 then
      RegDeleteValue(HKEY_CURRENT_USER,
        'Software\Microsoft\Windows\CurrentVersion\Run', Name);
  end;
end;

// Clean up our startup entries during uninstall — in usUninstall, not InitializeUninstall,
// so cancelling the confirmation prompt leaves the key alone. 'Maktaby_Debug' covers
// dev-machine installs that ran a Debug build's toggle.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteRunValue('Maktaby');
    DeleteRunValue('Maktaby_Debug');
  end;
end;
