#define MyAppName "Signal Auto Scheduler"
#ifndef MyAppVersion
 #define MyAppVersion "8.0.0-alpha.7"
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
OutputBaseFilename=SignalScheduler_Setup_V8.0.0-alpha.7
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
RestartIfNeededByRun=no
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallDisplayName=Signal Auto Scheduler V8
UsePreviousAppDir=yes
UsePreviousGroup=yes
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PublishRoot}\Desktop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\Engine\*"; DestDir: "{app}\Engine"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\Runtime\*"; DestDir: "{app}\Runtime"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "update-protocol-v1.marker"; DestDir: "{app}"; Flags: ignoreversion
Source: "third_party\ChineseSimplified-LICENSE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{userdesktop}\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"; WorkingDir: "{app}"
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\Signal Auto Scheduler"; Filename: "{app}\SignalScheduler.exe"; WorkingDir: "{app}"; Flags: createonlyiffileexists

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SignalSchedulerEngine"; ValueData: """{app}\Engine\SignalScheduler.Engine.exe"" --background"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Engine\SignalScheduler.Engine.exe"; Parameters: "--background"; Flags: nowait runhidden skipifsilent; StatusMsg: "正在启动后台引擎…"
Filename: "{app}\SignalScheduler.exe"; Description: "立即启动 Signal Auto Scheduler"; Flags: nowait postinstall skipifsilent

[CustomMessages]
chinesesimplified.SafeClosingOldVersion=正在安全关闭旧版本…
chinesesimplified.WaitingForBackground=正在等待后台任务安全结束…
chinesesimplified.LegacyFallback=检测到旧测试版本，请先正常关闭旧版程序后重试…
chinesesimplified.SafeUpdateBlocked=当前仍有任务正在运行或消息正在发送。为避免重复发送或漏发，本次升级已停止。请先在软件中停止所有运行任务，确认没有正在发送的消息后，再重新运行安装包。
chinesesimplified.EngineDidNotExit=旧版本后台没有安全退出。为保护任务和数据，本次升级已停止。请重新打开软件，停止所有任务后再试；不要选择强制覆盖安装。
chinesesimplified.LegacyEngineDidNotExit=旧测试版本后台仍未退出，无法安全覆盖文件。请重启电脑后，在不打开旧版本的情况下重新运行安装包。
chinesesimplified.InstallingFiles=正在安装程序文件…
chinesesimplified.StartingApp=正在启动 Signal 调度台…
chinesesimplified.CloseDesktopFirst=检测到 Signal 调度台窗口仍在运行。请先关闭该窗口，再重新运行安装程序。
chinesesimplified.HandshakeFailed=无法确认旧版后台是否已安全停止。为了保护账号与任务数据，安装已取消。请先关闭旧版软件，必要时重新启动电脑后重试。
chinesesimplified.LegacyManualStop=检测到 V8 早期测试版后台仍在运行。请先正常退出旧版程序，或重新启动电脑后在不打开旧程序的情况下安装；本安装器不会强行结束未知 Java 进程。

[Code]
function IsEngineRunning(): Boolean;
begin
  Result := CheckForMutexes('Local\SignalScheduler.V8.Engine');
end;

function WaitForEngineStop(MaxWaitMs: Integer): Boolean;
var
  Elapsed: Integer;
begin
  Elapsed := 0;
  while IsEngineRunning() and (Elapsed < MaxWaitMs) do
  begin
    Sleep(250);
    Elapsed := Elapsed + 250;
  end;
  Result := not IsEngineRunning();
end;

function IsDesktopRunning(): Boolean;
begin
  Result := CheckForMutexes('Local\SignalScheduler.V8.Desktop');
end;

function StopEngineForMaintenance(ShowInstallStatus: Boolean): String;
var
  EnginePath: String;
  MarkerPath: String;
  ResultCode: Integer;
begin
  Result := '';

  EnginePath := ExpandConstant('{app}\Engine\SignalScheduler.Engine.exe');
  MarkerPath := ExpandConstant('{app}\update-protocol-v1.marker');

  { Nothing is running, so do not launch an old Engine merely to ask it to stop. }
  if not IsEngineRunning() then
    Exit;

  if ShowInstallStatus then
    WizardForm.StatusLabel.Caption := CustomMessage('SafeClosingOldVersion');

  { alpha.4+ understands this command. alpha.1-alpha.3 will simply exit the
    second Engine instance because the real Engine already owns the mutex. }
  if FileExists(EnginePath) then
  begin
    if Exec(
         EnginePath,
         '--prepare-update',
         ExtractFileDir(EnginePath),
         SW_HIDE,
         ewWaitUntilTerminated,
         ResultCode
       ) then
    begin
      if ResultCode = 20 then
      begin
        Result := CustomMessage('SafeUpdateBlocked');
        Exit;
      end;
      if ResultCode <> 0 then
      begin
        Result := CustomMessage('HandshakeFailed');
        Exit;
      end;
    end
    else
    begin
      Result := CustomMessage('HandshakeFailed');
      Exit;
    end;
  end
  else
  begin
    Result := CustomMessage('HandshakeFailed');
    Exit;
  end;

  if ShowInstallStatus then
    WizardForm.StatusLabel.Caption := CustomMessage('WaitingForBackground');

  if WaitForEngineStop(15000) then
    Exit;

  { A marker means the installed build supports safe-update. Never force-kill
    such a build after a failed handshake because it may have active/in-flight work. }
  if FileExists(MarkerPath) then
  begin
    Result := CustomMessage('EngineDidNotExit');
    Exit;
  end;

  { Earlier builds do not implement the safe handshake. Do not terminate
    processes by image name: an unrelated instance or Java app may exist. }
  Result := CustomMessage('LegacyManualStop');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  if IsDesktopRunning() then
  begin
    Result := CustomMessage('CloseDesktopFirst');
    Exit;
  end;
  Result := StopEngineForMaintenance(True);
end;

function InitializeUninstall(): Boolean;
var
  StopError: String;
begin
  if IsDesktopRunning() then
  begin
    MsgBox(CustomMessage('CloseDesktopFirst'), mbError, MB_OK);
    Result := False;
    Exit;
  end;
  StopError := StopEngineForMaintenance(False);

  if StopError <> '' then
  begin
    MsgBox(StopError, mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingFiles');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WizardForm.StatusLabel.Caption := CustomMessage('StartingApp');
end;
