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

public sealed record ControlRequest(string Command, JsonElement? Payload = null);
public sealed record ControlResponse(bool Ok, string? Error = null, object? Data = null);
public static class ControlCommands { public const string Status="status"; public const string Ping="ping"; }
