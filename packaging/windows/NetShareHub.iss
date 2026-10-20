; Script Inno Setup 6 (https://jrsoftware.org/isinfo.php) — installeur Windows de NetShare Hub.
; Compilation : iscc /DAppVersion=1.0.1 /DSourceDir=..\..\dist\win-x64 NetShareHub.iss   (build.ps1 le fait tout seul si Inno Setup est installé)

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\win-x64"
#endif

#define AppName "NetShare Hub"
#define AppExe "NetShareHub.exe"

[Setup]
; Identifiant stable : permet les mises à jour par-dessus une version existante.
AppId={{6F0B8D3E-2C41-4B9A-8E57-5D1A9C7E3B20}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\..\dist
OutputBaseFilename=NetShareHub-Setup-{#AppVersion}-x64
SetupIconFile=..\..\Src\NetShareHub\Desktop\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Installation pour tous les utilisateurs : le partage de connexion demande de toute façon les droits administrateur.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
