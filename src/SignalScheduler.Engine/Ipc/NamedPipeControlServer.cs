using Microsoft.Extensions.Hosting;
using System.IO.Pipes;
using System.Text.Json;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Ipc;

public sealed class NamedPipeControlServer : BackgroundService
{
    const string PipeName="SignalScheduler.V8.Control";
    readonly StateStore _store;

    public NamedPipeControlServer(StateStore store)=>_store=store;

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
                        response=new ControlResponse(true,Data:new{
                            version="8.0.0-alpha.2",
                            engine="running",
                            transport="disabled-foundation-stage",
                            process=Environment.ProcessId
                        });
                        break;
                    case ControlCommands.Dashboard:
                        response=new ControlResponse(true,Data:await _store.GetDashboardAsync(ct));
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
