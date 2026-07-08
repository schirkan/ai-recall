using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Configuration;
using AiRecall.Trigger;

using Serilog;

namespace AiRecall.Core.Tests.Trigger;

/// <summary>
/// Tests fuer <see cref="MeetingPresencePoller"/> (Spec 0013 v0.3 §1).
/// Edge-Detection + Start-Debounce (MinMeetingDurationSeconds) + Stop-Edge.
/// Tests laufen deterministisch mit FakeProbe + FakeTicker + FakeClock.
/// </summary>
public class MeetingPresencePollerTests
{
    // =============================================================================
    // Fakes
    // =============================================================================

    /// <summary>Probe mit Queue: pro Aufruf wird ein Snapshot entnommen.</summary>
    private sealed class FakeProbe : IMeetingPresenceProbe
    {
        private readonly Queue<MeetingPresenceSnapshot> _queue = new();
        private TaskCompletionSource _lastCallTcs = new();
        private Func<Exception?>? _throwFactory;

        public int CallCount { get; private set; }
        public Task LastCallTask => _lastCallTcs.Task;

        public void Push(MeetingPresenceSnapshot snap) => _queue.Enqueue(snap);

        public void ThrowOnNextCall(Func<Exception?> throwFactory) => _throwFactory = throwFactory;

        public async Task<MeetingPresenceSnapshot> GetSnapshotAsync(TeamsConfig cfg, CancellationToken ct)
        {
            CallCount++;
            var tcs = _lastCallTcs;
            _lastCallTcs = new TaskCompletionSource();
            tcs.TrySetResult();
            ct.ThrowIfCancellationRequested();

            if (_throwFactory is not null)
            {
                var ex = _throwFactory();
                _throwFactory = null;
                if (ex is not null) throw ex;
            }

            if (!_queue.TryDequeue(out var snap))
            {
                throw new InvalidOperationException(
                    "FakeProbe: keine Snapshots mehr in der Queue (Call #" + CallCount + ")");
            }
            await Task.Yield();
            return snap;
        }
    }

    /// <summary>Manuell gesteuerter Ticker: <see cref="TickOnce"/> loest genau einen Wait auf.</summary>
    private sealed class FakeTicker : IPresenceTicker
    {
        private readonly SemaphoreSlim _gate = new(0, 1);
        public int TickCount { get; private set; }
        public bool Disposed { get; private set; }

        public void TickOnce()
        {
            TickCount++;
            _gate.Release();
        }

        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken ct)
        {
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            return !Disposed;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            // Loop ggf. freigeben, falls er noch wartet
            try { _gate.Release(); } catch (SemaphoreFullException) { }
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Manuell gesteuerte Uhr.</summary>
    private sealed class FakeClock : IPresenceClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
        public void Set(DateTimeOffset t) => UtcNow = t;
    }

    private static TeamsConfig ValidConfig(int minDurationSec = 30, int pollSec = 5)
    {
        var c = new TeamsConfig { Enabled = true, AutoRecordMeetings = true };
        c.MinMeetingDurationSeconds = minDurationSec;
        c.PresencePollIntervalSeconds = pollSec;
        return c;
    }

    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    /// <summary>
    /// Erzeugt einen TCS, der resolved, sobald der Poller den naechsten Snapshot
    /// per Evaluate verarbeitet hat. Setzt den internen OnSnapshotEvaluated-Hook
    /// entsprechend. <b>Deterministisch</b>: nach dem await ist der interne
    /// State (IsActive, Events, Current) konsistent.
    /// </summary>
    private static Task<MeetingPresenceSnapshot> NewProcessedSignal(MeetingPresencePoller poller)
    {
        var tcs = new TaskCompletionSource<MeetingPresenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        poller.OnSnapshotEvaluated = s => { tcs.TrySetResult(s); return Task.CompletedTask; };
        return tcs.Task;
    }

    // =============================================================================
    // Tests
    // =============================================================================

    [Fact]
    public void Constructor_NotRunning_ByDefault()
    {
        var poller = new MeetingPresencePoller(new FakeProbe(), SilentLogger());
        Assert.False(poller.IsRunning);
        Assert.False(poller.IsActive);
        Assert.Null(poller.Current);
    }

    [Fact]
    public async Task StartAsync_SetsIsRunning_AndStopAsync_ClearsIt()
    {
        var ticker = new FakeTicker();
        var poller = new MeetingPresencePoller(new FakeProbe(), ticker, new FakeClock(), SilentLogger());
        await poller.StartAsync(ValidConfig(), CancellationToken.None);
        Assert.True(poller.IsRunning);
        await poller.StopAsync(CancellationToken.None);
        Assert.False(poller.IsRunning);
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        var ticker = new FakeTicker();
        var poller = new MeetingPresencePoller(new FakeProbe(), ticker, new FakeClock(), SilentLogger());
        await poller.StartAsync(ValidConfig(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => poller.StartAsync(ValidConfig(), CancellationToken.None));
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_AfterDispose_Throws()
    {
        var poller = new MeetingPresencePoller(new FakeProbe(), SilentLogger());
        await poller.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => poller.StartAsync(ValidConfig(), CancellationToken.None));
    }

    [Fact]
    public async Task FirstActiveTick_StartsDebounce_NoEventFires()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        await poller.StartAsync(ValidConfig(), CancellationToken.None);
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);

        Assert.Empty(events);
        Assert.False(poller.IsActive);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ActiveTickAfterDebounceWindow_EmitsPresenceChangedTrue()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        await poller.StartAsync(ValidConfig(minDurationSec: 30), CancellationToken.None);

        // 1. Tick: active, debounce startet
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);

        // 2. Tick: 20s spaeter, noch innerhalb Debounce → kein Event
        clock.Advance(TimeSpan.FromSeconds(20));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Empty(events);

        // 3. Tick: 15s spaeter (insgesamt 35s) → Debounce erreicht → Event
        clock.Advance(TimeSpan.FromSeconds(15));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        var processed = await NewProcessedSignal(poller);

        Assert.True(processed.IsActive);
        Assert.Single(events);
        Assert.True(poller.IsActive);
        var ev = events[0];
        Assert.Equal("Topic A", ev.Topic);
        Assert.Equal("abc12345", ev.ChatIdShort);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InactiveToActiveToInactive_WithinDebounce_NoEvent()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        await poller.StartAsync(ValidConfig(minDurationSec: 30), CancellationToken.None);

        // active, debounce startet
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);

        // 10s spaeter: inactive → Debounce wird verworfen
        clock.Advance(TimeSpan.FromSeconds(10));
        probe.Push(new MeetingPresenceSnapshot(false, null, null, null));
        ticker.TickOnce();
        await NewProcessedSignal(poller);

        // 20s spaeter: noch inactive → kein Event
        clock.Advance(TimeSpan.FromSeconds(20));
        probe.Push(new MeetingPresenceSnapshot(false, null, null, null));
        ticker.TickOnce();
        await NewProcessedSignal(poller);

        Assert.Empty(events);
        Assert.False(poller.IsActive);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ActiveToInactive_AfterConfirmed_EmitsPresenceChangedFalse()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        await poller.StartAsync(ValidConfig(minDurationSec: 5), CancellationToken.None);

        // active → debounce (5s)
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        clock.Advance(TimeSpan.FromSeconds(10));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.True(poller.IsActive);
        Assert.Single(events);

        // inactive → stop-event
        probe.Push(new MeetingPresenceSnapshot(false, null, null, null));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Equal(2, events.Count);
        Assert.False(events[1].IsActive);
        Assert.Null(events[1].Topic);
        Assert.False(poller.IsActive);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RepeatedActiveTicks_NoRepeatEvent()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        await poller.StartAsync(ValidConfig(minDurationSec: 5), CancellationToken.None);

        // active → debounce
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);

        // 10s spaeter: confirmed-active
        clock.Advance(TimeSpan.FromSeconds(10));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Single(events);

        // nochmal active: kein zweites Event
        clock.Advance(TimeSpan.FromSeconds(5));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Single(events);

        // und nochmal
        clock.Advance(TimeSpan.FromSeconds(5));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Single(events);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AutoRecordMeetingsFalse_NeverFiresEvent()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        var cfg = ValidConfig(minDurationSec: 1);
        cfg.AutoRecordMeetings = false;

        await poller.StartAsync(cfg, CancellationToken.None);

        for (int i = 0; i < 5; i++)
        {
            probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
            ticker.TickOnce();
            await NewProcessedSignal(poller);
            clock.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.Empty(events);
        // Current wird trotzdem aktualisiert
        Assert.NotNull(poller.Current);
        Assert.True(poller.Current!.IsActive);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProbeThrows_LoopContinues_NextTickWorks()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        await poller.StartAsync(ValidConfig(minDurationSec: 5), CancellationToken.None);

        // Tick 1: probe wirft → Loop ueberlebt, Current bleibt null.
        // Hook wird bei Exception NICHT gefeuert (continue ueberspringt den Rest),
        // daher kein NewProcessedSignal hier. Stattdessen: Warten, dass der
        // Probe-Call durch ist, und der Loop auf den naechsten Tick wartet.
        probe.ThrowOnNextCall(() => new InvalidOperationException("boom"));
        ticker.TickOnce();
        // Probe wirft → Loop ruft KEINEN Hook auf (continue vor Evaluate).
        // Wir warten, dass die Probe durch ist + Loop parat fuer naechsten Tick.
        await Task.Delay(50);

        // Tick 2: jetzt active → debounce startet
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Empty(events);

        // Tick 3: bestaetigt
        clock.Advance(TimeSpan.FromSeconds(10));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Single(events);

        Assert.True(poller.IsRunning);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Current_IsUpdated_EachTick_EvenWithoutEvent()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());

        await poller.StartAsync(ValidConfig(minDurationSec: 999), CancellationToken.None);

        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.NotNull(poller.Current);
        Assert.True(poller.Current!.IsActive);
        Assert.Equal("Topic A", poller.Current.Topic);

        probe.Push(new MeetingPresenceSnapshot(false, null, null, null));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.NotNull(poller.Current);
        Assert.False(poller.Current!.IsActive);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_StopsLoop_NoMoreEvents()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, SilentLogger());
        var events = new List<MeetingPresenceStateChangedEventArgs>();
        poller.PresenceChanged += (_, e) => events.Add(e);

        await poller.StartAsync(ValidConfig(minDurationSec: 5), CancellationToken.None);
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        clock.Advance(TimeSpan.FromSeconds(10));
        probe.Push(new MeetingPresenceSnapshot(true, "Topic A", "Meeting | Topic A - MS Teams", "abc12345"));
        ticker.TickOnce();
        await NewProcessedSignal(poller);
        Assert.Single(events);

        await poller.StopAsync(CancellationToken.None);

        // Nach Stop: kein Tick mehr, kein Event
        probe.Push(new MeetingPresenceSnapshot(false, null, null, null));
        ticker.TickOnce();
        await Task.Delay(150);
        Assert.Single(events);
    }

    [Fact]
    public async Task DisposeAsync_StopsAndDisposes()
    {
        var ticker = new FakeTicker();
        var poller = new MeetingPresencePoller(new FakeProbe(), ticker, new FakeClock(), SilentLogger());
        await poller.StartAsync(ValidConfig(), CancellationToken.None);
        await poller.DisposeAsync();
        Assert.False(poller.IsRunning);
    }
}
