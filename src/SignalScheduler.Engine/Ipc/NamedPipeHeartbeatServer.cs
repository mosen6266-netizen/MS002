using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Ipc;

/// <summary>
/// Independent, lightweight engine heartbeat. The regular control pipe handles
/// database operations serially; a delayed dashboard must not be mistaken for
/// a crashed Signal engine. This endpoint never accesses the database, Signal,
/// task cursors or delivery transport.
/// </summary>
public sealed class NamedPipeHeartbeatServer : BackgroundService
{
    public const string PipeName="SignalScheduler.V8.Heartbeat";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            await using var pipe=new NamedPipeServerStream(
                PipeName,PipeDirection.InOut,1,
                PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader=new StreamReader(pipe,leaveOpen:true);
                using var writer=new StreamWriter(pipe,leaveOpen:true)
                {AutoFlush=true};
                var line=await reader.ReadLineAsync(timeout.Token);
                if(string.IsNullOrWhiteSpace(line))continue;
                var request=JsonSerializer.Deserialize<ControlRequest>(line);
                var response=request?.Command==ControlCommands.Ping
                    ?new ControlResponse(true,Data:new{pong=true})
                    :new ControlResponse(false,Error:"unknown_command");
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException
                or JsonException)
            {
                // A stalled or disconnected UI client does not affect
                // the independent worker or the next health check.
            }
        }
    }
}
