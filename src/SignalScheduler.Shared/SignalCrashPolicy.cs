namespace SignalScheduler.Shared;

/// <summary>
/// Bounded crash-loop protection and fixed-code diagnostics for a local
/// Signal daemon. No account names, paths, message bodies, or stdout/stderr
/// are included in descriptions sent to the desktop or exported reports.
/// </summary>
public static class SignalCrashPolicy
{
    public const int MaxUnexpectedExits=3;
    public static readonly TimeSpan Window=TimeSpan.FromMinutes(5);

    public static string Classify(string? line)
    {
        if(string.IsNullOrWhiteSpace(line) ||
           line.TrimStart().StartsWith("{",StringComparison.Ordinal) ||
           line.Contains("\"envelope\"",StringComparison.OrdinalIgnoreCase))
            return "";
        bool Has(string value)=>line.Contains(value,StringComparison.OrdinalIgnoreCase);
        if(Has("MissingResourceException") && Has("ArgumentParserImpl"))
            return "SIGNAL_BUNDLE";
        if(Has("OutOfMemoryError") || Has("unable to create native thread") ||
           Has("Native memory allocation") || Has("There is insufficient memory"))
            return "JVM_MEMORY";
        if(Has("UnsatisfiedLinkError") || Has("Could not find java.dll") ||
           Has("failed to load library") || Has("Can't load AMD 64-bit"))
            return "JVM_NATIVE";
        if(Has("NoClassDefFoundError") || Has("ClassNotFoundException") ||
           Has("Could not find or load main class"))
            return "JVM_CLASSPATH";
        if(Has("AccessDeniedException") || Has("Access is denied") ||
           Has("Permission denied") || Has("FileSystemException"))
            return "FILE_PERMISSION";
        if(Has("BindException") || Has("Address already in use"))
            return "RPC_PORT";
        if(Has("AccountCheckException") || Has("InvalidKeyException"))
            return "SIGNAL_ACCOUNT";
        if(Has("SSLHandshakeException") || Has("UnknownHostException") ||
           Has("ConnectException") || Has("SocketTimeoutException"))
            return "SIGNAL_NETWORK";
        return "";
    }

    /// <summary>Windows native termination codes when Java produced no usable stderr.</summary>
    public static string ClassifyExitCode(int exitCode)=>exitCode switch
    {
        unchecked((int)0xC0000005)=>"OS_ACCESS_VIOLATION",
        unchecked((int)0xC0000135)=>"OS_DLL_MISSING",
        unchecked((int)0xC0000142)=>"OS_DLL_INIT",
        unchecked((int)0xC000001D)=>"OS_ILLEGAL_INSTRUCTION",
        unchecked((int)0xC0000409)=>"OS_STACK_BUFFER_OVERRUN",
        _=>""
    };

    public static string SafeCode(string? category)=>
        category is "SIGNAL_BUNDLE" or "JVM_MEMORY" or "JVM_NATIVE" or
            "JVM_CLASSPATH" or "FILE_PERMISSION" or "RPC_PORT" or
            "SIGNAL_ACCOUNT" or "SIGNAL_NETWORK" or
            "OS_ACCESS_VIOLATION" or "OS_DLL_MISSING" or "OS_DLL_INIT" or
            "OS_ILLEGAL_INSTRUCTION" or "OS_STACK_BUFFER_OVERRUN"
            ?category:"UNCLASSIFIED_EXIT";

    public static string Explain(string? category)=>SafeCode(category) switch
    {
        "SIGNAL_BUNDLE"=>"Java 语言资源未能加载",
        "JVM_MEMORY"=>"Java 内存或线程资源不足",
        "JVM_NATIVE"=>"Java 本地运行库加载异常",
        "JVM_CLASSPATH"=>"Java 类依赖加载失败",
        "FILE_PERMISSION"=>"本地文件访问权限异常",
        "RPC_PORT"=>"本地 Signal RPC 端口冲突",
        "SIGNAL_ACCOUNT"=>"Signal 账号资料加载异常",
        "SIGNAL_NETWORK"=>"Signal 网络或 TLS 连接异常",
        "OS_ACCESS_VIOLATION"=>"进程发生 Windows 内存访问异常",
        "OS_DLL_MISSING"=>"Windows 缺少进程所需运行库",
        "OS_DLL_INIT"=>"Windows 运行库初始化失败",
        "OS_ILLEGAL_INSTRUCTION"=>"当前处理器不支持某条机器指令",
        "OS_STACK_BUFFER_OVERRUN"=>"Windows 检测到进程栈保护异常",
        _=>"尚未取得可确认的退出原因"
    };

    public static string ExitDetail(int exitCode,string? category,bool exhausted,int count)
    {
        // The numeric exit code is obtained directly from the owned Process.
        // An exit code by itself never establishes the underlying cause.
        var detail=$"Signal 进程意外退出（代码 {exitCode}，故障分类 "+
            $"{SafeCode(category)}：{Explain(category)}；近5分钟 {Math.Clamp(count,0,99)} 次）";
        return exhausted
            ?detail+"。已停止自动重启，保留任务和账号数据。请导出脱敏诊断报告核查，不要盲目重新发送。"
            :detail+"。将有限次数尝试恢复，未确认发送结果的任务不会自动继续。";
    }
}

public sealed class SignalRestartBudget
{
    readonly Queue<DateTimeOffset> _exits=new();
    public int Count=>_exits.Count;

    public bool Register(DateTimeOffset now)
    {
        while(_exits.Count>0 && now-_exits.Peek()>SignalCrashPolicy.Window)
            _exits.Dequeue();
        _exits.Enqueue(now);
        return _exits.Count<SignalCrashPolicy.MaxUnexpectedExits;
    }

    public void ResetAfterStableRun()=>_exits.Clear();
}
