namespace AiRecall.Core.Audio;

/// <summary>
/// Lifecycle-State einer <see cref="RecordingSession"/>.
/// </summary>
public enum RecordingState
{
    /// <summary>Session erzeugt, aber noch nicht gestartet.</summary>
    Created,
    /// <summary>Aufnahme laeuft (NAudio-Stream aktiv).</summary>
    Recording,
    /// <summary>Aufnahme gestoppt, Audio-Files geschrieben, bereit fuer Worker.</summary>
    Recorded,
    /// <summary>Aufnahme fehlgeschlagen (Device-Fehler, Write-Fehler, etc.).</summary>
    Failed
}