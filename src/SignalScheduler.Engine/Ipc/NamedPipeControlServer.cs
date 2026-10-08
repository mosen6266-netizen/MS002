using System.IO.Pipes;
using System.Text.Json;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Ipc;

public sealed class NamedPipeControlServer : BackgroundService
{
    const string PipeName="SignalScheduler.V8.Control";
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
                var response=request?.Command switch
                {
                    ControlCommands.Ping=>new ControlResponse(true,Data:new{pong=true}),
                    ControlCommands.Status=>new ControlResponse(true,Data:new{
                        version="8.0.0-alpha.1",
                        engine="running",
                        transport="disabled-foundation-stage",
                        process=Environment.ProcessId
                    }),
                    _=>new ControlResponse(false,Error:"unknown_command")
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch{await Task.Delay(300,ct);}
        }
    }
}
