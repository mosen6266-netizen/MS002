using System.Threading;
using System.Windows;

namespace SignalScheduler.Desktop;

public partial class App : Application
{
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
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
