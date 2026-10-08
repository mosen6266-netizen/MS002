using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
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
            await Task.Delay(1100);
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
            var raw=await SendAsync(ControlCommands.Dashboard,2200);
            if(string.IsNullOrWhiteSpace(raw)) throw new IOException("后台无响应");

            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(!root.TryGetProperty("Ok",out var ok) || !ok.GetBoolean())
                throw new IOException(root.TryGetProperty("Error",out var err)?err.GetString():"后台返回错误");

            var data=root.GetProperty("Data");
            var snapshot=JsonSerializer.Deserialize<DashboardSnapshot>(data.GetRawText())
                ?? throw new IOException("无法解析后台状态");

            EngineBadge.Text="● 后台引擎正常";
            EngineBadge.Foreground=Brushes.LightGreen;

            var signalState=snapshot.SignalState.ToLowerInvariant();
            SignalStatusText.Text=signalState switch
            {
                "healthy"=>"正常",
                "busy"=>"繁忙 / 同步中",
                "starting"=>"启动中",
                "fault"=>"异常",
                "external-conflict"=>"端口冲突",
                "missing-runtime"=>"Runtime 缺失",
                _=>snapshot.SignalState
            };
            SignalStatusText.Foreground=signalState switch
            {
                "healthy"=>Brushes.LightGreen,
                "busy" or "starting"=>Brushes.Gold,
                _=>Brushes.IndianRed
            };
            SignalDetailText.Text=snapshot.SignalDetail;
            RuntimeVersionText.Text=$"signal-cli: {snapshot.SignalCliVersion}";

            AccountsCountText.Text=$"{snapshot.LiveSignalAccounts} 在线 · {snapshot.EnabledAccounts}/{snapshot.Accounts} 已登记";
            GroupsCountText.Text=snapshot.Groups.ToString();
            ScriptsCountText.Text=snapshot.Scripts.ToString();

            MigrationBadge.Text=snapshot.MetadataMigrated
                ?"✓ 已读取 V7 本地资料"
                : snapshot.LegacyDetected
                    ?"检测到旧版资料，等待迁移完成"
                    :"当前为全新 V8 数据";

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
            SignalStatusText.Text="未知";
            SignalStatusText.Foreground=Brushes.IndianRed;
            SignalDetailText.Text="后台未连接";
            EngineBadge.Text="● 后台引擎未连接";
            EngineBadge.Foreground=Brushes.IndianRed;
            MigrationBadge.Text=ex.Message;
        }
        finally{_refreshing=false;}
    }

    static async Task<string?> SendAsync(string command,int timeoutMs)
    {
        using var cts=new CancellationTokenSource(timeoutMs);
        await using var pipe=new NamedPipeClientStream(".","SignalScheduler.V8.Control",PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs,cts.Token);
        using var reader=new StreamReader(pipe,leaveOpen:true);
        using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
        await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlRequest(command)));
        return await reader.ReadLineAsync(cts.Token);
    }
}
