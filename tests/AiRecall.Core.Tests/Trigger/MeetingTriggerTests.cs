using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Audio;
using AiRecall.Core.Configuration;
using AiRecall.Core.Tests.Audio;
using AiRecall.Trigger;
using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Core.Tests.Trigger;

/// <summary>
/// Tests fuer <see cref="MeetingTrigger"/> (Spec 0013 v0.3 Trigger-Wiring).
/// Poller.PresenceChanged → RecordingSession start/stop → TranscriptionWorker.Enqueue.
/// </summary>
public class MeetingTriggerTests
{
    private readonly string _root;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public MeetingTriggerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "meeting-trigger-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // =============================================================================
    // Fakes
    // =============================================================================

    private sealed class FakeProbe : IMeetingPresenceProbe
    {
        private readonly Queue<MeetingPresenceSnapshot> _queue = new();
        private TaskCompletionSource _lastCallTcs = new();
        public int CallCount { get; private set; }
        public Task LastCallTask => _lastCallTcs.Task;
        public void Push(MeetingPresenceSnapshot snap) => _queue.Enqueue(snap);
        public async Task<MeetingPresenceSnapshot> GetSnapshotAsync(TeamsConfig cfg, CancellationToken ct)
        {
            CallCount++;
            var tcs = _lastCallTcs;
            _lastCallTcs = new TaskCompletionSource();
            tcs.TrySetResult();
            ct.ThrowIfCancellationRequested();
            if (!_queue.TryDequeue(out var snap))
                throw new InvalidOperationException("FakeProbe: queue empty");
            await Task.Yield();
            return snap;
        }
    }

    private sealed class FakeTicker : IPresenceTicker
    {
        private readonly SemaphoreSlim _gate = new(0, 1);
        public int TickCount { get; private set; }
        public bool Disposed { get; private set; }
        public void TickOnce() { TickCount++; _gate.Release(); }
        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken ct)
        {
            try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            return !Disposed;
        }
        public ValueTask DisposeAsync() { Disposed = true; try { _gate.Release(); } catch { } _gate.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeClock : IPresenceClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan d) => UtcNow = UtcNow.Add(d);
    }

    private sealed class FakeProvider : ITranscriptionProvider
    {
        public string Name => "fake";
        public int CallCount { get; private set; }
        public Task<TranscriptionResult> TranscribeAsync(
            string stereoPath, TranscriptionOptions options,
            IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new TranscriptionResult(
                Segments: new List<TranscriptionSegment> {
                    new("S0", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hi"),
                },
                ProviderName: "fake",
                AudioDuration: TimeSpan.FromSeconds(1),
                SpeakerCount: 1,
                SpeakerLabels: new List<string> { "S0" },
                ErrorMessage: null));
        }
    }

    /// <summary>Minimaler Fake-AudioRecorder (leere Sample-Generierung).</summary>
    private sealed class FakeAudioRecorder : IAudioRecorder
    {
        public AudioFormat Format { get; }
        public RecordingState State { get; private set; } = RecordingState.Created;
        public int StartCallCount { get; private set; }
        public int StopCallCount { get; private set; }
        public byte[]? StoppedData { get; private set; }

        public FakeAudioRecorder(AudioFormat format) { Format = format; }
        public void Start() { StartCallCount++; State = RecordingState.Recording; }
        public byte[] Stop() { StopCallCount++; State = RecordingState.Recorded; StoppedData = Array.Empty<byte>(); return StoppedData; }
        public void Dispose() { State = RecordingState.Failed; }
    }

    private sealed class FakeRecorderFactory : IAudioRecorderFactory
    {
        public List<FakeAudioRecorder> Created { get; } = new();
        public IAudioRecorder Create(AudioDeviceInfo device, AudioFormat format, bool loopback)
        {
            var r = new FakeAudioRecorder(format);
            Created.Add(r);
            return r;
        }
    }

    private sealed class FakeDeviceProvider : IAudioDeviceProvider
    {
        private readonly AudioDeviceInfo _input = new("mic-1", "Default Mic", "USB");
        private readonly AudioDeviceInfo _loop = new("loop-1", "Default Speakers", "USB");
        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => new[] { _input };
        public IReadOnlyList<AudioDeviceInfo> EnumerateLoopbackDevices() => new[] { _loop };
        public AudioDeviceInfo? GetDefaultInputDevice() => _input;
        public AudioDeviceInfo? GetDefaultLoopbackDevice() => _loop;
    }

    private static TranscriptionOptions DefaultOptions() => new(
        Language: "deu", DiarizationRequired: true, MaxSpeakers: 4, ApiKey: "test", EndpointOverride: null);

    /// <summary>Echter RecordingSession mit Fakes (analog RecordingSessionTests).</summary>
    private (MeetingPresencePoller poller, TranscriptionWorker worker, Func<MeetingRecordingContext, RecordingSession> factory) NewPollerWorkerFactory()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, _logger);
        var worker = new TranscriptionWorker(new FakeProvider(), maxParallel: 1, logger: _logger);

        var input = new AudioDeviceInfo("mic-1", "Default Mic", "USB");
        var loop = new AudioDeviceInfo("loop-1", "Default Speakers", "USB");
        var devices = new FakeDeviceProvider();
        var recorders = new FakeRecorderFactory();
        // AudioConfig.StorageRoot auf den Test-Temp-Pfad setzen, damit
        // parallele xUnit-Tests nicht in den geteilten AppContext.BaseDirectory-
        // Pfad schreiben (sonst IOException auf meta.md).
        var audioConfig = new AudioConfig { StorageRoot = _root };

        Func<MeetingRecordingContext, RecordingSession> factory = ctx =>
        {
            var folder = Path.Combine(_root, "meetings", ctx.ChatIdShort);
            return new RecordingSession(
                meetingIdShort: ctx.ChatIdShort,
                startedAt: DateTimeOffset.UtcNow,
                topic: ctx.Topic,
                config: audioConfig,
                logger: _logger,
                recorderFactory: recorders,
                deviceProvider: devices);
        };
        return (poller, worker, factory);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000, string? what = null)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        if (!condition()) throw new TimeoutException($"WaitUntilAsync: {what ?? "?"} not met within {timeoutMs}ms");
    }

    // =============================================================================
    // Tests (alle async Task wegen IAsyncDisposable-MeetingTrigger)
    // =============================================================================

    [Fact]
    public async Task Constructor_NotSubscribed_ByDefault()
    {
        var (poller, worker, factory) = NewPollerWorkerFactory();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger);
        Assert.Equal(0, trigger.ActiveCount);
    }

    [Fact]
    public async Task Start_Stop_Idempotent()
    {
        var (poller, worker, factory) = NewPollerWorkerFactory();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger);
        trigger.Start();
        trigger.Start();
        Assert.Equal(0, trigger.ActiveCount);
        trigger.Stop();
        trigger.Stop();
    }

    [Fact]
    public async Task PresenceChangedTrue_StartsRecording_ActiveCountIs1()
    {
        var (poller, worker, factory) = NewPollerWorkerFactory();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger);
        trigger.Start();

        var args = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "Test Meeting", WindowTitle: "Meeting | Test - Teams",
            ChatIdShort: "abc12345", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(args);

        Assert.Equal(1, trigger.ActiveCount);
    }

    [Fact]
    public async Task PresenceChangedTrueThenFalse_EnqueuesTranscription_AndActiveCountZero()
    {
        var (poller, worker, factory) = NewPollerWorkerFactory();
        var enqueuedTopics = new List<string>();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger);
        trigger.TranscriptEnqueued += (_, e) => enqueuedTopics.Add(e.Topic);
        trigger.Start();

        var startArgs = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "Standup", WindowTitle: "Meeting | Standup - Teams",
            ChatIdShort: "standup1", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(startArgs);
        Assert.Equal(1, trigger.ActiveCount);

        var stopArgs = startArgs with { IsActive = false };
        poller.RaisePresenceChangedForTest(stopArgs);

        await WaitUntilAsync(() => enqueuedTopics.Count == 1, what: "1 TranscriptEnqueued-Event");
        // Hinweis: worker.CompletedCount/FailedCount haengen vom TranscriptionWorker
        // ab (Fake-WAVs sind 0 bytes → Concatenate schlaegt fehl). Trigger-Punkt
        // ist TranscriptEnqueued — das ist hier die zu pruefende Beobachtung.
        Assert.Equal(0, trigger.ActiveCount);
        Assert.Equal(new[] { "Standup" }, enqueuedTopics);
    }

    [Fact]
    public async Task PresenceChangedFalse_WithoutActiveRecording_NoOp()
    {
        var (poller, worker, factory) = NewPollerWorkerFactory();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger);
        trigger.Start();

        var stopArgs = new MeetingPresenceStateChangedEventArgs(
            IsActive: false, Topic: null, WindowTitle: null, ChatIdShort: "unknown",
            DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(stopArgs);

        await Task.Delay(50);
        Assert.Equal(0, worker.PendingCount);
        Assert.Equal(0, trigger.ActiveCount);
    }

    [Fact]
    public async Task MultipleMeetings_TrackedSeparately()
    {
        var (poller, worker, factory) = NewPollerWorkerFactory();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger);
        trigger.Start();

        var a = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "A", WindowTitle: "Meeting | A - Teams",
            ChatIdShort: "aaaa1111", DetectedAt: DateTimeOffset.UtcNow);
        var b = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "B", WindowTitle: "Meeting | B - Teams",
            ChatIdShort: "bbbb2222", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(a);
        poller.RaisePresenceChangedForTest(b);
        Assert.Equal(2, trigger.ActiveCount);

        poller.RaisePresenceChangedForTest(a with { IsActive = false });
        await WaitUntilAsync(() => trigger.ActiveCount == 1, what: "ActiveCount == 1 nach A-Stop");
        Assert.Equal(1, trigger.ActiveCount);
    }

    // =============================================================================
    // Tests fuer IRecordingControl (Spec 0014 v0.1 Iter. 1)
    // =============================================================================

    /// <summary>Wie <see cref="NewPollerWorkerFactory"/>, aber mit zusaetzlicher
    /// <c>manualRecorderFactory</c> fuer <see cref="MeetingTrigger.StartManualAsync"/>.</summary>
    private (MeetingPresencePoller poller, TranscriptionWorker worker,
             Func<MeetingRecordingContext, RecordingSession> factory,
             Func<RecordingSession> manualFactory)
        NewPollerWorkerFactoryWithManual()
    {
        var probe = new FakeProbe();
        var ticker = new FakeTicker();
        var clock = new FakeClock();
        var poller = new MeetingPresencePoller(probe, ticker, clock, _logger);
        var worker = new TranscriptionWorker(new FakeProvider(), maxParallel: 1, logger: _logger);

        var recorders = new FakeRecorderFactory();
        var devices = new FakeDeviceProvider();
        var folderCounter = 0;
        // AudioConfig.StorageRoot auf den Test-Temp-Pfad setzen, damit
        // parallele xUnit-Tests nicht in den geteilten AppContext.BaseDirectory-
        // Pfad schreiben (sonst IOException auf meta.md).
        var audioConfig = new AudioConfig { StorageRoot = _root };

        Func<MeetingRecordingContext, RecordingSession> factory = ctx =>
        {
            var folder = Path.Combine(_root, "meetings", ctx.ChatIdShort);
            return new RecordingSession(
                meetingIdShort: ctx.ChatIdShort,
                startedAt: DateTimeOffset.UtcNow,
                topic: ctx.Topic,
                config: audioConfig,
                logger: _logger,
                recorderFactory: recorders,
                deviceProvider: devices);
        };
        Func<RecordingSession> manualFactory = () =>
        {
            // Eindeutiger Key: GUID mit 32 hex + Counter als Suffix fuer
            // Race-Freiheit bei parallelen Tests im selben Millisekunden-Bereich.
            var id = $"manual-{Guid.NewGuid():N}-{Interlocked.Increment(ref folderCounter):D3}";
            return new RecordingSession(
                meetingIdShort: id,
                startedAt: DateTimeOffset.UtcNow,
                topic: "(manual recording)",
                config: audioConfig,
                logger: _logger,
                recorderFactory: recorders,
                deviceProvider: devices);
        };
        return (poller, worker, factory, manualFactory);
    }

    [Fact]
    public async Task IsRecording_DefaultsToFalse_WhenNoSession()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);
        Assert.False(trigger.IsRecording);
        Assert.Equal(0, trigger.ActiveCount);
    }

    [Fact]
    public async Task IsRecording_True_WhenMeetingAutoStarted()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);
        trigger.Start();

        var args = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "Test", WindowTitle: "Meeting | Test - Teams",
            ChatIdShort: "isrec01", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(args);

        Assert.True(trigger.IsRecording);
        Assert.Equal(1, trigger.ActiveCount);
    }

    [Fact]
    public async Task IsRecording_False_WhenMeetingAutoStopped()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);
        trigger.Start();

        var startArgs = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "Test", WindowTitle: "Meeting | Test - Teams",
            ChatIdShort: "isrec02", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(startArgs);
        Assert.True(trigger.IsRecording);

        var stopArgs = startArgs with { IsActive = false };
        poller.RaisePresenceChangedForTest(stopArgs);

        await WaitUntilAsync(() => !trigger.IsRecording, what: "IsRecording == false nach Auto-Stop");
        Assert.False(trigger.IsRecording);
        Assert.Equal(0, trigger.ActiveCount);
    }

    [Fact]
    public async Task RecordingStateChanged_Fired_OnAutoStart()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);
        var events = new List<RecordingStateChangedEventArgs>();
        trigger.RecordingStateChanged += (_, e) => events.Add(e);
        trigger.Start();

        var args = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "Standup", WindowTitle: "Meeting | Standup - Teams",
            ChatIdShort: "statev01", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(args);

        var startEvent = Assert.Single(events);
        Assert.True(startEvent.IsRecording);
        Assert.Equal(RecordingSource.MeetingAuto, startEvent.Source);
        Assert.Equal("statev01", startEvent.Key);
        Assert.Equal("Standup", startEvent.Topic);
    }

    [Fact]
    public async Task RecordingStateChanged_Fired_OnAutoStop()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);
        var events = new List<RecordingStateChangedEventArgs>();
        trigger.RecordingStateChanged += (_, e) => events.Add(e);
        trigger.Start();

        var startArgs = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "X", WindowTitle: "Meeting | X - Teams",
            ChatIdShort: "statev02", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(startArgs);
        poller.RaisePresenceChangedForTest(startArgs with { IsActive = false });

        await WaitUntilAsync(() => events.Count == 2, what: "2 RecordingStateChanged-Events");
        var stopEvent = events[1];
        Assert.False(stopEvent.IsRecording);
        Assert.Equal(RecordingSource.MeetingAuto, stopEvent.Source);
        Assert.Equal("statev02", stopEvent.Key);
    }

    [Fact]
    public async Task StartManualAsync_Throws_WhenNoFactoryProvided()
    {
        var (poller, worker, factory, _) = NewPollerWorkerFactoryWithManual();
        // manual-Factory WIRD NICHT injiziert (null)
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger);
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await trigger.StartManualAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartManualAsync_CreatesSession_FiresEvent_ReturnsKey()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);
        var events = new List<RecordingStateChangedEventArgs>();
        trigger.RecordingStateChanged += (_, e) => events.Add(e);

        var key = await trigger.StartManualAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(key));
        Assert.True(trigger.IsRecording);
        Assert.Equal(1, trigger.ActiveCount);

        var startEvent = Assert.Single(events);
        Assert.True(startEvent.IsRecording);
        Assert.Equal(RecordingSource.Manual, startEvent.Source);
        Assert.Equal(key, startEvent.Key);
        Assert.Null(startEvent.Topic); // manuelle Aufnahme: keine Topic-Info
    }

    [Fact]
    public async Task StartManualAsync_Key_HasManualPrefix()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);

        var key = await trigger.StartManualAsync(CancellationToken.None);

        Assert.StartsWith("manual-", key);
        Assert.True(key.Length > "manual-".Length);
    }

    [Fact]
    public async Task StopAsync_StopsActiveSession()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);

        var key = await trigger.StartManualAsync(CancellationToken.None);
        Assert.True(trigger.IsRecording);

        await trigger.StopAsync();
        await WaitUntilAsync(() => !trigger.IsRecording, what: "IsRecording == false nach Stop");
        Assert.False(trigger.IsRecording);
        Assert.Equal(0, trigger.ActiveCount);
    }

    [Fact]
    public async Task StopAsync_WithoutActiveRecording_IsNoOp()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);

        // Keine aktive Session - StopAsync() soll still sein (kein Throw).
        await trigger.StopAsync();
        Assert.False(trigger.IsRecording);
        Assert.Equal(0, trigger.ActiveCount);
    }

    [Fact]
    public async Task StartManualAsync_WhileAutoRecording_Throws()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);
        trigger.Start();

        // Auto-Aufnahme via Poller starten
        var autoArgs = new MeetingPresenceStateChangedEventArgs(
            IsActive: true, Topic: "Auto", WindowTitle: "Meeting | Auto - Teams",
            ChatIdShort: "saactive1", DetectedAt: DateTimeOffset.UtcNow);
        poller.RaisePresenceChangedForTest(autoArgs);
        Assert.True(trigger.IsRecording);

        // Manual waehrend Auto -> InvalidOperationException (Single-Active-Constraint)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await trigger.StartManualAsync(CancellationToken.None));
        Assert.Contains("Single-Active", ex.Message);
    }

    [Fact]
    public async Task StartManualAsync_WhileManualRecording_Throws()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);

        await trigger.StartManualAsync(CancellationToken.None);
        Assert.True(trigger.IsRecording);

        // Zweiter Manual waehrend erstem laeuft -> InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await trigger.StartManualAsync(CancellationToken.None));
        Assert.Contains("Single-Active", ex.Message);
    }

    [Fact]
    public async Task StartManualAsync_AfterStopAsync_AllowsNewStart()
    {
        var (poller, worker, factory, manual) = NewPollerWorkerFactoryWithManual();
        await using var trigger = new MeetingTrigger(poller, worker, factory, DefaultOptions(), _logger, manual);

        var key1 = await trigger.StartManualAsync(CancellationToken.None);
        await trigger.StopAsync();
        await WaitUntilAsync(() => !trigger.IsRecording, what: "IsRecording == false nach Stop");

        var key2 = await trigger.StartManualAsync(CancellationToken.None);
        Assert.NotEqual(key1, key2);
        Assert.True(trigger.IsRecording);

        await trigger.StopAsync();
    }
}
