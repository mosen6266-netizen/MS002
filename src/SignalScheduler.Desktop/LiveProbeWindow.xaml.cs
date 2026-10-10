using MessageBox = SignalScheduler.Desktop.Ui2MessageBox;
using System.IO;
using System.Text.Json;
using System.Windows;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class LiveProbeWindow : Window
{
    bool _busy;

    public LiveProbeWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>await ReloadAsync();
    }

    static T Extract<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("后台无响应，发送结果不能确定，请检查恢复中心。");
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.GetProperty("Ok").GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var err)
                ?err.GetString():"后台拒绝了本次操作。");
        return JsonSerializer.Deserialize<T>(root.GetProperty("Data").GetRawText())
            ??throw new IOException("后台未返回完整结果，请检查恢复中心。");
    }

    async Task ReloadAsync()
    {
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.AccountGroupCatalog,9000);
            var data=Extract<AccountGroupOverview>(raw);
            AccountBox.ItemsSource=data.Accounts.Where(x=>x.Enabled&&x.Online).ToArray();
            GroupBox.ItemsSource=data.Groups.Where(x=>x.Selected).ToArray();
            AccountBox.SelectedIndex=AccountBox.Items.Count>0?0:-1;
            GroupBox.SelectedIndex=GroupBox.Items.Count>0?0:-1;
            StatusText.Text=$"当前在线可用账号 {AccountBox.Items.Count} 个，已勾选的群组 {GroupBox.Items.Count} 个。"+
                "请先在 Signal 创建一个仅供测试的群，并在群组管理里勾选它。";
        }
        catch(Exception ex){StatusText.Text=$"读取账号或群组失败：{ex.Message}";}
        UpdateButton();
    }

    void Consent_Changed(object sender,RoutedEventArgs e)=>UpdateButton();

    void UpdateButton()
    {
        if(SendButton is not null)
            SendButton.IsEnabled=!_busy && ConsentBox.IsChecked==true &&
                AccountBox.SelectedItem is ManagedAccount &&
                GroupBox.SelectedItem is ManagedGroup;
    }

    async void Reload_Click(object sender,RoutedEventArgs e)=>await ReloadAsync();

    async void Send_Click(object sender,RoutedEventArgs e)
    {
        if(_busy || AccountBox.SelectedItem is not ManagedAccount account ||
           GroupBox.SelectedItem is not ManagedGroup group ||
           ConsentBox.IsChecked!=true)return;

        var question=$"这会真实发送一条 Signal 消息。\n\n"+
            $"账号：{account.Account}\n群组：{group.Name}\n\n"+
            "必须确认群组成员同意测试，并且这是你专门用于测试的群组。\n"+
            "发送后无法撤回，出现超时也不能盲目重试。\n\n确定立即实发吗？";
        if(MessageBox.Show(this,question,"最后确认：真实发出一条消息",
            MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
            return;

        _busy=true;
        UpdateButton();
        StatusText.Text="正在提交真实发送请求。请不要再次点击或直接终止软件。";
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LiveProbeSend,45000,
                new LiveProbeRequest(account.Account,group.GroupId,true));
            var result=Extract<LiveProbeResult>(raw);
            StatusText.Text=$"任务编号：{result.JobId}\n"+
                $"结果：{result.State}\n消息时间戳：{result.ProviderMessageId??"无"}\n"+
                $"{result.Detail}\n请到 Signal 测试群确认是否看到这条消息。";
            if(result.State!="Completed")
                MainWindow.ShowCriticalAlert(
                    $"Signal 实发测试需要核对。\n\n任务：{result.JobId}\n"+
                    $"群组：{result.GroupName}\n状态：{result.State}\n{result.Detail}\n"+
                    "请进入恢复中心，不要重新点击发送。");
        }
        catch(Exception ex)
        {
            StatusText.Text="实发测试没有得到完整确认。\n"+
                $"{ex.Message}\n请先进入恢复中心查看发送记录，禁止盲目重发。";
            MainWindow.ShowCriticalAlert(
                "Signal 实发测试异常，可能已经发出。\n\n"+ex.Message+
                "\n请在 Signal 及恢复中心核对，不要立即重试。");
        }
        finally
        {
            _busy=false;
            ConsentBox.IsChecked=false;
            UpdateButton();
        }
    }
}
