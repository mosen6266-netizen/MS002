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
    readonly SignalCliSupervisor _supervisor;
    readonly SignalLinkCoordinator _links;

    public NamedPipeControlServer(StateStore store,SignalCliSupervisor supervisor,SignalLinkCoordinator links)
    {
        _store=store;
        _supervisor=supervisor;
        _links=links;
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
                try
                {
                    switch(request?.Command)
                    {
                        case ControlCommands.Ping:
                            response=new ControlResponse(true,Data:new{pong=true});
                            break;

                        case ControlCommands.Status:
                            var s=_supervisor.Snapshot;
                            response=new ControlResponse(true,Data:new{
                                version="8.0.0-alpha.3",
                                engine="running",
                                transport=s.State.ToString().ToLowerInvariant(),
                                detail=s.Detail,
                                signalCliVersion=s.Version,
                                runtimeReady=s.RuntimeReady,
                                externalDaemon=s.ExternalDaemon,
                                process=Environment.ProcessId
                            });
                            break;

                        case ControlCommands.Dashboard:
                            response=new ControlResponse(true,Data:await _store.GetDashboardAsync(_supervisor.Snapshot,ct));
                            break;

                        case ControlCommands.SignalStartLink:
                            var deviceName="Signal Auto Scheduler";
                            if(request.Payload is JsonElement startPayload &&
                               startPayload.ValueKind==JsonValueKind.Object &&
                               startPayload.TryGetProperty("deviceName",out var dn) &&
                               !string.IsNullOrWhiteSpace(dn.GetString()))
                                deviceName=dn.GetString()!;
                            response=new ControlResponse(true,Data:await _links.StartAsync(deviceName,ct));
                            break;

                        case ControlCommands.SignalLinkStatus:
                            if(request.Payload is not JsonElement payload ||
                               payload.ValueKind!=JsonValueKind.Object ||
                               !payload.TryGetProperty("sessionId",out var sessionNode) ||
                               string.IsNullOrWhiteSpace(sessionNode.GetString()))
                            {
                                response=new ControlResponse(false,Error:"missing_session_id");
                                break;
                            }
                            var link=_links.Get(sessionNode.GetString()!);
                            response=link is null
                                ?new ControlResponse(false,Error:"link_session_not_found")
                                :new ControlResponse(true,Data:link);
                            break;

                        default:
                            response=new ControlResponse(false,Error:"unknown_command");
                            break;
                    }
                }
                catch(Exception ex)
                {
                    response=new ControlResponse(false,Error=ex.Message);
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch{await Task.Delay(300,ct);}
        }
    }
}
