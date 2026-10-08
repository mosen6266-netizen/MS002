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
public sealed record RecoveryJobItem(
    string JobId,string Name,string State,long Cursor,bool IsLegacy,bool NeedsReview);
public sealed record RecoveryDispatchItem(
    string DispatchKey,string JobId,long Cursor,string GroupId,string AccountId,
    string State,string? ProviderMessageId,string? Detail,long UpdatedAt);
public sealed record RecoveryOverview(
    IReadOnlyList<RecoveryJobItem> Jobs,IReadOnlyList<RecoveryDispatchItem> Dispatches);
public sealed record PauseJobRequest(string JobId);
public sealed record PauseJobResult(string JobId,string State,string Detail);

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

public sealed record ManagedAccount(
    string Account,string Label,bool Enabled,bool Online,long Revision);
public sealed record ManagedGroup(
    string GroupId,string Name,int MemberAccounts,bool Selected);
public sealed record AccountGroupOverview(
    IReadOnlyList<ManagedAccount> Accounts,IReadOnlyList<ManagedGroup> Groups);
public sealed record UpdateManagedAccount(
    string Account,string Label,bool Enabled,long ExpectedRevision);
public sealed record UpdateGroupSelection(IReadOnlyList<string> GroupIds);

public sealed record UpdateReadiness(
    bool CanUpdate,
    int ActiveJobs,
    int InFlightDispatches,
    string Reason);

public sealed record ControlRequest(string Command, JsonElement? Payload = null);
public sealed record ControlResponse(bool Ok, string? Error = null, object? Data = null);

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
    public const string ScriptList="script-list";
    public const string ScriptRead="script-read";
    public const string ScriptSave="script-save";
    public const string AccountGroupCatalog="account-group-catalog";
    public const string UpdateAccount="update-account";
    public const string SetSelectedGroups="set-selected-groups";
}
