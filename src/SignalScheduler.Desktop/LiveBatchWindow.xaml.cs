using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Data;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class LiveBatchWindow : UserControl
{
    sealed record ReadOptionPreferences(bool ReadReceipts=true,bool LinkedDeviceSync=true);
    static readonly string ReadOptionsPath=Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SignalSchedulerData","read-options.json");
    // WPF CheckBox Checked/Unchecked can fire inside InitializeComponent,
    // before both controls and persisted preferences have been restored.
    // Never persist these intermediate/default checkbox states.
    bool _loadingReadOptions=true;

    void RestoreReadOptions()
    {
        _loadingReadOptions=true;
        try
        {
            ReadOptionPreferences? options=null;
            if(File.Exists(ReadOptionsPath))
                options=JsonSerializer.Deserialize<ReadOptionPreferences>(
                    File.ReadAllText(ReadOptionsPath));
            ReadReceiptToggle.IsChecked=options?.ReadReceipts??true;
            LinkedReadSyncToggle.IsChecked=options?.LinkedDeviceSync??true;
        }
        catch
        {
            ReadReceiptToggle.IsChecked=true;
            LinkedReadSyncToggle.IsChecked=true;
        }
        finally{_loadingReadOptions=false;}
    }

    async void CheckReadHealth_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            ReadHealthLabel.Text="正在检查…";
            var raw=await MainWindow.SendAsync(ControlCommands.ReadHealth,10000);
            var health=Unwrap<ReadHealthSnapshot>(raw);
            var last=health.LastEventMs>0
                ?DateTimeOffset.FromUnixTimeMilliseconds(health.LastEventMs)
                    .ToLocalTime().ToString("MM-dd HH:mm:ss")
                :"没有捕获到群消息";
            var lastFailure=health.LastFailureMs>0
                ?DateTimeOffset.FromUnixTimeMilliseconds(health.LastFailureMs)
                    .ToLocalTime().ToString("MM-dd HH:mm:ss")
                :"没有记录";
            ReadHealthLabel.Text=$"监听：{health.StreamState} · 最近消息：{last} · "+
                $"待处理：{health.Pending}（等待重试 {health.WaitingRetry}） · "+
                $"回执被接口接受：{health.Attempted} · 尝试失败：{health.Failed} · "+
                $"结果未知：{health.Unknown} · 上次监听故障：{lastFailure}"+
                (string.IsNullOrWhiteSpace(health.LastFailureType)?""
                    :$"（{health.LastFailureType}）")+
                "。只在轮到相应账号发言时发送回执，不会提前将所有账号已读。";
        }
        catch(Exception ex){ReadHealthLabel.Text="已读状态检查失败："+ex.Message;}
    }

    void ReadOptionsChanged(object sender,RoutedEventArgs e)
    {
        if(_loadingReadOptions || ReadReceiptToggle is null ||
           LinkedReadSyncToggle is null)return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReadOptionsPath)!);
            File.WriteAllText(ReadOptionsPath,JsonSerializer.Serialize(
                new ReadOptionPreferences(
                    ReadReceiptToggle.IsChecked==true,
                    LinkedReadSyncToggle.IsChecked==true)));
        }
        catch(Exception ex)
        {
            if(StatusText is not null)
                StatusText.Text="无法保存已读选项："+ex.Message;
        }
    }

    public bool IsScriptDropDownOpen=>ScriptBox?.IsDropDownOpen==true;
    const int GroupsPerPage=10;
    int _groupPage;
    ICollectionView? _groupsView;
    readonly HashSet<string> _visibleGroupIds=new(StringComparer.Ordinal);
    void GroupSearchBox_TextChanged(object sender,TextChangedEventArgs e)
    {
        _groupPage=0;
        RefreshGroupPage();
    }
    void RefreshGroupPage()
    {
        if(GroupsGrid is null)return;
        var term=GroupSearchBox?.Text.Trim()??"";
        var matches=_groups.Where(g=>term.Length==0 ||
            g.Name.Contains(term,StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var pages=Math.Max(1,(matches.Length+GroupsPerPage-1)/GroupsPerPage);
        _groupPage=Math.Clamp(_groupPage,0,pages-1);
        _visibleGroupIds.Clear();
        foreach(var row in matches.Skip(_groupPage*GroupsPerPage).Take(GroupsPerPage))
            _visibleGroupIds.Add(row.GroupId);
        _groupsView?.Refresh();
        if(GroupsPageLabel is not null)
            GroupsPageLabel.Text=$"第 {_groupPage+1} / {pages} 页 · 找到 {matches.Length} / {_groups.Count} 个群";
        if(PreviousGroupsPage is not null)PreviousGroupsPage.IsEnabled=_groupPage>0;
        if(NextGroupsPage is not null)NextGroupsPage.IsEnabled=_groupPage<pages-1;
    }
    void PreviousGroupsPage_Click(object sender,RoutedEventArgs e)
    {
        _groupPage--;
        RefreshGroupPage();
    }
    void NextGroupsPage_Click(object sender,RoutedEventArgs e)
    {
        _groupPage++;
        RefreshGroupPage();
    }

    // The shell caches this page, but WPF reloads catalog rows on Loaded.
    // Persist group choices independently of rows even if the page is recreated.
    static readonly GroupSelectionLedger SessionGroups=new();
    bool _rebuildingGroups;
    readonly ObservableCollection<BatchGroupRow> _groups=new();
    readonly ObservableCollection<BatchGroupTag> _selectedTags=new();
    bool _busy;

    public LiveBatchWindow()
    {
        InitializeComponent();
        RestoreReadOptions();
        _groupsView=CollectionViewSource.GetDefaultView(_groups);
        _groupsView.Filter=o=>o is BatchGroupRow row &&
            _visibleGroupIds.Contains(row.GroupId);
        GroupsGrid.ItemsSource=_groupsView;
        SelectedGroupTags.ItemsSource=_selectedTags;
        Loaded+=async(_,_)=>await LoadCatalogAsync();
        SizeChanged+=(_,_)=> {
            if(GroupsGrid is null)return;
            var compact=ActualHeight<650;
            GroupsGrid.RowHeight=compact?22:29;
            GroupsGrid.ColumnHeaderHeight=compact?28:31;
        };
        UpdateButtons();
    }

    static T Unwrap<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("后台未响应，请确认任务状态后再操作。");
        using var doc=JsonDocument.Parse(raw);
        var result=doc.RootElement;
        if(!result.TryGetProperty("Ok",out var success) || !success.GetBoolean())
            throw new IOException(result.TryGetProperty("Error",out var error)
                ?error.GetString():"后台拒绝了请求。");
        return JsonSerializer.Deserialize<T>(result.GetProperty("Data").GetRawText())
            ??throw new IOException("后台返回的任务数据不完整。");
    }

    async Task LoadCatalogAsync()
    {
        try
        {
            var results=await Task.WhenAll(
                MainWindow.SendAsync(ControlCommands.ScriptList,8000),
                MainWindow.SendAsync(ControlCommands.AccountGroupCatalog,8000));
            var scripts=Unwrap<List<ScriptEditorSummary>>(results[0]);
            var overview=Unwrap<AccountGroupOverview>(results[1]);
            var count=overview.Accounts.Count;
            var healthy=overview.Accounts.Count(x=>x.Enabled&&x.Online);
            var offline=count-healthy;
            AccountsSummary.Text=$"共 {count} 个账号 · 正常 {healthy} 个"+
                (offline>0?$" · 需处理 {offline} 个":" · 全部正常");
            AccountsSummary.Foreground=offline==0
                ?System.Windows.Media.Brushes.LightGreen
                :System.Windows.Media.Brushes.Gold;
            // Preserve a selection made while the catalog was loading.
            var old=(ScriptBox.SelectedItem as ScriptEditorSummary)?.ScriptId;
            ScriptBox.ItemsSource=scripts;
            ScriptBox.SelectedItem=scripts.FirstOrDefault(x=>x.ScriptId==old)
                ??scripts.FirstOrDefault();
            // Rebuild only the displayed rows; never use the WPF row list
            // as the authoritative selection during an asynchronous refresh.
            _rebuildingGroups=true;
            try
            {
                var available=overview.Groups.Where(g=>g.MemberAccounts>0).ToArray();
                if(available.Length>0)
                    SessionGroups.RetainAvailable(available.Select(g=>g.GroupId));
                _groups.Clear();
                foreach(var group in available)
                {
                    var row=new BatchGroupRow(group);
                    row.Selected=SessionGroups.Contains(row.GroupId);
                    row.PropertyChanged+=(_,e)=>
                    {
                        if(_rebuildingGroups ||
                           e.PropertyName!=nameof(BatchGroupRow.Selected))return;
                        if(!SessionGroups.Set(row.GroupId,row.Selected))
                        {
                            row.Selected=false;
                            StatusText.Text="最多只能选择 20 个群组，请先取消其他群组。";
                            return;
                        }
                        RefreshSelectedTags();
                    };
                    _groups.Add(row);
                }
            }
            finally{_rebuildingGroups=false;}
            RefreshSelectedTags();
            RefreshGroupPage();
            StatusText.Text=$"已读取 {scripts.Count} 个剧本与 {_groups.Count} 个 Signal 群。"+
                "直接勾选本次群组即可，草稿空白气泡不会发送。";
        }
        catch(Exception ex){StatusText.Text=$"刷新目录失败：{ex.Message}";}
        UpdateButtons();
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)
    {
        await LoadCatalogAsync();
        if(Window.GetWindow(this) is MainWindow shell)
            await shell.RefreshTaskBadgeAsync();
    }

    void SelectAll_Click(object sender,RoutedEventArgs e)
    {
        // Respect the current search filter. Keep already selected items and
        // cap selection at twenty, even when groups span several pages.
        var query=GroupSearchBox.Text.Trim();
        foreach(var row in _groups.Where(x=>query.Length==0 ||
            x.Name.Contains(query,StringComparison.CurrentCultureIgnoreCase)))
        {
            if(_groups.Count(x=>x.Selected)>=20)break;
            row.Selected=true;
        }
        RefreshSelectedTags();
    }

    void ClearAll_Click(object sender,RoutedEventArgs e)
    {
        SessionGroups.Clear();
        foreach(var group in _groups)group.Selected=false;
        RefreshSelectedTags();
    }

    void ToggleGroupName_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button {Tag:string groupId})return;
        var row=_groups.FirstOrDefault(x=>x.GroupId==groupId);
        if(row is null)return;
        if(!row.Selected && SessionGroups.Count>=20)
        {
            StatusText.Text="最多只能选择 20 个群组，请先取消其他群组。";
            return;
        }
        row.Selected=!row.Selected;
    }

    void RefreshSelectedTags()
    {
        if(SelectedGroupTags is null)return;
        _selectedTags.Clear();
        foreach(var item in _groups.Where(x=>SessionGroups.Contains(x.GroupId)))
            _selectedTags.Add(new BatchGroupTag(item.GroupId,item.Name));
        SelectedGroupCount.Text=$"已选 {_selectedTags.Count} / 20 个";
        UpdateButtons();
    }

    void RemoveGroupTag_Click(object sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:string id})
        {
            var row=_groups.FirstOrDefault(x=>x.GroupId==id);
            if(row is not null)row.Selected=false;
            RefreshSelectedTags();
        }
    }

    void AccountsOpen_Click(object sender,RoutedEventArgs e)=>
        (Window.GetWindow(this) as MainWindow)?.NavigateTo("accounts");

    void ScriptBox_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateButtons();
    void GroupsGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateButtons();

    void UpdateButtons()
    {
        if(StartButton is null)return;
        StartButton.IsEnabled=!_busy &&
            ScriptBox.SelectedItem is ScriptEditorSummary &&
            SessionGroups.Count is >=1 and <=20;
        if(PreflightButton is not null)
            PreflightButton.IsEnabled=StartButton.IsEnabled;

    }

    async Task<LiveBatchPreflightResult> ReadPreflightAsync(
        string scriptId,string[] groupIds)
    {
        var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchPreflight,
            30000,new LiveBatchPreflightRequest(scriptId,groupIds));
        return Unwrap<LiveBatchPreflightResult>(raw);
    }

    static string FormatPreflight(LiveBatchPreflightResult result)
    {
        var details=result.Issues.Take(20)
            .Select(i=>$"【{i.Level}】{i.Message}").ToArray();
        return $"剧本：{result.ScriptName}\n群组：{result.SelectedGroups} 个"+
               $"\n可用消息：约 {result.SendableRows} 条"+
               $"\n运行条件：{(result.CanStart?"可启动":"存在阻止启动的问题")}"+
               (details.Length==0?"\n所有已检查条件通过。":
                   "\n\n"+string.Join("\n",details))+
               (result.Issues.Count>details.Length
                   ?$"\n另有 {result.Issues.Count-details.Length} 条，请逐项处理。":"")+
               "\n\n此检查不会发送消息；实际启动前仍会重新验证。";
    }

    async void Preflight_Click(object sender,RoutedEventArgs e)
    {
        if(_busy || ScriptBox.SelectedItem is not ScriptEditorSummary script)return;
        GroupsGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        GroupsGrid.CommitEdit(DataGridEditingUnit.Row,true);
        var ids=_groups.Where(x=>SessionGroups.Contains(x.GroupId))
            .Select(x=>x.GroupId).ToArray();
        if(ids.Length is <1 or >20)
        {
            StatusText.Text="请先选择 1～20 个群组。";
            return;
        }
        _busy=true;
        UpdateButtons();
        try
        {
            StatusText.Text="正在检查运行条件，此过程不会发送消息…";
            var result=await ReadPreflightAsync(script.ScriptId,ids);
            StatusText.Text=result.CanStart
                ?$"运行条件检查通过，含 {result.Issues.Count} 条提醒。"
                :$"运行条件检查发现 {result.Issues.Count(x=>x.Level=="错误")} 个阻止启动的问题。";
            MessageBox.Show(Window.GetWindow(this),FormatPreflight(result),
                "运行前健康检查",MessageBoxButton.OK,
                result.CanStart?MessageBoxImage.Information:MessageBoxImage.Warning);
        }
        catch(Exception ex)
        {
            StatusText.Text="运行前检查失败："+ex.Message;
        }
        finally
        {
            _busy=false;
            UpdateButtons();
        }
    }

    async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(_busy || ScriptBox.SelectedItem is not ScriptEditorSummary script)return;
        GroupsGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        GroupsGrid.CommitEdit(DataGridEditingUnit.Row,true);
        var ids=_groups.Where(x=>SessionGroups.Contains(x.GroupId))
            .Select(x=>x.GroupId).ToArray();
        if(ids.Length is <1 or >20)
        {
            StatusText.Text="一次请勾选 1～20 个群组。";
            return;
        }
        // Lock before the first await to prevent overlapping real-send starts.
        _busy=true;
        UpdateButtons();
        try
        {
            var readiness=await ReadPreflightAsync(script.ScriptId,ids);
            if(!readiness.CanStart)
            {
                StatusText.Text="启动已阻止，请先解决运行条件检查中的错误。";
                MessageBox.Show(Window.GetWindow(this),FormatPreflight(readiness),
                    "无法启动任务",MessageBoxButton.OK,MessageBoxImage.Warning);
                return;
            }
            var inspection=await MainWindow.SendAsync(ControlCommands.LiveBatchInspect,
                30000,new LiveBatchMediaInspectionRequest(script.ScriptId));
            var media=Unwrap<LiveBatchMediaInspection>(inspection);
            var mediaProblems=media.Issues.Count>0;
            var warning=mediaProblems
                ?"\n\n检测到旧图片无法使用：\n"+
                 string.Join("\n",media.Issues.Take(8)
                    .Select(x=>$"第 {x.Position} 条：{x.Action}"))+
                 (media.Issues.Count>8?$"\n另有 {media.Issues.Count-8} 条":"")+
                 "\n\n继续运行将按上述方式忽略失效图片，但不修改原剧本。"
                :"";
            var preview=$"剧本：{script.Name}\n"+
                $"实际可发送：{media.SendableRows} 条 / 原有 {media.TotalRows} 条\n"+
                $"选中群组：{ids.Length} 个\n"+
                "这些内容将真实发送到选定群组，确定开始？"+warning;
            if(MessageBox.Show(Window.GetWindow(this),preview,
                mediaProblems?"确认旧图片缺失处理与真实发送":"确认真实发送",
                MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
                return;
            var selectedNow=_groups.Where(x=>SessionGroups.Contains(x.GroupId))
                .Select(x=>x.GroupId).ToArray();
            if((ScriptBox.SelectedItem as ScriptEditorSummary)?.ScriptId!=script.ScriptId ||
               !ids.SequenceEqual(selectedNow))
            {
                StatusText.Text="确认期间剧本或群组已变化，本次启动取消；请重新检查。";
                return;
            }
            // Final authorization, dispatch and duplicate checks are in the engine.
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchStart,
                60000,new LiveBatchStartRequest(script.ScriptId,ids,true,mediaProblems));
            var started=Unwrap<LiveBatchStartResult>(raw);
            // Intentionally retain SessionGroups: clicking Start does not clear
            // the user's checked target groups.
            StatusText.Text=$"已启动 {started.GroupCount} 个群组任务，每群 "+
                $"{started.MessageCount} 条。请在左侧「当前运行任务」页面查看和管理。";
            if(Window.GetWindow(this) is MainWindow shell)
                await shell.RefreshTaskBadgeAsync();
        }
        catch(Exception ex)
        {
            StatusText.Text=$"启动失败或结果尚未确认：{ex.Message}。"+
                "请先检查运行任务和恢复中心，勿重复点击启动。";
        }
        finally{_busy=false;UpdateButtons();}
    }


}

public sealed class BatchJobRow : INotifyPropertyChanged
{
    public BatchJobRow(LiveBatchItem item)
    {
        JobId=item.JobId;
        _snapshot=item;
        Update(item);
    }

    public string JobId {get;}
    public string ScriptName {get;private set;}="";
    public string GroupName {get;private set;}="";
    public string State {get;private set;}="";
    public long Cursor {get;private set;}
    public int TotalSteps {get;private set;}
    public string Detail {get;private set;}="";
    public string Phase {get;private set;}="—";
    public string NextSend {get;private set;}="—";
    public string NextCountdown {get;private set;}="—";
    public string RemainingEstimate {get;private set;}="—";
    public string EarliestFinish {get;private set;}="—";
    LiveBatchItem _snapshot;
    public LiveBatchItem Snapshot=>_snapshot;
    long _estimateSampleMs;

    public void RefreshCountdown()
    {
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timing=LiveBatchTiming.Format(_snapshot,_estimateSampleMs,now);
        if(Phase!=timing.Phase)
        {
            Phase=timing.Phase;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Phase)));
        }
        if(NextCountdown!=timing.NextCountdown)
        {
            NextCountdown=timing.NextCountdown;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(NextCountdown)));
        }
        if(RemainingEstimate!=timing.RemainingEstimate)
        {
            RemainingEstimate=timing.RemainingEstimate;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(RemainingEstimate)));
        }
        if(EarliestFinish!=timing.EarliestFinish)
        {
            EarliestFinish=timing.EarliestFinish;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(EarliestFinish)));
        }
        if(NextSend!=timing.NextSend)
        {
            NextSend=timing.NextSend;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(NextSend)));
        }
    }

    public string Progress=>$"{Cursor}/{TotalSteps}";
    public string StateDisplay=>StatusLabels.Task(State);
    public bool CanPause=>State=="Running";
    public bool CanResume=>State=="Paused" && Cursor<TotalSteps;
    public bool CanStop=>State is "Running" or "Paused" or "RecoveryRequired" or "WaitingSignal";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(LiveBatchItem item)
    {
        // Store the new state BEFORE calculating timing. Otherwise a newly
        // resumed or paused job can show the previous timer for one UI tick.
        var changed=ScriptName!=item.ScriptName ||
            GroupName!=item.GroupName || State!=item.State ||
            Cursor!=item.Cursor || TotalSteps!=item.TotalSteps ||
            Detail!=item.Detail;
        _snapshot=item;
        _estimateSampleMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ScriptName=item.ScriptName;
        GroupName=item.GroupName;
        State=item.State;
        Cursor=item.Cursor;
        TotalSteps=item.TotalSteps;
        Detail=item.Detail;
        RefreshCountdown();
        if(!changed)return;
        foreach(var name in new[]{
            nameof(ScriptName),nameof(GroupName),nameof(State),
            nameof(StateDisplay),nameof(Cursor),nameof(TotalSteps),
            nameof(Progress),nameof(Detail),
            nameof(CanPause),nameof(CanResume),nameof(CanStop)})
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    }
}

public sealed record BatchGroupTag(string GroupId,string Name);

public sealed class BatchGroupRow : INotifyPropertyChanged
{
    // Require the operator to explicitly pick target groups for each real run.
    bool _selected=false;
    public BatchGroupRow(ManagedGroup group)
    {
        GroupId=group.GroupId;
        Name=group.Name;
        MemberAccounts=group.MemberAccounts;
    }
    public string GroupId {get;}
    public string Name {get;}
    public int MemberAccounts {get;}
    public bool Selected
    {
        get=>_selected;
        set
        {
            if(_selected==value)return;
            _selected=value;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
