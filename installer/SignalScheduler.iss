#define MyAppName "Signal Auto Scheduler"
#ifndef MyAppVersion
 #define MyAppVersion "8.0.0-alpha.3"
#endif
#ifndef PublishRoot
 #define PublishRoot "..\artifacts\publish"
#endif
#ifndef OutputRoot
 #define OutputRoot "..\artifacts\installer"
#endif

[Setup]
AppId={{E7AF7F8E-10B1-4F56-9D6E-35F7E7BB8002}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=MS002
DefaultDirName={localappdata}\Programs\Signal Auto Scheduler
DefaultGroupName=Signal Auto Scheduler
DisableDirPage=no
DisableProgramGroupPage=no
OutputDir={#OutputRoot}
OutputBaseFilename=SignalScheduler_Setup_V8.0.0-alpha.3
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
RestartIfNeededByRun=no
CloseApplications=yes
SetupLogging=yes
UninstallDisplayName=Signal Auto Scheduler V8
UsePreviousAppDir=yes
UsePreviousGroup=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PublishRoot}\Desktop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\Engine\*"; DestDir: "{app}\Engine"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\Runtime\*"; DestDir: "{app}\Runtime"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userdesktop}\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"; WorkingDir: "{app}"
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"; WorkingDir: "{app}"; Flags: createonlyiffileexists

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SignalSchedulerEngine"; ValueData: """{app}\Engine\SignalScheduler.Engine.exe"" --background"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Engine\SignalScheduler.Engine.exe"; Parameters: "--background"; Flags: nowait runhidden skipifsilent; StatusMsg: "正在启动后台引擎…"
Filename: "{app}\SignalScheduler.exe"; Description: "立即启动 Signal Auto Scheduler"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SignalScheduler.Engine.exe /T /F"; Flags: runhidden waituntilterminated
