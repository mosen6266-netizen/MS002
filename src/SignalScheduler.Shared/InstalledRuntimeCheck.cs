namespace SignalScheduler.Shared;

/// <summary>
/// Fast, read-only presence check for bundled runtime components. It does
/// not read user accounts, run Java, modify files or claim hash integrity.
/// </summary>
public sealed record InstalledRuntimeStatus(string Java,string SignalCli,string Assurance);

public static class InstalledRuntimeCheck
{
    public static InstalledRuntimeStatus Inspect(string? desktopDirectory)
    {
        if(string.IsNullOrWhiteSpace(desktopDirectory))
            return new("未知","未知","未检查");
        try
        {
            // Desktop EXE installs in {app}; Engine EXE installs in {app}/Engine.
            // This inspector is called by the Desktop and must resolve from
            // that root, not from the Engine's AppContext.BaseDirectory.
            var runtime=Path.GetFullPath(Path.Combine(
                desktopDirectory,"Runtime","signal-stack-v1"));
            var java=Path.Combine(runtime,"jre","bin","java.exe");
            var signalLib=Path.Combine(runtime,"signal-cli","lib");

            var javaStatus=File.Exists(java) ? "存在":"缺失";
            var signalStatus=Directory.Exists(signalLib) &&
                Directory.EnumerateFiles(signalLib,"*.jar",SearchOption.TopDirectoryOnly).Any()
                ? "存在":"缺失";
            return new(javaStatus,signalStatus,"仅检查存在性，未核验文件哈希或完整运行能力");
        }
        catch(Exception)
        {
            return new("检查失败","检查失败","无法访问运行文件，未修改任何文件");
        }
    }
}
