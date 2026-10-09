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
                "。以上均不代表其他设备未读数已清零。";
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

    readonly ObservableCollection<BatchGroupRow> _groups=new();
    readonly ObservableCollection<BatchJobRow> _jobs=new();
    const int JobsPerPage=10;
    int _jobsPage;
    ICollectionView? _jobsView;
    void RefreshJobsPage()
    {
        if(JobsGrid is null)return;
        var pages=Math.Max(1,(_jobs.Count+JobsPerPage-1)/JobsPerPage);
        _jobsPage=Math.Clamp(_jobsPage,0,pages-1);
        _jobsView?.Refresh();
        if(JobsPageLabel is not null)
            JobsPageLabel.Text=$"第 {_jobsPage+1} / {pages} 页 · 共 {_jobs.Count} 条";
        if(PreviousJobsPage is not null)PreviousJobsPage.IsEnabled=_jobsPage>0;
        if(NextJobsPage is not null)NextJobsPage.IsEnabled=_jobsPage<pages-1;
    }
    void PreviousJobsPage_Click(object sender,RoutedEventArgs e)
    {_jobsPage--;RefreshJobsPage();}
    void NextJobsPage_Click(object sender,RoutedEventArgs e)
    {_jobsPage++;RefreshJobsPage();}

    readonly ObservableCollection<BatchGroupTag> _selectedTags=new();
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly DispatcherTimer _countdownTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly HashSet<string> _alerted=new(StringComparer.Ordinal);
    bool _busy;
    bool _refreshing;

    public LiveBatchWindow()
    {
        InitializeComponent();
        RestoreReadOptions();
        _groupsView=CollectionViewSource.GetDefaultView(_groups);
        _groupsView.Filter=o=>o is BatchGroupRow row &&
            _visibleGroupIds.Contains(row.GroupId);
        GroupsGrid.ItemsSource=_groupsView;
        _jobsView=CollectionViewSource.GetDefaultView(_jobs);
        _jobsView.Filter=o=>o is BatchJobRow row &&
            _jobs.IndexOf(row)/JobsPerPage==_jobsPage;
        JobsGrid.ItemsSource=_jobsView;
        SelectedGroupTags.ItemsSource=_selectedTags;
        Loaded+=async(_,_)=>{
            await LoadCatalogAsync();
            await LoadJobsAsync();
            _timer.Start();
            _countdownTimer.Start();
        };
        _timer.Tick+=async(_,_)=>{await LoadJobsAsync();foreach(var job in _jobs)job.RefreshCountdown();};
        Unloaded+=(_,_)=>{_timer.Stop();_countdownTimer.Stop();};
        _countdownTimer.Tick+=(_,_)=>{foreach(var job in _jobs)job.RefreshCountdown();};
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
            // Preserve in-progress group selections when refreshing the catalog.
            var selectedBeforeRefresh=_groups.Where(x=>x.Selected)
                .Select(x=>x.GroupId).ToHashSet(StringComparer.Ordinal);
            _groups.Clear();
            foreach(var group in overview.Groups.Where(g=>g.MemberAccounts>0))
            {
                var row=new BatchGroupRow(group);
                if(selectedBeforeRefresh.Contains(row.GroupId))
                    row.Selected=true;
                row.PropertyChanged+=(_,e)=>
                {
                    if(e.PropertyName==nameof(BatchGroupRow.Selected))
                        RefreshSelectedTags();
                };
                _groups.Add(row);
            }
            RefreshSelectedTags();
            RefreshGroupPage();
            StatusText.Text=$"已读取 {scripts.Count} 个剧本与 {_groups.Count} 个 Signal 群。"+
                "直接勾选本次群组即可，草稿空白气泡不会发送。";
        }
        catch(Exception ex){StatusText.Text=$"刷新目录失败：{ex.Message}";}
        UpdateButtons();
    }

    async Task LoadJobsAsync()
    {
        if(_refreshing)return;
        _refreshing=true;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchList,10000);
            var jobs=Unwrap<List<LiveBatchItem>>(raw);
            // Update existing observable rows without clearing the DataGrid.
            // This keeps scrolling, keyboard focus and per-job buttons stable
            // across the 2-second status refresh.
            var keys=new HashSet<string>(StringComparer.Ordinal);
            var byId=_jobs.ToDictionary(x=>x.JobId,StringComparer.Ordinal);
            var position=0;
            foreach(var item in jobs)
            {
                keys.Add(item.JobId);
                byId.TryGetValue(item.JobId,out var existing);
                if(existing is null)
                {
                    existing=new BatchJobRow(item);
                    _jobs.Insert(Math.Min(position,_jobs.Count),existing);
                }
                else
                {
                    existing.Update(item);
                    var oldPosition=_jobs.IndexOf(existing);
                    if(oldPosition!=position)_jobs.Move(oldPosition,position);
                }
                position++;
                if(item.State=="RecoveryRequired" ||
                   (item.State=="Paused" &&
                    (item.Detail.Contains("提醒",StringComparison.Ordinal) ||
                     item.Detail.Contains("异常",StringComparison.Ordinal) ||
                     item.Detail.Contains("断开",StringComparison.Ordinal) ||
                     item.Detail.Contains("授权",StringComparison.Ordinal) ||
                     item.Detail.Contains("失败",StringComparison.Ordinal))))
                {
                    var key=$"{item.JobId}:{item.State}:{item.Cursor}";
                    if(_alerted.Add(key))
                        MainWindow.ShowCriticalAlert(
                            $"剧本任务已中断，需要人工处理。\n\n"+
                            $"剧本：{item.ScriptName}\n群：{item.GroupName}\n"+
                            $"进度：{item.Cursor}/{item.TotalSteps}\n"+
                            $"状态：{item.State}\n详情：{item.Detail}\n\n"+
                            "如发送结果未知，请在恢复中心核对后再操作。");
                }
            }
            for(var i=_jobs.Count-1;i>=0;i--)
                if(!keys.Contains(_jobs[i].JobId))_jobs.RemoveAt(i);
            RefreshJobsPage();
            UpdateButtons();
        }
        catch(Exception ex){StatusText.Text=$"刷新运行任务失败：{ex.Message}";}
        finally{_refreshing=false;}
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)
    {
        await LoadCatalogAsync();
        await LoadJobsAsync();
    }

    void SelectAll_Click(object sender,RoutedEventArgs e)
    {
        // Do not silently select more groups than the configured per-run limit.
        foreach(var (group,index) in _groups.Select((value,index)=>(value,index)))
            group.Selected=index<20;
        RefreshSelectedTags();
    }

    void ClearAll_Click(object sender,RoutedEventArgs e)
    {
        foreach(var group in _groups) group.Selected=false;
        RefreshSelectedTags();
    }

    void RefreshSelectedTags()
    {
        if(SelectedGroupTags is null)return;
        _selectedTags.Clear();
        foreach(var item in _groups.Where(x=>x.Selected))
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

    void JobsGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateButtons();

    void UpdateButtons()
    {
        if(StartButton is null)return;
        StartButton.IsEnabled=!_busy &&
            ScriptBox.SelectedItem is ScriptEditorSummary &&
            _groups.Any(x=>x.Selected) && _groups.Count(x=>x.Selected)<=20;
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
        var ids=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
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
        var ids=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
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
            var selectedNow=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
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
            StatusText.Text=$"已启动 {started.GroupCount} 个真实群组任务，"+
                $"每群 {started.MessageCount} 条。可在下表逐群暂停、继续或停止。";
            await LoadJobsAsync();
        }
        catch(Exception ex)
        {
            StatusText.Text=$"启动失败或结果尚未确认：{ex.Message}。"+
                "请先检查运行任务和恢复中心，勿重复点击启动。";
        }
        finally{_busy=false;UpdateButtons();}
    }

    async Task ControlAsync(string action,BatchJobRow? selected)
    {
        if(_busy || selected is null)return;
        _busy=true;UpdateButtons();
        try
        {
        if(action is "resume" or "stop")
        {
            var msg=action=="resume"
                ?"将从已保存的下一条继续真实发送。请确认没有待核对的消息。"
                :"停止这个群组任务后，不能直接从相同位置重新开始。确定停止吗？";
            if(MessageBox.Show(Window.GetWindow(this),msg,"确认任务操作",
                MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
                return;
        }
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchControl,
                15000,new LiveBatchControlRequest(selected.JobId,action));
            var result=Unwrap<LiveBatchItem>(raw);
            StatusText.Text=$"群「{result.GroupName}」当前状态：{result.State}。{result.Detail}";
            await LoadJobsAsync();
        }
        catch(Exception ex){StatusText.Text=$"操作结果不确定：{ex.Message}。请先到恢复中心核对，勿重复操作。";}
        finally{_busy=false;UpdateButtons();}
    }

    async void JobPause_Click(object sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:BatchJobRow row})
            await ControlAsync("pause",row);
    }
    async void JobResume_Click(object sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:BatchJobRow row})
            await ControlAsync("resume",row);
    }
    async void JobStop_Click(object sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:BatchJobRow row})
            await ControlAsync("stop",row);
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
    public bool CanStop=>State is "Running" or "Paused";
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
