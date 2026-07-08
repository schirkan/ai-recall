using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Serilog;

namespace AiRecall.Transcription;

/// <summary>
/// Rohe Segment-Daten vom Azure-Speech-SDK (Spec 0013 v0.3 §5.4 Azure).
/// Ein Wort-Cluster mit gleichem (ChannelId, SpeakerId)-Tupel.
/// </summary>
internal sealed record AzureSpeechSegment(
    int ChannelId,
    int SpeakerId,
    string Text,
    TimeSpan Offset,
    TimeSpan Duration);

/// <summary>
/// Indirektion zum Azure-Speech-SDK. Production-Default ruft das echte SDK;
/// Tests koennen einen Fake-Delegate einsetzen.
/// </summary>
internal delegate Task<IReadOnlyList<AzureSpeechSegment>> AzureSpeechTranscriber(
    string stereoPath, TranscriptionOptions options, CancellationToken cancellationToken);

/// <summary>
/// Transkriptions-Provider fuer Azure Cognitive Services Speech (Spec 0013 v0.3 §5.4).
/// <list type="bullet">
///   <item>Name: <c>"azure-speech"</c></item>
///   <item>Stereo-Handling: ChannelId + SpeakerId pro Segment
///         (z. B. "C0-S1" fuer Channel 0 / Speaker 1)</item>
///   <item>Diarization ist Pflicht (MVP 3)</item>
/// </list>
/// </summary>
public sealed class AzureSpeechTranscriptionProvider : ITranscriptionProvider
{
    private readonly AzureSpeechTranscriber _transcriber;
    private readonly ILogger? _logger;

    /// <inheritdoc />
    public string Name => "azure-speech";

    /// <summary>Produktions-Konstruktor: nutzt das echte Azure-Speech-SDK.</summary>
    public AzureSpeechTranscriptionProvider(ILogger? logger = null)
        : this(DefaultTranscriber, logger)
    {
    }

    /// <summary>Test-Konstruktor mit eigenem Transcriber-Delegate.</summary>
    internal AzureSpeechTranscriptionProvider(AzureSpeechTranscriber transcriber, ILogger? logger = null)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
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
                ErrorMessage: "Azure Speech: API-Key fehlt (transcription.apiKey).");
        }
        if (string.IsNullOrWhiteSpace(options.EndpointOverride))
        {
            // Azure: Region wird per SpeechConfig gesetzt, EndpointOverride bleibt null.
            // Pruefung im Default-Transcriber.
        }

        progress?.Report(new TranscriptionProgress(5, "Sending to Azure Speech"));
        IReadOnlyList<AzureSpeechSegment> raw;
        try
        {
            raw = await _transcriber(stereoPath, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation durchlassen (.NET-Konvention).
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "AzureSpeechTranscriptionProvider: SDK-Aufruf fehlgeschlagen");
            return new TranscriptionResult(
                Segments: Array.Empty<TranscriptionSegment>(),
                ProviderName: Name,
                AudioDuration: TimeSpan.Zero,
                SpeakerCount: 0,
                SpeakerLabels: Array.Empty<string>(),
                ErrorMessage: $"Azure Speech: {ex.Message}");
        }
        progress?.Report(new TranscriptionProgress(90, "Mapping segments"));

        // Rohe SDK-Segmente → Domain-TranscriptionSegment
        // (ChannelId, SpeakerId) → "C{channel}-S{speaker}", rohe Provider-Speaker-IDs.
        var mapped = raw
            .OrderBy(s => s.Offset)
            .Select(s => new TranscriptionSegment(
                Speaker: $"C{s.ChannelId}-S{s.SpeakerId}",
                Start: s.Offset,
                End: s.Offset + s.Duration,
                Text: s.Text))
            .ToList();

        var audioDuration = mapped.Count > 0
            ? mapped.Max(s => s.End)
            : TimeSpan.Zero;
        var distinctSpeakers = mapped
            .Select(s => s.Speaker)
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

    /// <summary>
    /// Default-Transcriber: ruft das echte Azure-Speech-SDK auf.
    /// Implementiert in einer separaten Partial-Class-Datei, damit Tests ohne
    /// SDK-Code kompilieren koennen.
    /// </summary>
    private static async Task<IReadOnlyList<AzureSpeechSegment>> DefaultTranscriber(
        string stereoPath, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        // Region aus ApiKey+EndpointOverride? Nein — Azure-Speech-Region ist eine
        // eigene Konfig (TranscriptionConfig.AzureRegion). Provider hat hier nur
        // Zugriff auf options.EndpointOverride (= null bei Azure). Daher:
        // Default-Transcriber liest Region aus ENV "AZURE_SPEECH_REGION" als
        // Fallback. Produktion: Provider mit Region im Konstruktor erweitern.
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        if (string.IsNullOrWhiteSpace(region))
        {
            throw new InvalidOperationException(
                "Azure Speech: Region nicht konfiguriert (ENV AZURE_SPEECH_REGION oder TranscriptionConfig.AzureRegion).");
        }
        return await AzureSpeechSdkTranscriber.RunAsync(stereoPath, options, region, cancellationToken).ConfigureAwait(false);
    }
}
