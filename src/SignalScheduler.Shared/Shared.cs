using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignalScheduler.Shared;

public enum TaskState { Created, Running, Paused, WaitingSignal, RecoveryRequired, Stopping, Stopped, Completed, Failed }
public enum DispatchState { Queued, Prepared, Sending, Confirmed, Committed, DefinitelyNotSent, Unknown, RecoveryRequired, Skipped, Failed, Cancelled }
public enum SignalDeliveryOutcome { Confirmed, DefinitelyNotSent, Ambiguous }

public sealed record SignalSendResult(SignalDeliveryOutcome Outcome, string? ProviderMessageId = null, string? Detail = null);

public sealed record DispatchIdentity(string JobId,string RunToken,long RunCycle,long Cursor,string GroupId,string AccountId,string PayloadHash)
{
    public string DispatchKey {
        get {
            var raw=string.Join("|",JobId,RunToken,RunCycle,Cursor,GroupId,AccountId,PayloadHash);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
    }
}

public sealed record SignalGroupCatalogItem(
    string Account,
    string GroupId,
    string Name,
    bool IsMember,
    IReadOnlyList<string> Members);

public sealed record SignalLinkSnapshot(
    string State,
    string DeviceLinkUri,
    string Account,
    string Detail,
    DateTimeOffset StartedAt);

public sealed record SignalGuardianSnapshot(
    string State,
    string Detail,
    string SignalCliVersion,
    bool OwnsProcess,
    int RestartCount,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> LiveAccounts);

public sealed record DashboardAccount(long LegacyId,string Account,string Label,bool Enabled);
public sealed record DashboardGroup(long LegacyId,string Account,string GroupId,string Name,bool Enabled);
public sealed record DashboardScript(long LegacyId,string Name,long StepCount);
public sealed record DashboardJob(long LegacyId,string Name,string GroupId,string State,long Cursor,bool RecoveryRequired);

public sealed record DashboardSnapshot(
    string Version,
    string EngineState,
    string TransportState,
    string SignalState,
    string SignalDetail,
    string SignalCliVersion,
    int LiveSignalAccounts,
    bool LegacyDetected,
    bool MetadataMigrated,
    int Accounts,
    int EnabledAccounts,
    int Groups,
    int Scripts,
    int Jobs,
    int RecoveryJobs,
    IReadOnlyList<DashboardAccount> AccountItems,
    IReadOnlyList<DashboardGroup> GroupItems,
    IReadOnlyList<DashboardScript> ScriptItems,
    IReadOnlyList<DashboardJob> JobItems);

/// <summary>
/// Read-only recovery audit contract. Legacy V7 jobs are included for context,
/// but only native V8 jobs can be paused from the recovery center.
/// </summary>
/// <summary>Single UI vocabulary for persisted task and delivery states.</summary>
public static class StatusLabels
{
    public static string Task(string? state)=>state switch
    {
        "Created"=>"已创建","Running"=>"运行中","Paused"=>"已暂停",
        "WaitingSignal"=>"等待 Signal","Sending"=>"正在发送",
        "RecoveryRequired"=>"结果待人工核对","Stopping"=>"停止中",
        "Stopped"=>"已停止","Completed"=>"已完成","Failed"=>"异常停止",
        "Pending"=>"等待执行","Cancelled"=>"已取消",
        null or ""=>"未知状态",_=>"未知状态"
    };
    public static string Delivery(string? state)=>state switch
    {
        "Queued"=>"等待发送","Prepared"=>"准备发送","Reserved"=>"等待发送",
        "Sending"=>"正在发送","Confirmed"=>"Signal 已确认提交",
        "Committed"=>"已记录发送确认","Completed"=>"已完成",
        "DefinitelyNotSent"=>"确认未发送","Unknown" or "Ambiguous" or
        "RecoveryRequired"=>"发送结果待核对",
        "Skipped"=>"已跳过","Failed"=>"发送失败",
        "Cancelled"=>"已取消","ManuallyConfirmed"=>"人工核实已发送",
        null or ""=>"未执行",_=>"未知状态"
    };
}

public sealed record RecoveryJobItem(
    string JobId,string Name,string State,long Cursor,bool IsLegacy,bool NeedsReview,
    string Detail="")
{
    public string StateDisplay=>StatusLabels.Task(State);
    public bool NeedsAttention=>NeedsReview ||
        State is "RecoveryRequired" or "Running" or "WaitingSignal" or "Stopping" ||
        (State=="Paused" && (
            Detail.Contains("异常",StringComparison.Ordinal) ||
            Detail.Contains("重启",StringComparison.Ordinal) ||
            Detail.Contains("断开",StringComparison.Ordinal) ||
            Detail.Contains("核对",StringComparison.Ordinal) ||
            Detail.Contains("失败",StringComparison.Ordinal)));
}
public sealed record RecoveryDispatchItem(
    string DispatchKey,string JobId,long Cursor,string GroupId,string AccountId,
    string State,string? ProviderMessageId,string? Detail,long UpdatedAt,
    string? GroupName=null,string? AccountLabel=null)
{
    public string StateDisplay=>StatusLabels.Delivery(State);
    public string GroupDisplay=>string.IsNullOrWhiteSpace(GroupName)?"未找到群名":GroupName;
    public string AccountDisplay=>string.IsNullOrWhiteSpace(AccountLabel)?"未备注账号":AccountLabel;
}
public sealed record RecoveryOverview(
    IReadOnlyList<RecoveryJobItem> Jobs,IReadOnlyList<RecoveryDispatchItem> Dispatches);
public sealed record PauseJobRequest(string JobId);
public sealed record PauseJobResult(string JobId,string State,string Detail);
public sealed record ManualDispatchReviewRequest(
    string JobId,string DispatchKey,string Decision,string Evidence);
public sealed record ManualDispatchReviewResult(
    string JobId,string DispatchKey,string JournalState,string JobState,
    long Cursor,string Detail);

public sealed record ScriptEditorSummary(
    string ScriptId,string Name,int Revision,int StepCount,bool ImportedFromV7);
public sealed record ScriptEditorStep(
    int Position,string Account,string Message,string Attachment,
    bool PauseAfter,string ReminderText,int DelayAfter,int TypingSeconds);
public sealed record ScriptEditorDocument(
    string ScriptId,string Name,string TargetGroupId,int Revision,
    IReadOnlyList<ScriptEditorStep> Steps,bool ImportedFromV7);
public sealed record ScriptSaveRequest(
    string? ScriptId,string Name,string TargetGroupId,int Revision,
    IReadOnlyList<ScriptEditorStep> Steps);
public sealed record ScriptReadRequest(string ScriptId);
public sealed record ScriptVersionSummary(int Revision,string Name,long SavedAt);
public sealed record ScriptVersionRequest(string ScriptId,int Revision);

public sealed record ScriptDeleteRequest(string ScriptId,int ExpectedRevision);
public sealed record ScriptDeleteResult(string ScriptId,bool Deleted);

public sealed record ManagedAccount(
    string Account,string Label,bool Enabled,bool Online,long Revision);
public sealed record ManagedGroup(
    string GroupId,string Name,int MemberAccounts,bool Selected);
public sealed record AccountGroupOverview(
    IReadOnlyList<ManagedAccount> Accounts,IReadOnlyList<ManagedGroup> Groups);
public sealed record UpdateManagedAccount(
    string Account,string Label,bool Enabled,long ExpectedRevision);
public sealed record UpdateGroupSelection(IReadOnlyList<string> GroupIds);

/// <summary>
/// A rehearsal task runs the exact stored script schedule, without sending
/// any Signal message. Its progress is durable, and it never touches the
/// irreversible Signal dispatch journal.
/// </summary>
public sealed record PreviewTaskPlanRequest(string ScriptId,IReadOnlyList<string> GroupIds);
public sealed record PreviewTaskPlanResult(int Created,IReadOnlyList<string> JobIds);
public sealed record PreviewTaskControlRequest(string JobId,string Action);
public sealed record PreviewTaskItem(
    string JobId,string Name,string ScriptId,string GroupId,string GroupName,
    string State,long Cursor,int TotalSteps,long NextDueAt,string Detail);

public sealed record ImageImportRequest(string SourcePath);
public sealed record ImageLookupRequest(string Reference);
public sealed record ImageCheckRequest(IReadOnlyList<string> References);
public sealed record ImageCheckResult(string Reference,string Status,string Detail);
public sealed record ImageAttachmentInfo(
    string Reference,string AbsolutePath,string OriginalName,long Bytes,string Sha256);

public sealed record LicenseActivationRequest(string LicenseKey);
public sealed record LicensePublicStatus(
    string State,string Detail,string TypeName,long ActivatedAt,
    long ExpiresAt,long LeaseUntil,bool ServerReachable,bool HasSavedLicense);

/// <summary>
/// One-off, explicitly confirmed live delivery probe. This is NOT the
/// multi-group script runner. Only an approved, selected Signal group may be
/// used; the user must acknowledge that a real message will be sent.
/// </summary>
public sealed record LiveProbeRequest(string Account,string GroupId,bool ConfirmRealSend);
public sealed record LiveProbeResult(
    string JobId,string GroupName,string Account,string State,
    string Detail,string? ProviderMessageId);

/// <summary>
/// Supervised pilot: at most 3 real messages from one account to one
/// explicit test group, with per-step spacing and immediate recovery stop.
/// </summary>
public sealed record LivePilotPlanRequest(
    string ScriptId,string Account,string GroupId,bool ConfirmRealSend);
public sealed record LivePilotControlRequest(string JobId,string Action);
public sealed record LivePilotItem(
    string JobId,string ScriptName,string GroupName,string Account,
    string State,long Cursor,int TotalSteps,long NextDueMs,string Detail);

/// <summary>Explicit user-started native script dispatch; group IDs must be saved/selected.</summary>
public sealed record LiveBatchStartRequest(string ScriptId,IReadOnlyList<string> GroupIds,bool ConfirmRealSend,bool SkipUnavailableImages=false);
public sealed record LiveBatchPreflightRequest(
    string ScriptId,IReadOnlyList<string> GroupIds);
public sealed record LiveBatchPreflightIssue(string Level,string Message);
public sealed record LiveBatchPreflightResult(
    string ScriptName,int SelectedGroups,int SendableRows,
    IReadOnlyList<LiveBatchPreflightIssue> Issues,bool CanStart);
public sealed record LiveBatchMediaInspectionRequest(string ScriptId);
public sealed record LiveBatchMediaIssue(int Position,string Problem,string Action);
public sealed record LiveBatchMediaInspection(
    string ScriptName,int TotalRows,int SendableRows,
    IReadOnlyList<LiveBatchMediaIssue> Issues);
public sealed record LiveBatchStartResult(IReadOnlyList<string> JobIds,int GroupCount,int MessageCount);
public sealed record LiveBatchControlRequest(string JobId,string Action);
public sealed record LiveBatchHistoryPageRequest(int Page,int PageSize,string? Search=null,string? State=null);
public sealed record LiveBatchHistoryPage(IReadOnlyList<LiveBatchItem> Jobs,long Total,int Page,int PageSize);
public sealed record LiveBatchHistoryDetailRequest(string JobId);
public sealed record LiveBatchHistoryMessage(
    int Position,string AccountLabel,string Content,
    string State,string Detail,string SentAt);
public sealed record LiveBatchHistoryDetail(
    string JobId,string ScriptName,string GroupName,string State,
    long Cursor,int TotalSteps,IReadOnlyList<LiveBatchHistoryMessage> Messages);

public sealed record LiveBatchItem(
    string JobId,string ScriptName,string GroupId,string GroupName,
    string State,long Cursor,int TotalSteps,long NextDueMs,string Detail,
    long EstimatedRemainingMs=0,string DispatchState="",string RuntimePhase="");
/// <summary>Internal, not a UI-supplied raw file path.</summary>
public sealed record SignalMessagePayload(string Message,string? AttachmentPath);

public sealed record UpdateReadiness(
    bool CanUpdate,
    int ActiveJobs,
    int InFlightDispatches,
    string Reason);

public sealed record ControlRequest(string Command, JsonElement? Payload = null);
public sealed record ControlResponse(bool Ok, string? Error = null, object? Data = null);

public sealed record ReadHealthSnapshot(string StreamState,long LastConnectedMs,long LastEventMs,int Pending,int Attempted,string LastError,int Failed=0,int WaitingRetry=0);

public static class ControlCommands
{
    public const string Status="status";
    public const string Ping="ping";
    public const string Dashboard="dashboard";
    public const string SignalStatus="signal-status";
    public const string StartLink="start-link";
    public const string LinkStatus="link-status";
    public const string CancelLink="cancel-link";
    public const string PrepareUpdate="prepare-update";
    public const string UpdateStatus="update-status";
    public const string RecoveryOverview="recovery-overview";
    public const string PauseJob="pause-job";
    public const string ManualDispatchReview="manual-dispatch-review";
    public const string ScriptList="script-list";
    public const string ScriptRead="script-read";
    public const string ScriptVersions="script-versions";
    public const string ScriptVersionRead="script-version-read";
    public const string ScriptSave="script-save";
    public const string ScriptDelete="script-delete";
    public const string AccountGroupCatalog="account-group-catalog";
    public const string UpdateAccount="update-account";
    public const string SetSelectedGroups="set-selected-groups";
    public const string PreviewPlan="preview-plan";
    public const string PreviewList="preview-list";
    public const string PreviewControl="preview-control";
    public const string ImageImport="image-import";
    public const string ImageLookup="image-lookup";
    public const string ImageCheck="image-check";
    public const string LicenseStatus="license-status";
    public const string LicenseActivate="license-activate";
    public const string LicenseCheck="license-check";
    public const string LiveProbeSend="live-probe-send";
    public const string LivePilotPlan="live-pilot-plan";
    public const string LivePilotList="live-pilot-list";
    public const string LivePilotControl="live-pilot-control";
    public const string LiveBatchPreflight="live-batch-preflight";
    public const string LiveBatchStart="live-batch-start";
    public const string LiveBatchInspect="live-batch-inspect";
    public const string LiveBatchList="live-batch-list";
    public const string ReadHealth="read-health";
    public const string LiveBatchHistoryDetail="live-batch-history-detail";
    public const string LiveBatchHistoryPage="live-batch-history-page";
    public const string LiveBatchControl="live-batch-control";
}
