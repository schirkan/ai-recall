using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class TrayIconStateTests
{
    [Fact]
    public void FromSupervisor_Stopped_StartEnabledStopDisabled()
    {
        var s = TrayIconState.FromSupervisor(TriggerState.Stopped, captureCount: 0, crashCount: 0);
        Assert.True(s.StartEnabled);
        Assert.False(s.StopEnabled);
        Assert.Equal("Stopped", s.StatusText);
        Assert.Contains("Stopped", s.TooltipText);
    }

    [Fact]
    public void FromSupervisor_Starting_BothDisabled()
    {
        var s = TrayIconState.FromSupervisor(TriggerState.Starting, captureCount: 0, crashCount: 0);
        Assert.False(s.StartEnabled);
        Assert.False(s.StopEnabled);
        Assert.Equal("Starting…", s.StatusText);
    }

    [Fact]
    public void FromSupervisor_Running_StartDisabledStopEnabled()
    {
        var s = TrayIconState.FromSupervisor(TriggerState.Running, captureCount: 42, crashCount: 0);
        Assert.False(s.StartEnabled);
        Assert.True(s.StopEnabled);
        Assert.Contains("42", s.StatusText);
        Assert.Contains("Running", s.TooltipText);
        Assert.Contains("42", s.TooltipText);
    }

    [Fact]
    public void FromSupervisor_Stopping_BothDisabled()
    {
        var s = TrayIconState.FromSupervisor(TriggerState.Stopping, captureCount: 5, crashCount: 0);
        Assert.False(s.StartEnabled);
        Assert.False(s.StopEnabled);
        Assert.Equal("Stopping…", s.StatusText);
    }

    [Fact]
    public void FromSupervisor_Crashed_StartEnabled()
    {
        var s = TrayIconState.FromSupervisor(TriggerState.Crashed, captureCount: 0, crashCount: 3);
        Assert.True(s.StartEnabled);
        Assert.False(s.StopEnabled);
        Assert.Contains("Crashed", s.StatusText);
        Assert.Contains("3", s.TooltipText);
    }

    [Fact]
    public void FromSupervisor_Stopped_WithPriorCrashes_ShowsCrashCount()
    {
        var s = TrayIconState.FromSupervisor(TriggerState.Stopped, captureCount: 0, crashCount: 2);
        Assert.True(s.StartEnabled);
        Assert.Contains("2", s.TooltipText);
    }

    [Fact]
    public void FromSupervisor_InvalidState_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TrayIconState.FromSupervisor((TriggerState)999, 0, 0));
    }

    [Fact]
    public void IconGlyph_ReflectsState()
    {
        Assert.Equal("🔴", TrayIconState.FromSupervisor(TriggerState.Stopped, 0, 0).IconGlyph);
        Assert.Equal("🟡", TrayIconState.FromSupervisor(TriggerState.Starting, 0, 0).IconGlyph);
        Assert.Equal("🟢", TrayIconState.FromSupervisor(TriggerState.Running, 5, 0).IconGlyph);
        Assert.Equal("🟡", TrayIconState.FromSupervisor(TriggerState.Stopping, 5, 0).IconGlyph);
        Assert.Equal("⚠", TrayIconState.FromSupervisor(TriggerState.Crashed, 0, 1).IconGlyph);
    }
}