; Inno Setup 6/7 — mở file này trong Inno Setup Compiler rồi Build → Compile.
; Trước đó chạy prepare-inno.bat (copy app + Chromium vào thư mục files\).
; Tiếng Việt: Vietnamese.isl nằm cạnh script.

#ifexist "version.iss"
  #include "version.iss"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif

#define MyAppName "AutoClick"
#define MyAppPublisher "AutoClick"
#define MyAppExeName "AutoClick.exe"
#define MyAppURL "https://scan.thuoc360.com"

[Setup]
AppId={{B4E19C72-8A31-4F6D-9E20-7C5A1D8B3F44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=AutoClick-Setup-{#MyAppVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no

[Languages]
Name: "vietnamese"; MessagesFile: "Vietnamese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "files\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
