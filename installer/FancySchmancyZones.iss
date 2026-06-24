; Inno Setup script for Fancy Schmancy Zones
; Builds a one-click, per-user installer (no admin needed) that creates Start Menu
; and Desktop shortcuts, with an optional "start at sign-in" choice.

#define AppName "Fancy Schmancy Zones"
#define AppVersion "0.3.1"
#define AppPublisher "Keith Blanco"
#define AppExeName "FancySchmancyZones.exe"
#define AppId "{{B6F1B3A2-7C4E-4E2A-9C1D-9F3E5A0C2D11}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Fancy Schmancy Zones
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=FancySchmancyZones-Setup-{#AppVersion}
SetupIconFile=..\src\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &Desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start &automatically when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\src\bin\Release\net8.0-windows\win-x64\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
