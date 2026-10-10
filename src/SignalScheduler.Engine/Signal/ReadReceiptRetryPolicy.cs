namespace SignalScheduler.Engine.Signal;

/// <summary>
/// Retrying receipts must never block ordinary scheduled delivery for long.
/// The retry schedule is persistent (next_retry_ms in SQLite), and the
/// bounded batch size applies per outgoing group message.
/// </summary>
public static class ReadReceiptRetryPolicy
{
    public const int MaxAttempts=5;
    public const int MaxReceiptsPerSend=40;
    public const int MaxAuthorsPerSpeaker=8;
    public const int MaxRoundsPerSpeaker=8;
    public static readonly TimeSpan MaxPhase=TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RpcTimeout=TimeSpan.FromSeconds(4);

    public static TimeSpan NextDelay(int attempt)
    {
        return attempt switch
        {
            <=0=>TimeSpan.Zero,
            1=>TimeSpan.FromSeconds(15),
            2=>TimeSpan.FromMinutes(1),
            3=>TimeSpan.FromMinutes(5),
            4=>TimeSpan.FromMinutes(30),
            _=>TimeSpan.Zero
        };
    }

    public static bool ShouldRetry(int attempts,long nextRetryMs,long nowMs)=>
        attempts<MaxAttempts && nextRetryMs<=nowMs;
}
