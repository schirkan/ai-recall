using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Core.Tests.Transcription;

/// <summary>
/// Tests fuer <see cref="AzureSpeechTranscriptionProvider"/> (Spec 0013 v0.3 §5.4).
/// Mocking via Constructor-Injection (AzureSpeechTranscriber-Delegate).
/// </summary>
public class AzureSpeechTranscriptionProviderTests
{
    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    private static TranscriptionOptions ValidOptions() => new(
        Language: "deu",
        DiarizationRequired: true,
        MaxSpeakers: 4,
        ApiKey: "test-key",
        EndpointOverride: null);

    [Fact]
    public void Name_IsAzureSpeech()
    {
        var provider = new AzureSpeechTranscriptionProvider(SilentLogger());
        Assert.Equal("azure-speech", provider.Name);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyApiKey_ReturnsErrorResult_DoesNotCallSdk()
    {
        var called = false;
        AzureSpeechTranscriber transcriber = (_, _, _) =>
        {
            called = true;
            return Task.FromResult<IReadOnlyList<AzureSpeechSegment>>(Array.Empty<AzureSpeechSegment>());
        };
        var provider = new AzureSpeechTranscriptionProvider(transcriber, SilentLogger());
        var opts = ValidOptions() with { ApiKey = "" };

        var result = await provider.TranscribeAsync("dummy.wav", opts, null, CancellationToken.None);

        Assert.False(called);
        Assert.False(result.IsSuccess);
        Assert.Contains("API-Key fehlt", result.ErrorMessage);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyPath_Throws()
    {
        var provider = new AzureSpeechTranscriptionProvider(SilentLogger());
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranscribeAsync("", ValidOptions(), null, CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_SdkThrows_ReturnsErrorResult()
    {
        AzureSpeechTranscriber transcriber = (_, _, _) =>
            throw new InvalidOperationException("HTTP 401: Unauthorized");
        var provider = new AzureSpeechTranscriptionProvider(transcriber, SilentLogger());

        var result = await provider.TranscribeAsync("dummy.wav", ValidOptions(), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("HTTP 401", result.ErrorMessage);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task TranscribeAsync_SingleSegment_MapsToChannelAndSpeaker()
    {
        var raw = new List<AzureSpeechSegment>
        {
            new(ChannelId: 0, SpeakerId: 1, Text: "Hallo Welt", Offset: TimeSpan.FromSeconds(0), Duration: TimeSpan.FromSeconds(2)),
        };
        AzureSpeechTranscriber transcriber = (_, _, _) => Task.FromResult<IReadOnlyList<AzureSpeechSegment>>(raw);
        var provider = new AzureSpeechTranscriptionProvider(transcriber, SilentLogger());

        var result = await provider.TranscribeAsync("dummy.wav", ValidOptions(), null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var seg = Assert.Single(result.Segments);
        Assert.Equal("C0-S1", seg.Speaker);
        Assert.Equal("Hallo Welt", seg.Text);
        Assert.Equal(TimeSpan.FromSeconds(0), seg.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), seg.End);
        Assert.Equal(1, result.SpeakerCount);
        Assert.Equal(new[] { "C0-S1" }, result.SpeakerLabels);
    }

    [Fact]
    public async Task TranscribeAsync_MultipleSegments_PreservesOrderAndAggregatesSpeakers()
    {
        var raw = new List<AzureSpeechSegment>
        {
            new(0, 0, "Erste Nachricht", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2)),
            new(1, 1, "Antwort von Speaker 1", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)),
            new(0, 0, "Zweite Nachricht", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
        };
        AzureSpeechTranscriber transcriber = (_, _, _) => Task.FromResult<IReadOnlyList<AzureSpeechSegment>>(raw);
        var provider = new AzureSpeechTranscriptionProvider(transcriber, SilentLogger());

        var result = await provider.TranscribeAsync("dummy.wav", ValidOptions(), null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal("C0-S0", result.Segments[0].Speaker);
        Assert.Equal("C1-S1", result.Segments[1].Speaker);
        Assert.Equal("C0-S0", result.Segments[2].Speaker);
        Assert.Equal(2, result.SpeakerCount);
        Assert.Equal(new[] { "C0-S0", "C1-S1" }, result.SpeakerLabels);
    }

    [Fact]
    public async Task TranscribeAsync_OutOfOrderSegments_SortsByOffset()
    {
        var raw = new List<AzureSpeechSegment>
        {
            new(0, 0, "Zweite", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
            new(0, 0, "Erste",  TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2)),
        };
        AzureSpeechTranscriber transcriber = (_, _, _) => Task.FromResult<IReadOnlyList<AzureSpeechSegment>>(raw);
        var provider = new AzureSpeechTranscriptionProvider(transcriber, SilentLogger());

        var result = await provider.TranscribeAsync("dummy.wav", ValidOptions(), null, CancellationToken.None);

        Assert.Equal("Erste", result.Segments[0].Text);
        Assert.Equal("Zweite", result.Segments[1].Text);
    }

    [Fact]
    public async Task TranscribeAsync_ReportsProgress()
    {
        var raw = new List<AzureSpeechSegment>
        {
            new(0, 0, "Hi", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
        };
        AzureSpeechTranscriber transcriber = (_, _, _) => Task.FromResult<IReadOnlyList<AzureSpeechSegment>>(raw);
        var provider = new AzureSpeechTranscriptionProvider(transcriber, SilentLogger());
        // Synchroner IProgress<T>: Progress<T> postet auf den SyncContext,
        // was in xUnit-Tests zu Race-Conditions fuehrt. Eigene Impl.
        var recorder = new RecordingProgress();
        await provider.TranscribeAsync("dummy.wav", ValidOptions(), recorder, CancellationToken.None);

        Assert.NotEmpty(recorder.Reports);
        Assert.Equal(100, recorder.Reports[^1].PercentComplete);
    }

    private sealed class RecordingProgress : IProgress<TranscriptionProgress>
    {
        public List<TranscriptionProgress> Reports { get; } = new();
        public void Report(TranscriptionProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task TranscribeAsync_RespectsCancellation()
    {
        AzureSpeechTranscriber transcriber = (_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AzureSpeechSegment>>(Array.Empty<AzureSpeechSegment>());
        };
        var provider = new AzureSpeechTranscriptionProvider(transcriber, SilentLogger());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.TranscribeAsync("dummy.wav", ValidOptions(), null, cts.Token));
    }
}
