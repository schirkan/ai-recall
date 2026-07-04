using System.Threading.Channels;
using AiRecall.Trigger;

namespace AiRecall.Core.Tests.Trigger;

public class HeartbeatThreadTests
{
    [Fact]
    public void Constructor_StoresIntervalAndWriter()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var hb = new HeartbeatThread(intervalSeconds: 30, channel.Writer);
        Assert.Equal(30, hb.IntervalSeconds);
        hb.Dispose();
    }

    [Fact]
    public void NegativeInterval_ClampedToZero()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var hb = new HeartbeatThread(intervalSeconds: -5, channel.Writer);
        Assert.Equal(0, hb.IntervalSeconds);
        hb.Dispose();
    }

    [Fact]
    public void ZeroInterval_DoesNotSpawnThread()
    {
        // IntervalSeconds = 0 → Heartbeat ist deaktiviert, Start() tut nichts
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var hb = new HeartbeatThread(intervalSeconds: 0, channel.Writer);
        hb.Start();
        // kein Sleep nötig — Thread wurde gar nicht gestartet
        Assert.True(channel.Reader.Count == 0);
    }

    [Fact]
    public void StartStop_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var hb = new HeartbeatThread(intervalSeconds: 1, channel.Writer);
        hb.Start();
        // Kurz warten, damit der Thread die Sleep-Iteration startet
        Thread.Sleep(50);
        hb.Stop();
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var hb = new HeartbeatThread(intervalSeconds: 5, channel.Writer);
        hb.Start();
        hb.Start(); // darf nicht werfen
        Thread.Sleep(50);
        hb.Stop();
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var hb = new HeartbeatThread(intervalSeconds: 5, channel.Writer);
        hb.Stop();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var hb = new HeartbeatThread(intervalSeconds: 5, channel.Writer);
        hb.Dispose();
        hb.Dispose(); // darf nicht werfen
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var hb = new HeartbeatThread(intervalSeconds: 5, channel.Writer);
        hb.Dispose();
        Assert.Throws<ObjectDisposedException>(() => hb.Start());
    }

    [Fact]
    public void LoggerCallbacks_DoNotThrowOnMissing()
    {
        // LoggerCallbacks sind optional und werden nur bei Fehlern aufgerufen.
        // Hier nur Smoke-Check, dass kein NullReferenceException kommt.
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        using var hb = new HeartbeatThread(
            intervalSeconds: 1,
            channel.Writer,
            logWarn: null,
            logError: null);
        hb.Start();
        Thread.Sleep(50);
        hb.Stop();
    }
}