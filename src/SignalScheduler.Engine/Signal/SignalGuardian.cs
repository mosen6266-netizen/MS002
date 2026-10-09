using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

public sealed class SignalGuardian : BackgroundService
{
    const string RpcUrl="http://127.0.0.1:7583/api/v1/rpc";
    static readonly Regex PhoneRegex=new(@"\+\d{7,15}",RegexOptions.Compiled);
    static readonly string[] DiagnosticTokens={
        "INFO","WARN","ERROR","FATAL","Exception","Connection","Socket","SSL","HTTP","failed","unable","timeout","Timeout"
    };

    // Read receipt preference is applied when this daemon is launched.
    // An already-running daemon needs a normal restart to pick up changes.
    bool ReadReceiptsEnabled()
    {
        try
        {
            var path=Path.Combine(_paths.DataRoot,"read-options.json");
            if(!File.Exists(path))return true;
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            return !doc.RootElement.TryGetProperty("ReadReceipts",out var flag) ||
                flag.ValueKind!=JsonValueKind.False;
        }
        catch{return true;}
    }

    readonly RuntimePaths _paths;
    readonly StateStore _store;
    readonly HttpClient _http=new(){Timeout=TimeSpan.FromSeconds(6)};
    readonly object _gate=new();
    Process? _owned;
    DateTimeOffset _lastOutput=DateTimeOffset.MinValue;
    DateTimeOffset? _firstFailure;
    int _restartCount;
    int _consecutiveFailures;
    SignalGuardianSnapshot _snapshot=new(
        "starting","等待 Signal Guardian 启动","",false,0,DateTimeOffset.UtcNow,Array.Empty<string>());

    public SignalGuardian(RuntimePaths paths,StateStore store)
    {
        _paths=paths;
        _store=store;
    }

    public SignalGuardianSnapshot Snapshot
    {
        get { lock(_gate) return _snapshot; }
    }

    void SetSnapshot(string state,string detail,string version,bool owns,IReadOnlyList<string>? accounts=null)
    {
        lock(_gate)
        {
            _snapshot=new SignalGuardianSnapshot(
                state,detail,version,owns,_restartCount,DateTimeOffset.UtcNow,
                accounts ?? _snapshot.LiveAccounts);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
        if(!File.Exists(_paths.JavaExe) || !Directory.Exists(_paths.SignalCliHome))
        {
            SetSnapshot("missing-runtime","缺少内置 Java / signal-cli Runtime","",false,Array.Empty<string>());
            return;
        }

        var version=await ReadSignalCliVersionAsync(stoppingToken);

        var initial=await ProbeAsync(stoppingToken);
        if(initial.Kind=="valid")
        {
            SetSnapshot("healthy","检测到已运行的 signal-cli，复用现有服务",version,false,initial.Accounts);
        }
        else if(initial.Kind=="foreign")
        {
            SetSnapshot("external-conflict","7583 端口被非 signal-cli 服务占用，未自动结束其他程序",version,false,Array.Empty<string>());
        }
        else
        {
            await StartOwnedAsync(version,stoppingToken);
        }

        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);
                var probe=await ProbeAsync(stoppingToken);

                if(probe.Kind=="valid")
                {
                    _consecutiveFailures=0;
                    _firstFailure=null;
                    SetSnapshot("healthy","Signal JSON-RPC 正常",version,_owned is { HasExited:false },probe.Accounts);
                    continue;
                }

                if(probe.Kind=="foreign")
                {
                    SetSnapshot("external-conflict","7583 端口返回的不是 signal-cli JSON-RPC；Guardian 不会误杀未知进程",version,false,Array.Empty<string>());
                    continue;
                }

                _consecutiveFailures++;
                _firstFailure ??= DateTimeOffset.UtcNow;

                if(_owned is { HasExited:true })
                {
                    SetSnapshot("fault","本程序启动的 signal-cli 已退出，正在恢复",version,true,Array.Empty<string>());
                    await RestartOwnedAsync(version,stoppingToken);
                    continue;
                }

                var failureAge=DateTimeOffset.UtcNow-_firstFailure.Value;
                var outputAge=DateTimeOffset.UtcNow-_lastOutput;

                if(failureAge<TimeSpan.FromSeconds(45) || outputAge<TimeSpan.FromSeconds(25))
                {
                    SetSnapshot("busy","signal-cli 暂时繁忙或正在同步，暂不重启",version,_owned is { HasExited:false },Snapshot.LiveAccounts);
                    continue;
                }

                if(_owned is { HasExited:false } && _consecutiveFailures>=5)
                {
                    SetSnapshot("fault","signal-cli 长时间无 RPC 且无处理输出，Guardian 准备安全重启",version,true,Snapshot.LiveAccounts);
                    await RestartOwnedAsync(version,stoppingToken);
                }
                else if(_owned is null)
                {
                    // A reused daemon disappeared. Pause task state before
                    // replacing it; this must never resume any task implicitly.
                    if(await _store.TryPrepareGuardianRestartAsync(true,stoppingToken))
                        await StartOwnedAsync(version,stoppingToken);
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch(Exception ex)
            {
                SetSnapshot("fault",$"Guardian 异常：{Short(ex.Message)}",version,_owned is { HasExited:false },Snapshot.LiveAccounts);
                try { await Task.Delay(3000,stoppingToken); } catch { }
            }
        }

        }
        finally
        {
            // BackgroundService cancellation can interrupt startup/probing,
            // before the normal loop tail runs. Always reap the Java daemon
            // we launched, even when a task is cancelled or startup throws.
            // The IPC safety gate decides whether shutdown is permitted.
            await StopOwnedAsync();
        }
    }

    async Task StartOwnedAsync(string version,CancellationToken ct)
    {
        if(!await IsPortFreeAsync(ct))
        {
            var probe=await ProbeAsync(ct);
            if(probe.Kind=="valid")
            {
                SetSnapshot("healthy","复用其他实例提供的 signal-cli",version,false,probe.Accounts);
                return;
            }
            SetSnapshot("external-conflict","7583 仍被其他程序占用，Guardian 未启动第二个 daemon",version,false,Array.Empty<string>());
            return;
        }

        var psi=new ProcessStartInfo
        {
            FileName=_paths.JavaExe,
            WorkingDirectory=_paths.SignalCliHome,
            Arguments=$"-classpath \"{_paths.SignalCliLibWildcard}\" org.asamk.signal.Main --output=json daemon --http=127.0.0.1:7583{(ReadReceiptsEnabled()?" --send-read-receipts":"")}",
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        psi.Environment["JAVA_HOME"]=Path.GetDirectoryName(Path.GetDirectoryName(_paths.JavaExe))!;
        psi.Environment["PATH"]=$"{Path.GetDirectoryName(_paths.JavaExe)};{psi.Environment["PATH"]}";

        var p=new Process{StartInfo=psi,EnableRaisingEvents=true};
        p.OutputDataReceived+=(_,e)=>OnProcessLine(e.Data);
        p.ErrorDataReceived+=(_,e)=>OnProcessLine(e.Data);
        p.Exited+=(_,_)=>SetSnapshot("fault",$"signal-cli 进程退出，代码 {SafeExitCode(p)}",version,true,Snapshot.LiveAccounts);

        if(!p.Start())
            throw new InvalidOperationException("无法启动 signal-cli");

        _owned=p;
        _lastOutput=DateTimeOffset.UtcNow;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        SetSnapshot("starting","正在启动内置 signal-cli",version,true,Array.Empty<string>());

        for(var i=0;i<18 && !ct.IsCancellationRequested;i++)
        {
            await Task.Delay(1500,ct);
            var probe=await ProbeAsync(ct);
            if(probe.Kind=="valid")
            {
                _consecutiveFailures=0;
                _firstFailure=null;
                SetSnapshot("healthy","内置 signal-cli 已启动",version,true,probe.Accounts);
                return;
            }
            if(p.HasExited) break;
        }

        SetSnapshot("busy","signal-cli 启动较慢，Guardian 继续观察",version,true,Array.Empty<string>());
    }

    async Task RestartOwnedAsync(string version,CancellationToken ct)
    {
        // No Java process may be killed while an irreversible send is in flight.
        // If Java already crashed, quarantine ambiguous messages before starting
        // another daemon. The SQLite phase pauses tasks and never auto-resumes.
        var exited=_owned is null || _owned.HasExited;
        if(!await _store.TryPrepareGuardianRestartAsync(exited,ct))
        {
            SetSnapshot("busy",
                "仍有正在发送或结果未知的消息，已暂停任务，暂不重启 Signal。",
                version,_owned is {HasExited:false},Snapshot.LiveAccounts);
            return;
        }

        _restartCount++;
        await StopOwnedAsync();
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(20,5+_restartCount*3)),ct);
        _consecutiveFailures=0;
        _firstFailure=null;
        await StartOwnedAsync(version,ct);
    }

    async Task StopOwnedAsync()
    {
        var p=_owned;
        _owned=null;
        if(p is null)return;
        try
        {
            if(!p.HasExited)
            {
                try{p.Kill(entireProcessTree:true);}
                catch(InvalidOperationException){} // exited during the kill
                // Keep waiting for actual termination rather than releasing
                // the Process object immediately while java.dll is still mapped.
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
        catch(Exception ex)
        {
            SetSnapshot("fault",
                "后台 Java 进程未确认完全退出："+ex.GetType().Name,
                Snapshot.SignalCliVersion,true,Snapshot.LiveAccounts);
            // The installer independently checks that bundled runtime files
            // are unlocked, and must fail closed if the process survives.
        }
        finally{p.Dispose();}
    }

    void OnProcessLine(string? line)
    {
        if(string.IsNullOrWhiteSpace(line)) return;
        _lastOutput=DateTimeOffset.UtcNow;
        if(!DiagnosticTokens.Any(t=>line.Contains(t,StringComparison.OrdinalIgnoreCase))) return;

        try
        {
            var sanitized=PhoneRegex.Replace(line,"[phone]");
            if(sanitized.Length>1200) sanitized=sanitized[..1200];
            RotateLogIfNeeded();
            File.AppendAllText(_paths.SignalCliLogPath,$"{DateTimeOffset.Now:O} {sanitized}{Environment.NewLine}");
        }
        catch { }
    }

    void RotateLogIfNeeded()
    {
        try
        {
            var f=new FileInfo(_paths.SignalCliLogPath);
            if(!f.Exists || f.Length<4*1024*1024) return;
            var bak=_paths.SignalCliLogPath+".1";
            if(File.Exists(bak)) File.Delete(bak);
            File.Move(_paths.SignalCliLogPath,bak);
        }
        catch { }
    }

    async Task<string> ReadSignalCliVersionAsync(CancellationToken ct)
    {
        try
        {
            var psi=new ProcessStartInfo
            {
                FileName=_paths.JavaExe,
                WorkingDirectory=_paths.SignalCliHome,
                Arguments=$"-classpath \"{_paths.SignalCliLibWildcard}\" org.asamk.signal.Main --version",
                UseShellExecute=false,
                CreateNoWindow=true,
                RedirectStandardOutput=true,
                RedirectStandardError=true
            };
            psi.Environment["JAVA_HOME"]=Path.GetDirectoryName(Path.GetDirectoryName(_paths.JavaExe))!;
            using var p=Process.Start(psi)!;
            var stdout=await p.StandardOutput.ReadToEndAsync(ct);
            var stderr=await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var s=string.IsNullOrWhiteSpace(stdout)?stderr:stdout;
            return Short(s.Trim());
        }
        catch(Exception ex) { return $"未知 ({Short(ex.Message)})"; }
    }

    async Task<(string Kind,List<string> Accounts)> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var req=new HttpRequestMessage(HttpMethod.Post,RpcUrl)
            {
                Content=JsonContent.Create(new{jsonrpc="2.0",method="listAccounts",id=1})
            };
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp=await _http.SendAsync(req,linked.Token);

            var text=await resp.Content.ReadAsStringAsync(linked.Token);
            if(!resp.IsSuccessStatusCode)
                return ("foreign",new());

            using var doc=JsonDocument.Parse(text);
            var root=doc.RootElement;
            if(!root.TryGetProperty("jsonrpc",out var jr) || jr.GetString()!="2.0")
                return ("foreign",new());
            if(!root.TryGetProperty("result",out var result) || result.ValueKind!=JsonValueKind.Array)
                return ("foreign",new());

            var accounts=new List<string>();
            foreach(var item in result.EnumerateArray())
            {
                if(item.ValueKind==JsonValueKind.String)
                    accounts.Add(item.GetString()!);
                else if(item.ValueKind==JsonValueKind.Object)
                {
                    if(item.TryGetProperty("number",out var n) && n.ValueKind==JsonValueKind.String)
                        accounts.Add(n.GetString()!);
                    else if(item.TryGetProperty("aci",out var aci) && aci.ValueKind==JsonValueKind.String)
                        accounts.Add(aci.GetString()!);
                    else if(item.TryGetProperty("username",out var u) && u.ValueKind==JsonValueKind.String)
                        accounts.Add(u.GetString()!);
                }
            }
            return ("valid",accounts);
        }
        catch(HttpRequestException) { return ("offline",new()); }
        catch(TaskCanceledException) { return ("offline",new()); }
        catch(JsonException) { return ("foreign",new()); }
        catch { return ("offline",new()); }
    }

    static async Task<bool> IsPortFreeAsync(CancellationToken ct)
    {
        try
        {
            using var tcp=new System.Net.Sockets.TcpClient();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(350);
            await tcp.ConnectAsync(IPAddress.Loopback,7583,timeout.Token);
            return false;
        }
        catch { return true; }
    }

    static string Short(string s)
    {
        s=s.Replace("\r"," ").Replace("\n"," ").Trim();
        return s.Length<=180?s:s[..180];
    }

    static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }
}
