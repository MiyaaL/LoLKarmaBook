#ifndef PackageDir
  #error PackageDir must point to a complete Windows self-contained package.
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be supplied by installer-windows.ps1.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by installer-windows.ps1.
#endif
#ifndef AppVersionNumber
  #error AppVersionNumber must be supplied by installer-windows.ps1.
#endif
#ifndef InstallerBaseName
  #error InstallerBaseName must be supplied by installer-windows.ps1.
#endif

[Setup]
AppId={{CDC65738-79AB-41A0-BAA9-BBD847FA3406}
AppName=LoLKarmaBook
SetupIconFile=..\icon\app-icon.ico
AppVersion={#AppVersion}
AppPublisher=MiyaaL
AppPublisherURL=https://github.com/MiyaaL/LoLKarmaBook
AppSupportURL=https://github.com/MiyaaL/LoLKarmaBook/issues
DefaultDirName=D:\LoLKarmaBook
DefaultGroupName=LoLKarmaBook
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
DisableWelcomePage=no
UsePreviousAppDir=no
AllowRootDirectory=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#InstallerOutputDir}
OutputBaseFilename={#InstallerBaseName}
VersionInfoVersion={#AppVersionNumber}
VersionInfoProductVersion={#AppVersionNumber}
VersionInfoTextVersion={#AppVersion}
Compression=lzma2
SolidCompression=yes
DiskSpanning=no
WizardStyle=modern
Uninstallable=yes
UninstallDisplayIcon={app}\LoLKarmaBook.exe
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.DesktopShortcut=创建桌面快捷方式
chinesesimplified.InvalidDataDir=请选择独立的程序安装目录，不要安装到笔记数据目录。
english.DesktopShortcut=Create a desktop shortcut
english.InvalidDataDir=Choose a separate program directory instead of a notes data directory.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LoLKarmaBook"; Filename: "{app}\LoLKarmaBook.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\LoLKarmaBook"; Filename: "{app}\LoLKarmaBook.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function IsNotesDataDirectory(const Directory: String): Boolean;
var
  Selected: String;
begin
  Selected := AddBackslash(ExpandFileName(Directory));
  { Only known program directories bypass the pure-data-directory guard. }
  if FileExists(Selected + 'LoLKarmaBook.exe') then
  begin
    Result := False;
    Exit;
  end;
  Result := FileExists(Selected + 'desktop-settings.json')
    or DirExists(Selected + 'data\profiles');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and IsNotesDataDirectory(WizardDirValue) then
  begin
    MsgBox(CustomMessage('InvalidDataDir'), mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsNotesDataDirectory(ExpandConstant('{app}')) then
    Result := CustomMessage('InvalidDataDir');
end;
