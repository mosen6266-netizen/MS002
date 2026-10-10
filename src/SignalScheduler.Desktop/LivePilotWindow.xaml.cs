using MessageBox = SignalScheduler.Desktop.Ui2MessageBox;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class LivePilotWindow : Window
{
    readonly ObservableCollection<LivePilotRow> _jobs=new();
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly HashSet<string> _alerts=new(StringComparer.Ordinal);
    bool _busy;
    bool _refreshing;

    public LivePilotWindow()
    {
        InitializeComponent();
        Ui2WindowChrome.Attach(this);
        JobGrid.ItemsSource=_jobs;
        Loaded+=async(_,_)=>{
            await LoadCatalogAsync();
            await LoadJobsAsync();
            _timer.Start();
        };
        _timer.Tick+=async(_,_)=>await LoadJobsAsync();
        Closed+=(_,_)=>_timer.Stop();
        Buttons();
    }

    static T Extract<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("后台没有响应，真实发送状态未知，需核对恢复中心。");
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.GetProperty("Ok").GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var error)
                ?error.GetString():"后台拒绝操作。");
        return JsonSerializer.Deserialize<T>(root.GetProperty("Data").GetRawText())
            ??throw new IOException("后台任务数据不完整。");
    }

    async Task LoadCatalogAsync()
    {
        try
        {
            var data=await Task.WhenAll(
                MainWindow.SendAsync(ControlCommands.ScriptList,8000),
                MainWindow.SendAsync(ControlCommands.AccountGroupCatalog,8000));
            var scripts=Extract<List<ScriptEditorSummary>>(data[0]);
            var overview=Extract<AccountGroupOverview>(data[1]);
            ScriptBox.ItemsSource=scripts.Where(x=>x.StepCount is >=1 and <=3).ToList();
            AccountBox.ItemsSource=overview.Accounts.Where(x=>x.Enabled&&x.Online).ToList();
            GroupBox.ItemsSource=overview.Groups.Where(x=>x.Selected).ToList();
            ScriptBox.SelectedIndex=ScriptBox.Items.Count>0?0:-1;
            AccountBox.SelectedIndex=AccountBox.Items.Count>0?0:-1;
            GroupBox.SelectedIndex=GroupBox.Items.Count>0?0:-1;
            StatusText.Text="已读取剧本和已勾选的群组。开始前，请确认全部消息内容用于这个专用测试群。";
        }
        catch(Exception ex){StatusText.Text=$"读取数据失败：{ex.Message}";}
        Buttons();
    }

    async Task LoadJobsAsync()
    {
        if(_refreshing)return;
        _refreshing=true;
        try
        {
            var current=(JobGrid.SelectedItem as LivePilotRow)?.JobId;
            var raw=await MainWindow.SendAsync(ControlCommands.LivePilotList,9000);
            var items=Extract<List<LivePilotItem>>(raw);
            _jobs.Clear();
            foreach(var x in items)
            {
                _jobs.Add(new LivePilotRow(x));
                if(x.State=="RecoveryRequired" ||
                   (x.State=="Paused" &&
                    (x.Detail.Contains("按剧本设置暂停",StringComparison.Ordinal) ||
                     x.Detail.Contains("异常",StringComparison.Ordinal) ||
                     x.Detail.Contains("不健康",StringComparison.Ordinal) ||
                     x.Detail.Contains("授权",StringComparison.Ordinal))))
                {
                    var key=$"{x.JobId}:{x.Cursor}:{x.State}";
                    if(_alerts.Add(key))
                        MainWindow.ShowCriticalAlert(
                            $"真实自动测试已中断。\n\n剧本：{x.ScriptName}\n群组：{x.GroupName}\n"+
                            $"进度：{x.Cursor}/{x.TotalSteps}\n状态：{x.State}\n\n{x.Detail}\n"+
                            "请人工核对并决定是否继续，程序不会自动恢复。");
                }
            }
            JobGrid.SelectedItem=_jobs.FirstOrDefault(x=>x.JobId==current)
                ??_jobs.FirstOrDefault();
            Buttons();
        }
        catch(Exception ex){StatusText.Text=$"查询实发状态失败：{ex.Message}";}
        finally{_refreshing=false;}
    }

    void Buttons()
    {
        var row=JobGrid?.SelectedItem as LivePilotRow;
        if(StartButton is null)return;
        StartButton.IsEnabled=!_busy&&ConfirmBox.IsChecked==true&&
            ScriptBox.SelectedItem is ScriptEditorSummary &&
            AccountBox.SelectedItem is ManagedAccount &&
            GroupBox.SelectedItem is ManagedGroup;
        PauseButton.IsEnabled=!_busy&&row?.State=="Running";
        ResumeButton.IsEnabled=!_busy&&row?.State=="Paused"&&row.Cursor<row.TotalSteps;
        StopButton.IsEnabled=!_busy&&(row?.State is "Running" or "Paused");
    }

    void Consent_Changed(object sender,RoutedEventArgs e)=>Buttons();
    void JobGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>Buttons();

    async void Refresh_Click(object sender,RoutedEventArgs e)
    {
        await LoadCatalogAsync();
        await LoadJobsAsync();
    }

    async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(_busy || ConfirmBox.IsChecked!=true ||
           ScriptBox.SelectedItem is not ScriptEditorSummary script ||
           AccountBox.SelectedItem is not ManagedAccount account ||
           GroupBox.SelectedItem is not ManagedGroup group)return;

        if(MessageBox.Show(this,
            $"这是一次真实的自动发送测试！\n\n"+
            $"剧本：{script.Name}（{script.StepCount} 条）\n"+
            $"账号：{account.Account}\n"+
            $"群：{group.Name}\n\n"+
            "仅允许专用测试群和知情同意的群成员。"+
            "每条会真实发出，不能撤回。暂停、断线、结果未知时不得盲目重试。\n\n"+
            "确认立即启动这一次真实自动发送？",
            "最终确认：自动实发测试",MessageBoxButton.YesNo,
            MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;

        _busy=true;Buttons();
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LivePilotPlan,16000,
                new LivePilotPlanRequest(script.ScriptId,account.Account,group.GroupId,true));
            var item=Extract<LivePilotItem>(raw);
            StatusText.Text=$"已启动真实自动测试：{item.JobId}。\n"+
                "后台按间隔继续下一条，关闭窗口不影响运行；任何不确定结果会暂停。";
            await LoadJobsAsync();
        }
        catch(Exception ex)
        {
            StatusText.Text=$"未成功启动自动实发：{ex.Message}";
        }
        finally
        {
            _busy=false;ConfirmBox.IsChecked=false;Buttons();
        }
    }

    async Task ControlAsync(string action)
    {
        if(_busy || JobGrid.SelectedItem is not LivePilotRow selected)return;
        if(action is "resume" or "stop")
        {
            var prompt=action=="resume"
                ?"这会让已暂停的真实自动发送任务继续下一条。确定吗？"
                :"停止后不会自动恢复。确定终止这次真实测试吗？";
            if(MessageBox.Show(this,prompt,"确认实发任务操作",
                MessageBoxButton.YesNo,MessageBoxImage.Warning)
                !=MessageBoxResult.Yes)return;
        }

        _busy=true;Buttons();
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LivePilotControl,9000,
                new LivePilotControlRequest(selected.JobId,action));
            var result=Extract<LivePilotItem>(raw);
            StatusText.Text=$"任务 {result.JobId}：{result.State}。{result.Detail}";
            await LoadJobsAsync();
        }
        catch(Exception ex)
        {
            StatusText.Text=$"任务操作失败：{ex.Message}。发送进行中或结果不明时不能跳过。";
        }
        finally{_busy=false;Buttons();}
    }

    async void Pause_Click(object sender,RoutedEventArgs e)=>await ControlAsync("pause");
    async void Resume_Click(object sender,RoutedEventArgs e)=>await ControlAsync("resume");
    async void Stop_Click(object sender,RoutedEventArgs e)=>await ControlAsync("stop");
}

public sealed class LivePilotRow
{
    public LivePilotRow(LivePilotItem item)
    {
        JobId=item.JobId;ScriptName=item.ScriptName;GroupName=item.GroupName;
        State=item.State;Cursor=item.Cursor;TotalSteps=item.TotalSteps;Detail=item.Detail;
    }
    public string JobId {get;}
    public string ScriptName {get;}
    public string GroupName {get;}
    public string State {get;}
    public long Cursor {get;}
    public int TotalSteps {get;}
    public string Progress=>$"{Cursor}/{TotalSteps}";
    public string Detail {get;}
}
