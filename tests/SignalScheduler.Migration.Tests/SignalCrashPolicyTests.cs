using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class SignalCrashPolicyTests
{
    [Theory]
    [InlineData("java.lang.OutOfMemoryError: Java heap space SECRET", "JVM_MEMORY")]
    [InlineData("java.lang.UnsatisfiedLinkError: 1234", "JVM_NATIVE")]
    [InlineData("NoClassDefFoundError: net/sourceforge/argparse4j/X", "JVM_CLASSPATH")]
    [InlineData("MissingResourceException: ArgumentParserImpl, locale en_US", "SIGNAL_BUNDLE")]
    [InlineData("java.net.BindException: Address already in use", "RPC_PORT")]
    [InlineData("java.nio.file.AccessDeniedException: C:\\secret", "FILE_PERMISSION")]
    [InlineData("AccountCheckException +49111111111", "SIGNAL_ACCOUNT")]
    [InlineData("java.net.SSLHandshakeException: remote fault", "SIGNAL_NETWORK")]
    public void RecognizesFixedFailureCategoriesWithoutLeakingRawDetails(
        string raw,string expected)
    {
        Assert.Equal(expected,SignalCrashPolicy.Classify(raw));
        var description=SignalCrashPolicy.ExitDetail(1,
            SignalCrashPolicy.Classify(raw),true,3);
        Assert.Contains(expected,description);
        Assert.DoesNotContain(raw,description);
        Assert.DoesNotContain("+49111111111",description);
        Assert.DoesNotContain("C:\\secret",description);
    }

    [Theory]
    [InlineData("{\"envelope\":{\"dataMessage\":{\"message\":\"OutOfMemoryError\"}}}")]
    [InlineData("INFO PRIVATE CONTACT SEND 123456789")]
    [InlineData("")]
    public void NeverClassifiesPrivateEnvelopeOrArbitraryLogs(string raw)
    {
        Assert.Equal("",SignalCrashPolicy.Classify(raw));
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000005),"OS_ACCESS_VIOLATION")]
    [InlineData(unchecked((int)0xC0000135),"OS_DLL_MISSING")]
    [InlineData(unchecked((int)0xC0000142),"OS_DLL_INIT")]
    [InlineData(unchecked((int)0xC000001D),"OS_ILLEGAL_INSTRUCTION")]
    [InlineData(unchecked((int)0xC0000409),"OS_STACK_BUFFER_OVERRUN")]
    [InlineData(0,"")]
    [InlineData(1,"")]
    public void WindowsExitCodesAreSafelyClassified(int exitCode,string category)
    {
        Assert.Equal(category,SignalCrashPolicy.ClassifyExitCode(exitCode));
        if(category.Length>0)
            Assert.Contains(category,SignalCrashPolicy.ExitDetail(
                exitCode,category,true,3));
    }

    [Fact]
    public void ThreeRapidUnexpectedExitsOpenCircuitInsteadOfInfiniteRestart()
    {
        var limiter=new SignalRestartBudget();
        var start=new DateTimeOffset(2026,11,15,12,0,0,TimeSpan.Zero);
        Assert.True(limiter.Register(start));
        Assert.True(limiter.Register(start.AddSeconds(9)));
        Assert.False(limiter.Register(start.AddSeconds(18)));
        Assert.Equal(3,limiter.Count);
        Assert.False(limiter.Register(start.AddSeconds(27)));
        Assert.Contains("已停止自动重启",
            SignalCrashPolicy.ExitDetail(-1,"JVM_MEMORY",true,4));
    }

    [Fact]
    public void CrashCounterExpiresOldWindowAndResetsAfterStableRun()
    {
        var limiter=new SignalRestartBudget();
        var start=DateTimeOffset.UtcNow;
        Assert.True(limiter.Register(start));
        Assert.True(limiter.Register(start.AddMinutes(2)));
        Assert.True(limiter.Register(start.AddMinutes(8)));
        Assert.Equal(1,limiter.Count);
        limiter.ResetAfterStableRun();
        Assert.Equal(0,limiter.Count);
        Assert.True(limiter.Register(start.AddMinutes(9)));
    }
}
