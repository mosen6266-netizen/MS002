using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "Signal Auto Scheduler V8");
builder.Services.AddSingleton<RuntimePaths>();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<ISignalTransport, SignalCliTransport>();
builder.Services.AddSingleton<DurableTaskEngine>();
builder.Services.AddHostedService<NamedPipeControlServer>();
var host = builder.Build();
await host.Services.GetRequiredService<StateStore>().InitializeAsync(CancellationToken.None);
await host.RunAsync();

sealed class RuntimePaths
{
    public string DataRoot { get; }
    public string DatabasePath { get; }

    public RuntimePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataRoot = Path.Combine(local, "SignalSchedulerData");
        Directory.CreateDirectory(DataRoot);
        DatabasePath = Path.Combine(DataRoot, "data.db");
    }
}

sealed class StateStore
{
    private readonly RuntimePaths _paths;
    public StateStore(RuntimePaths paths) => _paths = paths;

    private SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
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
        await using var tx=await c.BeginTransactionAsync(ct);
        await using var q=c.CreateCommand();
        q.Transaction=tx;
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
        await using var cmd=c.CreateCommand();
        cmd.Transaction=tx;
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
        await tx.CommitAsync(ct);
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
        await using var tx=await c.BeginTransactionAsync(ct);
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
        await tx.CommitAsync(ct);
    }
}

interface ISignalTransport
{
    bool IsReady { get; }
    Task<SignalSendResult> SendAsync(DispatchIdentity dispatch,string payload,CancellationToken ct);
}

sealed class SignalCliTransport : ISignalTransport
{
    public bool IsReady => false;
    public Task<SignalSendResult> SendAsync(DispatchIdentity dispatch,string payload,CancellationToken ct) =>
        throw new InvalidOperationException("Signal transport is disabled in foundation stage.");
}

sealed class DurableTaskEngine
{
    private readonly StateStore _store; private readonly ISignalTransport _signal;
    public DurableTaskEngine(StateStore store,ISignalTransport signal){_store=store;_signal=signal;}

    public async Task<SignalSendResult> DispatchAsync(DispatchIdentity d,string payload,CancellationToken ct)
    {
        if(!_signal.IsReady) throw new InvalidOperationException("Signal unavailable; dispatch blocked fail-closed.");
        await _store.ReserveAsync(d,ct);
        await _store.MarkSendingAsync(d,ct);
        SignalSendResult result;
        try { result=await _signal.SendAsync(d,payload,ct); }
        catch(OperationCanceledException){await _store.MarkRecoveryAsync(d,"Cancelled after entering Sending.",CancellationToken.None);throw;}
        catch(Exception ex){await _store.MarkRecoveryAsync(d,ex.Message,CancellationToken.None);throw;}

        if(result.Outcome==SignalDeliveryOutcome.Confirmed) await _store.CommitConfirmedAsync(d,result,ct);
        else if(result.Outcome==SignalDeliveryOutcome.DefinitelyNotSent) await _store.MarkDefinitelyNotSentAsync(d,result.Detail,ct);
        else await _store.MarkRecoveryAsync(d,result.Detail,ct);
        return result;
    }
}

sealed class NamedPipeControlServer : BackgroundService
{
    public const string PipeName="SignalScheduler.V8.Control";
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            await using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
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
                    ControlCommands.Status=>new ControlResponse(true,Data:new{version="8.0.0-alpha.1",service="running",transport="disabled-foundation-stage"}),
                    _=>new ControlResponse(false,Error:"unknown_command")
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch{await Task.Delay(500,ct);}
        }
    }
}
