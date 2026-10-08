using System.Text.Json.Serialization;

namespace SignalScheduler.Engine.Signal;

public enum SignalHealthState
{
    Starting,
    Healthy,
    Busy,
    Fault,
    RuntimeMissing
}

public sealed record SignalAccountInfo(string Account,string? Number,string? Aci);
public sealed record SignalGroupInfo(string Account,string GroupId,string Name,bool IsMember,bool IsBlocked,string MembersJson);

public sealed record SignalHealthSnapshot(
    SignalHealthState State,
    string Detail,
    string Version,
    bool RuntimeReady,
    bool ExternalDaemon,
    IReadOnlyList<SignalAccountInfo> Accounts,
    DateTimeOffset UpdatedAt);

public enum SignalLinkState
{
    WaitingForScan,
    Linked,
    Failed,
    Expired
}

public sealed record SignalLinkSession(
    string SessionId,
    string DeviceLinkUri,
    SignalLinkState State,
    string Detail,
    string? Account,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
