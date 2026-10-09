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
    bool DatabasePresent);

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
            return version.Length<=50 &&
                version.All(c=>char.IsAsciiLetterOrDigit(c) ||
                    c is '.' or '-' or '+')
                ?version:"未知";
        }

        static int Count(int value)=>Math.Clamp(value,0,10000000);

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
        sb.AppendLine($"账号数量：{Count(input.Accounts)}");
        sb.AppendLine($"启用账号：{Count(input.EnabledAccounts)}");
        sb.AppendLine($"群组数量：{Count(input.Groups)}");
        sb.AppendLine($"剧本数量：{Count(input.Scripts)}");
        sb.AppendLine($"任务数量：{Count(input.Jobs)}");
        sb.AppendLine($"待核对任务：{Count(input.RecoveryJobs)}");
        sb.AppendLine();
        sb.AppendLine("已读事件监听（不代表其他设备已读清零）");
        sb.AppendLine($"监听状态：{read}");
        sb.AppendLine($"待处理回执：{Count(input.PendingReadReceipts)}");
        sb.AppendLine($"已请求回执：{Count(input.AttemptedReadReceipts)}");
        sb.AppendLine($"最近捕获事件：{last}");
        sb.AppendLine();
        sb.AppendLine("隐私保护：未收集手机号、账号备注、群名、地址、消息、附件内容、密钥或日志原文。");
        return sb.ToString();
    }
}
