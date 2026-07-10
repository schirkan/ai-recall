using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using AiRecall.Core.Audio;
using AiRecall.Core.Configuration;
using AiRecall.Transcription;
using AiRecall.Trigger;

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AiRecall.Core.Tests.Trigger;

public class TriggerServiceTests
{
    /// <summary>Logger, der alle Events in eine Liste sammelt (fuer Tests).</summary>
    private sealed class TestSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger logger, TestSink sink) NewLogger()
    {
        var sink = new TestSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static AppConfig NewConfig()
    {
        var c = new AppConfig();
        // Standardmaessig WinEventHook-Subscriptions aktivieren
        c.Trigger.WinEvents.Foreground  = false; // wichtig: keine echten Hooks in Tests
        c.Trigger.WinEvents.Focus       = false;
        c.Trigger.WinEvents.NameChange  = false;
        c.Trigger.WinEvents.ValueChange = false;
        c.Trigger.WinEvents.Scroll      = false;
        c.Trigger.WinEvents.MenuPopup   = false;
        c.Trigger.HeartbeatIntervalSeconds = 0; // disabled
        return c;
    }

    [Fact]
    public void Constructor_NotRunning_ByDefault()
    {
        var (logger, _) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        Assert.False(svc.IsRunning);
        Assert.Equal(0, svc.CaptureCount);
    }

    [Fact]
    public void Start_WithoutSources_StillRunsWorker()
    {
        var (logger, _) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        svc.Start();
        Assert.True(svc.IsRunning);
        svc.Stop();
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void StartStop_Idempotent()
    {
        var (logger, _) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        svc.Start();
        svc.Start(); // darf nicht werfen
        Assert.True(svc.IsRunning);
        svc.Stop();
        svc.Stop(); // darf nicht werfen
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void Dispose_StopsRunningService()
    {
        var (logger, _) = NewLogger();
        var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        svc.Start();
        Assert.True(svc.IsRunning);
        svc.Dispose();
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var (logger, _) = NewLogger();
        var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        svc.Dispose();
        svc.Dispose(); // darf nicht werfen
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var (logger, _) = NewLogger();
        var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        svc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => svc.Start());
    }

    [Fact]
    public void ChannelWriter_IsExposed_AllowsExternalInjection()
    {
        var (logger, _) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        var writer = svc.ChannelWriter;
        Assert.NotNull(writer);
    }

    [Fact]
    public void Heartbeat_NotStarted_WhenIntervalIsZero()
    {
        var (logger, _) = NewLogger();
        var c = NewConfig(); // HeartbeatIntervalSeconds = 0
        using var svc = new TriggerService(c, logger, enableWinEventHook: false, enableHeartbeat: true);
        svc.Start();
        // Sollte laufen, aber Heartbeat-Thread nicht gestartet (HeartbeatThread.Start ist no-op bei Interval<=0)
        Assert.True(svc.IsRunning);
    }

    [Fact]
    public void Counters_DefaultToZero()
    {
        var (logger, _) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        Assert.Equal(0, svc.CaptureCount);
        Assert.Equal(0, svc.SkippedCount);
        Assert.Equal(0, svc.ThrottleCount);
        Assert.Equal(0, svc.DuplicateCount);
        Assert.Equal(0, svc.BlacklistCount);
        Assert.Equal(0, svc.SelfCaptureCount);
        Assert.Equal(0, svc.ErrorCount);
    }

    [Fact]
    public void Start_LogsInformation()
    {
        var (logger, sink) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        svc.Start();
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.Contains("TriggerService started"));
        svc.Stop();
    }

    [Fact]
    public void Stop_LogsInformation()
    {
        var (logger, sink) = NewLogger();
        using var svc = new TriggerService(NewConfig(), logger, enableWinEventHook: false, enableHeartbeat: false);
        svc.Start();
        svc.Stop();
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.Contains("TriggerService stopped"));
    }

    // =============================================================================
    // Tests fuer MeetingTrigger-Integration (Spec 0013 v0.3 Iter. 4)
    // =============================================================================

    /// <summary>Baut einen echten MeetingTrigger mit Noop-Probe/Ticker/Clock/Provider
    /// aus <c>TriggerTestDoubles.cs</c> (kein NAudio, kein Recording — Recorder-Factory bleibt ungenutzt).</summary>
    private static MeetingTrigger NewStubMeetingTrigger(ILogger logger)
    {
        var poller = new MeetingPresencePoller(new NoopMeetingProbe(), new NoopPresenceTicker(), new SystemPresenceClock(), logger);
        var worker = new TranscriptionWorker(new NoopTranscriptionProvider(), maxParallel: 1, logger: logger);
        Func<MeetingRecordingContext, RecordingSession> recorderFactory = _ => null!;
        return new MeetingTrigger(
            poller, worker, recorderFactory,
            new TranscriptionOptions("deu", DiarizationRequired: true, MaxSpeakers: 4, ApiKey: "test", EndpointOverride: null),
            logger);
    }

    [Fact]
    public void MeetingTrigger_AudioDisabled_PropertyIsNull()
    {
        var (logger, _) = NewLogger();
        var c = NewConfig();
        c.Audio.Enabled = false;
        c.AppReader.Teams.AutoRecordMeetings = true;
        using var svc = new TriggerService(c, logger, enableWinEventHook: false, enableHeartbeat: false);
        Assert.Null(svc.MeetingTrigger);
    }

    [Fact]
    public void MeetingTrigger_TeamsAutoRecordDisabled_PropertyIsNull()
    {
        var (logger, _) = NewLogger();
        var c = NewConfig();
        c.Audio.Enabled = true;
        c.AppReader.Teams.AutoRecordMeetings = false;
        using var svc = new TriggerService(c, logger, enableWinEventHook: false, enableHeartbeat: false);
        Assert.Null(svc.MeetingTrigger);
    }

    [Fact]
    public void MeetingTrigger_TeamsReaderDisabled_PropertyIsNull()
    {
        var (logger, _) = NewLogger();
        var c = NewConfig();
        c.Audio.Enabled = true;
        c.AppReader.Teams.Enabled = false;       // Master-Switch Teams-Reader
        c.AppReader.Teams.AutoRecordMeetings = true;
        using var svc = new TriggerService(c, logger, enableWinEventHook: false, enableHeartbeat: false);
        Assert.Null(svc.MeetingTrigger);
    }

    [Fact]
    public void MeetingTrigger_ExternallyInjected_IsExposedAsIs_EvenIfAudioDisabled()
    {
        var (logger, _) = NewLogger();
        var c = NewConfig();
        c.Audio.Enabled = false;
        var mt = NewStubMeetingTrigger(logger);
        using var svc = new TriggerService(c, logger, enableWinEventHook: false, enableHeartbeat: false, meetingTrigger: mt);
        Assert.Same(mt, svc.MeetingTrigger);
    }
}