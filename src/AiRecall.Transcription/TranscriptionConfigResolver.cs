using System;

using AiRecall.Core.Configuration;

namespace AiRecall.Transcription;

/// <summary>
/// Resolved <see cref="TranscriptionConfig"/> in eine konkrete
/// <see cref="TranscriptionOptions"/>-Instanz plus den effektiven
/// Provider-Namen. Behandelt Provider-Fallback (unbekannt → "azure-speech").
/// Die eigentliche Provider-Instanziierung (Azure vs Deepgram) erfolgt
/// durch den Aufrufer; diese Klasse liefert nur die <i>Konfiguration</i>.
/// </summary>
public static class TranscriptionConfigResolver
{
    /// <summary>Default-Provider-Name bei unbekanntem Wert.</summary>
    public const string DefaultProviderName = "azure-speech";

    /// <summary>Alle bekannten Provider-IDs (in Settings-UI als Dropdown).</summary>
    public static readonly IReadOnlyList<string> KnownProviders = new[]
    {
        "azure-speech",
        "deepgram",
    };

    /// <summary>
    /// Liefert den effektiven Provider-Namen. Unbekannte Werte → Default.
    /// </summary>
    public static string ResolveProviderName(TranscriptionConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        var requested = (config.Provider ?? string.Empty).Trim();
        if (requested.Length == 0) return DefaultProviderName;
        foreach (var known in KnownProviders)
        {
            if (string.Equals(known, requested, StringComparison.OrdinalIgnoreCase))
                return known;
        }
        return DefaultProviderName;
    }

    /// <summary>
    /// Baut die <see cref="TranscriptionOptions"/> aus <paramref name="config"/>
    /// passend zum effektiven Provider (Azure braucht Region, Deepgram Endpoint).
    /// </summary>
    public static TranscriptionOptions ResolveOptions(TranscriptionConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        var providerName = ResolveProviderName(config);
        string? endpoint = providerName switch
        {
            "azure-speech" => null, // Region-basiert, kein Endpoint-Override noetig
            "deepgram" => string.IsNullOrWhiteSpace(config.DeepgramEndpoint)
                ? "https://api.deepgram.com"
                : config.DeepgramEndpoint,
            _ => null,
        };
        return new TranscriptionOptions(
            Language: string.IsNullOrWhiteSpace(config.DefaultLanguage) ? "deu" : config.DefaultLanguage,
            DiarizationRequired: true, // MVP 3: Diarization immer an
            MaxSpeakers: config.MaxSpeakers > 0 ? config.MaxSpeakers : 8,
            ApiKey: config.ApiKey ?? string.Empty,
            EndpointOverride: endpoint);
    }
}
