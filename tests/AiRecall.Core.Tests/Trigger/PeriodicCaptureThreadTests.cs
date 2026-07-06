using System.Threading.Channels;
using AiRecall.Trigger;

namespace AiRecall.Core.Tests.Trigger;

/// <summary>
/// Tests fuer <see cref="PeriodicCaptureThread"/> (Bug-Bash 2026-07-06 I-23).
/// Pattern analog zu HeartbeatThreadTests, aber mit IntervalMs statt IntervalSeconds.
/// </summary>
public class PeriodicCaptureThreadTests
{
    [Fact]
    public void Constructor_StoresIntervalAndWriter()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var pc = new PeriodicCaptureThread(intervalMs: 5000, channel.Writer);
        Assert.Equal(5000, pc.IntervalMs);
        pc.Dispose();
    }

    [Fact]
    public void NegativeInterval_ClampedToZero()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var pc = new PeriodicCaptureThread(intervalMs: -100, channel.Writer);
        Assert.Equal(0, pc.IntervalMs);
        pc.Dispose();
    }

    [Fact]
    public void ZeroInterval_DoesNotSpawnThread()
    {
        // IntervalMs = 0 → Periodic-Capture ist deaktiviert.
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var pc = new PeriodicCaptureThread(intervalMs: 0, channel.Writer);
        pc.Start();
        Assert.Equal(0, channel.Reader.Count);
    }

    [Fact]
    public void StartStop_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var pc = new PeriodicCaptureThread(intervalMs: 100, channel.Writer);
        pc.Start();
        Thread.Sleep(150);
        pc.Stop();
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var pc = new PeriodicCaptureThread(intervalMs: 500, channel.Writer);
        pc.Start();
        pc.Start();
        Thread.Sleep(50);
        pc.Stop();
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var pc = new PeriodicCaptureThread(intervalMs: 500, channel.Writer);
        pc.Stop();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var pc = new PeriodicCaptureThread(intervalMs: 500, channel.Writer);
        pc.Dispose();
        pc.Dispose();
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var pc = new PeriodicCaptureThread(intervalMs: 500, channel.Writer);
        pc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pc.Start());
    }
}
