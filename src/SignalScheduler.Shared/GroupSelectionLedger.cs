namespace SignalScheduler.Shared;

/// <summary>
/// User-controlled selection for the current desktop session. It is separate
/// from virtualized WPF rows and is never mutated by starting a script.
/// </summary>
public sealed class GroupSelectionLedger
{
    readonly HashSet<string> _ids=new(StringComparer.Ordinal);
    public int Count=>_ids.Count;
    public IReadOnlyCollection<string> SelectedIds=>_ids.ToArray();
    public bool Contains(string id)=>_ids.Contains(id);

    public bool Set(string id,bool selected,int max=20)
    {
        if(string.IsNullOrWhiteSpace(id))return false;
        if(!selected){_ids.Remove(id);return true;}
        if(_ids.Contains(id))return true;
        if(_ids.Count>=max)return false;
        _ids.Add(id);
        return true;
    }

    public void Clear()=>_ids.Clear();
    public void RetainAvailable(IEnumerable<string> ids)=>
        _ids.IntersectWith(ids.ToHashSet(StringComparer.Ordinal));
}
