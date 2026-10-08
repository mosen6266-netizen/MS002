#define MyAppName "Signal Auto Scheduler"
#ifndef MyAppVersion
 #define MyAppVersion "8.0.0-alpha.1"
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
DefaultDirName={autopf}\Signal Auto Scheduler
DefaultGroupName=Signal Auto Scheduler
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir={#OutputRoot}
OutputBaseFilename=SignalScheduler_Setup_V8.0.0-alpha.1
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
RestartIfNeededByRun=no

[Files]
Source: "{#PublishRoot}\Desktop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"
Name: "{group}\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"

[Run]
Filename: "{sys}\sc.exe"; Parameters: "stop SignalSchedulerV8"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete SignalSchedulerV8"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "create SignalSchedulerV8 binPath= ""{app}\Service\SignalScheduler.Service.exe"" start= auto DisplayName= ""Signal Auto Scheduler V8"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering background service…"
Filename: "{sys}\sc.exe"; Parameters: "start SignalSchedulerV8"; Flags: runhidden waituntilterminated; StatusMsg: "Starting background service…"
Filename: "{app}\SignalScheduler.exe"; Description: "Launch Signal Auto Scheduler"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop SignalSchedulerV8"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete SignalSchedulerV8"; Flags: runhidden waituntilterminated
