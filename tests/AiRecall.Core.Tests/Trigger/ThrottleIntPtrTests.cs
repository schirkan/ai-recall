using AiRecall.ScreenCapture.Trigger;

namespace AiRecall.Core.Tests.Trigger;

public class ThrottleIntPtrTests
{
    [Fact]
    public void NewHwnd_Allows()
    {
        var t = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(500));
        Assert.True(t.Allows(new IntPtr(0x1234), DateTimeOffset.Now));
    }

    [Fact]
    public void JustCaptured_Blocks()
    {
        var t = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0xABCD);
        t.Mark(hwnd, now);
        Assert.False(t.Allows(hwnd, now));
    }

    [Fact]
    public void AfterInterval_AllowsAgain()
    {
        var t = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(100));
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0x1234);
        t.Mark(hwnd, now);
        Assert.False(t.Allows(hwnd, now));
        Assert.False(t.Allows(hwnd, now.AddMilliseconds(50)));
        Assert.True(t.Allows(hwnd, now.AddMilliseconds(150)));
    }

    [Fact]
    public void DifferentHwnds_TrackedSeparately()
    {
        var t = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.Now;
        var hwndA = new IntPtr(0xAAAA);
        var hwndB = new IntPtr(0xBBBB);
        t.Mark(hwndA, now);
        Assert.False(t.Allows(hwndA, now));
        Assert.True(t.Allows(hwndB, now));
    }

    [Fact]
    public void ZeroWindow_OnlyAllowsOncePerCall()
    {
        var t = new Throttle<IntPtr>(TimeSpan.Zero);
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0x1234);
        t.Mark(hwnd, now);
        Assert.True(t.Allows(hwnd, now));
    }

    [Fact]
    public void NegativeWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Throttle<IntPtr>(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var t = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.Now;
        var hwnd = new IntPtr(0x1234);
        t.Mark(hwnd, now);
        t.Reset();
        Assert.True(t.Allows(hwnd, now));
    }

    [Fact]
    public void TrackedKeyCount_ReflectsState()
    {
        var t = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, t.TrackedKeyCount);
        t.Mark(new IntPtr(0x1), DateTimeOffset.Now);
        t.Mark(new IntPtr(0x2), DateTimeOffset.Now);
        Assert.Equal(2, t.TrackedKeyCount);
        t.Reset();
        Assert.Equal(0, t.TrackedKeyCount);
    }

    [Fact]
    public void HwndIntPtrZero_HandledAsKey()
    {
        // HWND 0 = "global" / Desktop. Sollte als normaler Key trackbar sein,
        // damit Filter upstream greifen kann (Worker verwirft HWND==0).
        var t = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.Now;
        var zero = IntPtr.Zero;
        t.Mark(zero, now);
        Assert.False(t.Allows(zero, now));
    }
}