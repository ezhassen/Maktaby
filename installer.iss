#define MyAppName "Desktop Boxes"
#define MyAppVersion  GetVersionNumbersString('src\DesktopBoxesUI\bin\Publish\DesktopBoxesUI.exe')

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
ShowLanguageDialog=auto
InternalCompressLevel=ultra64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
SourceDir=src\DesktopBoxesUI\bin\Publish

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}";
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked;
Name: "runApplication"; Description: "Launch Desktop Boxes"; GroupDescription: "Post-Installation:";

[Files]
; Include everything from the publish folder
Source: "*.*"; Excludes: "*.pdb,*.xml,*.log, createdump.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Desktop Boxes"; Filename: "{app}\DesktopBoxesUI.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Desktop Boxes"; Filename: "{app}\DesktopBoxesUI.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Desktop Boxes"; Filename: "{app}\DesktopBoxesUI.exe"; WorkingDir: "{app}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\DesktopBoxesUI.exe"; Description: "Launch Desktop Boxes"; Flags: nowait postinstall skipifsilent runascurrentuser

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := (MsgBox('Are you sure you want to uninstall Desktop Boxes?', mbConfirmation, MB_YESNO) = IDYES);
end;
