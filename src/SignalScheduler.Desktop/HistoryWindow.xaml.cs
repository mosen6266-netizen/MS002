using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class HistoryWindow : UserControl
{
    int _page;
    int _pages=1;
    bool _loading;
    bool _pendingRefresh;
    bool _exporting;
    int _requestId;
    int _detailGeneration;
    readonly DispatcherTimer _detailDebounce=new()
        {Interval=TimeSpan.FromMilliseconds(150)};
    readonly Dictionary<string,LiveBatchHistoryDetail> _detailCache=new(StringComparer.Ordinal);
    public HistoryWindow()
    {
        InitializeComponent();
        HistoryStateBox.SelectedIndex=0;
        _detailDebounce.Tick+=LoadDebouncedDetail;
        Loaded+=async(_,_)=>await RefreshAsync();
        Unloaded+=(_,_)=>_detailDebounce.Stop();
    }

    static T Unwrap<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))throw new IOException("后台未响应。");
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.TryGetProperty("Ok",out var ok)||!ok.GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var e)
                ?e.GetString():"后台拒绝读取历史记录。");
        return JsonSerializer.Deserialize<T>(
            root.GetProperty("Data").GetRawText())??throw new IOException("历史数据为空。");
    }

    async Task RefreshAsync()
    {
        if(_loading)
        {
            _pendingRefresh=true;
            return;
        }
        _loading=true;
        var requestId=++_requestId;
        try
        {
            var state=(HistoryStateBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var search=HistorySearchBox.Text?.Trim()??"";
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchHistoryPage,
                10000,new LiveBatchHistoryPageRequest(_page,10,search,state));
            var result=Unwrap<LiveBatchHistoryPage>(raw);
            if(requestId!=_requestId)return;
            _pages=Math.Max(1,(int)Math.Ceiling(result.Total/10.0));
            if(_page>=_pages)
            {
                _page=_pages-1;
                _loading=false;
                await RefreshAsync();
                return;
            }
            HistoryGrid.ItemsSource=result.Jobs.Select(x=>new BatchJobRow(x)).ToList();
            ++_detailGeneration;
            _detailDebounce.Stop();
            MessagesGrid.ItemsSource=null;
            HistoryPageLabel.Text=$"第 {_page+1} / {_pages} 页 · 共 {result.Total} 条";
            HistoryPrevButton.IsEnabled=_page>0;
            HistoryNextButton.IsEnabled=_page+1<_pages;
            StatusText.Text=$"历史记录共 {result.Total} 条。选择一条查看发送详情。";
        }
        catch(Exception ex)
        {
            if(requestId==_requestId)
                StatusText.Text="加载历史失败："+ex.Message;
        }
        finally
        {
            _loading=false;
            if(_pendingRefresh)
            {
                _pendingRefresh=false;
                await RefreshAsync();
            }
        }
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();
    void FiltersChanged(object sender,RoutedEventArgs e)
    {
        if(!IsLoaded)return;
        _page=0;
        ++_requestId;
        _=RefreshAsync();
    }
    async void PreviousPage_Click(object sender,RoutedEventArgs e)
    {if(_page>0){_page--; ++_requestId; await RefreshAsync();}}
    async void NextPage_Click(object sender,RoutedEventArgs e)
    {if(_page+1<_pages){_page++; ++_requestId; await RefreshAsync();}}

    void HistoryGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        _detailDebounce.Stop();
        ++_detailGeneration;
        if(HistoryGrid.SelectedItem is not BatchJobRow row)
        {
            MessagesGrid.ItemsSource=null;
            return;
        }
        if(_detailCache.TryGetValue(row.JobId,out var cached))
        {
            ShowDetail(cached);
            return;
        }
        StatusText.Text="正在读取「"+row.GroupName+"」的发送明细…";
        _detailDebounce.Start();
    }

    void ShowDetail(LiveBatchHistoryDetail detail)
    {
        // Finite-height grid keeps virtualization active even for long scripts.
        MessagesGrid.ItemsSource=detail.Messages;
        StatusText.Text=$"群组：{detail.GroupName} · 剧本：{detail.ScriptName} · "+
            $"已执行 {detail.Cursor}/{detail.TotalSteps} 条。发送结果以后台日志为准。";
    }

    async void LoadDebouncedDetail(object? sender,EventArgs e)
    {
        _detailDebounce.Stop();
        if(HistoryGrid.SelectedItem is not BatchJobRow row)return;
        var generation=++_detailGeneration;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchHistoryDetail,
                15000,new LiveBatchHistoryDetailRequest(row.JobId));
            if(generation!=_detailGeneration ||
               (HistoryGrid.SelectedItem as BatchJobRow)?.JobId!=row.JobId)return;
            var detail=Unwrap<LiveBatchHistoryDetail>(raw);
            if(detail.JobId!=row.JobId)
                throw new IOException("历史明细编号与当前选择不一致。");
            if(_detailCache.Count>=20)_detailCache.Clear();
            _detailCache[detail.JobId]=detail;
            ShowDetail(detail);
        }
        catch(Exception ex)
        {
            if(generation!=_detailGeneration)return;
            MessagesGrid.ItemsSource=null;
            StatusText.Text="读取发送明细失败："+ex.Message;
        }
    }

    async void ExportSelected_Click(object sender,RoutedEventArgs e)
    {
        if(_exporting || HistoryGrid.SelectedItem is not BatchJobRow job)
        {
            StatusText.Text="请先选择一条历史任务，再导出该任务的完整发送明细。";
            return;
        }
        _exporting=true;
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchHistoryDetail,
                15000,new LiveBatchHistoryDetailRequest(job.JobId));
            var detail=Unwrap<LiveBatchHistoryDetail>(raw);
            if(detail.JobId!=job.JobId)
                throw new IOException("后台返回的任务与所选记录不一致。");
            var picker=new SaveFileDialog
            {
                Title="导出此任务的完整发送明细",
                FileName="Signal-发送历史-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json",
                Filter="JSON 文件 (*.json)|*.json",
                AddExtension=true
            };
            if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
            // User deliberately exports messages/evidence: never send this data
            // to a server or log it; the destination is their chosen local file.
            var content=JsonSerializer.Serialize(detail,
                new JsonSerializerOptions{WriteIndented=true});
            var temp=picker.FileName+".tmp";
            try
            {
                File.WriteAllText(temp,content);
                File.Move(temp,picker.FileName,true);
            }
            finally
            {
                if(File.Exists(temp))File.Delete(temp);
            }
            StatusText.Text=$"已导出「{detail.GroupName}」的完整发送明细。请妥善保管包含消息内容的文件。";
        }
        catch(Exception ex)
        {
            StatusText.Text="导出失败："+ex.Message;
        }
        finally{_exporting=false;}
    }
}
