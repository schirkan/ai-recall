using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiRecall.Transcription;

/// <summary>
/// Provider-Schnittstelle fuer Audio-Transkription (Spec 0013 v0.3 §5.4).
/// Implementierungen: <c>AzureSpeechTranscriptionProvider</c>,
/// <c>DeepgramTranscriptionProvider</c>. Auswahl zur Laufzeit via
/// <see cref="Configuration.TranscriptionConfig.Provider"/>.
/// </summary>
public interface ITranscriptionProvider
{
    /// <summary>Stabiler Provider-Identifier: <c>"azure-speech"</c> oder <c>"deepgram"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Transkribiert die Combined-Stereo-WAV und liefert Diarization-Segmente.
    /// </summary>
    /// <param name="stereoPath">Pfad zur <c>combined-stereo.wav</c> (transient).</param>
    /// <param name="options">Provider-Optionen (Language, ApiKey, MaxSpeakers, EndpointOverride).</param>
    /// <param name="progress">Optional Progress-Callback (0–100, <c>CurrentStep</c>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<TranscriptionResult> TranscribeAsync(
        string stereoPath,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-Optionen (Spec 0013 v0.3 §5.4). Wird vom Worker aus
/// <c>TranscriptionConfig</c> + User-Overrides gebaut.
/// </summary>
public sealed record TranscriptionOptions(
    string Language,
    bool DiarizationRequired,
    int MaxSpeakers,
    string ApiKey,
    string? EndpointOverride);

/// <summary>
/// Transkriptions-Ergebnis. Segmente sind in Sprech-Reihenfolge.
/// <see cref="SpeakerLabels"/> enthaelt die rohen Provider-IDs (z. B. S0, S1).
/// </summary>
public sealed record TranscriptionResult(
    IReadOnlyList<TranscriptionSegment> Segments,
    string ProviderName,
    TimeSpan AudioDuration,
    int SpeakerCount,
    IReadOnlyList<string> SpeakerLabels,
    string? ErrorMessage)
{
    public bool IsSuccess => ErrorMessage is null;
}

/// <summary>
/// Ein Diarization-Segment (Spec 0013 v0.3 §5.4). <see cref="Speaker"/> ist die
/// rohe Provider-Speaker-ID (z. B. "S1"); Mapping auf reale Namen folgt in v0.4
/// (Outlook-Kontakt-Lookup).
/// </summary>
public sealed record TranscriptionSegment(
    string Speaker,
    TimeSpan Start,
    TimeSpan End,
    string Text);

/// <summary>
/// Fortschritts-Update fuer den TranscriptionWorker (Spec 0013 v0.3 §5.4).
/// <see cref="PercentComplete"/> 0–100, <see cref="CurrentStep"/> menschenlesbar.
/// </summary>
public sealed record TranscriptionProgress(
    int PercentComplete,
    string? CurrentStep);

/// <summary>
/// Task-Beschreibung fuer <c>TranscriptionWorker</c> (Spec 0013 v0.3 §5).
/// Pro Meeting-Aufzeichnung gibt es genau einen Task; Worker enqueued aus
/// <c>RecordingSession.Stop</c> oder Recovery-Scan. Stereo-Concatenation
/// laeuft intern im Worker (mic + loopback → combined-stereo.wav).
/// </summary>
public sealed record AudioTranscriptionTask(
    string Folder,
    string MicPath,
    string LoopbackPath,
    string MetadataPath,
    TranscriptionOptions Options,
    DateTimeOffset EnqueuedAt);
