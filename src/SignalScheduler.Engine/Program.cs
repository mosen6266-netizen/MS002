using System.IO.Pipes;
using System.Text.Json;
using SignalScheduler.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine.Engine;
using SignalScheduler.Engine.Ipc;
using SignalScheduler.Engine.Licensing;
using SignalScheduler.Engine.Migration;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;

if(args.Any(x=>string.Equals(x,"--prepare-update",StringComparison.OrdinalIgnoreCase)))
{
    Environment.ExitCode=await RequestPrepareUpdateAsync();
    return;
}

using var mutex = new Mutex(true, @"Local\SignalScheduler.V8.Engine", out var createdNew);
if (!createdNew) return;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<RuntimePaths>();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<LegacyV7Migrator>();
builder.Services.AddSingleton<SignalGuardian>();
builder.Services.AddSingleton<SignalLinkManager>();
builder.Services.AddSingleton<SignalCliTransport>();
builder.Services.AddSingleton<ISignalTransport>(sp=>sp.GetRequiredService<SignalCliTransport>());
builder.Services.AddSingleton<ISignalTypingTransport>(sp=>sp.GetRequiredService<SignalCliTransport>());
builder.Services.AddSingleton<ISignalReadBeforeSendTransport>(sp=>sp.GetRequiredService<SignalCliTransport>());
builder.Services.AddSingleton<DurableTaskEngine>();
builder.Services.AddSingleton<LiveProbeCoordinator>();
builder.Services.AddSingleton<LicenseManager>();
builder.Services.AddHostedService(sp=>sp.GetRequiredService<SignalGuardian>());
builder.Services.AddHostedService<SignalCatalogSyncService>();
builder.Services.AddHostedService<PreviewTaskRunner>();
builder.Services.AddHostedService<LivePilotRunner>();
builder.Services.AddHostedService<LiveBatchRunner>();
builder.Services.AddHostedService(sp=>sp.GetRequiredService<LicenseManager>());
builder.Services.AddHostedService<NamedPipeControlServer>();

var host = builder.Build();
var store = host.Services.GetRequiredService<StateStore>();
await store.InitializeAsync(CancellationToken.None);

var migrator = host.Services.GetRequiredService<LegacyV7Migrator>();
var inventory = await migrator.AnalyzeAsync(CancellationToken.None);
if (inventory.LegacyDetected && !inventory.MetadataMigrated)
    await migrator.MigrateMetadataAsync(CancellationToken.None);

// Copy legacy script metadata into a separate editable V8 authoring store.
// Safe to run repeatedly: existing edited scripts are never overwritten.
await store.InitializeScriptEditorAsync(CancellationToken.None);
await store.InitializePreviewTasksAsync(CancellationToken.None);
await store.InitializeLiveProbeAsync(CancellationToken.None);
await store.InitializeLivePilotAsync(CancellationToken.None);
await store.InitializeLiveBatchAsync(CancellationToken.None);

await host.RunAsync();

static async Task<int> RequestPrepareUpdateAsync()
{
    try
    {
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var pipe=new NamedPipeClientStream(
            ".",
            "SignalScheduler.V8.Control",
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(1200,cts.Token);
        }
        catch
        {
            // No Engine is listening, so there is nothing to stop.
            return 0;
        }

        using var reader=new StreamReader(pipe,leaveOpen:true);
        using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};

        await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlRequest(ControlCommands.PrepareUpdate)));
        var line=await reader.ReadLineAsync(cts.Token);
        if(string.IsNullOrWhiteSpace(line)) return 31;

        using var doc=JsonDocument.Parse(line);
        var root=doc.RootElement;
        if(!root.TryGetProperty("Ok",out var ok) || !ok.GetBoolean())
            return 31;

        if(!root.TryGetProperty("Data",out var data))
            return 31;

        var readiness=JsonSerializer.Deserialize<UpdateReadiness>(data.GetRawText());
        if(readiness is null) return 31;

        return readiness.CanUpdate?0:20;
    }
    catch
    {
        return 31;
    }
}
