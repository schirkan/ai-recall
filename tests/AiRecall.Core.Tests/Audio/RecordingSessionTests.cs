using AiRecall.Core.Audio;
using Serilog;

namespace AiRecall.Core.Tests.Audio;

public class RecordingSessionTests
{
    // RecordingSession erwartet Serilog.ILogger — direkt aus Serilog aufbauen,
    // ohne Umweg ueber Microsoft.Extensions.Logging (LoggerFactory-Helper entfernt).
    private static ILogger _silentLogger => new LoggerConfiguration().CreateLogger();

    private static IAudioDeviceProvider TwoDeviceProvider()
    {
        var input = new AudioDeviceInfo("mic-1", "Default Mic", "USB");
        var loop = new AudioDeviceInfo("loop-1", "Default Speakers", "USB");
        return new FakeDeviceProvider(
            inputDevices: new[] { input },
            loopbackDevices: new[] { loop },
            defaultInputId: "mic-1",
            defaultLoopbackId: "loop-1");
    }

    [Fact]
    public void Constructor_StoresMeetingIdShort()
    {
        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: DateTimeOffset.UtcNow,
            topic: "Test",
            config: new AudioConfig(),
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        Assert.Equal("abcd1234", session.MeetingIdShort);
        Assert.Equal(RecordingState.Created, session.State);
        Assert.Null(session.Folder);
        Assert.Equal(RecordingTriggerSource.Polling, session.TriggerSource);  // Spec 0014 Iter. 3.1
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyMeetingId()
    {
        Assert.Throws<ArgumentException>(() => new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
            meetingIdShort: "",
            startedAt: DateTimeOffset.UtcNow,
            topic: "Test",
            config: new AudioConfig(),
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider()));
    }

    [Fact]
    public async Task StopAsync_WithoutStart_Throws()
    {
        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: DateTimeOffset.UtcNow,
            topic: "Test",
            config: new AudioConfig(),
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.StopAsync());
    }

    [Fact]
    public async Task Start_CreatesMeetingFolder()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: new DateTimeOffset(2026, 7, 7, 22, 0, 0, TimeSpan.Zero),
            topic: "Daily Standup",
            config: config,
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        session.Start();
        try
        {
            Assert.NotNull(session.Folder);
            Assert.True(Directory.Exists(session.Folder!));
            Assert.Contains("2026-07-07", session.Folder!);
            Assert.Contains("abcd1234", session.Folder!);
            Assert.Equal(RecordingState.Recording, session.State);

            // Initiales meta.md sollte geschrieben sein
            var metaPath = Path.Combine(session.Folder!, "meta.md");
            Assert.True(File.Exists(metaPath));
            var meta = File.ReadAllText(metaPath);
            Assert.Contains("status: recording", meta);
            Assert.Contains("Daily Standup", meta);
            Assert.Contains("worker_task_enqueued: false", meta);
        }
        finally
        {
            await session.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StopAsync_WritesMicAndLoopbackFilesAndUpdatesMetaMd()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var factory = new FakeRecorderFactory();
        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: new DateTimeOffset(2026, 7, 7, 22, 0, 0, TimeSpan.Zero),
            topic: "Daily Standup",
            config: config,
            logger: _silentLogger,
            recorderFactory: factory,
            deviceProvider: TwoDeviceProvider());

        session.Start();
        var paths = await session.StopAsync();
        try
        {
            Assert.Equal(RecordingState.Recorded, session.State);
            Assert.True(File.Exists(paths.MicPath));
            Assert.True(File.Exists(paths.LoopbackPath));
            Assert.True(File.Exists(paths.MetadataPath));
            Assert.True(paths.AllExist);

            // Mock-Daten muessen in den Files sein
            var micBytes = await File.ReadAllBytesAsync(paths.MicPath);
            var loopBytes = await File.ReadAllBytesAsync(paths.LoopbackPath);
            Assert.Equal(1024, micBytes.Length);
            Assert.Equal(2048, loopBytes.Length);

            // meta.md muss aktualisiert sein
            var meta = await File.ReadAllTextAsync(paths.MetadataPath);
            Assert.Contains("status: recorded", meta);
            Assert.Contains("audio_files:", meta);
            Assert.Contains("mic.wav", meta);
            Assert.Contains("loopback.wav", meta);
        }
        finally
        {
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DoubleStart_Throws()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: DateTimeOffset.UtcNow,
            topic: "Test",
            config: config,
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        session.Start();
        try
        {
            Assert.Throws<InvalidOperationException>(() => session.Start());
        }
        finally
        {
            await session.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public void Start_WithNoInputDevice_Throws()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var emptyProvider = new FakeDeviceProvider(); // keine Devices
        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: DateTimeOffset.UtcNow,
            topic: "Test",
            config: config,
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: emptyProvider);

        Assert.Throws<InvalidOperationException>(() => session.Start());
        Assert.Equal(RecordingState.Failed, session.State);
        if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
    }

    // ----------------------------------------------------------------------
    // Spec 0014 Iter. 3.1 — trigger_source Parametrisierung + Folder-Key-Schema
    // ----------------------------------------------------------------------

    [Fact]
    public void Constructor_TriggerSource_DefaultsFromParameter()
    {
        var pollingSession = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,
            meetingIdShort: "abcd1234",
            startedAt: DateTimeOffset.UtcNow,
            topic: "Test",
            config: new AudioConfig(),
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());
        Assert.Equal(RecordingTriggerSource.Polling, pollingSession.TriggerSource);

        var manualSession = new RecordingSession(
            triggerSource: RecordingTriggerSource.ManualAudio,
            meetingIdShort: "abcd1234",
            startedAt: DateTimeOffset.UtcNow,
            topic: "Test",
            config: new AudioConfig(),
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());
        Assert.Equal(RecordingTriggerSource.ManualAudio, manualSession.TriggerSource);
    }

    [Fact]
    public async Task Start_FolderKey_PollingNoPrefix()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,
            meetingIdShort: "abcd1234",
            startedAt: new DateTimeOffset(2026, 7, 7, 22, 0, 0, TimeSpan.Zero),
            topic: "Test",
            config: config,
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        session.Start();
        try
        {
            // Polling: HHmmss-{meetingIdShort} ohne "manual-"-Prefix (Spec 0013 kompatibel)
            Assert.NotNull(session.Folder);
            Assert.DoesNotContain("manual-", session.Folder!);
            Assert.Contains("abcd1234", session.Folder!);
        }
        finally
        {
            await session.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Start_FolderKey_ManualHasPrefix()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.ManualAudio,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: new DateTimeOffset(2026, 7, 7, 22, 0, 0, TimeSpan.Zero),
            topic: "Test",
            config: config,
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        session.Start();
        try
        {
            // Manual: HHmmss-manual-{meetingIdShort} (Spec 0014 Z. 212: "manual-{guid}")
            Assert.NotNull(session.Folder);
            Assert.Contains("manual-abcd1234", session.Folder!);
        }
        finally
        {
            await session.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Start_MetaMd_TriggerSource_Polling()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.Polling,
            meetingIdShort: "abcd1234",
            startedAt: new DateTimeOffset(2026, 7, 7, 22, 0, 0, TimeSpan.Zero),
            topic: "Test",
            config: config,
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        session.Start();
        try
        {
            var meta = File.ReadAllText(Path.Combine(session.Folder!, "meta.md"));
            // Spec 0014 Iter. 3.1: Polling -> "polling" (Spec 0013 kompatibel)
            Assert.Contains("trigger_source: polling", meta);
            Assert.DoesNotContain("trigger_source: manual-audio", meta);
        }
        finally
        {
            await session.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Start_MetaMd_TriggerSource_ManualAudio()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        var config = new AudioConfig { StorageRoot = storageRoot };

        var session = new RecordingSession(
            triggerSource: RecordingTriggerSource.ManualAudio,  // Spec 0014 Iter. 3.1
            meetingIdShort: "abcd1234",
            startedAt: new DateTimeOffset(2026, 7, 7, 22, 0, 0, TimeSpan.Zero),
            topic: "Test",
            config: config,
            logger: _silentLogger,
            recorderFactory: new FakeRecorderFactory(),
            deviceProvider: TwoDeviceProvider());

        session.Start();
        try
        {
            var meta = File.ReadAllText(Path.Combine(session.Folder!, "meta.md"));
            // Spec 0014 Iter. 3.1: Manual -> "manual-audio" (Spec 0014 Tray-Manuell)
            Assert.Contains("trigger_source: manual-audio", meta);
            Assert.DoesNotContain("trigger_source: polling", meta);
        }
        finally
        {
            await session.DisposeAsync();
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }
}

/// <summary>Fake-Recorder, der sofort 1024/2048 PCM-Bytes liefert.</summary>
internal sealed class FakeRecorder : IAudioRecorder
{
    private readonly byte[] _data;
    private bool _started;

    public FakeRecorder(int sizeBytes)
    {
        _data = new byte[sizeBytes];
        for (int i = 0; i < sizeBytes; i++) _data[i] = (byte)(i % 256);
        Format = AudioFormat.Default;
    }

    public AudioFormat Format { get; }

    public void Start() { _started = true; }
    public byte[] Stop()
    {
        if (!_started) throw new InvalidOperationException("not started");
        _started = false;
        return _data;
    }

    public void Dispose() { }
}

internal sealed class FakeRecorderFactory : IAudioRecorderFactory
{
    private int _counter;

    public IAudioRecorder Create(AudioDeviceInfo device, AudioFormat format, bool loopback)
    {
        _counter++;
        // Erster Recorder = mic (1024 bytes), zweiter = loopback (2048 bytes)
        return new FakeRecorder(loopback ? 2048 : 1024);
    }
}