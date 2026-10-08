using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class LinkWindow : Window
{
    readonly string _sessionId;
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};

    public LinkWindow(string sessionId,string uri)
    {
        InitializeComponent();
        _sessionId=sessionId;
        RenderQr(uri);
        Loaded+=(_,_)=>_timer.Start();
        Closed+=(_,_)=>_timer.Stop();
        _timer.Tick+=async(_,_)=>await PollAsync();
    }

    void RenderQr(string uri)
    {
        using var generator=new QRCodeGenerator();
        using var data=generator.CreateQrCode(uri,QRCodeGenerator.ECCLevel.M);
        var png=new PngByteQRCode(data);
        var bytes=png.GetGraphic(8);
        var bitmap=new BitmapImage();
        using var ms=new MemoryStream(bytes);
        bitmap.BeginInit();
        bitmap.CacheOption=BitmapCacheOption.OnLoad;
        bitmap.StreamSource=ms;
        bitmap.EndInit();
        bitmap.Freeze();
        QrImage.Source=bitmap;
    }

    async Task PollAsync()
    {
        try
        {
            var raw=await SendAsync(ControlCommands.SignalLinkStatus,new{sessionId=_sessionId},2500);
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(!root.TryGetProperty("Ok",out var ok)||!ok.GetBoolean()) return;
            var data=root.GetProperty("Data");
            var state=data.GetProperty("State").GetString()??"WaitingForScan";
            var detail=data.GetProperty("Detail").GetString()??"";

            StatusText.Text=detail;
            if(state=="Linked")
            {
                StatusText.Foreground=System.Windows.Media.Brushes.LightGreen;
                _timer.Stop();
                await Task.Delay(800);
                DialogResult=true;
                Close();
            }
            else if(state is "Failed" or "Expired")
            {
                StatusText.Foreground=System.Windows.Media.Brushes.IndianRed;
                _timer.Stop();
            }
        }
        catch(Exception ex)
        {
            StatusText.Text="读取登录状态失败："+ex.Message;
            StatusText.Foreground=System.Windows.Media.Brushes.IndianRed;
        }
    }

    async void Close_Click(object sender,RoutedEventArgs e)=>Close();

    static async Task<string> SendAsync(string command,object? payload,int timeoutMs)
    {
        using var cts=new CancellationTokenSource(timeoutMs);
        await using var pipe=new NamedPipeClientStream(".","SignalScheduler.V8.Control",PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs,cts.Token);
        using var reader=new StreamReader(pipe,leaveOpen:true);
        using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
        JsonElement? element=payload is null?null:JsonSerializer.SerializeToElement(payload);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlRequest(command,element)));
        return await reader.ReadLineAsync(cts.Token)??throw new IOException("后台无响应");
    }
}
