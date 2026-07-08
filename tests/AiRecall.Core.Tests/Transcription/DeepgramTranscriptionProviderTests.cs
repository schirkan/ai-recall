using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Core.Tests.Transcription;

/// <summary>
/// Tests fuer <see cref="DeepgramTranscriptionProvider"/> (Spec 0013 v0.3 §5.4).
/// HttpClient mit FakeHttpMessageHandler (deterministische JSON-Responses).
/// </summary>
public class DeepgramTranscriptionProviderTests
{
    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    private static TranscriptionOptions ValidOptions(string endpoint = "") => new(
        Language: "deu",
        DiarizationRequired: true,
        MaxSpeakers: 4,
        ApiKey: "test-key",
        EndpointOverride: string.IsNullOrEmpty(endpoint) ? null : endpoint);

    /// <summary>Fake-HttpMessageHandler, der je nach URL eine vorbereitete Response liefert.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastRequestBody { get; private set; }
        public string? LastRequestAuth { get; private set; }
        public string? LastRequestUrl { get; private set; }

        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _contentType;

        public FakeHandler(HttpStatusCode status, string body, string contentType = "application/json")
        {
            _status = status;
            _body = body;
            _contentType = contentType;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestUrl = request.RequestUri?.ToString();
            if (request.Headers.TryGetValues("Authorization", out var auths))
                LastRequestAuth = auths.First();
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            };
        }
    }

    /// <summary>Schreibt eine minimale gueltige WAV-Datei (8 kHz, 16-bit, mono, 100 ms Silence).</summary>
    private static string WriteMinimalWav(string subdir)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deepgram-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, subdir);
        // 100 ms Silence, 16 kHz, 16-bit mono
        var sampleRate = 16000;
        var samples = sampleRate / 10;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        // RIFF header
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + samples * 2); // chunk size
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        // fmt subchunk
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // PCM
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        // data subchunk
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(samples * 2);
        for (int i = 0; i < samples; i++) bw.Write((short)0);
        return path;
    }

    // =============================================================================
    // Tests
    // =============================================================================

    [Fact]
    public void Name_IsDeepgram()
    {
        using var http = new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}"));
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        Assert.Equal("deepgram", provider.Name);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyApiKey_ReturnsErrorResult_DoesNotCallHttp()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");
        try
        {
            var result = await provider.TranscribeAsync(path, ValidOptions() with { ApiKey = "" }, null, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Contains("API-Key fehlt", result.ErrorMessage);
            Assert.Null(handler.LastRequest); // HttpClient nicht aufgerufen
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task TranscribeAsync_EmptyPath_Throws()
    {
        using var http = new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}"));
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranscribeAsync("", ValidOptions(), null, CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_FileMissing_ReturnsErrorResult()
    {
        using var http = new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}"));
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var result = await provider.TranscribeAsync("does-not-exist.wav", ValidOptions(), null, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains("nicht gefunden", result.ErrorMessage);
    }

    [Fact]
    public async Task TranscribeAsync_SingleUtterance_MapsToS0Speaker()
    {
        var json = """{"results":{"utterances":[{"speaker":0,"start":0.5,"end":2.5,"transcript":"Hallo Welt"}]}}""";
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");

        var result = await provider.TranscribeAsync(path, ValidOptions(), null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var seg = Assert.Single(result.Segments);
        Assert.Equal("S0", seg.Speaker);
        Assert.Equal("Hallo Welt", seg.Text);
        Assert.Equal(TimeSpan.FromSeconds(0.5), seg.Start);
        Assert.Equal(TimeSpan.FromSeconds(2.5), seg.End);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.AudioDuration);
        Assert.Equal(1, result.SpeakerCount);
        Assert.Equal(new[] { "S0" }, result.SpeakerLabels);
    }

    [Fact]
    public async Task TranscribeAsync_MultipleUtterances_AggregatesSpeakers()
    {
        var json = """
        {
          "results": {
            "utterances": [
              { "speaker": 0, "start": 0.0, "end": 2.0, "transcript": "Erste" },
              { "speaker": 1, "start": 2.5, "end": 5.0, "transcript": "Antwort" },
              { "speaker": 0, "start": 5.5, "end": 7.0, "transcript": "Zweite" }
            ]
          }
        }
        """;
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");

        var result = await provider.TranscribeAsync(path, ValidOptions(), null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal("S0", result.Segments[0].Speaker);
        Assert.Equal("S1", result.Segments[1].Speaker);
        Assert.Equal("S0", result.Segments[2].Speaker);
        Assert.Equal(2, result.SpeakerCount);
        Assert.Equal(new[] { "S0", "S1" }, result.SpeakerLabels);
    }

    [Fact]
    public async Task TranscribeAsync_SendsAuthorizationHeader()
    {
        var json = """{"results":{"utterances":[]}}""";
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");

        await provider.TranscribeAsync(path, ValidOptions(), null, CancellationToken.None);

        Assert.Equal("Token test-key", handler.LastRequestAuth);
    }

    [Fact]
    public async Task TranscribeAsync_SendsCorrectUrlAndQueryParams()
    {
        var json = """{"results":{"utterances":[]}}""";
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");

        await provider.TranscribeAsync(path, ValidOptions(), null, CancellationToken.None);

        Assert.NotNull(handler.LastRequestUrl);
        Assert.StartsWith("https://api.deepgram.com/v1/listen?", handler.LastRequestUrl);
        Assert.Contains("model=nova-2", handler.LastRequestUrl);
        Assert.Contains("language=deu", handler.LastRequestUrl);
        Assert.Contains("diarize=true", handler.LastRequestUrl);
        Assert.Contains("smart_format=true", handler.LastRequestUrl);
    }

    [Fact]
    public async Task TranscribeAsync_SendsWavBytesInBody()
    {
        var json = """{"results":{"utterances":[]}}""";
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");

        await provider.TranscribeAsync(path, ValidOptions(), null, CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        Assert.True(handler.LastRequestBody!.Length > 0);
        // Erste 4 Bytes sollten "RIFF" sein
        Assert.Equal((byte)'R', handler.LastRequestBody[0]);
        Assert.Equal((byte)'I', handler.LastRequestBody[1]);
        Assert.Equal((byte)'F', handler.LastRequestBody[2]);
        Assert.Equal((byte)'F', handler.LastRequestBody[3]);
    }

    [Fact]
    public async Task TranscribeAsync_HttpError_ReturnsErrorResult()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized, "{\"err\":\"Invalid API key\"}");
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");

        var result = await provider.TranscribeAsync(path, ValidOptions(), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("401", result.ErrorMessage);
    }

    [Fact]
    public async Task TranscribeAsync_RespectsCancellation()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(path, ValidOptions(), null, cts.Token));
    }

    [Fact]
    public async Task TranscribeAsync_ReportsProgress()
    {
        var json = """{"results":{"utterances":[{"speaker":0,"start":0,"end":1,"transcript":"Hi"}]}}""";
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");
        var recorder = new RecordingProgress();

        await provider.TranscribeAsync(path, ValidOptions(), recorder, CancellationToken.None);

        Assert.NotEmpty(recorder.Reports);
        Assert.Equal(100, recorder.Reports[^1].PercentComplete);
    }

    [Fact]
    public async Task TranscribeAsync_CustomEndpoint_UsesIt()
    {
        var json = """{"results":{"utterances":[]}}""";
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var provider = new DeepgramTranscriptionProvider(http, SilentLogger());
        var path = WriteMinimalWav("ok.wav");

        await provider.TranscribeAsync(
            path, ValidOptions(endpoint: "https://eu.deepgram.example"),
            null, CancellationToken.None);

        Assert.StartsWith("https://eu.deepgram.example/v1/listen?", handler.LastRequestUrl);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class RecordingProgress : IProgress<TranscriptionProgress>
    {
        public List<TranscriptionProgress> Reports { get; } = new();
        public void Report(TranscriptionProgress value) => Reports.Add(value);
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
    }
}
