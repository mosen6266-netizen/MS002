using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Signal;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class ReadReceiptRetryTests
{
    [Fact]
    public void RetryPolicyIsBoundedAndDoesNotRetryBeforeDueTime()
    {
        Assert.Equal(5,ReadReceiptRetryPolicy.MaxAttempts);
        Assert.Equal(40,ReadReceiptRetryPolicy.MaxReceiptsPerSend);
        Assert.Equal(8,ReadReceiptRetryPolicy.MaxAuthorsPerSpeaker);
        Assert.Equal(8,ReadReceiptRetryPolicy.MaxRoundsPerSpeaker);
        Assert.True(ReadReceiptRetryPolicy.MaxPhase>=TimeSpan.FromSeconds(16));
        Assert.True(ReadReceiptRetryPolicy.RpcTimeout<=TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(15),ReadReceiptRetryPolicy.NextDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(1),ReadReceiptRetryPolicy.NextDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(5),ReadReceiptRetryPolicy.NextDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(30),ReadReceiptRetryPolicy.NextDelay(4));
        Assert.False(ReadReceiptRetryPolicy.ShouldRetry(5,0,long.MaxValue));
        Assert.False(ReadReceiptRetryPolicy.ShouldRetry(2,200,199));
        Assert.True(ReadReceiptRetryPolicy.ShouldRetry(2,200,200));
    }

    [Fact]
    public async Task InterruptedFifthAttemptIsQuarantinedAsUnknownAfterRestart()
    {
        var dir=Path.Combine(Path.GetTempPath(),"MS002_ReadRestart",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=Path.Combine(dir,"data.db");
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var seed=c.CreateCommand();
            seed.CommandText="""
                CREATE TABLE v8_read_events(
                    account TEXT NOT NULL,group_id TEXT NOT NULL,
                    author TEXT NOT NULL,timestamp_ms INTEGER NOT NULL,
                    state TEXT NOT NULL DEFAULT 'pending',
                    detail TEXT NOT NULL DEFAULT '',
                    attempts INTEGER NOT NULL DEFAULT 0,
                    next_retry_ms INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(account,group_id,author,timestamp_ms)
                );
                INSERT INTO v8_read_events(account,group_id,author,timestamp_ms,
                    attempts,next_retry_ms)
                VALUES('account','group','sender',111,5,0),
                      ('account','group','sender',222,4,0);
                """;
            await seed.ExecuteNonQueryAsync();
        }
        var first=new SignalReadCoordinator(
            RuntimePaths.ForTesting(dir,db),
            NullLogger<SignalReadCoordinator>.Instance);
        var health=await first.GetHealthAsync(CancellationToken.None);
        Assert.Equal(1,health.Unknown);
        Assert.Equal(1,health.Pending);
        Assert.Equal(0,health.Failed);
        await using(var verify=new SqliteConnection($"Data Source={db}"))
        {
            await verify.OpenAsync();
            await using var q=verify.CreateCommand();
            q.CommandText="SELECT state,attempts FROM v8_read_events ORDER BY timestamp_ms;";
            await using var rows=await q.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal("unknown",rows.GetString(0));
            Assert.Equal(5,rows.GetInt32(1));
            Assert.True(await rows.ReadAsync());
            Assert.Equal("pending",rows.GetString(0));
            Assert.Equal(4,rows.GetInt32(1));
        }
        // Idempotent across a second process startup: never turn a possibly
        // accepted receipt back into a fresh retry.
        var second=new SignalReadCoordinator(
            RuntimePaths.ForTesting(dir,db),
            NullLogger<SignalReadCoordinator>.Instance);
        var again=await second.GetHealthAsync(CancellationToken.None);
        Assert.Equal(1,again.Unknown);
        Assert.Equal(1,again.Pending);
    }

    [Fact]
    public async Task LastFailureAndConnectionMetadataSurviveRestartWithoutMessageBodies()
    {
        var dir=Path.Combine(Path.GetTempPath(),"MS002_ReadHealth",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=Path.Combine(dir,"data.db");
        var first=new SignalReadCoordinator(
            RuntimePaths.ForTesting(dir,db),
            NullLogger<SignalReadCoordinator>.Instance);
        await first.GetHealthAsync(CancellationToken.None);
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="""
                UPDATE v8_read_stream_status SET state='连接中断',
                    last_error='HttpRequestException',
                    last_connected_ms=1000,last_event_ms=2000,
                    last_failure_ms=3000,
                    last_failure_type='HttpRequestException',updated_ms=3000
                WHERE id=1;
                """;
            await q.ExecuteNonQueryAsync();
        }
        var restarted=new SignalReadCoordinator(
            RuntimePaths.ForTesting(dir,db),
            NullLogger<SignalReadCoordinator>.Instance);
        var snapshot=await restarted.GetHealthAsync(CancellationToken.None);
        Assert.Equal(1000,snapshot.LastConnectedMs);
        Assert.Equal(2000,snapshot.LastEventMs);
        Assert.Equal(3000,snapshot.LastFailureMs);
        Assert.Equal("HttpRequestException",snapshot.LastFailureType);
        // Persisted previous connectivity is historical, never proof that the
        // newly launched SSE subscriber is connected.
        Assert.NotEqual("已连接",snapshot.StreamState);
    }

    [Fact]
    public async Task ExistingReadQueueMigratesInPlaceWithoutLosingPendingRows()
    {
        var dir=Path.Combine(Path.GetTempPath(),"MS002_ReadRetry",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=Path.Combine(dir,"data.db");
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="""
                CREATE TABLE v8_read_events(
                    account TEXT NOT NULL,group_id TEXT NOT NULL,
                    author TEXT NOT NULL,timestamp_ms INTEGER NOT NULL,
                    state TEXT NOT NULL DEFAULT 'pending',
                    detail TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY(account,group_id,author,timestamp_ms)
                );
                INSERT INTO v8_read_events(account,group_id,author,timestamp_ms)
                VALUES('test-account','test-group','test-sender',12345);
                """;
            await q.ExecuteNonQueryAsync();
        }
        var coordinator=new SignalReadCoordinator(
            RuntimePaths.ForTesting(dir,db),
            NullLogger<SignalReadCoordinator>.Instance);
        var health=await coordinator.GetHealthAsync(CancellationToken.None);
        Assert.Equal(1,health.Pending);
        Assert.Equal(0,health.Failed);
        Assert.Equal(0,health.WaitingRetry);
        await using var verify=new SqliteConnection($"Data Source={db}");
        await verify.OpenAsync();
        await using var q2=verify.CreateCommand();
        q2.CommandText="""
            SELECT attempts,next_retry_ms,state FROM v8_read_events
            WHERE account='test-account';
            """;
        await using var reader=await q2.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0,reader.GetInt32(0));
        Assert.Equal(0,reader.GetInt64(1));
        Assert.Equal("pending",reader.GetString(2));
    }
}
