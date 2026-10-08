using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class HistoryWindow : UserControl
{
    int _page;
    int _pages=1;
    bool _loading;
    int _requestId;
    public HistoryWindow()
    {
        InitializeComponent();
        HistoryStateBox.SelectedIndex=0;
        Loaded+=async(_,_)=>await RefreshAsync();
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
        if(_loading)return;
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
            MessagesGrid.ItemsSource=null;
            HistoryPageLabel.Text=$"第 {_page+1} / {_pages} 页 · 共 {result.Total} 条";
            HistoryPrevButton.IsEnabled=_page>0;
            HistoryNextButton.IsEnabled=_page+1<_pages;
            StatusText.Text=$"历史记录共 {result.Total} 条。选择一条查看发送详情。";
        }
        catch(Exception ex){StatusText.Text="加载历史失败："+ex.Message;}
        finally{_loading=false;}
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();
    void FiltersChanged(object sender,RoutedEventArgs e)
    {
        if(!IsLoaded)return;
        _page=0;
        _=RefreshAsync();
    }
    async void PreviousPage_Click(object sender,RoutedEventArgs e)
    {if(_page>0){_page--;await RefreshAsync();}}
    async void NextPage_Click(object sender,RoutedEventArgs e)
    {if(_page+1<_pages){_page++;await RefreshAsync();}}

    async void HistoryGrid_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(HistoryGrid.SelectedItem is not BatchJobRow row)return;
        try
        {
            StatusText.Text="正在读取「"+row.GroupName+"」的发送明细…";
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchHistoryDetail,
                10000,new LiveBatchHistoryDetailRequest(row.JobId));
            var detail=Unwrap<LiveBatchHistoryDetail>(raw);
            if(HistoryGrid.SelectedItem is not BatchJobRow selected ||
               selected.JobId!=detail.JobId)return;
            MessagesGrid.ItemsSource=detail.Messages;
            StatusText.Text=$"群组：{detail.GroupName} · 剧本：{detail.ScriptName} · "+
                $"已执行 {detail.Cursor}/{detail.TotalSteps} 条。发送结果以后台日志为准。";
        }
        catch(Exception ex)
        {
            MessagesGrid.ItemsSource=null;
            StatusText.Text="读取发送明细失败："+ex.Message;
        }
    }
}
