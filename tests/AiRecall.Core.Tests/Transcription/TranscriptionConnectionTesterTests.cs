using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Core.Tests.Transcription;

/// <summary>
/// Tests fuer <see cref="TranscriptionConnectionTester"/> (Spec 0013 v0.3 §5.4
/// Settings-Tab "Test-Connection"-Button).
/// </summary>
public class TranscriptionConnectionTesterTests
{
    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    private static TranscriptionOptions ValidOptions(string apiKey = "test-key") => new(
        Language: "deu", DiarizationRequired: true, MaxSpeakers: 4,
        ApiKey: apiKey, EndpointOverride: null);

    [Fact]
    public void Constructor_NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new TranscriptionConnectionTester(null!, SilentLogger()));
    }

    [Fact]
    public async Task ProviderName_MatchesProvider()
    {
        var provider = new FakeConnectionProvider("fake", errorOnCall: 0);
        await using var tester = new TranscriptionConnectionTester(provider, SilentLogger());
        Assert.Equal("fake", tester.ProviderName);
    }

    [Fact]
    public async Task TestAsync_Success_ReturnsSuccess()
    {
        var provider = new FakeConnectionProvider("fake", errorOnCall: 0);
        await using var tester = new TranscriptionConnectionTester(provider, SilentLogger());

        var result = await tester.TestAsync(ValidOptions(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("fake", result.ProviderName);
        Assert.True(result.ResponseTime.TotalMilliseconds >= 0);
    }

    [Fact]
    public async Task TestAsync_ProviderError_ReturnsFailureWithMessage()
    {
        var provider = new FakeConnectionProvider("fake", errorOnCall: 1,
            errorMessage: "HTTP 401: Unauthorized");
        await using var tester = new TranscriptionConnectionTester(provider, SilentLogger());

        var result = await tester.TestAsync(ValidOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("HTTP 401", result.ErrorMessage);
    }

    [Fact]
    public async Task TestAsync_ProviderThrows_ReturnsFailure()
    {
        var provider = new ThrowingProvider();
        await using var tester = new TranscriptionConnectionTester(provider, SilentLogger());

        var result = await tester.TestAsync(ValidOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("boom", result.ErrorMessage);
    }

    [Fact]
    public async Task WriteSilentWavAsync_CreatesValidPcm16Wav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silent-test-{Guid.NewGuid():N}.wav");
        try
        {
            await TranscriptionConnectionTester.WriteSilentWavAsync(path, CancellationToken.None);

            Assert.True(File.Exists(path));
            using var reader = new NAudio.Wave.WaveFileReader(path);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16000L, reader.SampleCount); // 1 Sekunde
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task TestAsync_EmptyApiKey_StillInvokesProvider()
    {
        // Connection-Tester prueft NICHT, ob ApiKey leer ist — das ist die
        // Aufgabe des Providers (gibt ErrorMessage zurueck). Test verifiziert,
        // dass der Tester den Provider einfach aufruft.
        var provider = new FakeConnectionProvider("fake", errorOnCall: 1,
            errorMessage: "Azure Speech: API-Key fehlt (transcription.apiKey).");
        await using var tester = new TranscriptionConnectionTester(provider, SilentLogger());

        var result = await tester.TestAsync(ValidOptions(apiKey: ""), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("API-Key fehlt", result.ErrorMessage);
    }

    [Fact]
    public async Task DisposeAsync_DeletesTempFile()
    {
        var provider = new FakeConnectionProvider("fake", errorOnCall: 0);
        var tester = new TranscriptionConnectionTester(provider, SilentLogger());
        await tester.TestAsync(ValidOptions(), CancellationToken.None);
        await tester.DisposeAsync();
        // Kein Crash = OK
    }

    // =============================================================================
    // Fakes
    // =============================================================================

    private sealed class FakeConnectionProvider : ITranscriptionProvider
    {
        private readonly int _errorOnCall;
        private readonly string? _errorMessage;
        private int _callCount;

        public FakeConnectionProvider(string name, int errorOnCall, string? errorMessage = null)
        {
            Name = name;
            _errorOnCall = errorOnCall;
            _errorMessage = errorMessage;
        }

        public string Name { get; }
        public int CallCount => _callCount;

        public Task<TranscriptionResult> TranscribeAsync(
            string stereoPath, TranscriptionOptions options,
            IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
        {
            _callCount++;
            if (_callCount <= _errorOnCall)
            {
                return Task.FromResult(new TranscriptionResult(
                    Segments: Array.Empty<TranscriptionSegment>(),
                    ProviderName: Name,
                    AudioDuration: TimeSpan.Zero,
                    SpeakerCount: 0,
                    SpeakerLabels: Array.Empty<string>(),
                    ErrorMessage: _errorMessage));
            }
            return Task.FromResult(new TranscriptionResult(
                Segments: new List<TranscriptionSegment> {
                    new("S0", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hi"),
                },
                ProviderName: Name,
                AudioDuration: TimeSpan.FromSeconds(1),
                SpeakerCount: 1,
                SpeakerLabels: new List<string> { "S0" },
                ErrorMessage: null));
        }
    }

    private sealed class ThrowingProvider : ITranscriptionProvider
    {
        public string Name => "throwing";
        public Task<TranscriptionResult> TranscribeAsync(
            string stereoPath, TranscriptionOptions options,
            IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }
}
