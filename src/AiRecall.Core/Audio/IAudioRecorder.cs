namespace AiRecall.Core.Audio;

/// <summary>
/// Audio-Capture-Stream (NAudio-Wrapper fuer Testbarkeit).
///
/// <para>
/// Spezifiziert in Spec 0013 v0.3 §2: zwei separate Mono-Streams (Mic + Loopback),
/// PCM 16-bit, 16 kHz, WAV-Container. Diese Schnittstelle kapselt genau einen
/// Stream; eine Aufnahme-Session verwendet zwei Recorder parallel (Mic + Loopback).
/// </para>
/// </summary>
public interface IAudioRecorder : IDisposable
{
    /// <summary>Wave-Format dieses Recorders (z.B. 16 kHz, 16-bit, Mono).</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Startet das Aufnahme-Capture. Daten landen in den internen Puffer.
    /// Wirft, wenn bereits gestartet.
    /// </summary>
    void Start();

    /// <summary>
    /// Stoppt das Aufnahme-Capture. Liefert die aufgezeichneten PCM-Rohbytes.
    /// Wirft, wenn noch nicht gestartet oder bereits gestoppt.
    /// </summary>
    /// <returns>PCM-Rohbytes (Mono, 16-bit Little-Endian, passend zum <see cref="Format"/>).</returns>
    byte[] Stop();
}

/// <summary>
/// Audio-Format-Beschreibung (PCM, Mono, 16 kHz, 16-bit fuer MVP 3).
/// </summary>
public sealed record AudioFormat(int SampleRate, int BitsPerSample, int Channels)
{
    /// <summary>Default-Format fuer MVP 3: 16 kHz, 16-bit, Mono (Whisper/Azure/Deepgram-kompatibel).</summary>
    public static AudioFormat Default => new(SampleRate: 16000, BitsPerSample: 16, Channels: 1);
}