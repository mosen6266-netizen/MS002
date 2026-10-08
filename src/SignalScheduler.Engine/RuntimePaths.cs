namespace SignalScheduler.Engine;

public sealed class RuntimePaths
{
    public string DataRoot { get; }
    public string DatabasePath { get; }
    public string BackupRoot { get; }

    public RuntimePaths()
    {
        var overrideRoot=Environment.GetEnvironmentVariable("SIGNAL_SCHEDULER_V8_TEST_DATA_ROOT");
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataRoot=string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(local,"SignalSchedulerData")
            : overrideRoot;
        DatabasePath=Path.Combine(DataRoot,"data.db");
        BackupRoot=Path.Combine(DataRoot,"backups");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupRoot);
    }

    RuntimePaths(string root,string db)
    {
        DataRoot=root;
        DatabasePath=db;
        BackupRoot=Path.Combine(root,"backups");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupRoot);
    }

    public static RuntimePaths ForTesting(string root,string databasePath)=>new(root,databasePath);
}
