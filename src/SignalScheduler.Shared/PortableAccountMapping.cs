namespace SignalScheduler.Shared;

/// <summary>
/// A script archive contains only human-defined account remarks; never Signal
/// account identifiers. Resolve them to local account IDs ONLY on the importing
/// machine. Missing or ambiguous labels fail closed before any scripts are saved.
/// </summary>
public static class PortableAccountMapping
{
    public static ScriptSaveRequest[] ToRemarks(
        IReadOnlyList<ScriptSaveRequest> scripts,
        IReadOnlyList<ManagedAccount> accounts)
    {
        var byId=accounts.ToDictionary(x=>x.Account,StringComparer.Ordinal);
        var used=scripts.SelectMany(x=>x.Steps).Select(x=>x.Account)
            .Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        var names=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var id in used)
        {
            if(!byId.TryGetValue(id,out var entry) ||
               string.IsNullOrWhiteSpace(entry.Label) ||
               entry.Label.Trim()==id)
                throw new InvalidDataException("存在没有设置独立备注的发言账号，请先在账号管理中设置备注。");
            var label=entry.Label.Trim();
            // Duplicate remarks are allowed in portable scripts. Every target
            // group will use only the eligible member with the same remark.
            names[id]=label;
        }
        return scripts.Select(x=>x with{
            ScriptId=null,Revision=0,TargetGroupId="",
            Steps=x.Steps.Select(step=>step with{
                Account=string.IsNullOrWhiteSpace(step.Account)?"":names[step.Account]
            }).ToArray()
        }).ToArray();
    }

    public static ScriptSaveRequest[] ResolveRemarks(
        IReadOnlyList<ScriptSaveRequest> scripts,
        IReadOnlyList<ManagedAccount> accounts)
    {
        var required=scripts.SelectMany(x=>x.Steps).Select(x=>x.Account)
            .Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        var resolved=new Dictionary<string,string>(StringComparer.Ordinal);
        var missing=new List<string>();
        foreach(var label in required)
        {
            var matching=accounts.Where(x=>x.Label.Trim()==label &&
                x.Account!=x.Label.Trim())
                .OrderBy(x=>x.Account,StringComparer.Ordinal).ToArray();
            if(matching.Length==0)missing.Add(label);
            else
                // Deterministic representative ID for the editable script.
                // Before a job starts, each group resolves this remark against
                // its own online, enabled Signal members; never guess at send time.
                resolved[label]=matching[0].Account;
        }
        if(missing.Count>0)
            throw new InvalidDataException(
                "账号备注匹配失败。缺少："+string.Join("、",missing)+
                "。请在本机设置对应备注后重新导入。没有导入任何剧本。");
        return scripts.Select(x=>x with{
            ScriptId=null,Revision=0,TargetGroupId="",
            Steps=x.Steps.Select(step=>step with{
                Account=string.IsNullOrWhiteSpace(step.Account)?"":resolved[step.Account]
            }).ToArray()
        }).ToArray();
    }
}
