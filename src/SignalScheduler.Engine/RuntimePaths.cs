namespace SignalScheduler.Engine;

public sealed class RuntimePaths
{
    public string DataRoot { get; }
    public string DatabasePath { get; }
    public string BackupRoot { get; }

    public RuntimePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataRoot = Path.Combine(local, "SignalSchedulerData");
        BackupRoot = Path.Combine(DataRoot, "backups");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupRoot);
        DatabasePath = Path.Combine(DataRoot, "data.db");
    }
}
