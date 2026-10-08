using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SignalScheduler.Shared;

namespace SignalScheduler.Desktop;

public partial class MainWindow : Window
{
    readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(2)};
    public MainWindow()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>await RefreshAsync();
        _timer.Tick+=async(_,_)=>await RefreshAsync();
        _timer.Start();
        Closed+=(_,_)=>_timer.Stop();
    }

    async Task RefreshAsync()
    {
        try
        {
            await using var pipe=new NamedPipeClientStream(".","SignalScheduler.V8.Control",PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500);
            using var reader=new StreamReader(pipe,leaveOpen:true);
            using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlRequest(ControlCommands.Status)));
            StatusText.Text=await reader.ReadLineAsync() ?? "No response";
        }
        catch(Exception ex){StatusText.Text=$"Service unavailable: {ex.Message}";}
    }
}
