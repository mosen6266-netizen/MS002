using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
    bool _engineFaultNotified;
    string? _signalFaultSignature;
    readonly HashSet<string> _notifiedRecoveryJobs=new(StringComparer.Ordinal);

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

    async void LinkAccount_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new LinkAccountWindow{Owner=this};
        dialog.ShowDialog();
        await RefreshAsync();
    }

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

            _engineFaultNotified=false;
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

            if(signalState is "fault" or "external-conflict" or "missing-runtime")
            {
                var signature=$"{signalState}|{snapshot.SignalDetail}";
                if(_signalFaultSignature!=signature)
                {
                    _signalFaultSignature=signature;
                    ShowCriticalAlert($"Signal 运行异常：{snapshot.SignalDetail}

任务不会因为 Signal 恢复而自动继续，请检查运行状态。");
                }
            }
            else
            {
                _signalFaultSignature=null;
            }
            LinkAccountButton.IsEnabled=signalState=="healthy";

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
                ?"暂无任务"
                :$"{snapshot.Jobs} 个任务 · {snapshot.RecoveryJobs} 个需要人工确认";

            var unresolved=snapshot.JobItems
                .Where(x=>x.RecoveryRequired ||
                    string.Equals(x.State,"RecoveryRequired",StringComparison.OrdinalIgnoreCase))
                .Select(x=>$"{x.LegacyId}:{x.Name}")
                .ToHashSet(StringComparer.Ordinal);
            var newlyUnresolved=unresolved.Except(_notifiedRecoveryJobs,StringComparer.Ordinal).Count();
            _notifiedRecoveryJobs.IntersectWith(unresolved);
            _notifiedRecoveryJobs.UnionWith(unresolved);
            if(newlyUnresolved>0)
            {
                ShowCriticalAlert(
                    $"检测到 {newlyUnresolved} 个新出现的待人工恢复任务（当前共 {snapshot.RecoveryJobs} 个）。

"+
                    "为避免重复发送或漏发，程序已将对应任务标记为待恢复，不会自动重新发送。请打开“运行任务”确认。");
            }
        }
        catch(Exception ex)
        {
            SignalStatusText.Text="未知";
            SignalStatusText.Foreground=Brushes.IndianRed;
            SignalDetailText.Text="后台未连接";
            LinkAccountButton.IsEnabled=false;
            EngineBadge.Text="● 后台引擎未连接";
            EngineBadge.Foreground=Brushes.IndianRed;
            MigrationBadge.Text=ex.Message;
            if(!_engineFaultNotified)
            {
                _engineFaultNotified=true;
                ShowCriticalAlert(
                    $"Signal 调度台后台连接异常：{ex.Message}

请检查后台引擎与本地日志，不要假定任务仍在正常运行。");
            }
        }
        finally{_refreshing=false;}
    }

    [DllImport("user32.dll",EntryPoint="MessageBoxW",CharSet=CharSet.Unicode)]
    static extern int NativeMessageBox(IntPtr parent,string message,string caption,uint type);

    /// <summary>
    /// An unowned native TOPMOST foreground alert stays visible even when the
    /// WPF window is minimized. Run it off the UI thread so status refreshes
    /// and the main window remain responsive while the user reads the alert.
    /// </summary>
    static void ShowCriticalAlert(string message)
    {
        _=Task.Run(()=>
        {
            try
            {
                const uint MbTopMost=0x00040000;
                const uint MbSetForeground=0x00010000;
                const uint MbIconWarning=0x00000030;
                NativeMessageBox(IntPtr.Zero,message,"Signal 调度台 - 重要提醒",
                    MbTopMost|MbSetForeground|MbIconWarning);
            }
            catch { }
        });
    }

    internal static async Task<string?> SendAsync(string command,int timeoutMs)
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
