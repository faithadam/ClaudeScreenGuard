; Inno Setup script for Claude Screen Guard
; Build: ISCC.exe /DMyAppVersion=1.0.0 installer\ClaudeScreenGuard.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#define MyAppName "Claude Screen Guard"
#define MyAppExe "ClaudeScreenGuard.exe"
#define MyAppURL "https://github.com/faithadam/ClaudeScreenGuard"

[Setup]
AppId={{5C3E2B7A-9D41-4F6B-8A2E-6C1D7F0B3A91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=faithadam
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={localappdata}\Programs\ClaudeScreenGuard
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ClaudeScreenGuard-Setup-{#MyAppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
#ifexist "ChineseSimplified.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"
#endif

[CustomMessages]
en.AutoStart=Start automatically when Windows starts
en.LaunchNow=Launch Claude Screen Guard now
#ifexist "ChineseSimplified.isl"
zh.AutoStart=开机时自动启动
zh.LaunchNow=立即启动 Claude 屏幕遮罩
#endif

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"

[Files]
Source: "..\bin\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\ClaudeScreenGuard"; Filename: "{app}\{#MyAppExe}"
Name: "{userstartup}\ClaudeScreenGuard"; Filename: "{app}\{#MyAppExe}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im {#MyAppExe} /f"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
Type: files; Name: "{app}\config.ini"
Type: files; Name: "{app}\config.ini.old"
Type: files; Name: "{app}\guard.log"
Type: files; Name: "{app}\guard.log.old"
Type: files; Name: "{app}\frost_preview.png"

[Code]
// Stop a running copy before files are replaced (upgrades)
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im {#MyAppExe} /f', '', SW_HIDE, ewWaitUntilTerminated, R);
  Sleep(500);
  Result := '';
end;
