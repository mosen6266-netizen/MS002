namespace SignalScheduler.Engine;

public sealed class RuntimePaths
{
    public string DataRoot { get; }
    public string DatabasePath { get; }
    public string BackupRoot { get; }
    public string LogRoot { get; }
    public string RuntimeRoot { get; }
    public string JavaExe { get; }
    public string SignalCliHome { get; }
    public string SignalCliLibWildcard { get; }
    public string SignalCliLogPath { get; }

    public RuntimePaths()
    {
        var overrideRoot=Environment.GetEnvironmentVariable("SIGNAL_SCHEDULER_V8_TEST_DATA_ROOT");
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataRoot=string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(local,"SignalSchedulerData")
            : overrideRoot;
        DatabasePath=Path.Combine(DataRoot,"data.db");
        BackupRoot=Path.Combine(DataRoot,"backups");
        LogRoot=Path.Combine(DataRoot,"logs");

        var installRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        RuntimeRoot=Path.Combine(installRoot,"Runtime","signal-stack-v1");
        JavaExe=Path.Combine(RuntimeRoot,"jre","bin","java.exe");
        SignalCliHome=Path.Combine(RuntimeRoot,"signal-cli");
        SignalCliLibWildcard=Path.Combine(SignalCliHome,"lib","*");
        SignalCliLogPath=Path.Combine(LogRoot,"signal_cli.log");

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(LogRoot);
    }

    RuntimePaths(string root,string db)
    {
        DataRoot=root;
        DatabasePath=db;
        BackupRoot=Path.Combine(root,"backups");
        LogRoot=Path.Combine(root,"logs");
        RuntimeRoot=Path.Combine(root,"_runtime_test","signal-stack-v1");
        JavaExe=Path.Combine(RuntimeRoot,"jre","bin","java.exe");
        SignalCliHome=Path.Combine(RuntimeRoot,"signal-cli");
        SignalCliLibWildcard=Path.Combine(SignalCliHome,"lib","*");
        SignalCliLogPath=Path.Combine(LogRoot,"signal_cli.log");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(LogRoot);
    }

    public static RuntimePaths ForTesting(string root,string databasePath)=>new(root,databasePath);
}
