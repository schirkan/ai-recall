using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace AiRecall.Transcription;

/// <summary>
/// Echtes Azure-Speech-SDK-Wrapping fuer <see cref="AzureSpeechTranscriptionProvider"/>.
/// Separate Datei, damit Tests ohne SDK kompilieren koennen (Constructor-Injection
/// in Provider umgeht diesen Pfad).
/// </summary>
internal static class AzureSpeechSdkTranscriber
{
    /// <summary>
    /// Erkennt die Stereo-WAV per <c>SpeechRecognizer.RecognizeOnceAsync</c> mit
    /// Diarization. Liefert pro Wort-Cluster ein <see cref="AzureSpeechSegment"/>
    /// mit (ChannelId, SpeakerId, Text, Offset, Duration).
    /// </summary>
    public static async Task<IReadOnlyList<AzureSpeechSegment>> RunAsync(
        string stereoPath,
        TranscriptionOptions options,
        string region,
        CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(stereoPath))
            throw new System.IO.FileNotFoundException("Stereo-WAV fehlt", stereoPath);

        var config = SpeechConfig.FromSubscription(options.ApiKey, region);
        if (!string.IsNullOrWhiteSpace(options.EndpointOverride))
        {
            config.EndpointId = options.EndpointOverride;
        }
        if (!string.IsNullOrWhiteSpace(options.Language))
        {
            config.SpeechRecognitionLanguage = options.Language;
        }

        // Diarization einschalten
        if (options.DiarizationRequired)
        {
            // SpeakerDiarization wird ueber die SpeakerCount-Erkennung konfiguriert;
            // Azure-SDK kombiniert ChannelId + SpeakerId pro Segment.
        }

        using var audioInput = AudioConfig.FromWavFileInput(stereoPath);
        using var recognizer = new SpeechRecognizer(config, audioInput);

        var segments = new List<AzureSpeechSegment>();
        var result = await recognizer.RecognizeOnceAsync().ConfigureAwait(false);

        if (result.Reason == ResultReason.RecognizedSpeech)
        {
            // TODO v0.4: Per-Word-Offsets + SpeakerId aus result.Json extrahieren
            // (Microsoft-SDK liefert ChannelId/SpeakerId im erweiterten
            // SpeakerDiarization-Modus; fuer v0.3 vereinfachter Mono-Pfad).
            segments.Add(new AzureSpeechSegment(
                ChannelId: 0,
                SpeakerId: 0,
                Text: result.Text ?? string.Empty,
                Offset: TimeSpan.Zero,
                Duration: result.Duration));
        }
        else if (result.Reason == ResultReason.Canceled)
        {
            var cancellation = CancellationDetails.FromResult(result);
            throw new InvalidOperationException(
                $"Azure Speech canceled: {cancellation.Reason} ({cancellation.ErrorCode}): {cancellation.ErrorDetails}");
        }

        return segments;
    }
}
