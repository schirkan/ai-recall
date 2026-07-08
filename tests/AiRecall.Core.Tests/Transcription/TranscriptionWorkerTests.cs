using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.Core.Audio;
using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Core.Tests.Transcription;

/// <summary>
/// Tests fuer <see cref="TranscriptionWorker"/> (Spec 0013 v0.3 §5.4).
/// End-to-end-Tests mit FakeProvider + echten WAV-Dateien + MetadataUpdater.
/// </summary>
public class TranscriptionWorkerTests : IDisposable
{
    private readonly string _root;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public TranscriptionWorkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "transcription-worker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // =============================================================================
    // Helpers
    // =============================================================================

    private static string WriteMonoWav(string path, int sampleRate, int sampleCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var format = new NAudio.Wave.WaveFormat(sampleRate, 16, 1);
        using var writer = new NAudio.Wave.WaveFileWriter(path, format);
        var buf = new float[Math.Min(sampleCount, 4096)];
        int written = 0;
        while (written < sampleCount)
        {
            int chunk = Math.Min(buf.Length, sampleCount - written);
            writer.WriteSamples(buf, 0, chunk);
            written += chunk;
        }
        return path;
    }

    private (string folder, string mic, string loop, string meta) NewMeeting(string name, int durationMs = 50)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var mic = Path.Combine(folder, "mic.wav");
        var loop = Path.Combine(folder, "loopback.wav");
        var meta = Path.Combine(folder, "meta.md");
        int samples = 16000 * durationMs / 1000;
        WriteMonoWav(mic, 16000, samples);
        WriteMonoWav(loop, 16000, samples);
        return (folder, mic, loop, meta);
    }

    private static AudioTranscriptionTask NewTask(
        string folder, string mic, string loop, string meta)
    {
        return new AudioTranscriptionTask(
            Folder: folder,
            MicPath: mic,
            LoopbackPath: loop,
            MetadataPath: meta,
            Options: new TranscriptionOptions(
                Language: "deu",
                DiarizationRequired: true,
                MaxSpeakers: 4,
                ApiKey: "test-key",
                EndpointOverride: null),
            EnqueuedAt: DateTimeOffset.UtcNow);
    }

    private static Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000, string? what = null)
    {
        return Task.Run(async () =>
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (condition()) return;
                await Task.Delay(20);
            }
            if (!condition())
                throw new TimeoutException($"WaitUntilAsync: condition not met within {timeoutMs}ms ({what ?? "?"})");
        });
    }

    // =============================================================================
    // Tests
    // =============================================================================

    [Fact]
    public async Task Enqueue_BeforeStart_Throws()
    {
        var provider = new FakeProvider("fake", new List<TranscriptionSegment>());
        await using var worker = new TranscriptionWorker(provider, maxParallel: 1, logger: _logger);
        var (folder, mic, loop, meta) = NewMeeting("t1");
        Assert.Throws<InvalidOperationException>(
            () => worker.Enqueue(NewTask(folder, mic, loop, meta)));
    }

    [Fact]
    public async Task Start_ThenEnqueue_ProcessesTask()
    {
        var provider = new FakeProvider("fake", new List<TranscriptionSegment>
        {
            new("S0", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hi"),
        });
        await using var worker = new TranscriptionWorker(provider, maxParallel: 1, logger: _logger);
        worker.Start();
        var (folder, mic, loop, meta) = NewMeeting("t2");

        worker.Enqueue(NewTask(folder, mic, loop, meta));

        await WaitUntilAsync(() => provider.CallCount == 1, what: "Provider wurde aufgerufen");
        await WaitUntilAsync(() => File.Exists(meta) && File.ReadAllText(meta).Contains("transcript_status: done"),
            what: "meta.md enthaelt 'transcript_status: done'");
        // Bei Erfolg: combined-stereo.wav wurde geloescht
        Assert.False(File.Exists(Path.Combine(folder, "combined-stereo.wav")));
    }

    [Fact]
    public async Task ProviderFailure_KeepsCombinedStereo_MarksMetaFailed()
    {
        var provider = new FakeProvider("fake", Array.Empty<TranscriptionSegment>(),
            errorMessage: "HTTP 401: Unauthorized");
        await using var worker = new TranscriptionWorker(provider, maxParallel: 1, logger: _logger);
        worker.Start();
        var (folder, mic, loop, meta) = NewMeeting("t3");

        worker.Enqueue(NewTask(folder, mic, loop, meta));

        await WaitUntilAsync(() => File.Exists(meta) && File.ReadAllText(meta).Contains("transcript_status: failed"),
            what: "meta.md enthaelt 'transcript_status: failed'");
        // Bei Fehler: combined-stereo.wav bleibt liegen (Debug-Evidence)
        Assert.True(File.Exists(Path.Combine(folder, "combined-stereo.wav")));
        // Error-Message in Frontmatter
        var metaContent = File.ReadAllText(meta);
        Assert.Contains("HTTP 401", metaContent);
    }

    [Fact]
    public async Task MultipleTasks_ProcessedInParallel()
    {
        var provider = new FakeProvider("fake", new List<TranscriptionSegment>
        {
            new("S0", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hi"),
        }, delayMs: 200);
        await using var worker = new TranscriptionWorker(provider, maxParallel: 2, logger: _logger);
        worker.Start();
        var meetings = Enumerable.Range(0, 4)
            .Select(i => NewMeeting($"parallel-{i}"))
            .ToList();

        foreach (var (folder, mic, loop, meta) in meetings)
            worker.Enqueue(NewTask(folder, mic, loop, meta));

        // Warten bis alle 4 Tasks durch sind
        await WaitUntilAsync(() => provider.CallCount == 4, timeoutMs: 5000, what: "Provider wurde 4x aufgerufen");
        await WaitUntilAsync(
            () => meetings.All(m => File.Exists(m.meta) && File.ReadAllText(m.meta).Contains("transcript_status: done")),
            timeoutMs: 5000, what: "alle 4 meta.md sind 'done'");
    }

    [Fact]
    public async Task StopAsync_DrainsQueue_StopsProcessing()
    {
        var provider = new FakeProvider("fake", new List<TranscriptionSegment>
        {
            new("S0", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hi"),
        }, delayMs: 100);
        await using var worker = new TranscriptionWorker(provider, maxParallel: 1, logger: _logger);
        worker.Start();
        var (folder1, mic1, loop1, meta1) = NewMeeting("stop1");
        var (folder2, mic2, loop2, meta2) = NewMeeting("stop2");

        worker.Enqueue(NewTask(folder1, mic1, loop1, meta1));
        await WaitUntilAsync(() => provider.CallCount == 1, what: "Task 1 durch");
        worker.Enqueue(NewTask(folder2, mic2, loop2, meta2));

        await worker.StopAsync(CancellationToken.None);

        // Task 2 sollte entweder durch oder gecancelt sein — nicht in Endlosschleife
        Assert.True(provider.CallCount >= 1);
    }

    [Fact]
    public async Task ConcatenateFailure_LeavesNoStereo_MarksMetaFailed()
    {
        var provider = new FakeProvider("fake", new List<TranscriptionSegment>());
        await using var worker = new TranscriptionWorker(provider, maxParallel: 1, logger: _logger);
        worker.Start();
        // Sample-Rate mismatch → Concatenator wirft
        var folder = Path.Combine(_root, "concatfail");
        Directory.CreateDirectory(folder);
        WriteMonoWav(Path.Combine(folder, "mic.wav"), 16000, 800);
        WriteMonoWav(Path.Combine(folder, "loopback.wav"), 8000, 400); // andere Rate
        var meta = Path.Combine(folder, "meta.md");

        worker.Enqueue(NewTask(folder, Path.Combine(folder, "mic.wav"), Path.Combine(folder, "loopback.wav"), meta));

        await WaitUntilAsync(() => File.Exists(meta) && File.ReadAllText(meta).Contains("transcript_status: failed"),
            what: "meta.md enthaelt 'transcript_status: failed'");
        // Provider wurde NICHT aufgerufen (Concatenate schlug vorher fehl)
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task DisposeAsync_StopsWorker()
    {
        var provider = new FakeProvider("fake", new List<TranscriptionSegment>
        {
            new("S0", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hi"),
        });
        var worker = new TranscriptionWorker(provider, maxParallel: 1, logger: _logger);
        worker.Start();
        await worker.DisposeAsync();
        // Enqueue nach Dispose sollte werfen
        var (folder, mic, loop, meta) = NewMeeting("dispose");
        Assert.Throws<ObjectDisposedException>(
            () => worker.Enqueue(NewTask(folder, mic, loop, meta)));
    }

    [Fact]
    public async Task TranscriptBlock_ContainsProviderAndSegments()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("S0", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hallo"),
            new("S1", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Welt"),
        };
        var provider = new FakeProvider("fake", segments);
        await using var worker = new TranscriptionWorker(provider, maxParallel: 1, logger: _logger);
        worker.Start();
        var (folder, mic, loop, meta) = NewMeeting("transcript-content");

        worker.Enqueue(NewTask(folder, mic, loop, meta));

        await WaitUntilAsync(() => File.Exists(meta) && File.ReadAllText(meta).Contains("[S0]"),
            what: "Transcript enthaelt [S0]-Block");
        var content = File.ReadAllText(meta);
        Assert.Contains("[S0]", content);
        Assert.Contains("Hallo", content);
        Assert.Contains("[S1]", content);
        Assert.Contains("Welt", content);
        Assert.Contains("Provider: fake", content);
    }

    // =============================================================================
    // Fakes
    // =============================================================================

    private sealed class FakeProvider : ITranscriptionProvider
    {
        private readonly IReadOnlyList<TranscriptionSegment> _segments;
        private readonly string? _errorMessage;
        private readonly int _delayMs;

        public FakeProvider(string name, IReadOnlyList<TranscriptionSegment> segments, string? errorMessage = null, int delayMs = 0)
        {
            Name = name;
            _segments = segments;
            _errorMessage = errorMessage;
            _delayMs = delayMs;
        }

        public string Name { get; }
        public int CallCount { get; private set; }

        public async Task<TranscriptionResult> TranscribeAsync(
            string stereoPath, TranscriptionOptions options,
            IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_delayMs > 0) await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            if (_errorMessage is not null)
            {
                return new TranscriptionResult(
                    Segments: Array.Empty<TranscriptionSegment>(),
                    ProviderName: Name,
                    AudioDuration: TimeSpan.Zero,
                    SpeakerCount: 0,
                    SpeakerLabels: Array.Empty<string>(),
                    ErrorMessage: _errorMessage);
            }
            return new TranscriptionResult(
                Segments: _segments,
                ProviderName: Name,
                AudioDuration: TimeSpan.FromSeconds(2),
                SpeakerCount: _segments.Select(s => s.Speaker).Distinct().Count(),
                SpeakerLabels: _segments.Select(s => s.Speaker).Distinct().OrderBy(x => x).ToList(),
                ErrorMessage: null);
        }
    }
}
