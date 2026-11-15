namespace SignalScheduler.Shared;

/// <summary>
/// Read-only diagnostics for the installed bundled runtime.
/// Does not read user accounts, run Java, or change any installed files.
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
            var integrity=RuntimeIntegrityCheck.Verify(runtime);
            return new(javaStatus,signalStatus,integrity.Healthy
                ?integrity.Detail
                :"运行库校验失败（"+integrity.Code+"）："+integrity.Detail);
        }
        catch(Exception)
        {
            return new("检查失败","检查失败","无法访问运行文件，未修改任何文件");
        }
    }
}
