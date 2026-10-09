using SignalScheduler.Engine.Signal;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class SignalDaemonDiagnosticSafetyTests
{
    [Fact]
    public void IncomingMessageEventIsNeverPersistedEvenWhenTextContainsInfo()
    {
        var raw="{\"envelope\":{\"sourceName\":\"PRIVATE PERSON\",\"dataMessage\":{\"message\":\"INFO ERROR WARNING SECRET\"}}}";
        Assert.Null(SignalGuardian.SanitizeDaemonDiagnostic(raw));
    }

    [Fact]
    public void DaemonExceptionRetainsOnlyFixedCategory()
    {
        var raw="2026-10-10 ERROR MultiAccountManager - Failed to load +491234567890: AccountCheckException";
        var safe=SignalGuardian.SanitizeDaemonDiagnostic(raw);
        Assert.Equal("ERROR SignalAccount.AccountCheckException",safe);
        Assert.DoesNotContain("+491234567890",safe!);
    }

    [Fact]
    public void ServerErrorDoesNotExposeUntrustedDetail()
    {
        var raw="Caused by: org.signal.libsignal.net.ServerSideErrorException: Server-side error for PRIVATE MEMBER";
        var safe=SignalGuardian.SanitizeDaemonDiagnostic(raw);
        Assert.Equal("ERROR SignalSend.ServerSideErrorException",safe);
        Assert.DoesNotContain("PRIVATE MEMBER",safe!);
    }

    [Fact]
    public void ArbitraryInfoOrSensitiveLineIsNeverWritten()
    {
        Assert.Null(SignalGuardian.SanitizeDaemonDiagnostic(
            "INFO USER SECRET MEMBER GROUP"));
        Assert.Null(SignalGuardian.SanitizeDaemonDiagnostic(null));
    }
}
