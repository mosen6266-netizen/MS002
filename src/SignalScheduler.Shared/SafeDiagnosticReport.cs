using System.Text;

namespace SignalScheduler.Shared;

/// <summary>
/// Allowlist-only diagnostics for sharing with support.
/// Do not add arbitrary logs, device identifiers, account/group labels,
/// message bodies, tokens, database filenames, or exception messages.
/// </summary>
public sealed record SafeDiagnosticSnapshot(
    DateTimeOffset GeneratedAt,
    string DesktopVersion,
    bool EngineConnected,
    string SignalConnectionState,
    int Accounts,
    int EnabledAccounts,
    int Groups,
    int Scripts,
    int Jobs,
    int RecoveryJobs,
    string ReadEventStreamState,
    int PendingReadReceipts,
    int AttemptedReadReceipts,
    long LastReadEventMs,
    bool DatabasePresent,
    bool DashboardAvailable=true,
    bool ReadHealthAvailable=true,
    int FailedReadReceipts=0,
    int DeferredReadReceipts=0,
    IReadOnlyList<DiagnosticCategoryCount>? DispatchDiagnostics=null,
    int ExaminedDispatches=0,
    bool RecoveryDataAvailable=false,
    InstalledRuntimeStatus? RuntimeStatus=null,
    string LastDaemonErrorCategory="",
    long LastDaemonErrorAtMs=0,
    string LastReceiptRpcCode="",
    long LastReceiptRpcAtMs=0,
    int LastReceiptSelected=0,
    int LastReceiptAccepted=0,
    int UnknownReadReceipts=0);

public sealed record DiagnosticCategoryCount(string Code,int Count);

public static class SafeDiagnosticReport
{
    public static string Render(SafeDiagnosticSnapshot input)
    {
        // Even if a caller passes an untrusted status or version, never let
        // arbitrary text from the daemon or filesystem enter the report.
        static string AllowState(string? state,string[] allowed)=>
            allowed.Contains(state,StringComparer.Ordinal)?state!:"未知";

        static string Version(string? version)
        {
            if(string.IsNullOrWhiteSpace(version))return "未知";
            return System.Text.RegularExpressions.Regex.IsMatch(version,
                @"^\d{1,4}(?:\.\d{1,4}){1,3}(?:-[A-Za-z0-9.-]{1,24})?$")
                ?version:"未知";
        }

        static int Count(int value)=>Math.Clamp(value,0,10000000);
        static string Metric(int value,bool available)=>
            available?Count(value).ToString():"未取得数据";

        var connection=AllowState(input.SignalConnectionState,new[]{
            "ready","running","offline","stopped","starting","degraded",
            "healthy","unhealthy","unknown","Connected","Disconnected",
            "Ready","Offline","Starting","Failed","NotStarted"});
        var read=AllowState(input.ReadEventStreamState,new[]{
            "已连接","连接中断","尚未连接","数据库错误","未知"});
        var last="未捕获";
        if(input.LastReadEventMs>0)
        {
            try
            {
                last=DateTimeOffset.FromUnixTimeMilliseconds(input.LastReadEventMs)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            }
            catch(ArgumentOutOfRangeException){last="无效时间";}
        }

        var sb=new StringBuilder();
        sb.AppendLine("MS002 · 脱敏诊断报告");
        sb.AppendLine("说明：仅包含允许公开的健康状态、数量和时间；不含原始日志或用户消息。");
        sb.AppendLine($"生成时间：{input.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"桌面版本：{Version(input.DesktopVersion)}");
        sb.AppendLine($"后台连接：{(input.EngineConnected?"正常":"无法连接")}");
        sb.AppendLine($"Signal 服务状态：{connection}");
        sb.AppendLine($"本地数据库：{(input.DatabasePresent?"文件存在":"文件不存在")}");
        sb.AppendLine();
        sb.AppendLine("数据概况（仅数量）");
        sb.AppendLine($"账号数量：{Metric(input.Accounts,input.DashboardAvailable)}");
        sb.AppendLine($"启用账号：{Metric(input.EnabledAccounts,input.DashboardAvailable)}");
        sb.AppendLine($"群组数量：{Metric(input.Groups,input.DashboardAvailable)}");
        sb.AppendLine($"剧本数量：{Metric(input.Scripts,input.DashboardAvailable)}");
        sb.AppendLine($"任务数量：{Metric(input.Jobs,input.DashboardAvailable)}");
        sb.AppendLine($"待核对任务：{Metric(input.RecoveryJobs,input.DashboardAvailable)}");
        sb.AppendLine();
        sb.AppendLine("已读事件监听（不代表其他设备已读清零）");
        sb.AppendLine($"监听状态：{(input.ReadHealthAvailable?read:"未取得数据")}");
        sb.AppendLine($"待处理回执：{Metric(input.PendingReadReceipts,input.ReadHealthAvailable)}");
        sb.AppendLine($"已请求回执：{Metric(input.AttemptedReadReceipts,input.ReadHealthAvailable)}");
        sb.AppendLine($"等待重试回执：{Metric(input.DeferredReadReceipts,input.ReadHealthAvailable)}");
        sb.AppendLine($"最终失败回执：{Metric(input.FailedReadReceipts,input.ReadHealthAvailable)}");
        sb.AppendLine($"最近捕获事件：{(input.ReadHealthAvailable?last:"未取得数据")}");
        sb.AppendLine($"结果不明回执：{Metric(input.UnknownReadReceipts,input.ReadHealthAvailable)}");
        sb.AppendLine();
        sb.AppendLine("已读回执 RPC 诊断（仅最近一次发言前的处理）");
        // The codes are fixed and allowlisted by the producer. Never export
        // a raw Signal exception or RPC response.
        var receiptCodes=new[]{
            "READ_DISABLED","NO_PENDING_FOR_SPEAKER","RPC_NOT_STARTED",
            "RPC_NOT_CONFIRMED","RPC_ACCEPTED","RPC_EMPTY_RESPONSE",
            "RPC_MISMATCH","RPC_INTERNAL_ERROR","RPC_ERROR_OTHER",
            "RPC_HTTP_ERROR","RPC_NO_RESULT","RPC_INVALID_JSON",
            "RPC_TIMEOUT","RPC_NETWORK_ERROR","RPC_RESPONSE_ERROR",
            "RPC_UNCLASSIFIED","RPC_INTERRUPTED","RPC_REJECTED_-32600",
            "RPC_REJECTED_-32601","RPC_REJECTED_-32602"
        };
        sb.AppendLine("最近 RPC 结果："+
            (input.ReadHealthAvailable && receiptCodes.Contains(input.LastReceiptRpcCode,
                StringComparer.Ordinal)?input.LastReceiptRpcCode:"未取得数据"));
        sb.AppendLine($"本轮选中回执：{Metric(input.LastReceiptSelected,input.ReadHealthAvailable)}");
        sb.AppendLine($"本轮 RPC 确认接收：{Metric(input.LastReceiptAccepted,input.ReadHealthAvailable)}");
        string receiptTime="未取得数据";
        if(input.ReadHealthAvailable && input.LastReceiptRpcAtMs>0)
        {
            try {
                receiptTime=DateTimeOffset.FromUnixTimeMilliseconds(input.LastReceiptRpcAtMs)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            }catch(ArgumentOutOfRangeException){}
        }
        sb.AppendLine("最近处理时间："+receiptTime);
        sb.AppendLine("说明：RPC 确认接收不保证另一端显示已读；不存在待处理事件不等于全部已读。");
        sb.AppendLine();
        sb.AppendLine("安装运行文件检查（只读）");
        static string SafeFileStatus(string? status)=>
            status is "存在" or "缺失" or "检查失败"?status:"未检查";
        sb.AppendLine($"Java 主程序：{SafeFileStatus(input.RuntimeStatus?.Java)}");
        sb.AppendLine($"signal-cli 库文件：{SafeFileStatus(input.RuntimeStatus?.SignalCli)}");
        sb.AppendLine("说明：只检查文件存在性，不证明文件未损坏，也不代表账号可正常发送。");
        sb.AppendLine();
        sb.AppendLine("后台最近一次异常（不能自动归因到某一任务）");
        var daemonCode=input.LastDaemonErrorCategory;
        var allowedDaemon=new[]{
            "ERROR SignalSend.ServerSideErrorException",
            "ERROR SignalAccount.AccountCheckException",
            "ERROR SignalAccount.UntrustedIdentity",
            "ERROR SignalSend.RpcDispatcher",
            "ERROR SignalNetwork.Connection",
            "ERROR SignalDaemon.OtherException"
        };
        sb.AppendLine("错误类型："+(allowedDaemon.Contains(daemonCode,
            StringComparer.Ordinal)?daemonCode:"未记录"));
        string daemonTime="未记录";
        if(input.LastDaemonErrorAtMs>0)
        {
            try { daemonTime=DateTimeOffset
                .FromUnixTimeMilliseconds(input.LastDaemonErrorAtMs)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"); }
            catch(ArgumentOutOfRangeException){ }
        }
        sb.AppendLine("最近出现时间："+daemonTime);
        sb.AppendLine("说明：并发任务可能同时运行，同期 daemon 错误不等于当前任务的直接原因。");
        sb.AppendLine();
        sb.AppendLine("最近发送异常分类（不含原始 RPC 内容）");
        if(!input.RecoveryDataAvailable)
            sb.AppendLine("恢复记录：未取得数据");
        else
        {
            sb.AppendLine($"已检查发送审计记录：{Count(input.ExaminedDispatches)}");
            var categories=input.DispatchDiagnostics?
                .Where(x=>x is not null &&
                   FailureDiagnostics.ExportableCodes.Contains(x.Code,StringComparer.Ordinal))
                .GroupBy(x=>x.Code,StringComparer.Ordinal)
                .Select(g=>new DiagnosticCategoryCount(g.Key,
                    (int)Math.Min(10000000L,g.Sum(x=>(long)Math.Max(0,x.Count)))))
                .OrderByDescending(x=>x.Count).ThenBy(x=>x.Code,StringComparer.Ordinal)
                .Take(10).ToArray()??Array.Empty<DiagnosticCategoryCount>();
            if(categories.Length==0)sb.AppendLine("暂无可归类的异常记录");
            foreach(var entry in categories)
                sb.AppendLine($"{entry.Code}：{entry.Count}");
        }
        sb.AppendLine();
        sb.AppendLine("隐私保护：不含手机号、账号备注、群名、地址、消息、附件内容、密钥或原始日志。");
        return sb.ToString();
    }
}
