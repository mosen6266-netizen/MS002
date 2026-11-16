using Microsoft.Data.Sqlite;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// Read all locally known account labels, including inactive accounts.
    /// Keeping inactive identities in the label catalog lets an older script
    /// referencing one of them resolve its duplicate-label replacement when
    /// the replacement is a current member of the selected group.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string,string>> ReadSenderLabelsAsync(
        SqliteConnection connection,SqliteTransaction? transaction,CancellationToken ct)
    {
        var labels=new Dictionary<string,string>(StringComparer.Ordinal);
        await using var query=connection.CreateCommand();
        query.Transaction=transaction;
        query.CommandText="""
            SELECT a.account, COALESCE(NULLIF(pref.label,''),NULLIF(a.label,''),a.account)
            FROM v8_signal_accounts a
            LEFT JOIN v8_account_settings pref ON pref.account=a.account;
            """;
        await using var reader=await query.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            labels[reader.GetString(0)]=reader.GetString(1);
        return labels;
    }
}
