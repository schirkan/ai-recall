using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Serilog;

namespace AiRecall.Transcription;

/// <summary>
/// Transkriptions-Provider fuer Deepgram (Nova-2) (Spec 0013 v0.3 §5.4).
/// <list type="bullet">
///   <item>Name: <c>"deepgram"</c></item>
///   <item>REST POST <c>https://api.deepgram.com/v1/listen</c> mit
///         <c>model=nova-2</c>, <c>language</c>, <c>diarize=true</c>,
///         <c>smart_format=true</c></item>
///   <item>Auth: <c>Authorization: Token {apiKey}</c></item>
///   <li>Body: WAV-File als Stream (kein echtes Multipart, da Deepgram raw-body akzeptiert)</li>
///   <li>Response-Format: <c>results.utterances[]</c> mit
///         <c>speaker</c>, <c>start</c>, <c>end</c>, <c>transcript</c></li>
///   <li>Diarization ist Pflicht (MVP 3)</li>
/// </list>
/// </summary>
public sealed class DeepgramTranscriptionProvider : ITranscriptionProvider
{
    /// <inheritdoc />
    public string Name => "deepgram";

    private const string DefaultEndpoint = "https://api.deepgram.com";
    private const string ListenPath = "/v1/listen";

    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    /// <summary>Produktions-Konstruktor: erstellt eigenen HttpClient.</summary>
    public DeepgramTranscriptionProvider(ILogger? logger = null)
        : this(new HttpClient(), logger)
    {
    }

    /// <summary>Test-Konstruktor: erlaubt HttpClient mit Fake-MessageHandler.</summary>
    public DeepgramTranscriptionProvider(HttpClient http, ILogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(
        string stereoPath,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(stereoPath)) throw new ArgumentException("Pfad fehlt", nameof(stereoPath));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new TranscriptionResult(
                Segments: Array.Empty<TranscriptionSegment>(),
                ProviderName: Name,
                AudioDuration: TimeSpan.Zero,
                SpeakerCount: 0,
                SpeakerLabels: Array.Empty<string>(),
                ErrorMessage: "Deepgram: API-Key fehlt (transcription.apiKey).");
        }
        if (!File.Exists(stereoPath))
        {
            return new TranscriptionResult(
                Segments: Array.Empty<TranscriptionSegment>(),
                ProviderName: Name,
                AudioDuration: TimeSpan.Zero,
                SpeakerCount: 0,
                SpeakerLabels: Array.Empty<string>(),
                ErrorMessage: $"Deepgram: Datei nicht gefunden: {stereoPath}");
        }

        progress?.Report(new TranscriptionProgress(10, "Uploading to Deepgram"));
        DeepgramResponse? response;
        try
        {
            response = await PostAsync(stereoPath, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "DeepgramTranscriptionProvider: HTTP-Aufruf fehlgeschlagen");
            return new TranscriptionResult(
                Segments: Array.Empty<TranscriptionSegment>(),
                ProviderName: Name,
                AudioDuration: TimeSpan.Zero,
                SpeakerCount: 0,
                SpeakerLabels: Array.Empty<string>(),
                ErrorMessage: $"Deepgram: {ex.Message}");
        }
        progress?.Report(new TranscriptionProgress(80, "Parsing response"));

        if (response is null || response.Results?.Utterances is null)
        {
            return new TranscriptionResult(
                Segments: Array.Empty<TranscriptionSegment>(),
                ProviderName: Name,
                AudioDuration: TimeSpan.Zero,
                SpeakerCount: 0,
                SpeakerLabels: Array.Empty<string>(),
                ErrorMessage: "Deepgram: leere Response.");
        }

        var mapped = response.Results.Utterances
            .OrderBy(u => u.Start ?? 0.0)
            .Where(u => !string.IsNullOrWhiteSpace(u.Transcript))
            .Select(u => new TranscriptionSegment(
                Speaker: $"S{u.Speaker}",
                Start: TimeSpan.FromSeconds(u.Start ?? 0.0),
                End: TimeSpan.FromSeconds(u.End ?? (u.Start ?? 0.0)),
                Text: u.Transcript!))
            .ToList();

        var audioDuration = mapped.Count > 0 ? mapped.Max(s => s.End) : TimeSpan.Zero;
        var distinctSpeakers = mapped.Select(s => s.Speaker)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        progress?.Report(new TranscriptionProgress(100, "Done"));
        return new TranscriptionResult(
            Segments: mapped,
            ProviderName: Name,
            AudioDuration: audioDuration,
            SpeakerCount: distinctSpeakers.Count,
            SpeakerLabels: distinctSpeakers,
            ErrorMessage: null);
    }

    private async Task<DeepgramResponse?> PostAsync(
        string stereoPath, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.EndpointOverride)
            ? DefaultEndpoint
            : options.EndpointOverride!;
        var url = $"{endpoint.TrimEnd('/')}{ListenPath}"
            + $"?model=nova-2&language={Uri.EscapeDataString(options.Language)}"
            + "&diarize=true&smart_format=true";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Token {options.ApiKey}");

        // Deepgram akzeptiert raw body (Content-Type: audio/wav) — kein Multipart noetig.
        await using var fs = File.OpenRead(stereoPath);
        request.Content = new StreamContent(fs);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Deepgram HTTP {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<DeepgramResponse>(responseStream, _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // DTOs fuer die Deepgram-JSON-Antwort (lowercase-Felder)
    private sealed class DeepgramResponse
    {
        [JsonPropertyName("results")]
        public DeepgramResults? Results { get; set; }
    }
    private sealed class DeepgramResults
    {
        [JsonPropertyName("utterances")]
        public List<DeepgramUtterance>? Utterances { get; set; }
    }
    private sealed class DeepgramUtterance
    {
        [JsonPropertyName("speaker")]
        public int Speaker { get; set; }

        [JsonPropertyName("start")]
        public double? Start { get; set; }

        [JsonPropertyName("end")]
        public double? End { get; set; }

        [JsonPropertyName("transcript")]
        public string? Transcript { get; set; }
    }
}
