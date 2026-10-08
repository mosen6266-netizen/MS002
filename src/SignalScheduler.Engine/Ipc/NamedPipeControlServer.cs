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

    static T ParsePayload<T>(JsonElement? payload)
    {
        if(payload is not { } data || data.ValueKind!=JsonValueKind.Object)
            throw new ArgumentException("请求缺少有效参数。");
        try
        {
            return JsonSerializer.Deserialize<T>(data.GetRawText())
                ??throw new ArgumentException("参数内容为空。");
        }
        catch(JsonException)
        {
            throw new ArgumentException("参数格式错误。");
        }
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
                            version="8.0.0-alpha.9",
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
                    case ControlCommands.PreviewList:
                        response=new ControlResponse(true,Data:await _store.ListPreviewTasksAsync(ct));
                        break;
                    case ControlCommands.PreviewPlan:
                        try
                        {
                            var plan=ParsePayload<PreviewTaskPlanRequest>(request.Payload);
                            response=new ControlResponse(true,
                                Data:await _store.PlanPreviewTasksAsync(plan,ct));
                        }
                        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException
                            or KeyNotFoundException)
                        {
                            response=new ControlResponse(false,Error:ex.Message);
                        }
                        break;
                    case ControlCommands.PreviewControl:
                        try
                        {
                            var action=ParsePayload<PreviewTaskControlRequest>(request.Payload);
                            response=new ControlResponse(true,
                                Data:await _store.ControlPreviewTaskAsync(action,ct));
                        }
                        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException
                            or KeyNotFoundException)
                        {
                            response=new ControlResponse(false,Error:ex.Message);
                        }
                        break;
                    case ControlCommands.RecoveryOverview:
                        response=new ControlResponse(true,Data:await _store.GetRecoveryOverviewAsync(ct));
                        break;
                    case ControlCommands.AccountGroupCatalog:
                        response=new ControlResponse(true,
                            Data:await _store.GetAccountGroupOverviewAsync(ct));
                        break;
                    case ControlCommands.UpdateAccount:
                        try
                        {
                            var account=ParsePayload<UpdateManagedAccount>(request.Payload);
                            response=new ControlResponse(true,
                                Data:await _store.UpdateManagedAccountAsync(account,ct));
                        }
                        catch(Exception ex) when(ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
                        {
                            response=new ControlResponse(false,Error:ex.Message);
                        }
                        break;
                    case ControlCommands.SetSelectedGroups:
                        try
                        {
                            var selection=ParsePayload<UpdateGroupSelection>(request.Payload);
                            response=new ControlResponse(true,
                                Data:await _store.SetSelectedGroupsAsync(selection,ct));
                        }
                        catch(ArgumentException ex)
                        {
                            response=new ControlResponse(false,Error:ex.Message);
                        }
                        break;
                    case ControlCommands.ScriptList:
                        response=new ControlResponse(true,Data:await _store.ListEditorScriptsAsync(ct));
                        break;
                    case ControlCommands.ScriptRead:
                        try
                        {
                            var read=ParsePayload<ScriptReadRequest>(request.Payload);
                            response=new ControlResponse(true,
                                Data:await _store.ReadEditorScriptAsync(read.ScriptId,ct));
                        }
                        catch(Exception ex) when(ex is ArgumentException or KeyNotFoundException)
                        {
                            response=new ControlResponse(false,Error:ex.Message);
                        }
                        break;
                    case ControlCommands.ScriptSave:
                        try
                        {
                            var save=ParsePayload<ScriptSaveRequest>(request.Payload);
                            response=new ControlResponse(true,
                                Data:await _store.SaveEditorScriptAsync(save,ct));
                        }
                        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
                        {
                            response=new ControlResponse(false,Error:ex.Message);
                        }
                        break;
                    case ControlCommands.PauseJob:
                        if(request.Payload is not { } pausePayload ||
                            pausePayload.ValueKind!=JsonValueKind.Object)
                        {
                            response=new ControlResponse(false,Error:"缺少任务编号。");
                            break;
                        }
                        try
                        {
                            var pauseRequest=JsonSerializer.Deserialize<PauseJobRequest>(
                                pausePayload.GetRawText());
                            if(pauseRequest is null)
                                throw new ArgumentException("任务编号无效。");
                            response=new ControlResponse(true,
                                Data:await _store.PauseJobAsync(pauseRequest.JobId,ct));
                        }
                        catch(Exception ex) when(ex is ArgumentException or KeyNotFoundException)
                        {
                            response=new ControlResponse(false,Error:ex.Message);
                        }
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
