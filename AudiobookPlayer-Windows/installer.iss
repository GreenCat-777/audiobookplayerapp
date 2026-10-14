; Optional installer (the Windows equivalent of the Mac DMG).
; 1) Run .\build.ps1   2) Install Inno Setup (https://jrsoftware.org/isinfo.php)   3) Open this file and press Compile.
#define AppName "Audiobook Player"
#define AppVersion "1.0.0"

[Setup]
AppId={{B7B1C9A4-5F0F-4E0E-9B5E-6A3B6C2C7C11}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=dist
OutputBaseFilename=AudiobookPlayer-Setup
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\AudiobookPlayer.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "dist\win-x64\AudiobookPlayer.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: not IsArm64
Source: "dist\win-arm64\AudiobookPlayer.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: IsArm64

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\AudiobookPlayer.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\AudiobookPlayer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AudiobookPlayer.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
