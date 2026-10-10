namespace SignalScheduler.Shared;

/// <summary>
/// Resolve a portable script role into the actual sender for ONE selected group.
/// Never substitute an unrelated account: a remark must match exactly and
/// only one enabled, online Signal member may match if the saved ID is absent.
/// The resulting concrete ID is frozen into each group's durable task snapshot.
/// </summary>
public static class GroupAccountResolver
{
    public static string Resolve(
        string? requestedAccount,
        IReadOnlyDictionary<string,string> knownRemarks,
        IReadOnlyCollection<string> eligibleAccounts)
    {
        if(eligibleAccounts.Count==0)
            throw new InvalidOperationException("该群组没有已加入且在线、已启用的账号。");

        var requested=requestedAccount?.Trim()??"";
        if(requested.Length==0)
            return eligibleAccounts.OrderBy(x=>x,StringComparer.Ordinal).First();

        // Prefer the originally selected account, but only if actually a member.
        if(eligibleAccounts.Contains(requested))
            return requested;

        if(!knownRemarks.TryGetValue(requested,out var remark) ||
           string.IsNullOrWhiteSpace(remark))
            throw new InvalidOperationException("剧本指定的账号未找到本机备注，无法匹配该群组。");

        var matches=eligibleAccounts.Where(account=>
            knownRemarks.TryGetValue(account,out var label) &&
            string.Equals(label?.Trim(),remark.Trim(),StringComparison.Ordinal))
            .OrderBy(x=>x,StringComparer.Ordinal).ToArray();

        return matches.Length switch
        {
            1=>matches[0],
            0=>throw new InvalidOperationException(
                "群组内没有备注「"+remark+"」对应的在线已启用账号。"),
            _=>throw new InvalidOperationException(
                "群组内有多个同名备注「"+remark+"」的可用账号，无法安全选择，请调整备注。")
        };
    }
}
