using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AiRecall.Core.Audio;

/// <summary>
/// Konfiguration fuer Audio-Recording (Spec 0013 v0.3 §3).
///
/// <para>
/// <c>enabled = false</c> (Default) bedeutet Privacy-First: kein versehentliches
/// Recording beim Erst-Start. Audio-Recording muss explizit in den Settings
/// aktiviert werden.
/// </para>
/// </summary>
public sealed class AudioConfig
{
    /// <summary>Master-Switch. false = keine Audio-Aufzeichnung.</summary>
    [Description("Master-Switch fuer Audio-Recording. false = keine Meeting-Aufzeichnung. Default false (Privacy-First).")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Mikrofon-Device-ID (leer = System-Default).</summary>
    [Description("Mikrofon-Device-ID (leer = System-Default).")]
    [JsonPropertyName("micDeviceId")]
    public string MicDeviceId { get; set; } = "";

    /// <summary>Speaker-Loopback-Device-ID (leer = System-Default).</summary>
    [Description("Speaker-Loopback-Device-ID (leer = System-Default).")]
    [JsonPropertyName("loopbackDeviceId")]
    public string LoopbackDeviceId { get; set; } = "";

    /// <summary>Sample-Rate in Hz. Default 16000 (Whisper/Azure/Deepgram-Kompatibilitaet).</summary>
    [Description("Sample-Rate in Hz. Default 16000 fuer Whisper/Azure/Deepgram-Kompatibilitaet.")]
    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; set; } = 16000;

    /// <summary>Bits pro Sample. Default 16 (PCM-16).</summary>
    [Description("Bits pro Sample. Default 16 (PCM-16).")]
    [JsonPropertyName("bitsPerSample")]
    public int BitsPerSample { get; set; } = 16;

    /// <summary>Speicher-Wurzel fuer Audio-Files. Relativ =&gt; AppContext.BaseDirectory.</summary>
    [Description("Speicher-Wurzel fuer Audio-Files. Relativ => AppContext.BaseDirectory.")]
    [JsonPropertyName("storageRoot")]
    public string StorageRoot { get; set; } = "audio";

    /// <summary>Mindestdauer eines Meetings in Sekunden. Kuerzer = verworfen.</summary>
    [Description("Mindestdauer (Sekunden), ab der ein erkanntes Meeting wirklich aufgezeichnet wird. Kuerzer = verworfen.")]
    [JsonPropertyName("minMeetingDurationSeconds")]
    public int MinMeetingDurationSeconds { get; set; } = 30;
}