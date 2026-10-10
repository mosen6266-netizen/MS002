using MessageBox = SignalScheduler.Desktop.Ui2MessageBox;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class PreviewTasksWindow : Window
{
    readonly ObservableCollection<ScriptEditorSummary> _scripts=new();
    readonly ObservableCollection<PreviewGroupRow> _groups=new();
    readonly ObservableCollection<PreviewTaskRow> _tasks=new();
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly HashSet<string> _shownReminders=new(StringComparer.Ordinal);
    bool _refreshing;

    public PreviewTasksWindow()
    {
        InitializeComponent();
        ScriptsBox.ItemsSource=_scripts;
        GroupsGrid.ItemsSource=_groups;
        TasksGrid.ItemsSource=_tasks;
        Loaded+=async(_,_)=>{
            await RefreshCatalogAsync();
            await RefreshTasksAsync();
            _timer.Start();
        };
        _timer.Tick+=async(_,_)=>await RefreshTasksAsync();
        Closed+=(_,_)=>_timer.Stop();
        UpdateButtons();
    }

    static T Unwrap<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw)) throw new IOException("后台没有响应。");
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.GetProperty("Ok").GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var err)
                ?err.GetString():"后台处理失败");
        return JsonSerializer.Deserialize<T>(root.GetProperty("Data").GetRawText())
            ??throw new IOException("后台返回的任务数据格式不正确。");
    }

    async Task RefreshCatalogAsync()
    {
        try
        {
            var oldScript=(ScriptsBox.SelectedItem as ScriptEditorSummary)?.ScriptId;
            var results=await Task.WhenAll(
                MainWindow.SendAsync(ControlCommands.ScriptList,7000),
                MainWindow.SendAsync(ControlCommands.AccountGroupCatalog,7000));
            var scriptRaw=results[0];
            var groupRaw=results[1];
            var scripts=Unwrap<List<ScriptEditorSummary>>(scriptRaw);
            var groups=Unwrap<AccountGroupOverview>(groupRaw);
            _scripts.Clear();
            foreach(var s in scripts) _scripts.Add(s);
            ScriptsBox.SelectedItem=_scripts.FirstOrDefault(x=>x.ScriptId==oldScript)
                ??_scripts.FirstOrDefault();
            _groups.Clear();
            foreach(var g in groups.Groups.Where(x=>x.Selected))
                _groups.Add(new PreviewGroupRow(g));
            StatusText.Text=$"可用剧本 {scripts.Count} 个，已保存的群组 {_groups.Count} 个。"+
                "勾选后可创建彼此独立的预演任务。";
        }
        catch(Exception ex){StatusText.Text=$"读取剧本或群组失败：{ex.Message}";}
    }

    async Task RefreshTasksAsync()
    {
        if(_refreshing) return;
        _refreshing=true;
        try
        {
            var previously=(TasksGrid.SelectedItem as PreviewTaskRow)?.JobId;
            var raw=await MainWindow.SendAsync(ControlCommands.PreviewList,7000);
            var tasks=Unwrap<List<PreviewTaskItem>>(raw);
            _tasks.Clear();
            foreach(var task in tasks)
            {
                var row=new PreviewTaskRow(task);
                _tasks.Add(row);
                if(task.State=="Paused" &&
                    task.Detail.Contains("按剧本设置暂停",StringComparison.Ordinal))
                {
                    var fingerprint=$"{task.JobId}:{task.Cursor}";
                    if(_shownReminders.Add(fingerprint))
                        MainWindow.ShowCriticalAlert(
                            $"预演任务已暂停\n\n剧本：{task.Name}\n群组：{task.GroupName}\n"+
                            $"进度：{task.Cursor}/{task.TotalSteps}\n\n{task.Detail}\n\n"+
                            "预演不会自动继续，请打开任务管理窗口手动操作。");
                }
            }
            TasksGrid.SelectedItem=_tasks.FirstOrDefault(x=>x.JobId==previously)
                ??_tasks.FirstOrDefault();
            UpdateButtons();
        }
        catch(Exception ex)
        {
            StatusText.Text=$"读取任务失败：{ex.Message}";
            PauseButton.IsEnabled=ResumeButton.IsEnabled=StopButton.IsEnabled=false;
        }
        finally{_refreshing=false;}
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)
    {
        await RefreshCatalogAsync();
        await RefreshTasksAsync();
    }

    async void Plan_Click(object sender,RoutedEventArgs e)
    {
        if(ScriptsBox.SelectedItem is not ScriptEditorSummary script)
        {
            StatusText.Text="请先选择一个剧本。";
            return;
        }
        GroupsGrid.CommitEdit(DataGridEditingUnit.Cell,true);
        GroupsGrid.CommitEdit(DataGridEditingUnit.Row,true);
        var chosen=_groups.Where(x=>x.Selected).Select(x=>x.GroupId).ToArray();
        if(chosen.Length==0)
        {
            StatusText.Text="请先勾选至少一个已保存的群组。";
            return;
        }
        if(MessageBox.Show(this,
            $"创建 {chosen.Length} 个预演任务，使用剧本「{script.Name}」？\n\n"+
            "注意：这里只验证调度和恢复，不向任何 Signal 群发送消息。",
            "确认开始预演",MessageBoxButton.YesNo,MessageBoxImage.Question)
            !=MessageBoxResult.Yes) return;

        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.PreviewPlan,15000,
                new PreviewTaskPlanRequest(script.ScriptId,chosen));
            var result=Unwrap<PreviewTaskPlanResult>(raw);
            StatusText.Text=$"创建成功：{result.Created} 个预演任务。"+
                "程序将按各自剧本顺序模拟执行，支持逐群暂停/继续/停止。";
            await RefreshTasksAsync();
        }
        catch(Exception ex){StatusText.Text=$"创建预演任务失败：{ex.Message}";}
    }

    void TasksGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateButtons();

    void UpdateButtons()
    {
        var job=TasksGrid.SelectedItem as PreviewTaskRow;
        PauseButton.IsEnabled=job?.State=="Running";
        ResumeButton.IsEnabled=job?.State=="Paused" && job.Cursor<job.TotalSteps;
        StopButton.IsEnabled=job?.State is "Running" or "Paused" or "Created";
    }

    async Task ControlAsync(string action)
    {
        if(TasksGrid.SelectedItem is not PreviewTaskRow selected) return;
        if(action=="stop" && MessageBox.Show(this,
            "停止后不能直接继续该任务。确定要永久停止这次预演吗？",
            "确认停止",MessageBoxButton.YesNo,MessageBoxImage.Warning)
            !=MessageBoxResult.Yes) return;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.PreviewControl,8000,
                new PreviewTaskControlRequest(selected.JobId,action));
            var item=Unwrap<PreviewTaskItem>(raw);
            StatusText.Text=$"{item.GroupName}：{item.State}。{item.Detail}";
            await RefreshTasksAsync();
        }
        catch(Exception ex){StatusText.Text=$"任务控制失败：{ex.Message}";}
    }

    async void Pause_Click(object sender,RoutedEventArgs e)=>await ControlAsync("pause");
    async void Resume_Click(object sender,RoutedEventArgs e)=>await ControlAsync("resume");
    async void Stop_Click(object sender,RoutedEventArgs e)=>await ControlAsync("stop");
    void Close_Click(object sender,RoutedEventArgs e)=>Close();
}

public sealed class PreviewTaskRow
{
    public PreviewTaskRow(PreviewTaskItem task)
    {
        JobId=task.JobId;
        Name=task.Name;
        GroupName=task.GroupName;
        State=task.State;
        Cursor=task.Cursor;
        TotalSteps=task.TotalSteps;
        Detail=task.Detail;
    }

    public string JobId {get;}
    public string Name {get;}
    public string GroupName {get;}
    public string State {get;}
    public long Cursor {get;}
    public int TotalSteps {get;}
    public string Progress=>$"{Cursor}/{TotalSteps}";
    public string Detail {get;}
}

public sealed class PreviewGroupRow : INotifyPropertyChanged
{
    bool _selected=true;
    public PreviewGroupRow(ManagedGroup group)
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
            if(_selected==value) return;
            _selected=value;
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
