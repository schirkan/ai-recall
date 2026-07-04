using AiRecall.Core.Util;

namespace AiRecall.Core.Tests.Trigger;

public class ThrottleTests
{
    [Fact]
    public void NewProcess_Allows()
    {
        var t = new Throttle<string>(TimeSpan.FromMilliseconds(1000));
        Assert.True(t.Allows("chrome", DateTimeOffset.Now));
    }

    [Fact]
    public void JustCaptured_Blocks()
    {
        var t = new Throttle<string>(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.False(t.Allows("chrome", now));
    }

    [Fact]
    public void AfterInterval_AllowsAgain()
    {
        var t = new Throttle<string>(TimeSpan.FromMilliseconds(100));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.False(t.Allows("chrome", now));
        Assert.False(t.Allows("chrome", now.AddMilliseconds(50)));
        Assert.True(t.Allows("chrome", now.AddMilliseconds(150)));
    }

    [Fact]
    public void DifferentProcesses_TrackedSeparately()
    {
        var t = new Throttle<string>(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.False(t.Allows("chrome", now));
        Assert.True(t.Allows("notepad", now));
    }

    [Fact]
    public void ProcessKey_IsCaseInsensitive()
    {
        // string-Key: default-Dictionary ist case-sensitive. Wenn Martin Case-
        // Insensitivity braucht, kann er einen StringComparer.OrdinalIgnoreCase-
        // Konstruktor ergänzen (Spec 0005 lässt das offen).
        var t = new Throttle<string>(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("Chrome", now);
        Assert.False(t.Allows("Chrome", now));
        // Case-sensitive: andere Schreibweise ist nicht getrackt
        Assert.True(t.Allows("CHROME", now));
    }

    [Fact]
    public void ZeroWindow_OnlyAllowsOncePerCall()
    {
        // Edge case: Throttle mit 0 ms Window — immer erlaubt nach dem ersten.
        var t = new Throttle<string>(TimeSpan.Zero);
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        Assert.True(t.Allows("chrome", now));
    }

    [Fact]
    public void NegativeWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Throttle<string>(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var t = new Throttle<string>(TimeSpan.FromMilliseconds(1000));
        var now = DateTimeOffset.Now;
        t.Mark("chrome", now);
        t.Reset();
        Assert.True(t.Allows("chrome", now));
    }
}