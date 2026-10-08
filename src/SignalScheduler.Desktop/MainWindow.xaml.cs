using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

/// <summary>
/// The only primary application Window. Pages are UserControls hosted in the
/// same content area; QR login and explicit confirmations remain true dialogs.
/// </summary>
public partial class MainWindow : Window
{
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(5)};
    readonly Dictionary<string,UserControl> _pages=new(StringComparer.Ordinal);
    readonly ObservableCollection<string> _notifications=new();
    readonly HashSet<string> _seenNotifications=new(StringComparer.Ordinal);
    readonly HashSet<string> _unresolvedJobs=new(StringComparer.Ordinal);
    readonly HashSet<string> _batchAlerts=new(StringComparer.Ordinal);
    bool _refreshing;
    bool _engineNotified;
    readonly Queue<string> _criticalQueue=new();
    bool _criticalDialogOpen;
    bool _hasLiveJobs;
    string? _signalIssue;
    string _currentPage="home";

    public MainWindow()
    {
        InitializeComponent();
        Application.Current.MainWindow=this;
        NotificationsList.ItemsSource=_notifications;
        Loaded+=async(_,_)=>
        {
            // Launch the Engine before the home page fetches scripts/groups.
            // Otherwise the first data request races Engine initialization
            // and displays an empty catalog until a manual refresh.
            await EnsureEngineAsync();
            Navigate("home");
            await RefreshAsync();
            _timer.Start();
        };
        _timer.Tick+=async(_,_)=>await RefreshAsync();
        Closed+=(_,_)=>_timer.Stop();
    }

    void WorkspaceScroll_PreviewMouseWheel(object sender,
        System.Windows.Input.MouseWheelEventArgs e)
    {
        if(sender is not ScrollViewer scroller || e.Delta==0)return;
        // Let grids and the scripts list consume their own wheel events.
        if(_currentPage is "accounts" or "scripts" or "recovery")return;
        // Do not move the underlying page while a script popup is open.
        if(_pages.TryGetValue("home",out var home) &&
           home is LiveBatchWindow batch &&
           batch.IsScriptDropDownOpen)
        {
            e.Handled=true;
            return;
        }
        scroller.ScrollToVerticalOffset(Math.Clamp(
            scroller.VerticalOffset-e.Delta/3.0,0,scroller.ScrollableHeight));
        e.Handled=true;
    }

    UserControl GetPage(string key)
    {
        if(_pages.TryGetValue(key,out var cached))return cached;
        UserControl page=key switch
        {
            "home"=>new LiveBatchWindow(),
            "accounts"=>new AccountGroupWindow(),
            "scripts"=>new ScriptEditorWindow(),
            "recovery"=>new RecoveryCenterWindow(),
            "license"=>new LicenseWindow(),
            _=>throw new ArgumentException("未知的导航位置。")
        };
        _pages.Add(key,page);
        return page;
    }

    public void NavigateHome()=>Navigate("home");
    public void NavigateTo(string page)=>Navigate(page);
    void Minimize_Click(object sender,RoutedEventArgs e)=>
        WindowState=WindowState.Minimized;

    void Maximize_Click(object sender,RoutedEventArgs e)
    {
        WindowState=WindowState==WindowState.Maximized
            ?WindowState.Normal:WindowState.Maximized;
        MaximizeButton.Content=WindowState==WindowState.Maximized?"❐":"□";
    }

    void WindowClose_Click(object sender,RoutedEventArgs e)=>Close();


    void Nav_Click(object sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:string key})Navigate(key);
    }

    void Navigate(string key)
    {
        if(key==_currentPage && PageHost.Content is not null)return;
        if(_currentPage=="scripts" &&
           _pages.TryGetValue("scripts",out var old) &&
           old is ScriptEditorWindow editor &&
           !editor.CanLeave())return;

        // The editor is a single-height workspace; other pages can use full-page scroll.
        WorkspaceScroll.VerticalScrollBarVisibility=key=="scripts"
            ?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
        PageHost.Height=key=="scripts"
            ?Math.Max(650,WorkspaceScroll.ActualHeight):double.NaN;
        PageHost.Content=GetPage(key);
        _currentPage=key;
        var (heading,subtitle)=key switch
        {
            "home"=>("首页 · 快速开始",
                "直接勾选群组、选择剧本并启动；进行中的任务在同一页独立管理。"),
            "accounts"=>("账号与群组",
                "管理账号备注、可用状态与 Signal 群组，不再打开新窗口。"),
            "scripts"=>("剧本管理",
                "编辑消息、图片、发送间隔和提醒；修改保存在本地。"),
            "recovery"=>("任务与恢复",
                "核对真实发送回执、暂停异常任务，避免未知结果被重复发送。"),
            "license"=>("卡密与授权",
                "激活或核验现有卡密；本机资料继续保存在用户目录。"),
            _=>("Signal 调度台","")
        };
        PageTitle.Text=heading;
        PageSubtitle.Text=subtitle;
        foreach(var b in new[]{HomeNav,AccountNav,ScriptNav,TaskNav,LicenseNav})
            b.Background=Equals(b.Tag,key)
                ?new SolidColorBrush(Color.FromRgb(34,73,111))
                :Brushes.Transparent;
    }

    void MainWindow_Closing(object sender,CancelEventArgs e)
    {
        if(_currentPage=="scripts" &&
            _pages.TryGetValue("scripts",out var p) &&
            p is ScriptEditorWindow editor && !editor.CanLeave())
        {
            e.Cancel=true;
            return;
        }
        if(_hasLiveJobs)
        {
            var result=MessageBox.Show(this,
                "仍有正在运行的真实群组任务。\n\n"+
                "关闭界面不等于停止后台发送。要停止，请先到首页暂停对应任务。\n\n"+
                "仍要关闭这个窗口、让后台继续运行吗？",
                "确认关闭 Signal 调度台",
                MessageBoxButton.YesNo,MessageBoxImage.Warning);
            if(result!=MessageBoxResult.Yes)e.Cancel=true;
        }
    }

    async Task EnsureEngineAsync()
    {
        try
        {
            var ping=await SendAsync(ControlCommands.Ping,600);
            if(ping is not null)return;
        }
        catch { }
        try
        {
            var path=Path.Combine(AppContext.BaseDirectory,
                "Engine","SignalScheduler.Engine.exe");
            if(!File.Exists(path))
            {
                BottomStatusText.Text="后台执行文件缺失，请重新安装。";
                return;
            }
            Process.Start(new ProcessStartInfo{
                FileName=path,Arguments="--background",
                UseShellExecute=false,CreateNoWindow=true,
                WindowStyle=ProcessWindowStyle.Hidden
            });
            // The Engine also migrates V7 metadata and opens signal-cli.
            // Give its local control pipe a short bounded readiness period.
            for(var i=0;i<12;i++)
            {
                await Task.Delay(450);
                try
                {
                    if(await SendAsync(ControlCommands.Ping,500) is not null)
                        break;
                }
                catch { }
            }
        }
        catch(Exception ex)
        {
            BottomStatusText.Text="启动后台失败："+ex.Message;
        }
    }

    async void RefreshButton_Click(object sender,RoutedEventArgs e)
    {
        await RefreshAsync();
        // Reload only the visible management page, not the script draft.
        if(_currentPage=="home")NavigateHome();
    }

    async void LinkAccount_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new LinkAccountWindow {Owner=this};
        dialog.ShowDialog();
        await RefreshAsync();
        // Avoid a stale account/group catalog after completing a QR login.
        _pages.Remove("accounts");
        if(_currentPage=="accounts")PageHost.Content=GetPage("accounts");
        if(_currentPage=="home")
        {
            _pages.Remove("home");
            PageHost.Content=GetPage("home");
        }
    }

    async Task RefreshAsync()
    {
        if(_refreshing)return;
        _refreshing=true;
        try
        {
            var raw=await SendAsync(ControlCommands.Dashboard,4000);
            if(string.IsNullOrWhiteSpace(raw))
                throw new IOException("后台没有响应。");
            var snapshot=Unwrap<DashboardSnapshot>(raw);
            EngineBadge.Text="● 后台引擎已连接";
            EngineBadge.Foreground=Brushes.LightGreen;
            _engineNotified=false;

            var healthy=string.Equals(snapshot.SignalState,"healthy",
                StringComparison.OrdinalIgnoreCase);
            SignalStatusText.Text="Signal："+(healthy?"正常":snapshot.SignalState);
            SignalStatusText.Foreground=healthy?Brushes.LightGreen:Brushes.Gold;
            ScanButton.IsEnabled=healthy;
            AccountsCountText.Text=$"账号 {snapshot.LiveSignalAccounts} 在线 · 群 {snapshot.Groups} · 剧本 {snapshot.Scripts}";
            RuntimeVersionText.Text="signal-cli "+snapshot.SignalCliVersion;
            MigrationBadge.Text=snapshot.MetadataMigrated
                ?"✓ V7 本地数据已读取"
                :snapshot.LegacyDetected?"检测到旧版资料":"独立 V8 本地数据";
            BottomStatusText.Text=healthy
                ?"后台服务正常 · 每个群组任务独立保存"
                :"Signal 服务："+snapshot.SignalDetail;

            var failed=snapshot.SignalState.ToLowerInvariant() is
                "fault" or "external-conflict" or "missing-runtime";
            var signature=failed?$"{snapshot.SignalState}|{snapshot.SignalDetail}":null;
            if(failed&&_signalIssue!=signature)
                ShowCriticalAlert("Signal 服务异常："+snapshot.SignalDetail+
                    "\n已运行的任务不会因为连接恢复而自动继续。");
            _signalIssue=signature;

            var unresolved=snapshot.JobItems
                .Where(x=>x.RecoveryRequired ||
                    x.State=="RecoveryRequired")
                .Select(x=>$"{x.LegacyId}:{x.Name}")
                .ToHashSet(StringComparer.Ordinal);
            if(unresolved.Except(_unresolvedJobs).Any())
                ShowCriticalAlert("检测到发送结果待确认的任务。\n"+
                    "请查看「任务与恢复」，在 Signal 核对后再决定后续操作。");
            _unresolvedJobs.Clear();
            _unresolvedJobs.UnionWith(unresolved);

            await RefreshLiveJobsAsync();
        }
        catch(Exception ex)
        {
            EngineBadge.Text="● 后台未连接";
            EngineBadge.Foreground=Brushes.IndianRed;
            ScanButton.IsEnabled=false;
            SignalStatusText.Text="Signal 状态未知";
            BottomStatusText.Text="后台连接失败："+ex.Message;
            if(!_engineNotified)
            {
                _engineNotified=true;
                AddNotification("Signal 后台连接异常："+ex.Message+
                    "\n请检查后台程序；不要假设任务仍在正常运行。",false);
            }
        }
        finally{_refreshing=false;}
    }

    async Task RefreshLiveJobsAsync()
    {
        try
        {
            var response=await SendAsync(ControlCommands.LiveBatchList,4000);
            var jobs=Unwrap<List<LiveBatchItem>>(response);
            _hasLiveJobs=jobs.Any(x=>x.State=="Running");
            var current=new HashSet<string>(StringComparer.Ordinal);
            foreach(var job in jobs)
            {
                var flagged=job.State=="RecoveryRequired" ||
                   (job.State=="Paused" &&
                    (job.Detail.Contains("提醒",StringComparison.Ordinal)||
                     job.Detail.Contains("异常",StringComparison.Ordinal)||
                     job.Detail.Contains("断开",StringComparison.Ordinal)||
                     job.Detail.Contains("授权",StringComparison.Ordinal)||
                     job.Detail.Contains("失败",StringComparison.Ordinal)));
                if(!flagged)continue;
                var marker=$"{job.JobId}:{job.Cursor}:{job.State}";
                current.Add(marker);
                if(_batchAlerts.Add(marker))
                    ShowCriticalAlert(
                        $"群组任务需要处理\n剧本：{job.ScriptName}\n"+
                        $"群组：{job.GroupName}\n进度：{job.Cursor}/{job.TotalSteps}\n"+
                        $"状态：{job.State}\n{job.Detail}");
            }
            _batchAlerts.IntersectWith(current);
        }
        catch { /* Connection diagnostic is handled by dashboard. */ }
    }

    static T Unwrap<T>(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))
            throw new IOException("后台没有返回数据。");
        using var doc=JsonDocument.Parse(raw);
        var reply=doc.RootElement;
        if(!reply.GetProperty("Ok").GetBoolean())
            throw new IOException(reply.TryGetProperty("Error",out var err)
                ?err.GetString():"读取后台数据失败。");
        return JsonSerializer.Deserialize<T>(
            reply.GetProperty("Data").GetRawText())
            ??throw new IOException("后台数据不完整。");
    }

    /// <summary>
    /// Single notification inbox. No unowned native MessageBox on worker
    /// threads; no overlapping independent windows hiding their owner.
    /// At most one owner-modal critical dialog is shown in a session.
    /// All subsequent alerts are retained in the notification drawer.
    /// </summary>
    internal static void ShowCriticalAlert(string message)
    {
        var app=Application.Current;
        if(app?.MainWindow is not MainWindow shell)return;
        app.Dispatcher.BeginInvoke(new Action(()=>
            shell.AddNotification(message,true)));
    }

    void AddNotification(string message,bool critical)
    {
        if(string.IsNullOrWhiteSpace(message) ||
           !_seenNotifications.Add(message))return;
        _notifications.Insert(0,$"{DateTime.Now:HH:mm:ss}  {message}");
        if(_notifications.Count>100)_notifications.RemoveAt(_notifications.Count-1);
        AlertStrip.Visibility=Visibility.Visible;
        AlertSummaryText.Text=$"当前有 {_notifications.Count} 条提示 · 点击查看";
        if(critical)
        {
            _criticalQueue.Enqueue(message);
            ShowNextCritical();
        }
    }

    void ShowNextCritical()
    {
        if(_criticalDialogOpen || _criticalQueue.Count==0)return;
        _criticalDialogOpen=true;
        var message=_criticalQueue.Dequeue();
        Dispatcher.BeginInvoke(new Action(()=>
        {
            var originalTopmost=Topmost;
            try
            {
                if(WindowState==WindowState.Minimized)
                    WindowState=WindowState.Normal;
                Show();
                Topmost=true;
                Activate();
                MessageBox.Show(this,
                    "发现任务或 Signal 异常，请及时处理。\\n\\n"+message,
                    "Signal 调度台 - 重要提醒",
                    MessageBoxButton.OK,MessageBoxImage.Warning);
            }
            finally
            {
                Topmost=originalTopmost;
                _criticalDialogOpen=false;
                ShowNextCritical();
            }
        }),DispatcherPriority.Background);
    }

    void ToggleAlerts_Click(object sender,RoutedEventArgs e)
    {
        AlertPanel.Visibility=AlertPanel.Visibility==Visibility.Visible
            ?Visibility.Collapsed:Visibility.Visible;
    }

    void ClearAlerts_Click(object sender,RoutedEventArgs e)
    {
        _notifications.Clear();
        _seenNotifications.Clear();
        AlertPanel.Visibility=Visibility.Collapsed;
        AlertStrip.Visibility=Visibility.Collapsed;
        // Do not reset the modal allowance; a flood cannot create new dialogs.
    }

    internal static async Task<string?> SendAsync(
        string command,int timeoutMs,object? payload=null)
    {
        using var cts=new CancellationTokenSource(timeoutMs);
        await using var pipe=new NamedPipeClientStream(
            ".","SignalScheduler.V8.Control",
            PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs,cts.Token);
        using var reader=new StreamReader(pipe,leaveOpen:true);
        using var writer=new StreamWriter(pipe,leaveOpen:true)
        {AutoFlush=true};
        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new ControlRequest(command,payload is null?null:
                JsonSerializer.SerializeToElement(payload))));
        return await reader.ReadLineAsync(cts.Token);
    }
}