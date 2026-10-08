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
    bool _loadingReadOptions;

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
    void RefreshGroupPage()
    {
        if(GroupsGrid is null)return;
        var pages=Math.Max(1,(_groups.Count+GroupsPerPage-1)/GroupsPerPage);
        _groupPage=Math.Clamp(_groupPage,0,pages-1);
        _groupsView?.Refresh();
        if(GroupsPageLabel is not null)
            GroupsPageLabel.Text=$"第 {_groupPage+1} / {pages} 页 · 共 {_groups.Count} 个群";
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
    readonly ObservableCollection<BatchGroupTag> _selectedTags=new();
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly HashSet<string> _alerted=new(StringComparer.Ordinal);
    bool _busy;
    bool _refreshing;

    public LiveBatchWindow()
    {
        InitializeComponent();
        RestoreReadOptions();
        _groupsView=CollectionViewSource.GetDefaultView(_groups);
        _groupsView.Filter=o=>o is BatchGroupRow row &&
            _groups.IndexOf(row)/GroupsPerPage==_groupPage;
        GroupsGrid.ItemsSource=_groupsView;
        JobsGrid.ItemsSource=_jobs;
        SelectedGroupTags.ItemsSource=_selectedTags;
        Loaded+=async(_,_)=>{
            await LoadCatalogAsync();
            await LoadJobsAsync();
            _timer.Start();
        };
        _timer.Tick+=async(_,_)=>await LoadJobsAsync();
        Unloaded+=(_,_)=>_timer.Stop();
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
            var old=(ScriptBox.SelectedItem as ScriptEditorSummary)?.ScriptId;
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
            ScriptBox.ItemsSource=scripts;
            ScriptBox.SelectedItem=scripts.FirstOrDefault(x=>x.ScriptId==old)
                ??scripts.FirstOrDefault();
            _groups.Clear();
            foreach(var group in overview.Groups.Where(g=>g.MemberAccounts>0))
            {
                var row=new BatchGroupRow(group);
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
            var position=0;
            foreach(var item in jobs)
            {
                keys.Add(item.JobId);
                var existing=_jobs.FirstOrDefault(x=>x.JobId==item.JobId);
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

    }

    async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(_busy ||
           ScriptBox.SelectedItem is not ScriptEditorSummary script)return;
        GroupsGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        GroupsGrid.CommitEdit(DataGridEditingUnit.Row,true);
        var ids=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
        if(ids.Length is <1 or >20)
        {
            StatusText.Text="一次请勾选 1～20 个群组。";
            return;
        }
        // Inspect all old V7 image references before any irreversible send.
        // If unavailable attachments would be omitted, require separate
        // explicit approval and show their original message indices.
        LiveBatchMediaInspection media;
        try
        {
            var inspection=await MainWindow.SendAsync(ControlCommands.LiveBatchInspect,30000,
                new LiveBatchMediaInspectionRequest(script.ScriptId));
            media=Unwrap<LiveBatchMediaInspection>(inspection);
        }
        catch(Exception ex)
        {
            StatusText.Text="无法检查剧本附件："+ex.Message;
            return;
        }
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

        _busy=true;UpdateButtons();
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchStart,
                60000,new LiveBatchStartRequest(script.ScriptId,ids,true,mediaProblems));
            var started=Unwrap<LiveBatchStartResult>(raw);
            StatusText.Text=$"已启动 {started.GroupCount} 个真实群组任务，"+
                $"每群 {started.MessageCount} 条。可在下表逐群暂停、继续或停止。";
            await LoadJobsAsync();
        }
        catch(Exception ex){StatusText.Text=$"启动失败：{ex.Message}。未完成的事务不会被视为成功。";}
        finally
        {
            _busy=false;
            UpdateButtons();
        }
    }

    async Task ControlAsync(string action,BatchJobRow? selected)
    {
        if(_busy || selected is null)return;
        if(action is "resume" or "stop")
        {
            var msg=action=="resume"
                ?"将从已保存的下一条继续真实发送。请确认没有待核对的消息。"
                :"停止这个群组任务后，不能直接从相同位置重新开始。确定停止吗？";
            if(MessageBox.Show(Window.GetWindow(this),msg,"确认任务操作",
                MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
                return;
        }
        _busy=true;UpdateButtons();
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchControl,
                15000,new LiveBatchControlRequest(selected.JobId,action));
            var result=Unwrap<LiveBatchItem>(raw);
            StatusText.Text=$"群「{result.GroupName}」当前状态：{result.State}。{result.Detail}";
            await LoadJobsAsync();
        }
        catch(Exception ex){StatusText.Text=$"操作失败：{ex.Message}。可先去恢复中心检查。";}
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
        Update(item);
    }
    public string JobId {get;}
    public string ScriptName {get;private set;}="";
    public string GroupName {get;private set;}="";
    public string State {get;private set;}="";
    public long Cursor {get;private set;}
    public int TotalSteps {get;private set;}
    public string Detail {get;private set;}="";
    public string NextSend {get;private set;}="—";
    public string Progress=>$"{Cursor}/{TotalSteps}";
    public string StateDisplay=>State switch
    {
        "Running"=>"运行中","Paused"=>"已暂停",
        "Completed"=>"已完成","RecoveryRequired"=>"需核对",
        "Stopped"=>"已停止","Failed"=>"异常",
        _=>State
    };
    public bool CanPause=>State=="Running";
    public bool CanResume=>State=="Paused" && Cursor<TotalSteps;
    public bool CanStop=>State is "Running" or "Paused";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(LiveBatchItem item)
    {
        // Avoid raising events if the displayed row has not changed.
        var nextSend=item.NextDueMs>0&&item.State=="Running"
            ?DateTimeOffset.FromUnixTimeMilliseconds(item.NextDueMs)
                .ToLocalTime().ToString("HH:mm:ss")
            :"—";
        if(ScriptName==item.ScriptName && GroupName==item.GroupName &&
           State==item.State && Cursor==item.Cursor &&
           TotalSteps==item.TotalSteps && Detail==item.Detail &&
           NextSend==nextSend)return;
        ScriptName=item.ScriptName;
        GroupName=item.GroupName;
        State=item.State;
        Cursor=item.Cursor;
        TotalSteps=item.TotalSteps;
        Detail=item.Detail;
        NextSend=nextSend;
        foreach(var name in new[]{
            nameof(ScriptName),nameof(GroupName),nameof(State),
            nameof(StateDisplay),nameof(Cursor),nameof(TotalSteps),
            nameof(Progress),nameof(Detail),nameof(NextSend),
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
