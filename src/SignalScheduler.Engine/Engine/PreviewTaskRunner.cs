using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine.Persistence;

namespace SignalScheduler.Engine.Engine;

/// <summary>
/// Rehearsal task clock. Advances persisted preview cursors only. It never
/// calls ISignalTransport or any Signal RPC method. A Windows restart changes
/// Running -> Paused on StateStore startup; users must resume explicitly.
/// </summary>
public sealed class PreviewTaskRunner : BackgroundService
{
    readonly StateStore _store;
    public PreviewTaskRunner(StateStore store)=>_store=store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.RunDuePreviewBatchAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),50,stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A failed database tick must never invent successful progress.
                // Leave the durable task state and the prior cursor in SQLite.
            }

            try { await Task.Delay(250,stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
