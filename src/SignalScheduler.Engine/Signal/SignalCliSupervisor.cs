using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine.Persistence;

namespace SignalScheduler.Engine.Signal;

public sealed class SignalCliSupervisor : BackgroundService
{
    readonly RuntimePaths _paths;
    readonly SignalCliClient _client;
    readonly StateStore _store;
    readonly object _gate=new();
    readonly Queue<DateTimeOffset> _restarts=new();

    Process? _ownedProcess;
    DateTimeOffset _lastOutput=DateTimeOffset.MinValue;
    DateTimeOffset _lastDeepCheck=DateTimeOffset.MinValue;
    volatile bool _syncRequested=true;
    bool _externalDaemon;

    SignalHealthSnapshot _snapshot=new(
        SignalHealthState.Starting,
        "正在启动 Signal 运行环境。",
        "unknown",
        false,
        false,
        Array.Empty<SignalAccountInfo>(),
        DateTimeOffset.UtcNow);

    public SignalCliSupervisor(RuntimePaths paths,SignalCliClient client,StateStore store)
    {
        _paths=paths;
        _client=client;
        _store=store;
    }

    public SignalHealthSnapshot Snapshot
    {
        get { lock(_gate) return _snapshot; }
    }

    public void RequestImmediateSync()=>_syncRequested=true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EnsureDaemonAsync(stoppingToken);
            while(!stoppingToken.IsCancellationRequested)
            {
                await MonitorOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){}
        finally
        {
            await StopOwnedProcessAsync();
        }
    }

    async Task EnsureDaemonAsync(CancellationToken ct)
    {
        var runtimeReady=File.Exists(_paths.JavaExe)&&File.Exists(_paths.SignalCliBat);
        if(!runtimeReady)
        {
            SetSnapshot(SignalHealthState.RuntimeMissing,"缺少内置 Java 或 signal-cli 运行环境。","unknown",false,false,Array.Empty<SignalAccountInfo>());
            return;
        }

        if(await TryDeepHealthAsync(ct) is { } existing)
        {
            _externalDaemon=true;
            SetSnapshot(SignalHealthState.Healthy,"已连接现有 signal-cli。",await _client.VersionAsync(ct),true,true,existing);
            await SyncRuntimeDataAsync(existing,ct);
            return;
        }

        if(await IsPortOpenAsync(7583,ct))
        {
            SetSnapshot(SignalHealthState.Fault,"7583 端口被未知程序占用，已停止接管以避免误杀。","unknown",true,false,Array.Empty<SignalAccountInfo>());
            return;
        }

        await StartOwnedDaemonAsync(ct);
    }

    async Task StartOwnedDaemonAsync(CancellationToken ct)
    {
        if(!CanRestart())
        {
            SetSnapshot(SignalHealthState.Fault,"Signal Guardian 在短时间内已达到重启上限。",Snapshot.Version,true,false,Snapshot.Accounts);
            return;
        }

        await StopOwnedProcessAsync();
        _externalDaemon=false;
        SetSnapshot(SignalHealthState.Starting,"正在启动 signal-cli…","unknown",true,false,Array.Empty<SignalAccountInfo>());

        var psi=new ProcessStartInfo
        {
            FileName=Environment.GetEnvironmentVariable("COMSPEC")??"cmd.exe",
            Arguments=$"/d /s /c \"\"{_paths.SignalCliBat}\" daemon --http 127.0.0.1:7583 --receive-mode on-start\"",
            WorkingDirectory=Path.GetDirectoryName(_paths.SignalCliBat)!,
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true,
            WindowStyle=ProcessWindowStyle.Hidden
        };
        psi.Environment["JAVA_HOME"]=Path.GetDirectoryName(Path.GetDirectoryName(_paths.JavaExe))!;
        psi.Environment["PATH"]=$"{Path.GetDirectoryName(_paths.JavaExe)};{psi.Environment["PATH"]}";

        var p=new Process{StartInfo=psi,EnableRaisingEvents=true};
        p.OutputDataReceived+=(_,e)=>HandleRuntimeLine(e.Data);
        p.ErrorDataReceived+=(_,e)=>HandleRuntimeLine(e.Data);
        if(!p.Start()) throw new InvalidOperationException("无法启动 signal-cli。");
        _ownedProcess=p;
        _lastOutput=DateTimeOffset.UtcNow;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        RegisterRestart();

        for(var i=0;i<40&&!ct.IsCancellationRequested;i++)
        {
            if(p.HasExited) break;
            var accounts=await TryDeepHealthAsync(ct);
            if(accounts is not null)
            {
                var version=await _client.VersionAsync(ct);
                SetSnapshot(SignalHealthState.Healthy,"Signal 服务正常。",version,true,false,accounts);
                await SyncRuntimeDataAsync(accounts,ct);
                return;
            }
            await Task.Delay(750,ct);
        }

        if(p.HasExited)
        {
            SetSnapshot(SignalHealthState.Fault,$"signal-cli 启动后退出，代码 {p.ExitCode}。","unknown",true,false,Array.Empty<SignalAccountInfo>());
            return;
        }

        SetSnapshot(SignalHealthState.Busy,"signal-cli 已启动，正在同步本地账号资料。","unknown",true,false,Array.Empty<SignalAccountInfo>());
    }

    async Task MonitorOnceAsync(CancellationToken ct)
    {
        var snapshot=Snapshot;
        if(snapshot.State==SignalHealthState.RuntimeMissing) return;

        if(_externalDaemon)
        {
            if(await _client.CheckAsync(TimeSpan.FromSeconds(2),ct))
            {
                if(_syncRequested || DateTimeOffset.UtcNow-_lastDeepCheck>TimeSpan.FromSeconds(60))
                    await DeepRefreshAsync(ct,true);
                return;
            }

            if(!await IsPortOpenAsync(7583,ct))
            {
                _externalDaemon=false;
                await StartOwnedDaemonAsync(ct);
            }
            else
                SetSnapshot(SignalHealthState.Busy,"现有 signal-cli 暂时无响应，未执行强制结束。",snapshot.Version,true,true,snapshot.Accounts);
            return;
        }

        if(_ownedProcess is null || _ownedProcess.HasExited)
        {
            SetSnapshot(SignalHealthState.Fault,"signal-cli 进程已退出，Guardian 正在尝试恢复。",snapshot.Version,true,false,snapshot.Accounts);
            await Task.Delay(1500,ct);
            await StartOwnedDaemonAsync(ct);
            return;
        }

        var lightHealthy=await _client.CheckAsync(TimeSpan.FromSeconds(2),ct);
        if(lightHealthy)
        {
            if(_syncRequested || DateTimeOffset.UtcNow-_lastDeepCheck>TimeSpan.FromSeconds(60))
                await DeepRefreshAsync(ct,false);
            else if(snapshot.State!=SignalHealthState.Healthy)
                SetSnapshot(SignalHealthState.Healthy,"Signal 服务已恢复。",snapshot.Version,true,false,snapshot.Accounts);
            return;
        }

        var quietFor=DateTimeOffset.UtcNow-_lastOutput;
        if(quietFor<TimeSpan.FromSeconds(60))
        {
            SetSnapshot(SignalHealthState.Busy,"Signal 正在同步/繁忙，Guardian 不会误杀进程。",snapshot.Version,true,false,snapshot.Accounts);
            return;
        }

        var confirm1=await _client.CheckAsync(TimeSpan.FromSeconds(3),ct);
        await Task.Delay(1000,ct);
        var confirm2=await _client.CheckAsync(TimeSpan.FromSeconds(3),ct);
        if(confirm1||confirm2)
        {
            SetSnapshot(SignalHealthState.Busy,"Signal 短暂延迟，继续观察。",snapshot.Version,true,false,snapshot.Accounts);
            return;
        }

        SetSnapshot(SignalHealthState.Fault,"signal-cli 持续无响应，Guardian 正在安全重启通信进程。",snapshot.Version,true,false,snapshot.Accounts);
        await StartOwnedDaemonAsync(ct);
    }

    async Task DeepRefreshAsync(CancellationToken ct,bool external)
    {
        var accounts=await TryDeepHealthAsync(ct);
        _lastDeepCheck=DateTimeOffset.UtcNow;
        _syncRequested=false;
        if(accounts is null)
        {
            var old=Snapshot;
            SetSnapshot(SignalHealthState.Busy,"Signal 深度检查暂时失败，继续保留账号数据。",old.Version,true,external,old.Accounts);
            return;
        }

        var version=Snapshot.Version;
        if(version=="unknown") version=await _client.VersionAsync(ct);
        SetSnapshot(SignalHealthState.Healthy,"Signal 服务正常。",version,true,external,accounts);
        await SyncRuntimeDataAsync(accounts,ct);
    }

    async Task<IReadOnlyList<SignalAccountInfo>?> TryDeepHealthAsync(CancellationToken ct)
    {
        try{return await _client.ListAccountsAsync(ct);}
        catch{return null;}
    }

    async Task SyncRuntimeDataAsync(IReadOnlyList<SignalAccountInfo> accounts,CancellationToken ct)
    {
        await _store.ReplaceSignalAccountsAsync(accounts,ct);
        foreach(var account in accounts)
        {
            try
            {
                var groups=await _client.ListGroupsAsync(account.Account,ct);
                await _store.ReplaceSignalGroupsAsync(account.Account,groups,ct);
            }
            catch
            {
                // Keep the previous group snapshot for this account. A transient listGroups
                // failure must not erase a user's known groups.
            }
        }
    }

    void HandleRuntimeLine(string? line)
    {
        if(string.IsNullOrWhiteSpace(line)) return;
        _lastOutput=DateTimeOffset.UtcNow;

        if(!ShouldPersist(line)) return;
        var sanitized=Regex.Replace(line,@"\+[1-9]\d{6,14}","[phone]");
        if(sanitized.Length>1200) sanitized=sanitized[..1200];
        try
        {
            RotateLogIfNeeded();
            File.AppendAllText(_paths.SignalCliLogPath,$"{DateTimeOffset.Now:O} {sanitized}{Environment.NewLine}");
        }
        catch{}
    }

    static bool ShouldPersist(string line)=>
        line.Contains("ERROR",StringComparison.OrdinalIgnoreCase)||
        line.Contains("WARN",StringComparison.OrdinalIgnoreCase)||
        line.Contains("Exception",StringComparison.OrdinalIgnoreCase)||
        line.Contains("Failed",StringComparison.OrdinalIgnoreCase)||
        line.Contains("Connection",StringComparison.OrdinalIgnoreCase)||
        line.Contains("WebSocket",StringComparison.OrdinalIgnoreCase);

    void RotateLogIfNeeded()
    {
        var f=new FileInfo(_paths.SignalCliLogPath);
        if(!f.Exists||f.Length<2*1024*1024) return;
        var old=_paths.SignalCliLogPath+".1";
        if(File.Exists(old)) File.Delete(old);
        File.Move(_paths.SignalCliLogPath,old);
    }

    bool CanRestart()
    {
        lock(_restarts)
        {
            var cutoff=DateTimeOffset.UtcNow-TimeSpan.FromMinutes(15);
            while(_restarts.Count>0&&_restarts.Peek()<cutoff) _restarts.Dequeue();
            return _restarts.Count<3;
        }
    }

    void RegisterRestart()
    {
        lock(_restarts) _restarts.Enqueue(DateTimeOffset.UtcNow);
    }

    async Task StopOwnedProcessAsync()
    {
        var p=_ownedProcess;
        _ownedProcess=null;
        if(p is null) return;
        try
        {
            if(!p.HasExited)
            {
                using var killer=Process.Start(new ProcessStartInfo
                {
                    FileName="taskkill.exe",
                    Arguments=$"/PID {p.Id} /T /F",
                    UseShellExecute=false,
                    CreateNoWindow=true
                });
                if(killer is not null) await killer.WaitForExitAsync();
            }
        }
        catch{}
        finally{p.Dispose();}
    }

    static async Task<bool> IsPortOpenAsync(int port,CancellationToken ct)
    {
        try
        {
            using var tcp=new TcpClient();
            using var cts=CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(600);
            await tcp.ConnectAsync("127.0.0.1",port,cts.Token);
            return true;
        }
        catch{return false;}
    }

    void SetSnapshot(SignalHealthState state,string detail,string version,bool runtimeReady,bool external,IReadOnlyList<SignalAccountInfo> accounts)
    {
        lock(_gate)
            _snapshot=new SignalHealthSnapshot(state,detail,version,runtimeReady,external,accounts,DateTimeOffset.UtcNow);
    }
}
