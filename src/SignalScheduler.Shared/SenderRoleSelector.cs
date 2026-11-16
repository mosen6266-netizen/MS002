namespace SignalScheduler.Shared;

/// <summary>
/// Script sender selection is resolved separately for each selected Signal
/// group. Human labels identify a ROLE; dispatch always uses the actual
/// account identifier (never the label) and freezes that mapping at Start.
/// </summary>
public static class SenderRoleSelector
{
    public const string Prefix="role:";

    public static string ForLabel(string label)
    {
        if(string.IsNullOrWhiteSpace(label) || label.Trim().Length>80)
            throw new ArgumentException("发言角色备注不能为空或超过80字。");
        return Prefix+label.Trim();
    }

    public static bool TryGetLabel(string? sender,out string label)
    {
        label="";
        if(sender is null || !sender.StartsWith(Prefix,StringComparison.Ordinal))
            return false;
        label=sender[Prefix.Length..];
        return !string.IsNullOrWhiteSpace(label) &&
            label.Length<=80 && label==label.Trim();
    }

    /// <summary>
    /// Eligible accounts must already be online, enabled AND group members;
    /// order is stable (SQL account ascending). A legacy explicitly selected
    /// phone may fall back ONLY when another known phone shares its exact
    /// user remark. The selection is never changed after job creation.
    /// </summary>
    public static string? Resolve(string? selected,
        IReadOnlyList<string> eligibleOrdered,
        IReadOnlyDictionary<string,string> labels)
    {
        if(eligibleOrdered.Count==0)return null;
        if(string.IsNullOrWhiteSpace(selected))
            return eligibleOrdered[0];

        if(TryGetLabel(selected,out var role))
            return eligibleOrdered.FirstOrDefault(account=>
                labels.TryGetValue(account,out var label) &&
                string.Equals(label,role,StringComparison.Ordinal));

        var accountId=selected.Trim();
        if(eligibleOrdered.Contains(accountId,StringComparer.Ordinal))
            return accountId;

        // A legacy script may have stored A's account ID before a second
        // account with the same remark was linked. Restrict replacement to
        // genuinely duplicated labels; otherwise preserve strict pinning.
        if(!labels.TryGetValue(accountId,out var originalLabel) ||
            string.IsNullOrWhiteSpace(originalLabel) ||
            labels.Count(p=>string.Equals(
                p.Value,originalLabel,StringComparison.Ordinal))<2)
            return null;

        return eligibleOrdered.FirstOrDefault(candidate=>
            labels.TryGetValue(candidate,out var otherLabel) &&
            string.Equals(otherLabel,originalLabel,StringComparison.Ordinal));
    }
}
