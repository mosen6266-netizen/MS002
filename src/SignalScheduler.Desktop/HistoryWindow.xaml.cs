using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class HistoryWindow : UserControl
{
    public HistoryWindow()
    {
        InitializeComponent();
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
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveBatchList,10000);
            var all=Unwrap<List<LiveBatchItem>>(raw);
            var records=all.Where(x=>x.State is "Completed" or "Stopped" or "Failed")
                .Select(x=>new BatchJobRow(x)).ToList();
            HistoryGrid.ItemsSource=records;
            StatusText.Text=$"共找到 {records.Count} 条已结束任务。选择一条记录查看逐条发送结果。";
            if(records.Count>0)HistoryGrid.SelectedIndex=0;
            else MessagesGrid.ItemsSource=null;
        }
        catch(Exception ex){StatusText.Text="加载历史失败："+ex.Message;}
    }

    async void Refresh_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();

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
