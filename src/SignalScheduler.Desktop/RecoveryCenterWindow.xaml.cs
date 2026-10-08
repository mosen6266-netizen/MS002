using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class RecoveryCenterWindow : Window
{
    RecoveryOverview? _overview;
    bool _refreshing;

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
            JobsGrid.ItemsSource=overview.Jobs;
            JobsGrid.SelectedItem=overview.Jobs.FirstOrDefault(x=>x.JobId==previous)
                ??overview.Jobs.FirstOrDefault(x=>x.NeedsReview)
                ??overview.Jobs.FirstOrDefault();
            UpdateSelection();
            StatusText.Text=$"共 {overview.Jobs.Count} 个任务、{overview.Dispatches.Count} 条消息记录；"+
                $"{overview.Jobs.Count(x=>x.NeedsReview)} 个任务需要人工核对。";
        }
        catch(Exception ex)
        {
            StatusText.Text=$"刷新失败：{ex.Message}。原有列表可能已经过期，请检查后台引擎。";
            PauseButton.IsEnabled=false;
        }
        finally{_refreshing=false;}
    }

    void JobsGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)=>
        UpdateSelection();

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
            job.State is "Running" or "WaitingSignal" or "Stopping";
    }

    async void Pause_Click(object sender,RoutedEventArgs e)
    {
        if(JobsGrid.SelectedItem is not RecoveryJobItem job || job.IsLegacy) return;
        var answer=MessageBox.Show(this,
            $"确认暂停任务「{job.Name}」？\n\n如果已有消息进入发送阶段，暂停不会撤回这条消息，必须核对发送记录。",
            "确认手动暂停",MessageBoxButton.YesNo,MessageBoxImage.Warning);
        if(answer!=MessageBoxResult.Yes) return;

        PauseButton.IsEnabled=false;
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
    }

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

    void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
