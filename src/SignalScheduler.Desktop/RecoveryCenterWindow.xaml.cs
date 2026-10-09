using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class RecoveryCenterWindow : UserControl
{
    RecoveryOverview? _overview;
    bool _refreshing;
    bool _reviewInProgress;
    bool _pauseInProgress;
    string? _selectedEvidenceJob;
    string? _selectedEvidenceDispatch;

    public RecoveryCenterWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>await RefreshAsync();
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();

    async Task RefreshAsync()
    {
        if(_refreshing) return;
        _refreshing=true;
        var previous=(JobsGrid.SelectedItem as RecoveryJobItem)?.JobId;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.RecoveryOverview,5000);
            if(string.IsNullOrWhiteSpace(raw)) throw new IOException("后台没有响应。");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(!root.GetProperty("Ok").GetBoolean())
                throw new IOException(root.TryGetProperty("Error",out var errorValue)?errorValue.GetString():"读取失败");
            var overview=JsonSerializer.Deserialize<RecoveryOverview>(
                root.GetProperty("Data").GetRawText())
                ??throw new IOException("任务记录格式错误。");

            _overview=overview;
            ApplyJobsFilter(previous);
        }
        catch(Exception ex)
        {
            StatusText.Text=$"刷新失败：{ex.Message}。原有列表可能已经过期，请检查后台引擎。";
            PauseButton.IsEnabled=false;
        }
        finally{_refreshing=false;}
    }

    void TasksFilterChanged(object sender,RoutedEventArgs e)
    {
        if(_overview is not null)
            ApplyJobsFilter((JobsGrid?.SelectedItem as RecoveryJobItem)?.JobId);
    }

    void ApplyJobsFilter(string? previousJobId)
    {
        if(_overview is null || JobsGrid is null)return;
        IReadOnlyList<RecoveryJobItem> visible=ShowAllTasks.IsChecked==true
            ?_overview.Jobs
            :_overview.Jobs.Where(x=>x.NeedsAttention).ToArray();
        JobsGrid.ItemsSource=visible;
        JobsGrid.SelectedItem=visible.FirstOrDefault(x=>x.JobId==previousJobId)
            ??visible.FirstOrDefault(x=>x.NeedsReview)
            ??visible.FirstOrDefault();
        UpdateSelection();
        var reviewing=_overview.Jobs.Count(x=>x.NeedsReview);
        StatusText.Text=$"共 {_overview.Jobs.Count} 个任务；{reviewing} 个必须核对发送结果，"+
            $"{_overview.Jobs.Count(x=>x.NeedsAttention)} 个需要关注。"+
            (visible.Count==0?" 当前没有需要处理的任务，可勾选“显示全部任务”查看历史。":"");
    }

    void JobsGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>
        UpdateSelection();

    void DispatchGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        var jobId=(JobsGrid?.SelectedItem as RecoveryJobItem)?.JobId;
        var key=(DispatchGrid?.SelectedItem as RecoveryDispatchItem)?.DispatchKey;
        if(jobId!=_selectedEvidenceJob || key!=_selectedEvidenceDispatch)
        {
            _selectedEvidenceJob=jobId;
            _selectedEvidenceDispatch=key;
            EvidenceBox?.Clear();
        }
        UpdateReviewButtons();
    }

    void UpdateReviewButtons()
    {
        var job=JobsGrid?.SelectedItem as RecoveryJobItem;
        var item=DispatchGrid?.SelectedItem as RecoveryDispatchItem;
        var allowed=!_reviewInProgress && !_pauseInProgress &&
                    job is {IsLegacy:false,State:"RecoveryRequired"} &&
                    item is {State:"RecoveryRequired"} &&
                    item.JobId==job.JobId && item.Cursor==job.Cursor;
        if(MarkSeenButton is not null)MarkSeenButton.IsEnabled=allowed;
        if(MarkNotSentButton is not null)MarkNotSentButton.IsEnabled=allowed;
    }


    void UpdateSelection()
    {
        if(_overview is null)
        {
            DispatchGrid.ItemsSource=null;
            PauseButton.IsEnabled=false;
            return;
        }
        var job=JobsGrid.SelectedItem as RecoveryJobItem;
        DispatchGrid.ItemsSource=job is null
            ?Array.Empty<RecoveryDispatchItem>()
            :_overview.Dispatches.Where(x=>x.JobId==job.JobId).ToArray();
        PauseButton.IsEnabled=job is {IsLegacy:false} &&
            (job.State is "Running" or "WaitingSignal" or "Stopping") &&
            !_reviewInProgress && !_pauseInProgress;
        UpdateReviewButtons();
    }

    async void Pause_Click(object sender,RoutedEventArgs e)
    {
        if(JobsGrid.SelectedItem is not RecoveryJobItem job || job.IsLegacy) return;
        var answer=MessageBox.Show(Window.GetWindow(this),
            $"确认暂停任务「{job.Name}」？\n\n如果已有消息进入发送阶段，暂停不会撤回这条消息，必须核对发送记录。",
            "确认手动暂停",MessageBoxButton.YesNo,MessageBoxImage.Warning);
        if(answer!=MessageBoxResult.Yes) return;

        if(_pauseInProgress || _reviewInProgress)return;
        _pauseInProgress=true;
        PauseButton.IsEnabled=false;
        UpdateReviewButtons();
        try
        {
            var raw=await MainWindow.SendAsync(
                ControlCommands.PauseJob,5000,new PauseJobRequest(job.JobId));
            if(string.IsNullOrWhiteSpace(raw)) throw new IOException("后台没有响应。");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(!root.GetProperty("Ok").GetBoolean())
                throw new IOException(root.TryGetProperty("Error",out var errorValue)?errorValue.GetString():"暂停失败");
            var result=JsonSerializer.Deserialize<PauseJobResult>(
                root.GetProperty("Data").GetRawText())
                ??throw new IOException("任务状态格式错误。");
            StatusText.Text=result.Detail;
            await RefreshAsync();
        }
        catch(Exception ex)
        {
            StatusText.Text=$"无法确认暂停是否成功：{ex.Message}。请刷新任务状态后核对。";
        }
        finally
        {
            _pauseInProgress=false;
            UpdateSelection();
        }
    }

    async Task ReviewAsync(string decision)
    {
        if(_reviewInProgress || _pauseInProgress)return;
        if(JobsGrid.SelectedItem is not RecoveryJobItem job || job.IsLegacy ||
           DispatchGrid.SelectedItem is not RecoveryDispatchItem message ||
           message.JobId!=job.JobId || job.State!="RecoveryRequired" ||
           message.State!="RecoveryRequired" || message.Cursor!=job.Cursor)return;

        var evidence=EvidenceBox.Text.Trim();
        if(evidence.Length<8)
        {
            StatusText.Text="请填写至少 8 个字符的实际核对依据，例如所查看的群消息和时间。";
            return;
        }
        var seen=decision=="seen";
        var description=seen
            ?"我已经在 Signal 群中核实这条消息确实已发出；不再重发，游标前进一步。"
            :"我已经在 Signal 群中核实这条消息没有发出；留在原位置，稍后可手动继续。";
        var confirmation=MessageBox.Show(Window.GetWindow(this),
            $"你正在人工裁定一条真实发送的未知结果。\n\n任务：{job.JobId}\n"+
            $"发送记录：{message.DispatchKey}\n\n结论：{description}\n\n"+
            $"核对依据：{evidence}\n\n该操作会写入持久审计记录，不能撤回。确定吗？",
            "再次确认发送核对结果",MessageBoxButton.YesNo,MessageBoxImage.Warning);
        if(confirmation!=MessageBoxResult.Yes)return;

        _reviewInProgress=true;
        MarkSeenButton.IsEnabled=false;
        MarkNotSentButton.IsEnabled=false;
        PauseButton.IsEnabled=false;
        try
        {
            var raw=await MainWindow.SendAsync(
                ControlCommands.ManualDispatchReview,10000,
                new ManualDispatchReviewRequest(job.JobId,message.DispatchKey,decision,evidence));
            if(string.IsNullOrWhiteSpace(raw))
                throw new IOException("后台没有响应，请刷新核对结果。");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(!root.GetProperty("Ok").GetBoolean())
                throw new IOException(root.TryGetProperty("Error",out var err)
                    ?err.GetString():"后台拒绝这次裁定。");
            var result=JsonSerializer.Deserialize<ManualDispatchReviewResult>(
                root.GetProperty("Data").GetRawText())
                ??throw new IOException("后台未返回有效的审计结果。");
            StatusText.Text=$"人工核对记录已保存：{result.Detail} 当前任务：{result.JobState}。";
            EvidenceBox.Clear();
            await RefreshAsync();
        }
        catch(Exception ex)
        {
            StatusText.Text=$"核对结果未确认成功：{ex.Message}。请先刷新，再检查审计记录；不要重复操作。";
        }
        finally
        {
            _reviewInProgress=false;
            UpdateSelection();
        }
    }

    async void MarkSeen_Click(object sender,RoutedEventArgs e)=>await ReviewAsync("seen");
    async void MarkNotSent_Click(object sender,RoutedEventArgs e)=>await ReviewAsync("not_seen");

    void Copy_Click(object sender,RoutedEventArgs e)
    {
        if(DispatchGrid.SelectedItem is not RecoveryDispatchItem d)
        {
            StatusText.Text="请先选中下方的一条发送记录。";
            return;
        }
        var detail=$"""
            Signal V8 发送核对记录
            任务：{d.JobId}
            消息序号：{d.Cursor}
            发送记录键：{d.DispatchKey}
            账号：{d.AccountId}
            群组：{d.GroupId}
            状态：{d.State}
            平台消息编号：{d.ProviderMessageId??"(无)"}
            说明：{d.Detail??"(无)"}
            更新时间（UTC 秒）：{d.UpdatedAt}
            """;
        try
        {
            Clipboard.SetText(detail);
            StatusText.Text="记录已复制。这里只是审计资料，不代表消息发送成功。";
        }
        catch(Exception ex){StatusText.Text=$"复制失败：{ex.Message}";}
    }

    void Close_Click(object sender,RoutedEventArgs e)=>
        (Window.GetWindow(this) as MainWindow)?.NavigateHome();
}
