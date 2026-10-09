using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

/// <summary>
/// Observes daemon SSE receive events without issuing competing receive RPCs.
/// Persist only routing metadata, never message text. Receipt failures are best effort.
/// </summary>
public sealed class SignalReadCoordinator : BackgroundService
{
    readonly RuntimePaths _paths;
    readonly ILogger<SignalReadCoordinator> _logger;
    long _lastStreamWarningMs;
    readonly HttpClient _http=new(){BaseAddress=new Uri("http://127.0.0.1:7583/"),Timeout=Timeout.InfiniteTimeSpan};
    readonly SemaphoreSlim _databaseGate=new(1,1);
    readonly SemaphoreSlim _sendGate=new(1,1);
    bool _initialized;
    string? _lastEventId;
    volatile string _streamState="尚未连接";
    volatile string _lastError="";
    long _lastConnectedMs;
    long _lastEventMs;

    public async Task<ReadHealthSnapshot> GetHealthAsync(CancellationToken ct)
    {
        var pending=0;
        var attempted=0;
        var failed=0;
        var waitingRetry=0;
        try
        {
            await InitializeAsync(ct);
            await _databaseGate.WaitAsync(ct);
            try
            {
                await using var db=Open();
                await using var q=db.CreateCommand();
                q.CommandText="""
                    SELECT state,COUNT(*),
                           SUM(CASE WHEN state='pending' AND
                               next_retry_ms>$now THEN 1 ELSE 0 END)
                    FROM v8_read_events GROUP BY state;
                    """;
                q.Parameters.AddWithValue("$now",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await using var r=await q.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))
                {
                    var state=r.GetString(0);
                    if(state=="pending")
                    {
                        pending+=r.GetInt32(1);
                        waitingRetry+=r.IsDBNull(2)?0:r.GetInt32(2);
                    }
                    if(state=="attempted")attempted+=r.GetInt32(1);
                    if(state=="failed")failed+=r.GetInt32(1);
                }
            }
            finally{_databaseGate.Release();}
        }
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {
            return new ReadHealthSnapshot("数据库错误",
                Interlocked.Read(ref _lastConnectedMs),
                Interlocked.Read(ref _lastEventMs),0,0,ex.GetType().Name);
        }
        return new ReadHealthSnapshot(_streamState,
            Interlocked.Read(ref _lastConnectedMs),
            Interlocked.Read(ref _lastEventMs),pending,attempted,_lastError,
            failed,waitingRetry);
    }


    public SignalReadCoordinator(RuntimePaths paths,ILogger<SignalReadCoordinator> logger)
    {
        _paths=paths;
        _logger=logger;
    }

    SqliteConnection Open()
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{
            DataSource=_paths.DatabasePath,Mode=SqliteOpenMode.ReadWriteCreate}.ToString());
        c.Open();
        using var cmd=c.CreateCommand();
        cmd.CommandText="PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    async Task InitializeAsync(CancellationToken ct)
    {
        if(_initialized)return;
        await _databaseGate.WaitAsync(ct);
        try
        {
            if(_initialized)return;
            await using var db=Open();
            await using var q=db.CreateCommand();
            q.CommandText="""
                CREATE TABLE IF NOT EXISTS v8_read_events(
                    account TEXT NOT NULL, group_id TEXT NOT NULL,
                    author TEXT NOT NULL, timestamp_ms INTEGER NOT NULL,
                    state TEXT NOT NULL DEFAULT 'pending',
                    detail TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY(account,group_id,author,timestamp_ms)
                );
                CREATE INDEX IF NOT EXISTS idx_read_events_pending
                ON v8_read_events(account,group_id,state);
                """;
            await q.ExecuteNonQueryAsync(ct);
            var columns=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using(var info=db.CreateCommand())
            {
                info.CommandText="PRAGMA table_info(v8_read_events);";
                await using var reader=await info.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))columns.Add(reader.GetString(1));
            }
            if(!columns.Contains("attempts"))
            {
                await using var alter=db.CreateCommand();
                alter.CommandText="""
                    ALTER TABLE v8_read_events
                    ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0;
                    """;
                await alter.ExecuteNonQueryAsync(ct);
            }
            if(!columns.Contains("next_retry_ms"))
            {
                await using var alter=db.CreateCommand();
                alter.CommandText="""
                    ALTER TABLE v8_read_events
                    ADD COLUMN next_retry_ms INTEGER NOT NULL DEFAULT 0;
                    """;
                await alter.ExecuteNonQueryAsync(ct);
            }
            _initialized=true;
        }
        finally{_databaseGate.Release();}
    }

    static string? String(JsonElement obj,string key)
    {
        return obj.ValueKind==JsonValueKind.Object &&
            obj.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String
                ?v.GetString():null;
    }

    static bool TryReadEvent(JsonElement root,out string account,
        out string group,out string author,out long timestamp)
    {
        account=group=author="";
        timestamp=0;
        if(root.ValueKind!=JsonValueKind.Object)return false;
        // Both raw daemon SSE envelopes and wrapped JSON-RPC receive
        // notifications are supported, including manual subscription format.
        var p=root;
        if(root.TryGetProperty("params",out var wrapped))p=wrapped;
        if(p.ValueKind!=JsonValueKind.Object)return false;
        if(p.TryGetProperty("result",out var result))p=result;
        if(p.ValueKind!=JsonValueKind.Object)return false;
        account=String(p,"account")??String(root,"account")??"";
        if(!p.TryGetProperty("envelope",out var envelope) ||
           !envelope.TryGetProperty("dataMessage",out var msg))return false;
        if(!msg.TryGetProperty("groupInfo",out var groupInfo) &&
           !msg.TryGetProperty("groupV2",out groupInfo))return false;
        group=String(groupInfo,"groupId")??String(groupInfo,"id")??"";
        author=String(envelope,"sourceUuid")??String(envelope,"sourceNumber")
            ??String(envelope,"source")??"";
        if(msg.TryGetProperty("timestamp",out var ts)&&ts.TryGetInt64(out var value))
            timestamp=value;
        else if(envelope.TryGetProperty("timestamp",out ts)&&ts.TryGetInt64(out value))
            timestamp=value;
        return account.Length>0 && group.Length>0 && author.Length>0 && timestamp>0;
    }

    async Task SaveEventAsync(string payload,CancellationToken ct)
    {
        using var doc=JsonDocument.Parse(payload);
        if(!TryReadEvent(doc.RootElement,out var account,out var group,
            out var author,out var timestamp))return;
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var db=Open();
            await using var q=db.CreateCommand();
            q.CommandText="""
                INSERT OR IGNORE INTO v8_read_events(account,group_id,author,timestamp_ms)
                VALUES($a,$g,$s,$t);
                """;
            q.Parameters.AddWithValue("$a",account);
            q.Parameters.AddWithValue("$g",group);
            q.Parameters.AddWithValue("$s",author);
            q.Parameters.AddWithValue("$t",timestamp);
            await q.ExecuteNonQueryAsync(ct);
            Interlocked.Exchange(ref _lastEventMs,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        finally{_databaseGate.Release();}
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try{await InitializeAsync(ct);}
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {
            _logger.LogError(ex,"Read event persistence initialization failed");
            return;
        }
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,"api/v1/events");
                request.Headers.Accept.ParseAdd("text/event-stream");
                if(!string.IsNullOrEmpty(_lastEventId))
                    request.Headers.TryAddWithoutValidation("Last-Event-ID",_lastEventId);
                using var response=await _http.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead,ct);
                response.EnsureSuccessStatusCode();
                _streamState="已连接";
                _lastError="";
                Interlocked.Exchange(ref _lastConnectedMs,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _logger.LogInformation("Signal read event stream connected");
                await using var stream=await response.Content.ReadAsStreamAsync(ct);
                using var reader=new StreamReader(stream);
                var data=new System.Text.StringBuilder();
                while(!ct.IsCancellationRequested)
                {
                    var line=await reader.ReadLineAsync(ct);
                    if(line is null)break;
                    if(line.StartsWith("id:",StringComparison.Ordinal))
                        _lastEventId=line[3..].Trim();
                    else if(line.StartsWith("data:",StringComparison.Ordinal))
                        data.Append(line[5..].TrimStart());
                    else if(line.Length==0 && data.Length>0)
                    {
                        try{await SaveEventAsync(data.ToString(),ct);}
                        catch(JsonException){ /* Ignore non-message SSE payloads. */ }
                        data.Clear();
                    }
                }
                if(!ct.IsCancellationRequested)
                {
                    _streamState="连接中断";
                    _lastError="事件流正常关闭，正在重新连接";
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                _streamState="连接中断";
                _lastError=ex.GetType().Name;
                // Avoid flooding logs while the daemon is temporarily offline.
                var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if(now-Interlocked.Read(ref _lastStreamWarningMs)>30000)
                {
                    Interlocked.Exchange(ref _lastStreamWarningMs,now);
                    _logger.LogWarning(ex,
                        "Signal read event stream unavailable; retrying");
                }
            }
            try{await Task.Delay(TimeSpan.FromSeconds(3),ct);}
            catch(OperationCanceledException){break;}
        }
    }

    bool ReadReceiptsEnabled()
    {
        try
        {
            var path=Path.Combine(_paths.DataRoot,"read-options.json");
            if(!File.Exists(path))return true;
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            return !doc.RootElement.TryGetProperty("ReadReceipts",out var value) ||
                value.ValueKind!=JsonValueKind.False;
        }
        catch{return true;}
    }

    public async Task TrySendForGroupAsync(string account,string group,CancellationToken ct)
    {
        if(!ReadReceiptsEnabled())return;
        // If another group is already attempting receipts, preserve normal
        // message dispatch latency. Pending receipts remain durably queued.
        if(!await _sendGate.WaitAsync(0,ct))return;
        try
        {
            await InitializeAsync(ct);
            var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pending=new List<(string Author,long Timestamp,int Attempt)>();
            await _databaseGate.WaitAsync(ct);
            try
            {
                await using var db=Open();
                await using var q=db.CreateCommand();
                q.CommandText="""
                    SELECT author,timestamp_ms,attempts
                    FROM v8_read_events
                    WHERE account=$a AND group_id=$g AND state='pending'
                      AND next_retry_ms<=$now AND attempts<$max
                    ORDER BY next_retry_ms,timestamp_ms LIMIT $limit;
                    """;
                q.Parameters.AddWithValue("$a",account);
                q.Parameters.AddWithValue("$g",group);
                q.Parameters.AddWithValue("$now",now);
                q.Parameters.AddWithValue("$max",ReadReceiptRetryPolicy.MaxAttempts);
                q.Parameters.AddWithValue("$limit",ReadReceiptRetryPolicy.MaxReceiptsPerSend);
                await using(var r=await q.ExecuteReaderAsync(ct))
                {
                    while(await r.ReadAsync(ct))
                        pending.Add((r.GetString(0),r.GetInt64(1),r.GetInt32(2)+1));
                }

                // Reserve each attempt durably before the network request.
                // A crash cannot silently reset the retry counter.
                foreach(var item in pending)
                {
                    await using var claim=db.CreateCommand();
                    claim.CommandText="""
                        UPDATE v8_read_events
                        SET attempts=$attempt,next_retry_ms=$next,detail=$detail
                        WHERE account=$a AND group_id=$g AND author=$sender
                          AND timestamp_ms=$timestamp AND state='pending';
                        """;
                    claim.Parameters.AddWithValue("$attempt",item.Attempt);
                    claim.Parameters.AddWithValue("$next",
                        now+(long)ReadReceiptRetryPolicy.NextDelay(item.Attempt).TotalMilliseconds);
                    claim.Parameters.AddWithValue("$detail","已请求发送回执，等待接口响应");
                    claim.Parameters.AddWithValue("$a",account);
                    claim.Parameters.AddWithValue("$g",group);
                    claim.Parameters.AddWithValue("$sender",item.Author);
                    claim.Parameters.AddWithValue("$timestamp",item.Timestamp);
                    await claim.ExecuteNonQueryAsync(ct);
                }
            }
            finally{_databaseGate.Release();}

            foreach(var item in pending)
            {
                var state="pending";
                var detail="已读回执请求尚未得到成功确认";
                try
                {
                    var id=Guid.NewGuid().ToString("N");
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ReadReceiptRetryPolicy.RpcTimeout);
                    using var response=await _http.PostAsJsonAsync("api/v1/rpc",new{
                        jsonrpc="2.0",method="sendReceipt",id,
                        @params=new{
                            account,recipient=item.Author,
                            targetTimestamp=item.Timestamp,type="read"
                        }
                    },timeout.Token);
                    var raw=await response.Content.ReadAsStringAsync(timeout.Token);
                    using var doc=JsonDocument.Parse(raw);
                    var root=doc.RootElement;
                    if(response.IsSuccessStatusCode &&
                       String(root,"id")==id &&
                       (!root.TryGetProperty("error",out var error) ||
                        error.ValueKind==JsonValueKind.Null))
                    {
                        state="attempted";
                        detail="Signal RPC 已接受已读回执（不代表其他设备清零）";
                    }
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch(Exception ex)
                {
                    detail="回执尝试失败："+ex.GetType().Name;
                    // Avoid logging one stack trace per message for a daemon outage.
                    if(item.Attempt==1 || item.Attempt==ReadReceiptRetryPolicy.MaxAttempts)
                        _logger.LogWarning(ex,
                            "Read receipt attempt {Attempt}/{Max} failed; outgoing dispatch proceeds",
                            item.Attempt,ReadReceiptRetryPolicy.MaxAttempts);
                }

                if(state!="attempted" &&
                   item.Attempt>=ReadReceiptRetryPolicy.MaxAttempts)
                {
                    state="failed";
                    detail="已达到最多 "+ReadReceiptRetryPolicy.MaxAttempts+
                        " 次重试，未确认已读回执成功";
                }

                await _databaseGate.WaitAsync(ct);
                try
                {
                    await using var db=Open();
                    await using var q=db.CreateCommand();
                    q.CommandText="""
                        UPDATE v8_read_events SET state=$state,detail=$detail
                        WHERE account=$a AND group_id=$g AND author=$sender
                          AND timestamp_ms=$timestamp AND attempts=$attempt;
                        """;
                    q.Parameters.AddWithValue("$state",state);
                    q.Parameters.AddWithValue("$detail",detail);
                    q.Parameters.AddWithValue("$a",account);
                    q.Parameters.AddWithValue("$g",group);
                    q.Parameters.AddWithValue("$sender",item.Author);
                    q.Parameters.AddWithValue("$timestamp",item.Timestamp);
                    q.Parameters.AddWithValue("$attempt",item.Attempt);
                    await q.ExecuteNonQueryAsync(ct);
                }
                finally{_databaseGate.Release();}
            }
        }
        finally{_sendGate.Release();}
    }

}
