using MessageBox = SignalScheduler.Desktop.Ui2MessageBox;
using System.Windows.Controls;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class LicenseWindow : UserControl
{
    bool _activating;
    int _statusRequestSerial;
    public LicenseWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>await RefreshAsync(false);
    }

    static LicensePublicStatus ReadStatus(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("后台授权模块没有响应。");
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.GetProperty("Ok").GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var err)
                ?err.GetString():"授权操作失败。");
        return JsonSerializer.Deserialize<LicensePublicStatus>(
            root.GetProperty("Data").GetRawText())
            ??throw new IOException("无法读取授权状态。");
    }

    async Task RefreshAsync(bool online)
    {
        var serial=++_statusRequestSerial;
        try
        {
            var raw=await MainWindow.SendAsync(
                online?ControlCommands.LicenseCheck:ControlCommands.LicenseStatus,
                online?20000:5000);
            if(serial==_statusRequestSerial)Render(ReadStatus(raw));
        }
        catch(Exception ex)
        {
            if(serial!=_statusRequestSerial)return;
            StateText.Text="查询失败";
            StateText.Foreground=Brushes.IndianRed;
            FeedbackText.Text=$"授权查询失败：{ex.Message}";
        }
    }

    void Render(LicensePublicStatus status)
    {
        StateText.Text=status.State switch
        {
            "active"=>"授权有效（在线已验证）",
            "active-offline"=>"短期离线授权有效",
            "unactivated"=>"未激活",
            "offline-expired"=>"离线授权过期",
            "invalid"=>"授权无效",
            "error"=>"授权检查异常",
            _=>"等待授权校验"
        };
        StateText.Foreground=status.State is "active" or "active-offline"
            ?Brushes.LightGreen:Brushes.Gold;
        TypeText.Text=$"卡密类型：{(string.IsNullOrWhiteSpace(status.TypeName)?"—":status.TypeName)}";
        ExpiryText.Text=status.ExpiresAt>0
            ?$"到期时间：{DateTimeOffset.FromUnixTimeSeconds(status.ExpiresAt).ToLocalTime():yyyy-MM-dd HH:mm}"
            :status.HasSavedLicense?"到期时间：永久或尚未读取":"到期时间：—";
        DetailText.Text=status.Detail;
        FeedbackText.Text="当前授权资料使用 Windows 当前用户加密保存。"+
            (status.ServerReachable?" 已连接授权服务器。":" 目前未确认服务器在线状态。");
    }

    async void Activate_Click(object sender,RoutedEventArgs e)
    {
        if(_activating)return;
        var card=KeyBox.Password.Trim();
        if(string.IsNullOrWhiteSpace(card))
        {
            FeedbackText.Text="请输入卡密。";
            return;
        }
        if(MessageBox.Show(Window.GetWindow(this),
            "首次激活可能立即开始计算卡密有效期，并绑定当前设备。\n\n确定要激活吗？",
            "确认卡密激活",MessageBoxButton.YesNo,MessageBoxImage.Warning)
            !=MessageBoxResult.Yes) return;

        _activating=true;
        ++_statusRequestSerial; // Ignore older status requests during activation.
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LicenseActivate,20000,
                new LicenseActivationRequest(card));
            Render(ReadStatus(raw));
            FeedbackText.Text="授权已存到本地用户数据目录（Windows 加密）。";
        }
        catch(Exception ex)
        {
            FeedbackText.Text=$"激活失败：{ex.Message}";
            MessageBox.Show(Window.GetWindow(this),ex.Message,"授权激活失败",
                MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            // Never keep the user's key visible in an input control.
            KeyBox.Clear();
            _activating=false;
        }
    }

    async void Check_Click(object sender,RoutedEventArgs e)=>await RefreshAsync(true);
}
