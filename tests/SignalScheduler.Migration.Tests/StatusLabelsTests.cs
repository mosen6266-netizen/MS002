using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class StatusLabelsTests
{
    [Theory]
    [InlineData("Running","运行中")]
    [InlineData("RecoveryRequired","结果待人工核对")]
    [InlineData("Stopped","已停止")]
    [InlineData("Completed","已完成")]
    public void TaskStatusIsLocalizedConsistently(string value,string expected)
    {
        Assert.Equal(expected,StatusLabels.Task(value));
        Assert.Equal(expected,new RecoveryJobItem("j","name",value,0,false,false).StateDisplay);
    }

    [Theory]
    [InlineData("Confirmed","Signal 已确认提交")]
    [InlineData("RecoveryRequired","发送结果待核对")]
    [InlineData("Unknown","发送结果待核对")]
    [InlineData("DefinitelyNotSent","确认未发送")]
    [InlineData("Failed","发送失败")]
    [InlineData(null,"未执行")]
    public void DeliveryStatusNeverClaimsRecipientReceipt(string? value,string expected)
    {
        Assert.Equal(expected,StatusLabels.Delivery(value));
    }

    [Fact]
    public void RecoveryItemUsesSameDeliveryLabels()
    {
        var item=new RecoveryDispatchItem("d","j",0,"g","a","Confirmed",null,null,0);
        Assert.Equal(StatusLabels.Delivery("Confirmed"),item.StateDisplay);
    }
}
