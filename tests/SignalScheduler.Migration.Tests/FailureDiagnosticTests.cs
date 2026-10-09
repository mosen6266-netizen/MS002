using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class FailureDiagnosticTests
{
    [Fact]
    public void HistoricRpcInternalCodeDoesNotClaimSpecificRootCause()
    {
        var d=FailureDiagnostics.Analyze(
            "Signal 返回错误码 -32603，是否已经局部发送未知。","RecoveryRequired");
        Assert.Equal("SIGNAL_RPC_INTERNAL",d.Code);
        Assert.Contains("无法",d.Certainty);
    }

    [Fact]
    public void EvidenceOfServerSideErrorIsSeparatedFromGenericRpcCode()
    {
        var d=FailureDiagnostics.Analyze(
            "Signal 错误码 -32603（Signal 服务端处理失败）。发送结果未确认","RecoveryRequired");
        Assert.Equal("SIGNAL_SERVER_ERROR",d.Code);
        Assert.Contains("未确定",d.Certainty);
    }

    [Fact]
    public void AccountLoadFailureRemainsDistinctFromDeliveryOutcome()
    {
        Assert.Equal("SIGNAL_ACCOUNT",
            FailureDiagnostics.Analyze("AccountCheckException: Closed unexpectedly").Code);
    }

    [Fact]
    public void NeverExportPrivateExceptionDetails()
    {
        var data=new SafeDiagnosticSnapshot(
            DateTimeOffset.UtcNow,"8.0.0",true,"healthy",
            11,11,8,13,15,1,"已连接",38,1703,0,true,
            DispatchDiagnostics:new[]{
                new DiagnosticCategoryCount("SIGNAL_SERVER_ERROR",3),
                new DiagnosticCategoryCount("PHONE +491234567890 SECRET",100)
            },
            ExaminedDispatches:10,RecoveryDataAvailable:true,
            RuntimeStatus:new InstalledRuntimeStatus("缺失","存在","untrusted"));
        var txt=SafeDiagnosticReport.Render(data);
        Assert.Contains("SIGNAL_SERVER_ERROR：3",txt);
        Assert.DoesNotContain("+491234567890",txt);
        Assert.DoesNotContain("SECRET",txt);
        Assert.Contains("Java 主程序：缺失",txt);
        Assert.Contains("signal-cli 库文件：存在",txt);
        Assert.DoesNotContain("untrusted",txt);
    }

    [Fact]
    public void RuntimePresenceCheckReportsMissingFilesWithoutChangingAnything()
    {
        var root=Path.Combine(Path.GetTempPath(),"ms002-runtime-"+Guid.NewGuid().ToString("N"));
        var desktop=Path.Combine(root,"Desktop");
        var java=Path.Combine(root,"Runtime","signal-stack-v1","jre","bin","java.exe");
        var lib=Path.Combine(root,"Runtime","signal-stack-v1","signal-cli","lib");
        Directory.CreateDirectory(desktop);
        try
        {
            var absent=InstalledRuntimeCheck.Inspect(desktop);
            Assert.Equal("缺失",absent.Java);
            Assert.Equal("缺失",absent.SignalCli);
            Directory.CreateDirectory(Path.GetDirectoryName(java)!);
            Directory.CreateDirectory(lib);
            File.WriteAllText(java,"fixture");
            File.WriteAllText(Path.Combine(lib,"signal-cli-fixture.jar"),"fixture");
            var present=InstalledRuntimeCheck.Inspect(desktop);
            Assert.Equal("存在",present.Java);
            Assert.Equal("存在",present.SignalCli);
        }
        finally
        {
            Directory.Delete(root,recursive:true);
        }
    }
}
