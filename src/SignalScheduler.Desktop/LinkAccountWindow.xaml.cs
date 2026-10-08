using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class LinkAccountWindow : Window
{
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    bool _starting;

    public LinkAccountWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>{
            await StartLinkAsync();
            _timer.Tick+=async(_,_)=>await PollAsync();
            _timer.Start();
        };
        Closed+=async(_,_)=>{
            _timer.Stop();
            try { await MainWindow.SendAsync(ControlCommands.CancelLink,700); } catch { }
        };
    }

    async void Regenerate_Click(object sender,RoutedEventArgs e)=>await StartLinkAsync();
    void Close_Click(object sender,RoutedEventArgs e)=>Close();

    async Task StartLinkAsync()
    {
        if(_starting) return;
        _starting=true;
        try
        {
            StatusText.Text="正在创建二维码…";
            DetailText.Text="";
            QrImage.Source=null;
            try { await MainWindow.SendAsync(ControlCommands.CancelLink,700); } catch { }

            var raw=await MainWindow.SendAsync(ControlCommands.StartLink,5000);
            var snap=Parse(raw);
            if(snap is null) throw new IOException("后台没有返回链接状态");
            Render(snap);
        }
        catch(Exception ex)
        {
            StatusText.Text="无法创建二维码";
            DetailText.Text=ex.Message;
        }
        finally{_starting=false;}
    }

    async Task PollAsync()
    {
        try
        {
            var raw=await MainWindow.SendAsync(ControlCommands.LinkStatus,1600);
            var snap=Parse(raw);
            if(snap is null) return;
            Render(snap);

            if(snap.State=="complete")
            {
                _timer.Stop();
                await Task.Delay(500);
                MessageBox.Show(this,
                    string.IsNullOrWhiteSpace(snap.Account)?"Signal 账号已连接。":$"Signal 账号已连接：\n{snap.Account}",
                    "连接成功",MessageBoxButton.OK,MessageBoxImage.Information);
                DialogResult=true;
                Close();
            }
        }
        catch { }
    }

    void Render(SignalLinkSnapshot snap)
    {
        StatusText.Text=snap.State switch
        {
            "starting"=>"正在创建二维码…",
            "waiting"=>"请使用手机 Signal 扫描二维码",
            "complete"=>"连接成功",
            "failed"=>"连接失败",
            "expired"=>"二维码已过期",
            "cancelled"=>"已取消",
            _=>snap.State
        };
        DetailText.Text=snap.Detail;

        if(!string.IsNullOrWhiteSpace(snap.DeviceLinkUri) && QrImage.Source is null)
            QrImage.Source=CreateQr(snap.DeviceLinkUri);
    }

    static SignalLinkSnapshot? Parse(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw)) return null;
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.TryGetProperty("Ok",out var ok) || !ok.GetBoolean())
            throw new IOException(root.TryGetProperty("Error",out var e)?e.GetString():"后台链接失败");
        return JsonSerializer.Deserialize<SignalLinkSnapshot>(root.GetProperty("Data").GetRawText());
    }

    static BitmapImage CreateQr(string text)
    {
        using var generator=new QRCodeGenerator();
        using var data=generator.CreateQrCode(text,QRCodeGenerator.ECCLevel.Q);
        var png=new PngByteQRCode(data).GetGraphic(8);
        var image=new BitmapImage();
        using var ms=new MemoryStream(png);
        image.BeginInit();
        image.CacheOption=BitmapCacheOption.OnLoad;
        image.StreamSource=ms;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
