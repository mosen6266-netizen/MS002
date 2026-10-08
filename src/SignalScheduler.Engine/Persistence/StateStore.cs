using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed class StateStore
{
    readonly RuntimePaths _paths;
    public StateStore(RuntimePaths paths)=>_paths=paths;

    SqliteConnection Open()
    {
        var cs=new SqliteConnectionStringBuilder{
            DataSource=_paths.DatabasePath,
            Mode=SqliteOpenMode.ReadWriteCreate,
            Cache=SqliteCacheMode.Shared
        }.ToString();
        var c=new SqliteConnection(cs); c.Open();
        using var cmd=c.CreateCommand();
        cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var c=Open();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="""
        CREATE TABLE IF NOT EXISTS v8_schema(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS v8_jobs(
          job_id TEXT PRIMARY KEY,state TEXT NOT NULL,cursor INTEGER NOT NULL DEFAULT 0,updated_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS v8_dispatch_journal(
          dispatch_key TEXT PRIMARY KEY,job_id TEXT NOT NULL,run_token TEXT NOT NULL,run_cycle INTEGER NOT NULL,
          cursor INTEGER NOT NULL,group_id TEXT NOT NULL,account_id TEXT NOT NULL,payload_hash TEXT NOT NULL,
          state TEXT NOT NULL,provider_message_id TEXT,detail TEXT,created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_v8_dispatch_job ON v8_dispatch_journal(job_id,state,updated_at);
        CREATE TABLE IF NOT EXISTS v8_event_log(
          id INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT,dispatch_key TEXT,event_type TEXT NOT NULL,detail TEXT,created_at INTEGER NOT NULL
        );
        INSERT INTO v8_schema(key,value) VALUES('schema_version','1')
          ON CONFLICT(key) DO UPDATE SET value=excluded.value;
        """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReserveAsync(DispatchIdentity d,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using var q=c.CreateCommand(); q.Transaction=tx;
        q.CommandText="SELECT state,payload_hash FROM v8_dispatch_journal WHERE dispatch_key=$k";
        q.Parameters.AddWithValue("$k",d.DispatchKey);
        await using var r=await q.ExecuteReaderAsync(ct);
        if(await r.ReadAsync(ct))
        {
            var state=r.GetString(0); var hash=r.GetString(1);
            if(hash!=d.PayloadHash) throw new InvalidOperationException("Dispatch key collision.");
            if(state is "Confirmed" or "Committed") throw new InvalidOperationException("Dispatch already confirmed.");
            if(state is "Sending" or "Unknown" or "RecoveryRequired") throw new InvalidOperationException("Manual recovery required.");
        }
        await r.DisposeAsync();

        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var cmd=c.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText="""
        INSERT INTO v8_dispatch_journal(dispatch_key,job_id,run_token,run_cycle,cursor,group_id,account_id,payload_hash,state,created_at,updated_at)
        VALUES($k,$j,$t,$cy,$cu,$g,$a,$h,'Prepared',$n,$n)
        ON CONFLICT(dispatch_key) DO UPDATE SET state='Prepared',updated_at=excluded.updated_at;
        """;
        cmd.Parameters.AddWithValue("$k",d.DispatchKey); cmd.Parameters.AddWithValue("$j",d.JobId);
        cmd.Parameters.AddWithValue("$t",d.RunToken); cmd.Parameters.AddWithValue("$cy",d.RunCycle);
        cmd.Parameters.AddWithValue("$cu",d.Cursor); cmd.Parameters.AddWithValue("$g",d.GroupId);
        cmd.Parameters.AddWithValue("$a",d.AccountId); cmd.Parameters.AddWithValue("$h",d.PayloadHash);
        cmd.Parameters.AddWithValue("$n",now);
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    public Task MarkSendingAsync(DispatchIdentity d,CancellationToken ct)=>SetStateAsync(d.DispatchKey,"Sending",null,ct);
    public Task MarkDefinitelyNotSentAsync(DispatchIdentity d,string? detail,CancellationToken ct)=>SetStateAsync(d.DispatchKey,"DefinitelyNotSent",detail,ct);
    public Task MarkRecoveryAsync(DispatchIdentity d,string? detail,CancellationToken ct)=>SetStateAsync(d.DispatchKey,"RecoveryRequired",detail,ct);

    async Task SetStateAsync(string key,string state,string? detail,CancellationToken ct)
    {
        await using var c=Open();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE v8_dispatch_journal SET state=$s,detail=$d,updated_at=$n WHERE dispatch_key=$k";
        cmd.Parameters.AddWithValue("$s",state); cmd.Parameters.AddWithValue("$d",(object?)detail??DBNull.Value);
        cmd.Parameters.AddWithValue("$n",DateTimeOffset.UtcNow.ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$k",key);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Dispatch transition failed.");
    }

    public async Task CommitConfirmedAsync(DispatchIdentity d,SignalSendResult result,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var journal=c.CreateCommand(); journal.Transaction=tx;
        journal.CommandText="""
        UPDATE v8_dispatch_journal SET state='Confirmed',provider_message_id=$p,detail=$d,updated_at=$n
        WHERE dispatch_key=$k AND state='Sending';
        """;
        journal.Parameters.AddWithValue("$p",(object?)result.ProviderMessageId??DBNull.Value);
        journal.Parameters.AddWithValue("$d",(object?)result.Detail??DBNull.Value);
        journal.Parameters.AddWithValue("$n",now); journal.Parameters.AddWithValue("$k",d.DispatchKey);
        if(await journal.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Confirmed transition rejected.");

        await using var job=c.CreateCommand(); job.Transaction=tx;
        job.CommandText="""
        INSERT INTO v8_jobs(job_id,state,cursor,updated_at) VALUES($j,'Running',$next,$n)
        ON CONFLICT(job_id) DO UPDATE SET state='Running',
          cursor=CASE WHEN v8_jobs.cursor<excluded.cursor THEN excluded.cursor ELSE v8_jobs.cursor END,
          updated_at=excluded.updated_at;
        """;
        job.Parameters.AddWithValue("$j",d.JobId); job.Parameters.AddWithValue("$next",d.Cursor+1); job.Parameters.AddWithValue("$n",now);
        await job.ExecuteNonQueryAsync(ct);

        await using var log=c.CreateCommand(); log.Transaction=tx;
        log.CommandText="INSERT INTO v8_event_log(job_id,dispatch_key,event_type,detail,created_at) VALUES($j,$k,'dispatch_confirmed',$d,$n)";
        log.Parameters.AddWithValue("$j",d.JobId); log.Parameters.AddWithValue("$k",d.DispatchKey);
        log.Parameters.AddWithValue("$d",(object?)result.Detail??DBNull.Value); log.Parameters.AddWithValue("$n",now);
        await log.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }
}
