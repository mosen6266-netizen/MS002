using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace SignalScheduler.Engine.Signal;

/// <summary>
/// Observes daemon SSE receive events without issuing competing receive RPCs.
/// Persist only routing metadata, never message text. Receipt failures are best effort.
/// </summary>
public sealed class SignalReadCoordinator : BackgroundService
{
    readonly RuntimePaths _paths;
    readonly HttpClient _http=new(){BaseAddress=new Uri("http://127.0.0.1:7583/"),Timeout=Timeout.InfiniteTimeSpan};
    readonly SemaphoreSlim _databaseGate=new(1,1);
    string? _lastEventId;

    public SignalReadCoordinator(RuntimePaths paths)=>_paths=paths;

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
        await _databaseGate.WaitAsync(ct);
        try
        {
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
        if(root.ValueKind!=JsonValueKind.Object ||
           !root.TryGetProperty("params",out var p))return false;
        // SSE may contain a JSON-RPC receive notification or the notification params.
        if(p.TryGetProperty("result",out var result))p=result;
        account=String(p,"account")??"";
        if(!p.TryGetProperty("envelope",out var envelope) ||
           !envelope.TryGetProperty("dataMessage",out var msg) ||
           !msg.TryGetProperty("groupInfo",out var groupInfo))return false;
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
        }
        finally{_databaseGate.Release();}
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try{await InitializeAsync(ct);}
        catch(Exception) when(!ct.IsCancellationRequested){return;}
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
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception){ /* Daemon offline: reconnect when available. */ }
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
        await InitializeAsync(ct);
        var pending=new List<(string Author,long Timestamp)>();
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var db=Open();
            await using var q=db.CreateCommand();
            q.CommandText="""
                SELECT author,timestamp_ms FROM v8_read_events
                WHERE account=$a AND group_id=$g AND state='pending'
                ORDER BY timestamp_ms LIMIT 100;
                """;
            q.Parameters.AddWithValue("$a",account);
            q.Parameters.AddWithValue("$g",group);
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))pending.Add((r.GetString(0),r.GetInt64(1)));
        }
        finally{_databaseGate.Release();}
        foreach(var item in pending)
        {
            var state="failed";
            var detail="发送已读回执未确认";
            try
            {
                var id=Guid.NewGuid().ToString("N");
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var response=await _http.PostAsJsonAsync("api/v1/rpc",new{
                    jsonrpc="2.0",method="sendReceipt",id,
                    @params=new{account,recipient=item.Author,timestamp=item.Timestamp,type="read"}
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
                    detail="Signal RPC 已接受已读回执";
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){detail=ex.GetType().Name;}
            await _databaseGate.WaitAsync(ct);
            try
            {
                await using var db=Open();
                await using var q=db.CreateCommand();
                q.CommandText="""
                    UPDATE v8_read_events SET state=$state,detail=$detail
                    WHERE account=$a AND group_id=$g AND author=$sender
                    AND timestamp_ms=$timestamp;
                    """;
                q.Parameters.AddWithValue("$state",state);
                q.Parameters.AddWithValue("$detail",detail);
                q.Parameters.AddWithValue("$a",account);
                q.Parameters.AddWithValue("$g",group);
                q.Parameters.AddWithValue("$sender",item.Author);
                q.Parameters.AddWithValue("$timestamp",item.Timestamp);
                await q.ExecuteNonQueryAsync(ct);
            }
            finally{_databaseGate.Release();}
        }
    }
}
