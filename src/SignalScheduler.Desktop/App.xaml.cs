using System.Threading;
using System.Windows;

namespace SignalScheduler.Desktop;

public partial class App : Application
{
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if(e.Args.Contains("--ui-layout-smoke",StringComparer.OrdinalIgnoreCase))
        {
            // WPF layout smoke uses the application's resources, but must not
            // launch MainWindow or initialize real Signal accounts.
            var report=e.Args.Length>1 && System.IO.Path.IsPathFullyQualified(e.Args[1])
                ?e.Args[1]
                :System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "ms002-ui-layout-smoke.txt");
            try{System.IO.File.WriteAllText(report,"Starting WPF layout smoke.\n");}
            catch{ }
            StartupUri=null;
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException+=(_,args)=>
            {
                try{System.IO.File.WriteAllText(report,
                    "Unhandled WPF layout error: "+args.Exception);}
                catch{ }
                args.Handled=true;
                Shutdown(1);
            };
            int result=1;
            try
            {
                result=UiLayoutSmoke.Run(report);
            }
            catch(Exception ex)
            {
                try{System.IO.File.WriteAllText(report,
                    "WPF layout bootstrap error: "+ex);}
                catch{ }
            }
            Shutdown(result);
            return;
        }
        _mutex=new Mutex(true,@"Local\SignalScheduler.V8.Desktop",out var createdNew);
        if(!createdNew)
        {
            MessageBox.Show("Signal Auto Scheduler 已经在运行。","Signal Auto Scheduler",MessageBoxButton.OK,MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try{_mutex?.ReleaseMutex();}catch{}
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
