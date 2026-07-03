using AiRecall.ScreenCapture.Trigger;

namespace AiRecall.Core.Tests.Trigger;

public class ThrottleTests
{
    [Fact]
    public void NewProcess_Allows()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(1000));
        Assert.True(t.Allows("chrome", DateTimeOffset.Now));
    }

    [Fact]
    public void JustCaptured_Blocks()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.False(t.Allows("chrome", now));
    }

    [Fact]
    public void AfterInterval_AllowsAgain()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(100));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.False(t.Allows("chrome", now));
        Assert.False(t.Allows("chrome", now.AddMilliseconds(50)));
        Assert.True(t.Allows("chrome", now.AddMilliseconds(150)));
    }

    [Fact]
    public void DifferentProcesses_TrackedSeparately()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.False(t.Allows("chrome", now));
        Assert.True(t.Allows("notepad", now));
    }

    [Fact]
    public void ProcessKey_IsCaseInsensitive()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("Chrome", now);
        Assert.False(t.Allows("CHROME", now));
    }

    [Fact]
    public void ZeroWindow_OnlyAllowsOncePerCall()
    {
        // Edge case: Throttle mit 0 ms Window — immer erlaubt nach dem ersten.
        var t = new Throttle(TimeSpan.Zero);
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.True(t.Allows("chrome", now));
    }

    [Fact]
    public void NegativeWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Throttle(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var t = new Throttle(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        t.Reset();
        Assert.True(t.Allows("chrome", now));
    }
}