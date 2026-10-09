using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class CriticalAlertQueueTests
{
    [Fact]
    public void TwoOverlappingCriticalFaultsArePresentedInOrderWithoutDroppingEither()
    {
        var queue=new CriticalAlertQueue();
        Assert.True(queue.Enqueue("群 A 意外停止"));
        Assert.True(queue.TryBegin(out var first));
        Assert.Equal("群 A 意外停止",first);
        Assert.True(queue.IsPresenting);

        Assert.True(queue.Enqueue("群 B 意外停止"));
        Assert.False(queue.TryBegin(out _)); // do not stack two modals
        Assert.Equal(2,queue.PendingCount); // current + waiting

        Assert.True(queue.CompleteCurrent());
        Assert.True(queue.TryBegin(out var second));
        Assert.Equal("群 B 意外停止",second);
        Assert.True(queue.CompleteCurrent());
        Assert.Equal(0,queue.PendingCount);
    }

    [Fact]
    public void DeferredModalRetainsItsPlaceAheadOfSubsequentFaults()
    {
        var queue=new CriticalAlertQueue();
        queue.Enqueue("先发生的异常");
        queue.Enqueue("第二个异常");
        Assert.True(queue.TryBegin(out var first));
        Assert.Equal("先发生的异常",first);

        // WPF owner not yet loaded: do not clear pending messages.
        queue.DeferCurrent();
        Assert.False(queue.IsPresenting);
        Assert.Equal(2,queue.PendingCount);
        Assert.True(queue.TryBegin(out var retry));
        Assert.Equal(first,retry);
        Assert.True(queue.CompleteCurrent());
        Assert.True(queue.TryBegin(out var second));
        Assert.Equal("第二个异常",second);
        Assert.True(queue.CompleteCurrent());
        Assert.Equal(0,queue.PendingCount);
    }

    [Fact]
    public void FailedDialogMustBeDeferredRatherThanCountedAsAcknowledged()
    {
        var queue=new CriticalAlertQueue();
        queue.Enqueue("发送结果不明确");
        Assert.True(queue.TryBegin(out _));
        queue.DeferCurrent();
        Assert.Equal(1,queue.PendingCount);
        Assert.False(queue.IsPresenting);
        Assert.True(queue.TryBegin(out var retry));
        Assert.Equal("发送结果不明确",retry);
        Assert.True(queue.CompleteCurrent());
        Assert.False(queue.CompleteCurrent());
    }

    [Fact]
    public void CriticalDialogBodyUsesRealLineBreaksRatherThanLiteralBackslashes()
    {
        var body=CriticalAlertQueue.DialogText("群 A 发送异常");
        Assert.Contains("\n\n",body);
        Assert.DoesNotContain(@"\n",body);
        Assert.EndsWith("群 A 发送异常",body);
    }

    [Fact]
    public void LargeAlertBurstRetainsFifoOrderAndAccuratePendingCount()
    {
        var queue=new CriticalAlertQueue();
        for(var n=0;n<250;n++)queue.Enqueue($"任务 {n}");
        Assert.Equal(250,queue.PendingCount);
        for(var n=0;n<250;n++)
        {
            Assert.True(queue.TryBegin(out var next));
            Assert.Equal($"任务 {n}",next);
            Assert.Equal(250-n,queue.PendingCount);
            Assert.True(queue.CompleteCurrent());
        }
        Assert.Equal(0,queue.PendingCount);
        Assert.False(queue.TryBegin(out _));
    }

    [Fact]
    public void DistinctEventsWithIdenticalDescriptionsRemainDistinct()
    {
        var queue=new CriticalAlertQueue();
        queue.Enqueue("发送失败");
        queue.Enqueue("发送失败");
        Assert.Equal(2,queue.PendingCount);
        Assert.True(queue.TryBegin(out _));
        Assert.True(queue.CompleteCurrent());
        Assert.True(queue.TryBegin(out _));
        Assert.True(queue.CompleteCurrent());
        Assert.False(queue.TryBegin(out _));
        Assert.False(queue.Enqueue(" "));
    }
}
