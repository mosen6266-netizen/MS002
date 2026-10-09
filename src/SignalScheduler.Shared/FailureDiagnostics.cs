namespace SignalScheduler.Shared;

/// <summary>
/// Safe, evidence-calibrated explanation of persisted dispatch results.
/// The original detail is never copied into an export. This intentionally
/// distinguishes a symptom from a proven root cause.
/// </summary>
public sealed record FailureDiagnosis(
    string Code,string Label,string Stage,string Certainty,string NextStep);

public static class FailureDiagnostics
{
    public static readonly string[] ExportableCodes={
        "SIGNAL_SERVER_ERROR","SIGNAL_RPC_INTERNAL","SIGNAL_RPC_PARAMS",
        "SIGNAL_IDENTITY","SIGNAL_RATE_LIMIT","SIGNAL_GROUP_ACCESS",
        "SIGNAL_ACCOUNT","SIGNAL_NETWORK","SIGNAL_ATTACHMENT",
        "RPC_MISMATCH","RPC_MISSING_TIMESTAMP","RPC_HTTP",
        "LOCAL_RUNTIME","PRESEND_BLOCKED","LOCAL_EXCEPTION",
        "SEND_UNCERTAIN","UNKNOWN"
    };

    public static FailureDiagnosis Analyze(string? detail,string? state=null)
    {
        var d=detail??"";
        bool Has(string s)=>d.Contains(s,StringComparison.OrdinalIgnoreCase);
        if(Has("ServerSideErrorException") || Has("Signal 服务端处理失败"))
            return new("SIGNAL_SERVER_ERROR","Signal 服务端处理失败","Signal 网络发送",
                "已确认故障层级；未确定服务端具体原因",
                "先在 Signal 中核对消息是否出现。检查账号关联状态、群组权限和网络；不要自动重发。");
        if(Has("AccountCheckException") || Has("Closed unexpectedly"))
            return new("SIGNAL_ACCOUNT","Signal 账号加载检查失败","账号加载",
                "日志直接证明加载错误；账号数据损坏尚未确认",
                "检查关联设备状态及程序数据目录；勿删除账号数据或盲目重新扫码。");
        if(Has("身份密钥需要人工核验") || Has("UntrustedIdentity"))
            return new("SIGNAL_IDENTITY","身份密钥需要核验","Signal 账号/身份验证",
                "依据 Signal 错误类别；投递结果未确认",
                "在 Signal 官方客户端检查安全号码变化，不要自动绕过身份验证。");
        if(Has("发送限流或服务验证") || Has("RateLimit") || Has("Captcha"))
            return new("SIGNAL_RATE_LIMIT","限流或验证要求","Signal 服务端",
                "依据错误类别；具体策略无法从客户端确认",
                "停止自动发送，按 Signal 官方流程完成验证，不要规避服务限制。");
        if(Has("群组不存在或账号已不在群组中"))
            return new("SIGNAL_GROUP_ACCESS","群组访问异常","发送前账号/群组检查",
                "依据服务端错误类别；群组实际状态需核对",
                "核对该账号是否在群内且具有发言权限。");
        if(Has("账号注册或认证状态异常"))
            return new("SIGNAL_ACCOUNT","账号认证异常","Signal 账号",
                "依据错误类别；账号状态需独立核对",
                "在官方 Signal 客户端检查关联设备是否正常。");
        if(Has("网络连接或远端响应异常") || Has("SocketTimeout") ||
           Has("HttpRequestException") || Has("TaskCanceledException"))
            return new("SIGNAL_NETWORK","网络或 RPC 响应异常","HTTP / Signal 网络",
                "无法确认消息是否送达",
                "检查网络和服务状态；先人工核对消息，再决定是否处理。");
        if(Has("附件文件异常") || Has("附件校验失败"))
            return new("SIGNAL_ATTACHMENT","附件文件异常","本地附件",
                "检测到附件错误；是否发出取决于记录状态",
                "在剧本管理中检查附件是否存在且可读取。");
        if(Has("缺少内置 Java") || Has("missing-runtime"))
            return new("LOCAL_RUNTIME","本地运行时文件缺失","启动/运行时",
                "本地文件检查异常",
                "重新检查安装目录的 Java 与 signal-cli 文件；保留用户数据。");
        if(Has("JSON-RPC 拒绝参数") || Has("错误码 -32602") ||
           Has("错误码 -32601") || Has("错误码 -32600"))
            return new("SIGNAL_RPC_PARAMS","Signal RPC 参数被拒绝","Signal RPC 参数验证",
                "服务端明确拒绝该请求",
                "检查当前 signal-cli 版本与接口契约，勿直接修改任务进度。");
        if(Has("错误码 -32603") || Has("错误码：-32603") ||
           Has("错误码 -32603") || Has("RPC -32603"))
            return new("SIGNAL_RPC_INTERNAL","Signal RPC 内部异常","Signal RPC",
                "已知通用 RPC 错误码；无法单凭此码确定底层原因",
                "比对相同时间的脱敏 signal-cli 异常类型；先核对是否已发送。");
        if(Has("回执未匹配请求编号"))
            return new("RPC_MISMATCH","RPC 请求与回执不匹配","Signal RPC 响应",
                "未取得可关联的发送结果",
                "不要重发；核查 RPC 调用和是否存在多实例。");
        if(Has("没有可确认的消息时间戳") || Has("回执无有效时间戳"))
            return new("RPC_MISSING_TIMESTAMP","缺少确认时间戳","Signal RPC 回执",
                "Signal 响应不足以证明消息已提交",
                "先在 Signal 中核对消息，保留响应格式线索。");
        if(Has("Signal HTTP "))
            return new("RPC_HTTP","Signal RPC HTTP 错误","本地 RPC HTTP",
                "已得到 HTTP 错误响应；发送结果不明确",
                "检查后台是否健康及 RPC 请求记录，勿自动重发。");
        if(Has("发送前校验失败") || Has("Pre-send validation"))
            return new("PRESEND_BLOCKED","发送前校验未通过","本地调度校验",
                "确认未进入实际发送调用",
                "检查账号在线、群组成员和任务状态。");
        if(Has("发送或回执异常") || Has("Transport failed during Sending") ||
           Has("发送异常："))
            return new("LOCAL_EXCEPTION","本地发送阶段异常","发送/回执处理",
                "异常类型已记录，但实际投递未确认",
                "检查脱敏错误分类与当时运行状态，不可推定未发送。");
        if(state is "RecoveryRequired" or "Unknown" or "Sending" || Has("等待人工核对"))
            return new("SEND_UNCERTAIN","发送结果未确认","发送/持久化",
                "未取得足够证据，不代表未发送",
                "在 Signal 中核对该条消息，核对前不要自动重试。");
        if(state=="DefinitelyNotSent")
            return new("PRESEND_BLOCKED","确认未发送","发送前校验或 RPC 拒绝",
                "后台已标记确定未发送",
                "检查错误详情及记录后手动处理。");
        return new("UNKNOWN","无可用错误分类","未确定",
            "当前记录不足以判断故障原因",
            "查看任务状态与其他脱敏诊断信息。");
    }

    public static string ExportableCode(string? detail,string? state=null)
    {
        var code=Analyze(detail,state).Code;
        return ExportableCodes.Contains(code,StringComparer.Ordinal)?code:"UNKNOWN";
    }
}
