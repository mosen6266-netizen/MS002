using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;

namespace SignalScheduler.Engine.Migration;

public sealed record LegacyInventory(bool LegacyDetected,bool MetadataMigrated,int Accounts,int Groups,int Scripts,int Steps,int Jobs);

public sealed class LegacyV7Migrator
{
    readonly RuntimePaths _paths;
    public LegacyV7Migrator(RuntimePaths paths)=>_paths=paths;

    SqliteConnection Open(SqliteOpenMode mode=SqliteOpenMode.ReadWriteCreate)
    {
        var cs=new SqliteConnectionStringBuilder{DataSource=_paths.DatabasePath,Mode=mode,Cache=SqliteCacheMode.Private}.ToString();
        var c=new SqliteConnection(cs); c.Open();
        return c;
    }

    static async Task<HashSet<string>> TablesAsync(SqliteConnection c,CancellationToken ct)
    {
        var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table'";
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    static async Task<int> CountAsync(SqliteConnection c,string table,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText=$"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)??0);
    }

    public async Task<LegacyInventory> AnalyzeAsync(CancellationToken ct)
    {
        if(!File.Exists(_paths.DatabasePath)) return new(false,false,0,0,0,0,0);
        await using var c=Open(SqliteOpenMode.ReadOnly);
        var tables=await TablesAsync(c,ct);
        var required=new[]{"accounts","groups","scripts","script_steps","group_jobs"};
        if(!required.All(tables.Contains)) return new(false,false,0,0,0,0,0);

        var migrated=false;
        if(tables.Contains("v8_schema"))
        {
            await using var q=c.CreateCommand();
            q.CommandText="SELECT value FROM v8_schema WHERE key='v7_metadata_migration'";
            migrated=(await q.ExecuteScalarAsync(ct) as string)=="complete-v1";
        }
        return new(true,migrated,
            await CountAsync(c,"accounts",ct),
            await CountAsync(c,"groups",ct),
            await CountAsync(c,"scripts",ct),
            await CountAsync(c,"script_steps",ct),
            await CountAsync(c,"group_jobs",ct));
    }

    public async Task<string> CreateBackupAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.BackupRoot);
        var target=Path.Combine(_paths.BackupRoot,$"v8-pre-migration-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        await using var source=Open();
        await using var dest=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=target,Mode=SqliteOpenMode.ReadWriteCreate}.ToString());
        await dest.OpenAsync(ct);
        source.BackupDatabase(dest);
        return target;
    }

    public async Task MigrateMetadataAsync(CancellationToken ct)
    {
        var inventory=await AnalyzeAsync(ct);
        if(!inventory.LegacyDetected || inventory.MetadataMigrated) return;

        await CreateBackupAsync(ct);

        await using var c=Open();
        using var tx=c.BeginTransaction();
        try
        {
            await using(var schema=c.CreateCommand())
            {
                schema.Transaction=tx;
                schema.CommandText="""
                CREATE TABLE IF NOT EXISTS v8_accounts(
                  legacy_id INTEGER PRIMARY KEY,account TEXT NOT NULL UNIQUE,label TEXT NOT NULL,enabled INTEGER NOT NULL,sort_order INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS v8_groups(
                  legacy_id INTEGER PRIMARY KEY,account TEXT NOT NULL,group_id TEXT NOT NULL,name TEXT NOT NULL,enabled INTEGER NOT NULL,
                  UNIQUE(account,group_id)
                );
                CREATE TABLE IF NOT EXISTS v8_scripts(
                  legacy_id INTEGER PRIMARY KEY,name TEXT NOT NULL,target_group_id TEXT NOT NULL,created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS v8_script_steps(
                  legacy_id INTEGER PRIMARY KEY,script_id INTEGER NOT NULL,sort_order INTEGER NOT NULL,account TEXT NOT NULL,
                  message TEXT NOT NULL,attachment TEXT NOT NULL,pause_after INTEGER NOT NULL,reminder_text TEXT NOT NULL,
                  delay_after INTEGER NOT NULL,typing_seconds INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS v8_legacy_jobs(
                  legacy_id INTEGER PRIMARY KEY,name TEXT NOT NULL,group_id TEXT NOT NULL,script_id INTEGER NOT NULL,
                  mapped_state TEXT NOT NULL,step_cursor INTEGER NOT NULL,recovery_needed INTEGER NOT NULL,inflight_cursor INTEGER NOT NULL,
                  snapshot_id INTEGER NOT NULL DEFAULT 0
                );
                """;
                await schema.ExecuteNonQueryAsync(ct);
            }

            await CopyAsync(c,tx,"""
              INSERT OR REPLACE INTO v8_accounts(legacy_id,account,label,enabled,sort_order)
              SELECT id,account,label,enabled,sort_order FROM accounts;
              """,ct);
            await CopyAsync(c,tx,"""
              INSERT OR REPLACE INTO v8_groups(legacy_id,account,group_id,name,enabled)
              SELECT id,account,group_id,name,enabled FROM groups;
              """,ct);
            await CopyAsync(c,tx,"""
              INSERT OR REPLACE INTO v8_scripts(legacy_id,name,target_group_id,created_at,updated_at)
              SELECT id,name,target_group_id,created_at,updated_at FROM scripts;
              """,ct);

            var columns=await ColumnsAsync(c,tx,"script_steps",ct);
            var typing=columns.Contains("typing_seconds")?"typing_seconds":"0";
            await CopyAsync(c,tx,$"""
              INSERT OR REPLACE INTO v8_script_steps(legacy_id,script_id,sort_order,account,message,attachment,pause_after,reminder_text,delay_after,typing_seconds)
              SELECT id,script_id,sort_order,account,message,attachment,pause_after,reminder_text,delay_after,{typing}
              FROM script_steps;
              """,ct);

            var jobColumns=await ColumnsAsync(c,tx,"group_jobs",ct);
            string Col(string name,string fallback)=>jobColumns.Contains(name)?name:fallback;
            var paused=Col("paused","0");
            var recovery=Col("recovery_needed","0");
            var inflight=Col("inflight_cursor","-1");
            var snapshot=Col("snapshot_id","0");
            var running=Col("running","0");
            await CopyAsync(c,tx,$"""
              INSERT OR REPLACE INTO v8_legacy_jobs(legacy_id,name,group_id,script_id,mapped_state,step_cursor,recovery_needed,inflight_cursor,snapshot_id)
              SELECT id,name,group_id,script_id,
                CASE
                  WHEN {recovery}=1 OR {inflight}>=0 THEN 'RecoveryRequired'
                  WHEN {paused}=1 OR {running}=1 THEN 'Paused'
                  ELSE 'Stopped'
                END,
                step_cursor,{recovery},{inflight},{snapshot}
              FROM group_jobs;
              """,ct);

            var checks=new[]{
                ("accounts","v8_accounts"),("groups","v8_groups"),("scripts","v8_scripts"),
                ("script_steps","v8_script_steps"),("group_jobs","v8_legacy_jobs")
            };
            foreach(var (source,target) in checks)
            {
                var src=await CountAsyncTx(c,tx,source,ct);
                var dst=await CountAsyncTx(c,tx,target,ct);
                if(src!=dst) throw new InvalidOperationException($"Migration count mismatch: {source}={src}, {target}={dst}");
            }

            await using var marker=c.CreateCommand(); marker.Transaction=tx;
            marker.CommandText="""
              INSERT INTO v8_schema(key,value) VALUES('v7_metadata_migration','complete-v1')
              ON CONFLICT(key) DO UPDATE SET value=excluded.value;
              """;
            await marker.ExecuteNonQueryAsync(ct);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    static async Task<HashSet<string>> ColumnsAsync(SqliteConnection c,SqliteTransaction tx,string table,CancellationToken ct)
    {
        var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=$"PRAGMA table_info({table})";
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) set.Add(r.GetString(1));
        return set;
    }

    static async Task CopyAsync(SqliteConnection c,SqliteTransaction tx,string sql,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=sql; await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<int> CountAsyncTx(SqliteConnection c,SqliteTransaction tx,string table,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=$"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)??0);
    }
}
