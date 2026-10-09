using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

/// <summary>
/// A dedicated read-mostly monitor. All mutations still go through the
/// existing durable, state-checked batch-control IPC endpoint.
/// </summary>
public partial class RunningTasksWindow : UserControl
{
    readonly ObservableCollection<BatchJobRow> _jobs=new();
    readonly DispatcherTimer _poll=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly DispatcherTimer _clock=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly HashSet<string> _pageIds=new(StringComparer.Ordinal);
    ICollectionView? _view;
    bool _refreshing;
    bool _busy;
    bool _snapshotFresh;
    int _page;
    const int PageSize=10;

    public RunningTasksWindow()
    {
        InitializeComponent();
        _view=CollectionViewSource.GetDefaultView(_jobs);
        _view.Filter=o=>o is BatchJobRow row && _pageIds.Contains(row.JobId);
        MonitorGrid.ItemsSource=_view;
        Loaded+=async (_,_)=>{
            _poll.Start();
            _clock.Start();
            await RefreshAsync();
        };
        Unloaded+=(_,_)=>{_poll.Stop();_clock.Stop();};
        _poll.Tick+=async (_,_)=>{
            if(AutoRefreshCheck.IsChecked==true)await RefreshAsync();
        };
        _clock.Tick+=(_,_)=>{
            foreach(var row in _jobs)row.RefreshCountdown();
            if(_snapshotFresh && _lastSuccessfulRefreshMs>0 &&
               DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-_lastSuccessfulRefreshMs>15000)
            {
                _snapshotFresh=false;
                StatusText.Text="后台状态超过 15 秒未更新，请刷新确认后再操作任务。";
                UpdateSelection();
            }
        };
    }

    long _lastSuccessfulRefreshMs;

    static T Unwrap<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("任务后台未响应，请先核对最新任务状态。");
        using var doc=JsonDocument.Parse(raw);
        if(doc.RootElement.ValueKind!=JsonValueKind.Object ||
           !doc.RootElement.TryGetProperty("Ok",out var ok) ||
           ok.ValueKind!=JsonValueKind.True)
            throw new IOException("后台拒绝或无法完成任务状态查询。");
        if(!doc.RootElement.TryGetProperty("Data",out var data))
            throw new IOException("后台任务数据不完整。");
        return JsonSerializer.Deserialize<T>(data.GetRawText())
            ??throw new IOException("后台任务数据为空。");
    }

    string SelectedFilter()=>
        (TaskFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "active";

    void RebuildPage(string? selectedId=null)
    {
        if(_view is null || MonitorGrid is null)return;
        var matching=_jobs.Where(x=>LiveTaskMonitor.IsVisible(
            x.Snapshot,SelectedFilter())).ToArray();
        var pages=Math.Max(1,(matching.Length+PageSize-1)/PageSize);
        _page=Math.Clamp(_page,0,pages-1);
        _pageIds.Clear();
        foreach(var row in matching.Skip(_page*PageSize).Take(PageSize))
            _pageIds.Add(row.JobId);
        _view.Refresh();
        JobsPageLabel.Text=$"第 {_page+1} / {pages} 页 · 匹配 {matching.Length} 条 · 每页 10 条";
        PreviousPageButton.IsEnabled=_page>0;
        NextPageButton.IsEnabled=_page<pages-1;
        if(selectedId is not null)
            MonitorGrid.SelectedItem=_jobs.FirstOrDefault(x=>x.JobId==selectedId &&
                _pageIds.Contains(x.JobId));
        UpdateSelection();
    }

    async Task RefreshAsync()
    {
        if(_refreshing || _busy)return;
        _refreshing=true;
        UpdateSelection();
        var selectedId=(MonitorGrid.SelectedItem as BatchJobRow)?.JobId;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchList,10000);
            var incoming=Unwrap<List<LiveBatchItem>>(raw);
            var ids=new HashSet<string>(incoming.Select(x=>x.JobId),StringComparer.Ordinal);
            var index=_jobs.ToDictionary(x=>x.JobId,StringComparer.Ordinal);
            var position=0;
            foreach(var item in incoming)
            {
                if(!index.TryGetValue(item.JobId,out var row))
                {
                    row=new BatchJobRow(item);
                    _jobs.Insert(Math.Min(position,_jobs.Count),row);
                }
                else
                {
                    row.Update(item);
                    var oldPosition=_jobs.IndexOf(row);
                    if(oldPosition!=position)_jobs.Move(oldPosition,position);
                }
                position++;
            }
            for(var i=_jobs.Count-1;i>=0;i--)
                if(!ids.Contains(_jobs[i].JobId))_jobs.RemoveAt(i);

            _lastSuccessfulRefreshMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _snapshotFresh=true;
            AllCountText.Text=$"任务总数：{_jobs.Count}";
            RunningCountText.Text=$"运行：{_jobs.Count(x=>x.State=="Running")}";
            PausedCountText.Text=$"暂停：{_jobs.Count(x=>x.State=="Paused")}";
            AttentionCountText.Text=$"需处理：{_jobs.Count(x=>
                LiveTaskMonitor.NeedsAttention(x.Snapshot))}";
            RebuildPage(selectedId);
            StatusText.Text=_jobs.Count==0
                ?"暂无任务。请在首页选择剧本与群组后启动。"
                :"状态读取成功 · 最后更新："+DateTime.Now.ToString("HH:mm:ss")+
                    "。如需处理未知发送结果，请进入异常恢复中心。";
        }
        catch(Exception)
        {
            _snapshotFresh=false;
            StatusText.Text="刷新任务失败，当前列表可能已过期；操作已禁用，避免根据旧状态误操作。";
        }
        finally
        {
            _refreshing=false;
            UpdateSelection();
        }
    }

    void FilterChanged(object sender,SelectionChangedEventArgs e)
    {
        _page=0;
        RebuildPage();
    }

    void PreviousPage_Click(object sender,RoutedEventArgs e)
    {
        if(_page>0){_page--;RebuildPage();}
    }

    void NextPage_Click(object sender,RoutedEventArgs e)
    {
        _page++;
        RebuildPage();
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)=>
        await RefreshAsync();

    void OpenRecovery_Click(object sender,RoutedEventArgs e)=>
        (Window.GetWindow(this) as MainWindow)?.NavigateTo("recovery");

    void MonitorGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>
        UpdateSelection();

    void UpdateSelection()
    {
        if(SelectedTaskText is null || PauseTaskButton is null)return;
        var row=MonitorGrid?.SelectedItem as BatchJobRow;
        var operable=_snapshotFresh && !_refreshing && !_busy;
        PauseTaskButton.IsEnabled=operable && row?.CanPause==true;
        ResumeTaskButton.IsEnabled=operable && row?.CanResume==true &&
            !LiveTaskMonitor.NeedsAttention(row.Snapshot);
        StopTaskButton.IsEnabled=operable && row?.CanStop==true &&
            !LiveTaskMonitor.NeedsAttention(row.Snapshot);
        if(row is null)
        {
            SelectedTaskText.Text="选择上表中的群组任务查看详情。";
            return;
        }
        SelectedTaskText.Text=$"群：{row.GroupName} · 剧本：{row.ScriptName}\n"+
            $"进度：{row.Progress} · 状态：{row.StateDisplay} · 阶段：{row.Phase}\n"+
            $"说明：{row.Detail}"+
            (LiveTaskMonitor.NeedsAttention(row.Snapshot)
                ?"\n该任务需要进一步核对，请通过异常恢复中心处理。":"");
    }

    async Task ControlAsync(string action)
    {
        if(_busy || !_snapshotFresh || _refreshing ||
           MonitorGrid.SelectedItem is not BatchJobRow row)return;
        if(action=="pause" && !row.CanPause)return;
        if(action=="resume" && (!row.CanResume ||
            LiveTaskMonitor.NeedsAttention(row.Snapshot)))return;
        if(action=="stop" && (!row.CanStop ||
            LiveTaskMonitor.NeedsAttention(row.Snapshot)))return;

        if(action is "resume" or "stop")
        {
            var question=action=="resume"
                ?$"确定手动继续群「{row.GroupName}」吗？请先确认没有待核对的消息。"
                :$"确定停止群「{row.GroupName}」吗？停止后不能从原位置直接继续。";
            if(MessageBox.Show(Window.GetWindow(this),question,
                "确认任务操作",MessageBoxButton.YesNo,
                MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        }

        _busy=true;
        UpdateSelection();
        try
        {
            var result=Unwrap<LiveBatchItem>(await MainWindow.SendAsync(
                ControlCommands.LiveBatchControl,15000,
                new LiveBatchControlRequest(row.JobId,action)));
            StatusText.Text=$"任务已更新：{StatusLabels.Task(result.State)}。{result.Detail}";
        }
        catch(Exception)
        {
            _snapshotFresh=false;
            StatusText.Text="任务操作结果不明确。请刷新或前往异常恢复中心核对；不要重复点击。";
        }
        finally
        {
            _busy=false;
            await RefreshAsync();
        }
    }

    async void PauseTask_Click(object sender,RoutedEventArgs e)=>
        await ControlAsync("pause");
    async void ResumeTask_Click(object sender,RoutedEventArgs e)=>
        await ControlAsync("resume");
    async void StopTask_Click(object sender,RoutedEventArgs e)=>
        await ControlAsync("stop");
}
