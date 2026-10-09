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
    readonly HashSet<string> _unresolvedJobs=new(StringComparer.Ordinal);
    readonly HashSet<string> _batchAlerts=new(StringComparer.Ordinal);
    bool _refreshing;
    bool _engineNotified;
    readonly CriticalAlertQueue _criticalQueue=new();
    bool _hasLiveJobs;
    bool _exportingDiagnostic;
    string? _signalIssue;
    string _currentPage="home";

    public MainWindow()
    {
        InitializeComponent();
        PreviewMouseWheel+=GlobalWheel_PreviewMouseWheel;
        SizeChanged+=(_,_)=>AdjustNavigationForViewport();
        WorkspaceScroll.SizeChanged+=(_,_)=>
        {
            if(_currentPage=="scripts")
                PageHost.Height=Math.Max(300,WorkspaceScroll.ActualHeight);
        };
        Application.Current.MainWindow=this;
        NotificationsList.ItemsSource=_notifications;
        Loaded+=(_,_)=>FitStartupWindowToWorkArea();
        Loaded+=(_,_)=>ShowNextCritical();
        // A temporarily unavailable modal can be presented again when the
        // operator reactivates this window. TryBegin prevents overlapping dialogs.
        Activated+=(_,_)=>ShowNextCritical();
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

    void FitStartupWindowToWorkArea()
    {
        // A 1500x950 default can exceed a 1366x768 laptop's usable desktop.
        // Shrink only the initial window, keeping resize/maximize fully usable.
        if(WindowState!=WindowState.Normal)return;
        var area=SystemParameters.WorkArea;
        if(area.Width<650 || area.Height<470)return;
        var maxWidth=area.Width-20;
        var maxHeight=area.Height-20;
        MinWidth=Math.Min(MinWidth,maxWidth);
        MinHeight=Math.Min(MinHeight,maxHeight);
        Width=Math.Min(Width,maxWidth);
        Height=Math.Min(Height,maxHeight);
        Left=area.Left+(area.Width-Width)/2;
        Top=area.Top+(area.Height-Height)/2;
        AdjustNavigationForViewport();
    }

    void AdjustNavigationForViewport()
    {
        // WPF sizes are device-independent pixels. At 125–200% scaling,
        // 215 DIP of navigation can unnecessarily crowd a laptop display.
        var compact=ActualWidth>0 && ActualWidth<1120;
        var newWidth=compact?184:215;
        if(Math.Abs(NavigationColumn.Width.Value-newWidth)>0.1)
            NavigationColumn.Width=new GridLength(newWidth);
        HomeNav.Content=compact?"⌂   首页":"⌂   首页 · 快速开始";
        AccountNav.Content=compact?"◉   账号群组":"◉   账号与群组";
        ScriptNav.Content="✎   剧本管理";
        TaskNav.Content="◷   运行任务";
        RecoveryNav.Content=compact?"⚠   恢复":"⚠   异常恢复";
        HistoryNav.Content="◷   历史记录";
        LicenseNav.Content=compact?"⚙   授权":"⚙   授权设置";
        // Keep an escape route for unusually small screens, rather than
        // clipping content silently. Normal widths never gain a scrollbar.
        WorkspaceScroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Auto;
    }

    void GlobalWheel_PreviewMouseWheel(object sender,
        System.Windows.Input.MouseWheelEventArgs e)
    {
        if(e.Delta==0 || e.OriginalSource is not DependencyObject source)return;
        // Do not override native text editing, dropdown, popup, scrollbar
        // or Shift/Ctrl wheel interactions.
        if(System.Windows.Input.Keyboard.Modifiers!=
           System.Windows.Input.ModifierKeys.None)return;
        DependencyObject? node=source;
        ScrollViewer? destination=null;
        while(node is not null)
        {
            if(node is TextBox ||
               node is ComboBox ||
               node is System.Windows.Controls.MenuItem ||
               node is System.Windows.Controls.ContextMenu ||
               node is System.Windows.Controls.Primitives.ScrollBar)
                return;
            if(node is ScrollViewer candidate && candidate.ScrollableHeight>0)
            {
                destination=candidate;
                break;
            }
            node=node is Visual
                ?VisualTreeHelper.GetParent(node)
                :LogicalTreeHelper.GetParent(node);
        }
        // The script ComboBox popup is owned by a different visual root,
        // but older WPF templates may still forward its wheel to the window.
        if(_pages.TryGetValue("home",out var home) &&
           home is LiveBatchWindow batch && batch.IsScriptDropDownOpen)return;
        destination??=WorkspaceScroll.ScrollableHeight>0
            ?WorkspaceScroll:null;
        if(destination is null)return;
        var step=destination==WorkspaceScroll
            ?Math.Min(Math.Abs(e.Delta)*0.55,70)
            :Math.Min(Math.Abs(e.Delta)*0.40,48);
        destination.ScrollToVerticalOffset(Math.Clamp(
            destination.VerticalOffset-Math.Sign(e.Delta)*step,
            0,destination.ScrollableHeight));
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
            "tasks"=>new RunningTasksWindow(),
            "recovery"=>new RecoveryCenterWindow(),
            "history"=>new HistoryWindow(),
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
            ?Math.Max(300,WorkspaceScroll.ActualHeight):double.NaN;
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
            "tasks"=>("运行任务",
                "按群监控消息阶段、等待时间与进度；异常发送请在恢复中心核对。"),
            "history"=>("历史记录","查看已结束任务及其每条消息的发送结果。"),
            "recovery"=>("异常恢复",
                "核对真实发送回执、暂停异常任务，避免未知结果被重复发送。"),
            "license"=>("卡密与授权",
                "激活或核验现有卡密；本机资料继续保存在用户目录。"),
            _=>("Signal 调度台","")
        };
        PageTitle.Text=heading;
        PageSubtitle.Text=subtitle;
        foreach(var b in new[]{HomeNav,AccountNav,ScriptNav,TaskNav,RecoveryNav,HistoryNav,LicenseNav})
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
        if(_hasLiveJobs || _criticalQueue.PendingCount>0)
        {
            var warning="";
            if(_hasLiveJobs)
                warning+="仍有正在运行的真实群组任务。\n"+
                    "关闭界面不等于停止后台发送，请先在运行任务页面核对。\n\n";
            if(_criticalQueue.PendingCount>0)
                warning+=$"还有 {_criticalQueue.PendingCount} 条重要异常提醒尚未确认。\n"+
                    "关闭此界面后，未确认的弹窗将无法继续显示。\n\n";
            var result=MessageBox.Show(this,
                warning+"确认仍要关闭 Signal 调度台吗？",
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
                var notice="Signal 后台连接异常："+ex.Message+
                    "\n请检查后台程序；不要假设任务仍在正常运行。";
                if(_hasLiveJobs)
                    ShowCriticalAlert("运行中的任务失去后台状态连接。"+
                        "\n当前发送结果未知，切勿重复启动或覆盖安装；"+
                        "\n请等待后台恢复并到任务恢复中心核对。"+
                        "\n\n"+notice);
                else
                    AddNotification(notice,false);
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
                var flagged=job.State is "RecoveryRequired" or "Failed" ||
                   (job.State=="Stopped" &&
                    !job.Detail.Contains("手动",StringComparison.Ordinal) &&
                    !job.Detail.Contains("用户",StringComparison.Ordinal)) ||
                   (job.State=="Paused" &&
                    new[]{"提醒","异常","断开","授权","失败","离线",
                        "不可用","错误","超时","不健康","失联","回执"}
                       .Any(reason=>job.Detail.Contains(reason,StringComparison.Ordinal)));
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
        if(string.IsNullOrWhiteSpace(message))return;
        // Critical events are already de-duplicated by job/cursor at the
        // source. A new job with the same error text must still alert.
        // The inbox retains only 100 entries: scan that bounded list rather
        // than keeping an unbounded lifetime set of every warning text.
        if(!critical && _notifications.Any(entry=>
               entry.EndsWith(message,StringComparison.Ordinal)))return;
        _notifications.Insert(0,$"{DateTime.Now:HH:mm:ss}  {message}");
        if(_notifications.Count>100)_notifications.RemoveAt(_notifications.Count-1);
        if(critical)
            _criticalQueue.Enqueue(message);
        UpdateAlertSummary();
        if(critical)ShowNextCritical();
    }

    void UpdateAlertSummary()
    {
        var pending=_criticalQueue.PendingCount;
        AlertStrip.Visibility=(_notifications.Count>0 || pending>0)
            ?Visibility.Visible:Visibility.Collapsed;
        AlertSummaryText.Text=pending>0
            ?$"当前有 {_notifications.Count} 条通知 · {pending} 条重要提醒待确认"
            :$"当前有 {_notifications.Count} 条通知 · 点击查看";
    }

    void ShowNextCritical()
    {
        if(!IsLoaded || !_criticalQueue.TryBegin(out var message))return;
        UpdateAlertSummary();
        Dispatcher.BeginInvoke(new Action(()=>
        {
            if(!IsLoaded)
            {
                // Do not lose a queued alert when switching or reloading
                // the window between enqueue and modal presentation.
                _criticalQueue.DeferCurrent();
                UpdateAlertSummary();
                return;
            }
            var originalTopmost=Topmost;
            var acknowledged=false;
            try
            {
                if(WindowState==WindowState.Minimized)
                    WindowState=WindowState.Normal;
                Show();
                Topmost=true;
                Activate();
                MessageBox.Show(this,
                    CriticalAlertQueue.DialogText(message),
                    "Signal 调度台 - 重要提醒",
                    MessageBoxButton.OK,MessageBoxImage.Warning);
                acknowledged=true;
            }
            catch(Exception)
            {
                // A failed Windows modal is not an acknowledgment. Keep the
                // original alert for next time this window can display it.
                AddNotification("重要弹窗暂时无法显示；请查看顶部通知并重新激活程序。",false);
            }
            finally
            {
                Topmost=originalTopmost;
                if(acknowledged)_criticalQueue.CompleteCurrent();
                else _criticalQueue.DeferCurrent();
                UpdateAlertSummary();
                if(acknowledged)ShowNextCritical();
            }
        }),DispatcherPriority.Background);
    }

    void ToggleAlerts_Click(object sender,RoutedEventArgs e)
    {
        AlertPanel.Visibility=AlertPanel.Visibility==Visibility.Visible
            ?Visibility.Collapsed:Visibility.Visible;
    }

    // Never export a raw IPC payload, database, log, account label or path.
    // Only specifically allowlisted fields are copied to a safe report DTO.
    async void ExportDiagnostic_Click(object sender,RoutedEventArgs e)
    {
        if(_exportingDiagnostic)return;
        var picker=new Microsoft.Win32.SaveFileDialog
        {
            Title="保存 MS002 脱敏诊断报告",
            Filter="文本报告 (*.txt)|*.txt",
            FileName="MS002_脱敏诊断_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".txt",
            AddExtension=true,
            OverwritePrompt=true
        };
        if(picker.ShowDialog(this)!=true)return;
        _exportingDiagnostic=true;
        ExportDiagnosticButton.IsEnabled=false;
        try
        {
            async Task<JsonElement?> SafeDataAsync(string command)
            {
                try
                {
                    var raw=await SendAsync(command,5000);
                    if(string.IsNullOrWhiteSpace(raw))return null;
                    using var doc=JsonDocument.Parse(raw);
                    var root=doc.RootElement;
                    if(root.ValueKind!=JsonValueKind.Object ||
                       !root.TryGetProperty("Ok",out var ok) ||
                       ok.ValueKind!=JsonValueKind.True ||
                       !root.TryGetProperty("Data",out var data) ||
                       data.ValueKind!=JsonValueKind.Object)return null;
                    return data.Clone();
                }
                catch(Exception){return null;}
            }

            static int Count(JsonElement? data,string key)
            {
                if(!data.HasValue ||
                   !data.Value.TryGetProperty(key,out var value) ||
                   !value.TryGetInt32(out var count))return 0;
                return Math.Max(0,count);
            }

            static long Number(JsonElement? data,string key)
            {
                if(!data.HasValue ||
                   !data.Value.TryGetProperty(key,out var value) ||
                   !value.TryGetInt64(out var number))return 0;
                return Math.Max(0,number);
            }

            static string State(JsonElement? data,string key)
            {
                if(!data.HasValue ||
                   !data.Value.TryGetProperty(key,out var value) ||
                   value.ValueKind!=JsonValueKind.String)return "";
                return value.GetString()??"";
            }

            var status=await SafeDataAsync(ControlCommands.Status);
            var dashboard=await SafeDataAsync(ControlCommands.Dashboard);
            var read=await SafeDataAsync(ControlCommands.ReadHealth);
            var local=Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var db=Path.Combine(local,"SignalSchedulerData","data.db");
            var report=SafeDiagnosticReport.Render(new SafeDiagnosticSnapshot(
                DateTimeOffset.Now,
                typeof(MainWindow).Assembly.GetName().Version?.ToString()??"",
                status.HasValue,
                State(status,"signal"),
                Count(dashboard,"Accounts"),
                Count(dashboard,"EnabledAccounts"),
                Count(dashboard,"Groups"),
                Count(dashboard,"Scripts"),
                Count(dashboard,"Jobs"),
                Count(dashboard,"RecoveryJobs"),
                State(read,"StreamState"),
                Count(read,"Pending"),
                Count(read,"Attempted"),
                Number(read,"LastEventMs"),
                File.Exists(db),
                dashboard.HasValue,
                read.HasValue,
                Count(read,"Failed"),
                Count(read,"WaitingRetry")));
            await File.WriteAllTextAsync(picker.FileName,report,
                new System.Text.UTF8Encoding(true));
            MessageBox.Show(this,
                "脱敏诊断报告已保存。\n"+
                "报告不包含手机号、群名、账号备注、剧本正文、授权密钥或原始日志。\n"+
                "如果后台不可用，相应状态会显示为未知。",
                "诊断报告",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception)
        {
            MessageBox.Show(this,"无法保存诊断报告，请检查磁盘空间和保存位置的写入权限。",
                "诊断报告保存失败",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            _exportingDiagnostic=false;
            ExportDiagnosticButton.IsEnabled=true;
        }
    }

    void ClearAlerts_Click(object sender,RoutedEventArgs e)
    {
        _notifications.Clear();
        AlertPanel.Visibility=Visibility.Collapsed;
        // Clearing the viewed inbox must not silently dismiss critical
        // alerts that are still waiting for their own acknowledgment.
        UpdateAlertSummary();
        ShowNextCritical();
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