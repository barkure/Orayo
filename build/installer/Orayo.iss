#define MyAppName "Orayo"
#define MyAppExeName "Orayo.exe"
#ifndef AppVersion
#define AppVersion "0.0.0.0"
#endif
#ifndef OutputBaseFilename
#define OutputBaseFilename "Orayo-setup"
#endif
#ifndef OutputDir
#define OutputDir "..\..\artifacts"
#endif

[Setup]
AppId={{8D00CF6F-0808-45D6-8B22-B1E979D28496}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher=Orayo
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename={#OutputBaseFilename}
OutputDir={#OutputDir}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
