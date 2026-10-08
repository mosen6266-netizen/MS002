using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class MainWindow : Window
{
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(5)};
    bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>{
            await EnsureEngineAsync();
            await RefreshAsync();
            _timer.Start();
        };
        _timer.Tick+=async(_,_)=>await RefreshAsync();
        Closed+=(_,_)=>_timer.Stop();
    }

    async Task EnsureEngineAsync()
    {
        try
        {
            var ping=await SendAsync(ControlCommands.Ping,400);
            if(ping is not null) return;
        }
        catch { }

        try
        {
            var engine=Path.Combine(AppContext.BaseDirectory,"Engine","SignalScheduler.Engine.exe");
            if(!File.Exists(engine)) return;
            Process.Start(new ProcessStartInfo{
                FileName=engine,
                Arguments="--background",
                UseShellExecute=false,
                CreateNoWindow=true,
                WindowStyle=ProcessWindowStyle.Hidden
            });
            await Task.Delay(900);
        }
        catch { }
    }

    async void RefreshButton_Click(object sender,RoutedEventArgs e)=>await RefreshAsync();

    async Task RefreshAsync()
    {
        if(_refreshing) return;
        _refreshing=true;
        try
        {
            var raw=await SendAsync(ControlCommands.Dashboard,1800);
            if(string.IsNullOrWhiteSpace(raw)) throw new IOException("后台无响应");

            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(!root.TryGetProperty("Ok",out var ok) || !ok.GetBoolean())
                throw new IOException(root.TryGetProperty("Error",out var err)?err.GetString():"后台返回错误");

            var data=root.GetProperty("Data");
            var snapshot=JsonSerializer.Deserialize<DashboardSnapshot>(data.GetRawText())
                ?? throw new IOException("无法解析后台状态");

            var signalHealthy=string.Equals(snapshot.TransportState,"healthy",StringComparison.OrdinalIgnoreCase);
            var signalBusy=string.Equals(snapshot.TransportState,"busy",StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(snapshot.TransportState,"starting",StringComparison.OrdinalIgnoreCase);
            EngineStatusText.Text=signalHealthy?"正常":signalBusy?"同步中":"异常";
            EngineStatusText.Foreground=signalHealthy?System.Windows.Media.Brushes.LightGreen:
                signalBusy?System.Windows.Media.Brushes.Khaki:System.Windows.Media.Brushes.IndianRed;
            EngineBadge.Text=$"● Signal {EngineStatusText.Text} · {snapshot.SignalCliVersion}";
            EngineBadge.Foreground=EngineStatusText.Foreground;
            ScanLoginButton.IsEnabled=signalHealthy;

            AccountsCountText.Text=$"{snapshot.EnabledAccounts} / {snapshot.Accounts}";
            GroupsCountText.Text=snapshot.Groups.ToString();
            ScriptsCountText.Text=snapshot.Scripts.ToString();

            MigrationBadge.Text=(snapshot.MetadataMigrated
                ?"✓ 已读取 V7.6.2 本地资料"
                : snapshot.LegacyDetected
                    ?"检测到旧版资料，等待迁移完成"
                    :"当前为全新 V8 数据") + $" · {snapshot.TransportDetail}";

            AccountsList.ItemsSource=snapshot.AccountItems
                .Select(x=>$"{(x.Enabled?"●":"○")} {x.Label}   {x.Account}")
                .ToArray();
            GroupsList.ItemsSource=snapshot.GroupItems
                .Select(x=>$"{(x.Enabled?"●":"○")} {x.Name}")
                .ToArray();
            ScriptsList.ItemsSource=snapshot.ScriptItems
                .Select(x=>$"{x.Name}   · {x.StepCount} 条")
                .ToArray();
            JobsList.ItemsSource=snapshot.JobItems
                .Select(x=>$"{x.Name}   · {x.State}   · 第 {x.Cursor+1} 条")
                .ToArray();
            JobsSummaryText.Text=snapshot.Jobs==0
                ?"暂无迁移任务"
                :$"{snapshot.Jobs} 个历史任务 · {snapshot.RecoveryJobs} 个需要人工确认";
        }
        catch(Exception ex)
        {
            EngineStatusText.Text="异常";
            EngineStatusText.Foreground=System.Windows.Media.Brushes.IndianRed;
            EngineBadge.Text="● 后台引擎未连接";
            EngineBadge.Foreground=System.Windows.Media.Brushes.IndianRed;
            MigrationBadge.Text=ex.Message;
            ScanLoginButton.IsEnabled=false;
        }
        finally{_refreshing=false;}
    }

    async void ScanLoginButton_Click(object sender,RoutedEventArgs e)
    {
        ScanLoginButton.IsEnabled=false;
        try
        {
            var raw=await SendAsync(ControlCommands.SignalStartLink,new{deviceName="Signal Auto Scheduler V8"},35000);
            if(string.IsNullOrWhiteSpace(raw)) throw new IOException("后台无响应");
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(!root.TryGetProperty("Ok",out var ok)||!ok.GetBoolean())
                throw new IOException(root.TryGetProperty("Error",out var er)?er.GetString():"无法创建二维码");

            var data=root.GetProperty("Data");
            var sessionId=data.GetProperty("SessionId").GetString();
            var uri=data.GetProperty("DeviceLinkUri").GetString();
            if(string.IsNullOrWhiteSpace(sessionId)||string.IsNullOrWhiteSpace(uri))
                throw new IOException("二维码会话无效");

            var window=new LinkWindow(sessionId!,uri!){Owner=this};
            var linked=window.ShowDialog()==true;
            if(linked)
            {
                await Task.Delay(1200);
                await RefreshAsync();
            }
        }
        catch(Exception ex)
        {
            MessageBox.Show(ex.Message,"Signal 扫码登录",MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            ScanLoginButton.IsEnabled=true;
        }
    }

    static Task<string?> SendAsync(string command,int timeoutMs)=>SendAsync(command,null,timeoutMs);

    static async Task<string?> SendAsync(string command,object? payload,int timeoutMs)
    {
        using var cts=new CancellationTokenSource(timeoutMs);
        await using var pipe=new NamedPipeClientStream(".","SignalScheduler.V8.Control",PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs,cts.Token);
        using var reader=new StreamReader(pipe,leaveOpen:true);
        using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
        JsonElement? element=payload is null?null:JsonSerializer.SerializeToElement(payload);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlRequest(command,element)));
        return await reader.ReadLineAsync(cts.Token);
    }
}
