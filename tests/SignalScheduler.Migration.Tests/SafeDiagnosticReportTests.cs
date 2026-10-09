using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class SafeDiagnosticReportTests
{
    [Fact]
    public void DiagnosticReportOmitsUntrustedNamesMessagesAndErrors()
    {
        const string sensitive="SENSITIVE-ACCOUNT-+491701234567";
        var data=new SafeDiagnosticSnapshot(
            new DateTimeOffset(2026,10,9,8,0,0,TimeSpan.FromHours(8)),
            sensitive,true,
            sensitive,
            12,10,5,3,9,2,
            sensitive,3,1,0,true);
        var report=SafeDiagnosticReport.Render(data);
        Assert.DoesNotContain(sensitive,report);
        Assert.DoesNotContain("+491701234567",report);
        Assert.Contains("后台连接：正常",report);
        Assert.Contains("账号数量：12",report);
        Assert.Contains("待处理回执：3",report);
        Assert.Contains("桌面版本：未知",report);
        Assert.Contains("Signal 服务状态：未知",report);
    }

    [Fact]
    public void MissingBackendDataIsNotPresentedAsZeroAccountsOrReceipts()
    {
        var data=new SafeDiagnosticSnapshot(
            DateTimeOffset.UtcNow,"8.0.0-beta.5",false,"",0,0,0,0,0,0,
            "",0,0,0,false,false,false);
        var report=SafeDiagnosticReport.Render(data);
        Assert.Contains("桌面版本：8.0.0-beta.5",report);
        Assert.Contains("后台连接：无法连接",report);
        Assert.Contains("账号数量：未取得数据",report);
        Assert.Contains("待处理回执：未取得数据",report);
        Assert.Contains("最近捕获事件：未取得数据",report);
        Assert.DoesNotContain("账号数量：0",report);
    }

    [Fact]
    public void InvalidMetricsAreClampedInsteadOfExportingSurprisingValues()
    {
        var data=new SafeDiagnosticSnapshot(DateTimeOffset.UtcNow,
            "8.0.0.0",true,"Ready",-2,int.MaxValue,0,0,0,0,
            "已连接",-5,int.MaxValue,0,true);
        var report=SafeDiagnosticReport.Render(data);
        Assert.Contains("账号数量：0",report);
        Assert.Contains("启用账号：10000000",report);
        Assert.Contains("待处理回执：0",report);
        Assert.Contains("已请求回执：10000000",report);
    }
}
