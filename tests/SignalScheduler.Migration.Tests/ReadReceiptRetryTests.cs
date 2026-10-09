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
        Assert.Equal(2,ReadReceiptRetryPolicy.MaxReceiptsPerSend);
        Assert.True(ReadReceiptRetryPolicy.RpcTimeout<=TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(15),ReadReceiptRetryPolicy.NextDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(1),ReadReceiptRetryPolicy.NextDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(5),ReadReceiptRetryPolicy.NextDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(30),ReadReceiptRetryPolicy.NextDelay(4));
        Assert.False(ReadReceiptRetryPolicy.ShouldRetry(5,0,long.MaxValue));
        Assert.False(ReadReceiptRetryPolicy.ShouldRetry(2,200,199));
        Assert.True(ReadReceiptRetryPolicy.ShouldRetry(2,200,200));
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
