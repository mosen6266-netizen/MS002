using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class LiveBatchWindow : UserControl
{
    readonly ObservableCollection<BatchGroupRow> _groups=new();
    readonly ObservableCollection<BatchJobRow> _jobs=new();
    readonly ObservableCollection<AccountHealthChip> _accounts=new();
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly HashSet<string> _alerted=new(StringComparer.Ordinal);
    bool _busy;
    bool _refreshing;

    public LiveBatchWindow()
    {
        InitializeComponent();
        GroupsGrid.ItemsSource=_groups;
        JobsGrid.ItemsSource=_jobs;
        AccountCards.ItemsSource=_accounts;
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
            _accounts.Clear();
            var healthy=0;
            foreach(var account in overview.Accounts
                .OrderBy(x=>x.Online && x.Enabled)
                .ThenBy(x=>x.Label,StringComparer.CurrentCulture))
            {
                var available=account.Enabled && account.Online;
                if(available)healthy++;
                var note=!string.IsNullOrWhiteSpace(account.Label) &&
                         account.Label!=account.Account
                    ?account.Label
                    :"账号 · "+account.Account[^Math.Min(4,account.Account.Length)..];
                var state=!account.Enabled?"已停用":account.Online?"在线 · 正常":"离线 · 需要检查";
                _accounts.Add(new AccountHealthChip(note,state,
                    available?"#72E3B2":"#FFBA80",
                    note+" | "+state+" | "+account.Account));
            }
            AccountsSummary.Text=$"{healthy}/{overview.Accounts.Count} 个可用"+
                (healthy==overview.Accounts.Count?" · 全部正常":" · 有账号需要处理");
            AccountsSummary.Foreground=healthy==overview.Accounts.Count
                ?System.Windows.Media.Brushes.LightGreen
                :System.Windows.Media.Brushes.Gold;
            ScriptBox.ItemsSource=scripts;
            ScriptBox.SelectedItem=scripts.FirstOrDefault(x=>x.ScriptId==old)
                ??scripts.FirstOrDefault();
            _groups.Clear();
            foreach(var group in overview.Groups.Where(g=>g.MemberAccounts>0))
            {
                var row=new BatchGroupRow(group);
                row.PropertyChanged+=(_,_)=>UpdateButtons();
                _groups.Add(row);
            }
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
        foreach(var group in _groups) group.Selected=true;
        UpdateButtons();
    }

    void ClearAll_Click(object sender,RoutedEventArgs e)
    {
        foreach(var group in _groups) group.Selected=false;
        UpdateButtons();
    }

    void ScriptBox_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateButtons();
    void GroupsGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateButtons();
    void Consent_Changed(object sender,RoutedEventArgs e)=>UpdateButtons();
    void JobsGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateButtons();

    void UpdateButtons()
    {
        if(StartButton is null)return;
        StartButton.IsEnabled=!_busy && ConsentBox.IsChecked==true &&
            ScriptBox.SelectedItem is ScriptEditorSummary &&
            _groups.Any(x=>x.Selected);

    }

    async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(_busy || ConsentBox.IsChecked!=true ||
           ScriptBox.SelectedItem is not ScriptEditorSummary script)return;
        GroupsGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        GroupsGrid.CommitEdit(DataGridEditingUnit.Row,true);
        var ids=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
        if(ids.Length is <1 or >20)
        {
            StatusText.Text="一次请勾选 1～20 个群组。";
            return;
        }
        var preview=$"确定开始真正发送？\n\n剧本：{script.Name}\n"+
            $"剧本气泡：{script.StepCount} 个（空白草稿行自动忽略）\n群组：{ids.Length} 个\n\n"+
            "所有群会在后台分别运行。发送的文字和图片将真实出现在 Signal 群内。"+
            "关闭此窗口不会停止任务；发生异常会暂停并提醒。\n\n"+
            "只有你管理且允许这样发送的群组可以启动。";
        if(MessageBox.Show(Window.GetWindow(this),preview,"确认多群真实运行",
            MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
            return;

        _busy=true;UpdateButtons();
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchStart,
                60000,new LiveBatchStartRequest(script.ScriptId,ids,true));
            var started=Unwrap<LiveBatchStartResult>(raw);
            StatusText.Text=$"已启动 {started.GroupCount} 个真实群组任务，"+
                $"每群 {started.MessageCount} 条。可在下表逐群暂停、继续或停止。";
            await LoadJobsAsync();
        }
        catch(Exception ex){StatusText.Text=$"启动失败：{ex.Message}。未完成的事务不会被视为成功。";}
        finally
        {
            _busy=false;
            ConsentBox.IsChecked=false;
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

public sealed record AccountHealthChip(
    string Label,string Status,string Color,string Detail);

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
