using Microsoft.Extensions.Hosting;
using System.IO.Pipes;
using System.Text.Json;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Ipc;

public sealed class NamedPipeControlServer : BackgroundService
{
    const string PipeName="SignalScheduler.V8.Control";
    readonly StateStore _store;
    readonly SignalGuardian _guardian;
    readonly SignalLinkManager _link;
    readonly IHostApplicationLifetime _lifetime;

    public NamedPipeControlServer(
        StateStore store,
        SignalGuardian guardian,
        SignalLinkManager link,
        IHostApplicationLifetime lifetime)
    {
        _store=store;
        _guardian=guardian;
        _link=link;
        _lifetime=lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            await using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var reader=new StreamReader(pipe,leaveOpen:true);
                using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
                var line=await reader.ReadLineAsync(ct);
                if(string.IsNullOrWhiteSpace(line)) continue;

                var request=JsonSerializer.Deserialize<ControlRequest>(line);
                ControlResponse response;
                switch(request?.Command)
                {
                    case ControlCommands.Ping:
                        response=new ControlResponse(true,Data:new{pong=true});
                        break;
                    case ControlCommands.Status:
                        var s=_guardian.Snapshot;
                        response=new ControlResponse(true,Data:new{
                            version="8.0.0-alpha.4",
                            engine="running",
                            signal=s.State,
                            signalDetail=s.Detail,
                            signalCli=s.SignalCliVersion,
                            liveAccounts=s.LiveAccounts.Count,
                            process=Environment.ProcessId
                        });
                        break;
                    case ControlCommands.SignalStatus:
                        response=new ControlResponse(true,Data:_guardian.Snapshot);
                        break;
                    case ControlCommands.Dashboard:
                        response=new ControlResponse(true,Data:await _store.GetDashboardAsync(_guardian.Snapshot,ct));
                        break;
                    case ControlCommands.StartLink:
                        response=new ControlResponse(true,Data:await _link.StartAsync("Signal Scheduler V8",ct));
                        break;
                    case ControlCommands.LinkStatus:
                        response=new ControlResponse(true,Data:_link.Snapshot);
                        break;
                    case ControlCommands.CancelLink:
                        _link.Cancel();
                        response=new ControlResponse(true,Data:_link.Snapshot);
                        break;
                    case ControlCommands.UpdateStatus:
                        response=new ControlResponse(true,Data:await _store.GetUpdateReadinessAsync(ct));
                        break;
                    case ControlCommands.PrepareUpdate:
                        var readiness=await _store.GetUpdateReadinessAsync(ct);
                        if(readiness.CanUpdate)
                        {
                            _link.Cancel();
                            response=new ControlResponse(true,Data:readiness);
                            _=Task.Run(async()=>{
                                try
                                {
                                    await Task.Delay(350);
                                    _lifetime.StopApplication();
                                }
                                catch { }
                            });
                        }
                        else
                        {
                            response=new ControlResponse(true,Data:readiness);
                        }
                        break;
                    default:
                        response=new ControlResponse(false,Error:"unknown_command");
                        break;
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch{await Task.Delay(300,ct);}
        }
    }
}
