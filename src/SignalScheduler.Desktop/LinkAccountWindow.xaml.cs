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
    bool _saving;
    bool _linkComplete;
    string _linkedAccount="";
    readonly HashSet<string> _beforeAccounts=new(StringComparer.Ordinal);

    public LinkAccountWindow()
    {
        InitializeComponent();
        Ui2WindowChrome.Attach(this);
        Loaded+=async(_,_)=>{
            try
            {
                var raw=await MainWindow.SendAsync(
                    ControlCommands.AccountGroupCatalog,3500);
                foreach(var item in UnwrapCatalog(raw).Accounts)
                    _beforeAccounts.Add(item.Account);
            }
            catch { /* Catalog may not be ready until after link. */ }
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
    async void Finish_Click(object sender,RoutedEventArgs e)=>await SaveRemarkAndFinishAsync();
    void Close_Click(object sender,RoutedEventArgs e)=>Close();

    async Task StartLinkAsync()
    {
        if(_starting) return;
        _starting=true;
        _linkComplete=false;
        _linkedAccount="";
        FinishButton.IsEnabled=false;
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
                _linkComplete=true;
                _linkedAccount=snap.Account;
                FinishButton.IsEnabled=true;
                StatusText.Text="✓ 已扫码连接成功";
                DetailText.Text="请确认上方备注，点击「保存备注并完成」。";
            }
        }
        catch { }
    }

    static AccountGroupOverview UnwrapCatalog(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("账户列表暂未就绪。");
        using var doc=JsonDocument.Parse(raw);
        var root=doc.RootElement;
        if(!root.GetProperty("Ok").GetBoolean())
            throw new IOException("后台尚未同步新账号。");
        return JsonSerializer.Deserialize<AccountGroupOverview>(
            root.GetProperty("Data").GetRawText())
            ??throw new IOException("账号列表数据不完整。");
    }

    async Task SaveRemarkAndFinishAsync()
    {
        if(!_linkComplete || _saving)return;
        _saving=true;
        FinishButton.IsEnabled=false;
        try
        {
            var note=RemarkBox.Text.Trim();
            if(note.Length>0)
            {
                ManagedAccount? account=null;
                // Guardian will publish the new account, then catalog sync
                // persists it. Give sync a bounded window to finish.
                for(var i=0;i<25;i++)
                {
                    var raw=await MainWindow.SendAsync(
                        ControlCommands.AccountGroupCatalog,4500);
                    var overview=UnwrapCatalog(raw);
                    account=overview.Accounts.FirstOrDefault(x=>
                        x.Account==_linkedAccount && !string.IsNullOrWhiteSpace(_linkedAccount));
                    account??=overview.Accounts.FirstOrDefault(x=>
                        !_beforeAccounts.Contains(x.Account) && x.Online);
                    if(account is not null)break;
                    StatusText.Text="连接成功，正在同步新账号并保存备注…";
                    await Task.Delay(800);
                }
                if(account is null)
                    throw new IOException(
                        "Signal 已连接，但新账号尚未进入列表。请稍后点击保存，或在账号管理中设置备注。");
                var response=await MainWindow.SendAsync(
                    ControlCommands.UpdateAccount,11000,
                    new UpdateManagedAccount(account.Account,note,
                        account.Enabled,account.Revision));
                using var doc=JsonDocument.Parse(response
                    ??throw new IOException("后台尚未响应"));
                if(!doc.RootElement.GetProperty("Ok").GetBoolean())
                    throw new IOException(
                        doc.RootElement.TryGetProperty("Error",out var err)
                        ?err.GetString():"备注未能保存。");
            }
            DialogResult=true;
            Close();
        }
        catch(Exception ex)
        {
            StatusText.Text="账号连接成功，但备注还没有保存";
            DetailText.Text=ex.Message;
            FinishButton.IsEnabled=true;
        }
        finally{_saving=false;}
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
