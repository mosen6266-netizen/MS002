using SignalScheduler.Engine.Engine;
using SignalScheduler.Engine.Ipc;
using SignalScheduler.Engine.Migration;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;

using var mutex = new Mutex(true, @"Local\SignalScheduler.V8.Engine", out var createdNew);
if (!createdNew) return;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<RuntimePaths>();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<LegacyV7Migrator>();
builder.Services.AddSingleton<ISignalTransport, SignalCliTransport>();
builder.Services.AddSingleton<DurableTaskEngine>();
builder.Services.AddHostedService<NamedPipeControlServer>();

var host = builder.Build();
var store = host.Services.GetRequiredService<StateStore>();
await store.InitializeAsync(CancellationToken.None);

var migrator = host.Services.GetRequiredService<LegacyV7Migrator>();
var inventory = await migrator.AnalyzeAsync(CancellationToken.None);
if (inventory.LegacyDetected && !inventory.MetadataMigrated)
    await migrator.MigrateMetadataAsync(CancellationToken.None);

await host.RunAsync();
