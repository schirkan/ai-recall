# 0013 — Audio Notes (MVP 3)

> **Status:** 🟡 **GEPLANT v0.3 (2026-07-07, Update 2 nach Martin-Feedback)**
> **Owner:** Martin
> **Abhängig von:** Spec 0005 (Trigger-Pipeline), Spec 0006 (Tray-EXE), Spec 0007 (Conversion-Worker-Pattern), Spec 0009 (Settings-Dialog), **Spec 0011 (Teams App-Reader — Trigger-Quelle)**, Spec 0012 (Modal-Dialog-Stil)

## Ziel

Wenn ein **Microsoft Teams Meeting** aktiv wird (vom Teams App Reader
aus Spec 0011 erkannt), soll AiRecall automatisch zweikanaliges Audio
aufzeichnen (Mikrofon + Speaker-Loopback), parallel dazu eine
**MD-Metadaten-Datei** anlegen und nach Meeting-Ende im Hintergrund
eine **Transkription mit Diarization** erstellen. Das Transkript wird
in dieselbe MD-Datei geschrieben (analog OCR-Pattern aus Spec 0007).

**Roadmap-Kontext:** Ehemaliger MVP-3-Scope (Audio + Wiki) wurde am
2026-07-06 entzerrt — Audio Notes ist jetzt MVP 3, Auto Wiki wird MVP 4
(siehe DECISIONS.md Roadmap-Reshuffle-Eintrag 2026-07-06).

## Anforderungen (Übersicht)

| # | Anforderung | Status |
| - | - | - |
| 1 | Automatische Teams-Meeting-Start-Erkennung | **Spezifiziert** über Teams App Reader (Spec 0011) |
| 2 | Background-Audio-Recording (zwei Kanäle: Mic + Speaker-Loopback) | Spezifiziert |
| 3 | Audio-Devices in Settings-Dialog auswählbar | Spezifiziert |
| 4 | MD-Datei mit Metadaten neben Audio-Files | Spezifiziert |
| 5 | Background-Worker transkribiert nach Meeting-Ende | Spezifiziert (**Provider: Azure Speech oder Deepgram — Martin wählt**) |
| 6 | Diarization als Pflicht-Anforderung | Spezifiziert |
| 7 | Transkription in MD-Datei schreiben (analog OCR) | Spezifiziert |

## 1. Teams-Meeting-Start-Erkennung — über Teams App Reader

**Martin-Direktive 2026-07-07:** _„Die Teams Meeting Detection soll über einen App Reader getriggert werden. Wir brauchen also ein Teams App Reader."_

**Architektur:** Der bereits vorhandene `TeamsAppReader` (Spec 0011) ist
die einzige Quelle für Meeting-Detection. **Kein separater
`IMeetingDetector` / `TeamsMeetingDetector`** (verworfen — würde
Parsing-Logik duplizieren).

### Voraussetzung: `TeamsChatKind.Meeting` ist schon da

Spec 0011 enthält bereits die nötige Detection-Infrastruktur:

- `TeamsChatKind.Meeting` (Enum-Wert in `TeamsUiaReader.cs`)
- `TeamsTitleInfo.IsMeeting` (bool-Flag, gesetzt wenn `Kind == Meeting`)
- Title-Parser zerlegt `"Meeting | Daily Standup - Microsoft Teams"` →
  `Kind = Meeting`, `FormattedTitle = "Daily Standup"`

Die 3-Strategy-Auflösung des App Readers (CDP → UIA → Title-Fallback,
Spec 0011) liefert damit auch die Meeting-Detection „kostenlos":

| Strategy | Meeting-Detection-Quelle |
| - | - |
| CDP-Pfad | DOM-Analyse: aktiver Call-Button / Meeting-Indicator |
| UIA-Pfad | Window-Title-Prefix „Meeting \|" |
| Title-Fallback | Window-Title-Prefix „Meeting \|" |

### Erweiterung des `TeamsAppReader` (Spec 0011)

Neue öffentliche API:

```csharp
public sealed class TeamsAppReader : IAppReader, IDisposable
{
    // existing Read(), SupportsBackgroundPolling, ...

    /// <summary>
    /// Wird gefeuert, wenn der App Reader einen Wechsel des
    /// Meeting-Zustands erkennt (false -> true = Started, true -> false = Ended).
    /// Quelle: 3-Strategy-Auflösung (CDP / UIA / Title) von Spec 0011.
    /// </summary>
    public event EventHandler<MeetingStateChangedEventArgs>? MeetingStateChanged;
}

public sealed record MeetingStateChangedEventArgs(
    bool IsActive,             // false -> true = Started, true -> false = Ended
    string Topic,              // z. B. "Daily Standup" (aus Title-Prefix)
    string WindowTitle,        // Voll-Title für Diagnose
    string ChatIdShort,        // SHA256(Title+Process+StartedAt) -> 8 hex (deterministisch)
    DateTimeOffset DetectedAt
);
```

### Wiring in `TriggerSupervisor`

```csharp
public sealed class TriggerSupervisor : ITriggerService
{
    private readonly TeamsAppReader _teamsReader;
    private readonly AudioRecorderSessionFactory _audioFactory;

    public Task StartAsync(CancellationToken ct)
    {
        _teamsReader.MeetingStateChanged += OnMeetingStateChanged;
        // ... existing trigger wiring
    }

    private void OnMeetingStateChanged(object? sender, MeetingStateChangedEventArgs e)
    {
        if (e.IsActive)
        {
            // Audio-Recording-Session starten (idempotent via ChatIdShort)
            _audioFactory.StartOrAttach(e);
        }
        else
        {
            _audioFactory.EndSession(e.ChatIdShort);
        }
    }
}
```

### Datenmodell (unverändert aus Spec 0013 v0.3 Entwurf)

```csharp
public sealed record MeetingEvent(
    Guid MeetingId,           // SHA256(Process + StartedAt-Tag) -> erste 8 hex
    string ProcessName,       // "ms-teams.exe"
    string WindowTitle,       // z. B. "Daily Standup | Meeting | Alice - Microsoft Teams"
    DateTimeOffset StartedAt,
    string? Topic             // best-effort aus Title-Parsing (Teams Meeting | Daily Standup)
);
```

`MeetingEvent` ist die App-Reader-Output-Domain; `MeetingStateChangedEventArgs`
ist die Event-Variante. Beide tragen dieselben Informationen, die
`AudioRecorderSession` für die Persistenz braucht.

### Debounce

- **30 s Mindest-Meeting-Dauer**, sonst verworfen. Konfigurierbar via
  `appReader.teams.minMeetingDurationSeconds` (Default: 30).
- Verhindert Aufnahme von 5-Sekunden-Test-Meetings oder schnellen Tab-Wechseln.
- Implementierung: `AudioRecorderSession` trackt `StartedAt`; wenn
  `MeetingStateChanged(IsActive=false)` innerhalb von 30 s kommt, wird
  der Recording-Ordner verworfen (kein WAV, keine MD, kein Worker-Task).

### Meeting-Ende-Erkennung

| Quelle | Wann feuert `IsActive=false`? |
| - | - |
| `TeamsAppReader` | Title verliert „Meeting \|"-Prefix ODER CDP zeigt keinen aktiven Call mehr |
| Manueller User-Stop | Tray-Menu „Stop Recording" (Force-Ende) |

### Konfiguration (Erweiterung von `TeamsConfig`)

```csharp
public sealed class TeamsConfig
{
    // ... existing ...

    /// <summary>true: Auto-Recording bei erkanntem Teams-Meeting starten.</summary>
    [Description("true: AiRecall startet Audio-Recording bei erkanntem Teams-Meeting automatisch.")]
    [JsonPropertyName("autoRecordMeetings")]
    public bool AutoRecordMeetings { get; set; } = true;

    /// <summary>Mindestdauer (Sekunden), ab der ein erkanntes Meeting wirklich aufgezeichnet wird.</summary>
    [Description("Mindestdauer (Sekunden), ab der ein erkanntes Meeting aufgezeichnet wird. Kuerzer = verworfen.")]
    [JsonPropertyName("minMeetingDurationSeconds")]
    public int MinMeetingDurationSeconds { get; set; } = 30;
}
```

**Obsolet (gegenüber Spec 0013 v0.3 Entwurf):** `meetingTitleSubstrings` entfällt,
weil die Title-Parsing-Logik jetzt zentral in `TeamsUiaReader` (Spec 0011) liegt
und nicht dupliziert werden soll.

### Ausbaustufe v0.4 — Outlook-Kalender-Lookup

**Martin-Direktive 2026-07-07:** _„Im Outlook Kalender einem Teams-Termin zur
aktuellen Uhrzeit suchen und Termin-Metadaten (Teilnehmer, Titel, Beschreibung
etc.) übernehmen."_

**Scope:** v0.4 (NICHT v0.3). Architektur-Vorbereitung in v0.3 durch
ein optional befüllbares `MeetingMetadata`-Feld in der MD-Frontmatter:

```csharp
public sealed record MeetingMetadata(
    string Topic,                          // aus Title-Parser (v0.3)
    IReadOnlyList<string> Participants,    // leer in v0.3, gefüllt in v0.4
    string? Description,                   // null in v0.3, gefüllt in v0.4
    string? CalendarAppointmentId,         // null in v0.3, gefüllt in v0.4
    string? Organizer                      // null in v0.3, gefüllt in v0.4
);
```

In v0.3 wird nur `Topic` befüllt; `Participants`, `Description`,
`CalendarAppointmentId`, `Organizer` bleiben leer/null.

In v0.4 wird eine Outlook-Kalender-Suche (analog Spec 0004 Iter. 3 Outlook-App-Reader)
eingehängt: Suche nach `Appointment` mit `IsOnlineMeeting == true` und
`Start <= DetectedAt <= End` im aktuellen Zeitfenster. Metadaten werden
in die laufende MD-Datei nachgetragen (kein Re-Recording).

## 2. Background-Audio-Recording (zwei Kanäle)

**Entscheidung (Martin bestätigt):** Zwei separate **Mono-WAV-Files** (kein
Stereo-Mix), **PCM 16-bit, 16 kHz** (kein Opus).

### Encoding

| Parameter | Wert | Begründung |
| - | - | - |
| Format | **PCM 16-bit**, **16 kHz**, **Mono** | Whisper-Standard-Eingabe; Azure Speech + Deepgram akzeptieren beide direkt |
| Container | **WAV (RIFF)** | Lossless, einfache Decoding-Pipeline |

### Zwei Streams

- **Stream A — Mikrofon:** `WasapiCapture` auf konfiguriertem Input-Device
  (`AiRecall.Core.Audio.WasapiAudioRecorder`, basiert auf NAudio `WasapiCapture`)
- **Stream B — Speaker-Loopback:** `WasapiLoopbackCapture` auf konfiguriertem
  Output-Device (Wasapi-API kann ein Output-Device als Capture "abhören",
  erfordert Windows 10+)

### File-Layout pro Meeting

```
%APPDATA%/AiRecall/audio/yyyy-MM-dd/HHmmss-{meetingIdShort}/
  mic.wav         # Mikrofon, Mono, 16kHz, PCM-16
  loopback.wav    # Speaker-Loopback, Mono, 16kHz, PCM-16
  meta.md         # Frontmatter + Meeting-Metadaten + späteres Transcript
```

**Begründung zwei Files statt Stereo:**
- Stereo-Mix würde Diarization erschweren (Welcher Speaker in welcher Spur?)
- Zwei separate Spuren erlauben getrennte Pre-Processing-Pipelines
  (z. B. Loopback-Rauschunterdrückung vs. Mic-AGC)
- Nachteil: doppelter Speicherbedarf (~30 MB/Stunde pro Stream bei 16 kHz Mono PCM-16)

### Datenmodell

```csharp
public sealed record MeetingRecordingPaths(
    string Folder,         // ".../2026-07-07/090000-a1b2c3d4/"
    string MicPath,        // ".../mic.wav"
    string LoopbackPath,   // ".../loopback.wav"
    string MetadataPath    // ".../meta.md"
);
```

## 3. Audio-Devices in Settings auswählbar

**Entscheidung:** Neuer Tab **„Audio"** im `SettingsDialog` (Spec 0009 Foundation).

### UI-Layout (Tab „Audio")

```
┌─ Settings ─────────────────────────────────────────────────────────┐
│ [ General | Trigger | Teams | Apps | OCR | Logging ] [ Audio ]    │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│  Mikrofon (Capture)                                                │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ ▼ Microphone (Realtek High Definition Audio)        [ Test ]  │  │
│  │   Microphone Array (Realtek)                                │  │
│  │   Headset Microphone (Jabra Evolve2 65)                     │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  Speaker-Loopback (Playback abgehört)                              │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ ▼ Speakers (Realtek High Definition Audio)        [ Test ]  │  │
│  │   Headphones (Jabra Evolve2 65)                             │  │
│  │   Digital Output (HDMI)                                     │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  Aufnahme-Einstellungen                                            │
│    Sample-Rate:    [ 16000 ▼ ] Hz                                  │
│    Bits/Sample:    [ 16 ▼ ]                                        │
│    Min-Dauer:      [ 30 ▼ ] s                                      │
│                                                                    │
│  [ Speichern ]  [ Abbrechen ]                                      │
└────────────────────────────────────────────────────────────────────┘
```

### Test-Button

- 3-Sekunden-Aufnahme auf dem selektierten Device
- Sofortiges Playback auf demselben Tab
- Funktioniert **unabhängig von `audio.enabled`** — Martin-Direktive 2026-07-07:
  Test-Button soll auch dann gehen, wenn Audio-Recording global deaktiviert ist
  (für Device-Selection und -Validation)

### Konfiguration (`AudioConfig`)

```csharp
public sealed class AudioConfig
{
    [Description("Master-Switch fuer Audio-Recording. false = keine Meeting-Aufzeichnung. Default false (Privacy-First, kein versehentliches Recording beim Erst-Start).")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [Description("Mikrofon-Device-ID (leer = System-Default).")]
    [JsonPropertyName("micDeviceId")]
    public string MicDeviceId { get; set; } = "";

    [Description("Speaker-Loopback-Device-ID (leer = System-Default).")]
    [JsonPropertyName("loopbackDeviceId")]
    public string LoopbackDeviceId { get; set; } = "";

    [Description("Sample-Rate in Hz. Default 16000 fuer Whisper/Azure/Deepgram-Kompatibilitaet.")]
    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; set; } = 16000;

    [Description("Bits pro Sample. Default 16 (PCM-16).")]
    [JsonPropertyName("bitsPerSample")]
    public int BitsPerSample { get; set; } = 16;

    [Description("Speicher-Wurzel fuer Audio-Files. Relativ => AppContext.BaseDirectory.")]
    [JsonPropertyName("storageRoot")]
    public string StorageRoot { get; set; } = "audio";
}
```

### Device-Enumeration

```csharp
public interface IAudioDeviceProvider
{
    IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices();
    IReadOnlyList<AudioDeviceInfo> EnumerateLoopbackDevices();
    AudioDeviceInfo? GetDefaultInputDevice();
    AudioDeviceInfo? GetDefaultLoopbackDevice();
}

public sealed record AudioDeviceInfo(
    string DeviceId,        // MMDevice-ID (Windows)
    string FriendlyName,    // "Microphone (Realtek ...)"
    string InterfaceName    // "USB Audio", "Realtek HD", ...
);
```

NAudio `MMDeviceEnumerator` liefert die Liste; `DeviceId` ist der stabile
Schlüssel für Persistenz. `FriendlyName` kann sich beim Replug ändern.

## 4. MD-Metadaten-Datei neben Audio

**Entscheidung:** Eigene `meta.md` pro Meeting, **parallel** zu `mic.wav` und `loopback.wav`.

### Initialer Inhalt (direkt nach Meeting-Start)

```markdown
---
type: audio-meeting
source: teams
meetingId: a1b2c3d4
started: 2026-07-07T09:00:00+02:00
ended: 2026-07-07T09:45:00+02:00
duration: 45m12s
topic: Daily Standup
mic_device: Microphone (Realtek High Definition Audio)
loopback_device: Speakers (Realtek High Definition Audio)
sample_rate: 16000
bits_per_sample: 16
transcript_status: pending        # pending | partial | done | failed
diarization: required
provider: ""                       # azure-speech | deepgram | ... (nach Auswahl)
language: deu
participants: []                   # leer in v0.3, gefüllt in v0.4 via Outlook-Kalender
calendar_appointment_id: null      # null in v0.3, gefüllt in v0.4
---

# Meeting: Daily Standup

**Aufnahme-Status:** läuft

## Teilnehmer

- (Liste wird in v0.4 per Outlook-Kalender-Lookup befüllt)

## Audio-Files

- Mikrofon: `mic.wav` (~22.5 MB)
- Speaker-Loopback: `loopback.wav` (~22.5 MB)

## Transkription

Wird im Hintergrund verarbeitet, sobald das Meeting beendet ist.
```

### `transcript_status`-Lifecycle

| Status | Wann | Bedeutung |
| - | - | - |
| `pending` | Direkt nach Meeting-Start | Aufnahme läuft, kein Transkript |
| `partial` | Nach Meeting-Ende, Transcription-Worker hat begonnen | Worker arbeitet |
| `done` | Transkription erfolgreich abgeschlossen | Transkriptions-Sektion vorhanden |
| `failed` | Worker-Fehler (Provider-Down, File-Corruption) | Fehlertext in `## Transcription`-Sektion |

## 5. Background-Worker für Transkription

**Entscheidung:** Eigener `TranscriptionWorker` analog `ConversionWorker` (Spec 0007).

### Architektur-Pattern (1:1 von Spec 0007)

```
TeamsAppReader (Spec 0011)
   │
   ├─ Event: MeetingStateChanged (IsActive)
   │
   ▼
TriggerSupervisor  ──▶  AudioRecorderSession.Start/End
   │
   ├─ finalize() ──▶  meta.md (transcript_status: pending)
   │
   ▼
Channel<AudioTranscriptionTask>
   │
   ▼
TranscriptionWorker (Background-Task, max. N parallel)
   │
   ├─ ITranscriptionProvider.TranscribeAsync(...)
   │
   ▼
meta.md (transcript_status: done + Transcript appended)
```

### Provider-Auswahl (Martin-Direktive 2026-07-07 Update 3)

**Martin:** _„Beide provider implementieren. Auswahl in settingsdialog."_

**Beide Provider werden parallel implementiert**, Auswahl erfolgt zur
Laufzeit via `TranscriptionConfig.Provider` (Settings-Dialog). User
kann ohne Code-Änderung wechseln.

| Provider | Lokal? | Diarization | API-Key | Kosten (ca.) | Sprache | Implementierung |
| - | - | - | - | - | - | - |
| **Azure Speech** (Cognitive Services) | nein | ja (nativ) | ja (Azure-Key + Region) | ~$1/h Audio | Multi | `AzureSpeechTranscriptionProvider.cs` |
| **Deepgram** (Nova-2) | nein | ja (nativ) | ja (Deepgram-Key) | ~$0.26/h (Pay-as-you-go) | Multi | `DeepgramTranscriptionProvider.cs` |

**Verworfen (gegenüber Spec 0013 v0.3 Entwurf):**
- WhisperX lokal (Martin-Direktive)
- faster-whisper (kein Diarization ohne pyannote)
- OpenAI Whisper API (kein Diarization)

### Provider-Selection via Settings

Im `SettingsDialog` unter neuem Tab **„Transcription"** (analog Audio-Tab):

```
┌─ Transcription ───────────────────────────────────────────────────┐
│                                                                    │
│  Provider                                                          │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ ▼ Azure Speech (Cognitive Services)                          │  │
│  │   Deepgram (Nova-2)                                          │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  API-Key                                                           │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ ************************************************************ │  │
│  └──────────────────────────────────────────────────────────────┘  │
│  [ Test-Connection ]                                               │
│                                                                    │
│  Azure-spezifisch                                                  │
│  Region:    [ westeurope ▼ ]                                       │
│                                                                    │
│  Deepgram-spezifisch                                               │
│  Endpoint:  [ https://api.deepgram.com ▼ ]                         │
│                                                                    │
│  Allgemein                                                         │
│  Default-Sprache:    [ deu ▼ ]                                     │
│  Max-Speakers:       [ 8 ▼ ]                                       │
│                                                                    │
│  [ Speichern ]  [ Abbrechen ]                                      │
└────────────────────────────────────────────────────────────────────┘
```

**Test-Connection-Button:** Sendet ein 1-Sekunden-Silent-Audio-Sample
an den konfigurierten Provider und prüft HTTP-200 + Diarization-Feld in
Response. Funktioniert unabhängig von `transcription.enabled`.

### Provider-Implementierungs-Details

#### Azure Speech

- **SDK**: `Microsoft.CognitiveServices.Speech` NuGet (offizielles Microsoft-Paket)
- **Endpoint**: `https://{region}.api.cognitive.microsoft.com/`
- **Auth**: API-Key im Header `Ocp-Apim-Subscription-Key`
- **API-Call**: `SpeechConfig.FromSubscription(key, region)` →
  `AudioConfig.FromWavFileInput(combinedStereoPath)` (siehe §5.4 Stereo-Concatenation) →
  `SpeechRecognizer.RecognizeOnceAsync()` mit `SpeakerDiarization` enabled
- **Response-Format**: `SpeechRecognitionResult` mit `SpeakerId` pro Word
- **Segmentierung**: Gruppierung aufeinanderfolgender Words mit gleichem `SpeakerId`
- **Fehler**: HTTP 401 → Invalid-Key-Marker; HTTP 429 → Rate-Limit-Backoff

#### Deepgram

- **SDK**: Kein offizielles .NET-SDK → direkter REST-Call via `HttpClient`
- **Endpoint**: `https://api.deepgram.com/v1/listen`
- **Auth**: API-Key im Header `Authorization: Token {key}`
- **Query-Params**: `model=nova-2`, `language={lang}`, `diarize=true`, `smart_format=true`
- **Request-Body**: `combined-stereo.wav` als Multipart-File (siehe §5.4 Stereo-Concatenation)
- **Response-Format**: JSON mit `results.utterances[]` (jedes Utterance hat `speaker`, `start`, `end`, `transcript`)
- **Fehler**: HTTP 401 → Invalid-Key-Marker; HTTP 429 → Rate-Limit-Backoff

### Dual-Provider-Tests

- Provider-Selection-Tests (~6): Default, JSON-Deserialisierung, ungültiger Provider → Fallback auf Default
- AzureSpeechTranscriptionProvider-Tests (~8): Mock-Response, Diarization-Parse, Fehler-Pfade, Retry
- DeepgramTranscriptionProvider-Tests (~8): Mock-HTTP-Response, Utterance-Parse, Fehler-Pfade, Retry
- SettingsDialog-Transcription-Tab-Tests (~5): Provider-Liste, Test-Connection-Button, Validation, Save-Reload

### §5.4 Stereo-Concatenation (Multi-Task-tauglich)

**Martin-Direktive 2026-07-07 Update 4:** _„Beachte, dass der background worker
auch parallel multi tasking laufen kann. Also kein fixer dateiname im temp ordner."_

**Architektur:** Das kombinierte Stereo-File wird im **Meeting-Ordner** (nicht
im OS-Temp) abgelegt — pro Task ein eigener Ordner, kein Collision-Risiko:

```
%APPDATA%/AiRecall/audio/yyyy-MM-dd/HHmmss-{meetingIdShort}/
  mic.wav                       # dauerhaft persistiert
  loopback.wav                  # dauerhaft persistiert
  combined-stereo.wav           # transient, gelöscht nach Transkription
  meta.md                       # dauerhaft persistiert
```

**Lifecycle von `combined-stereo.wav`:**
- **Create** im Meeting-Ordner vor ASR-Call (Worker iteriert Mono → Stereo)
- **Delete** nach erfolgreicher Transkription (im `finally`-Block des Worker-Tasks)
- **Keep on Failure** bei Worker-Fehler (Debug-Evidence) — User löscht manuell oder
  Cleanup-Job räumt nach 7 Tagen auf

**Concurrency-Garantie:**
- `TranscriptionWorker` hat `Channel<AudioTranscriptionTask>` + max-N parallel
- Jeder Task hat eigenen Meeting-Ordner (`meetingIdShort` ist SHA256-basiert eindeutig)
- Datei-Konflikt ausgeschlossen (kein fixer Dateiname im shared Temp)

**Algorithmus (Pseudo-Code):**

```csharp
public sealed class StereoConcatenator
{
    /// <summary>
    /// Liest mic.wav (Mono) und loopback.wav (Mono), schreibt
    /// combined-stereo.wav (Stereo: links=mic, rechts=loopback) in den
    /// selben Meeting-Ordner.
    /// </summary>
    public string Concatenate(MeetingRecordingPaths paths)
    {
        var stereoPath = Path.Combine(paths.Folder, "combined-stereo.wav");
        using var mic = new WaveFileReader(paths.MicPath);
        using var loop = new WaveFileReader(paths.LoopbackPath);

        // Validation: gleiche Sample-Rate, gleiche Länge, beide Mono
        if (mic.WaveFormat.SampleRate != loop.WaveFormat.SampleRate)
            throw new InvalidOperationException($"Sample-Rate mismatch: mic={mic.WaveFormat.SampleRate}, loop={loop.WaveFormat.SampleRate}");
        if (mic.WaveFormat.Channels != 1 || loop.WaveFormat.Channels != 1)
            throw new InvalidOperationException("Both files must be mono PCM-16");
        if (mic.SampleCount != loop.SampleCount)
            throw new InvalidOperationException($"Length mismatch: mic={mic.SampleCount}, loop={loop.SampleCount}");

        var stereoFormat = WaveFormat.CreateIeeeFloatWaveFormat(mic.WaveFormat.SampleRate, 2);
        using var writer = new WaveFileWriter(stereoPath, stereoFormat);

        var micBuf = new float[mic.WaveFormat.SampleRate / 10];   // 100 ms chunks
        var loopBuf = new float[mic.WaveFormat.SampleRate / 10];
        int read;
        while ((read = mic.Read(micBuf, 0, micBuf.Length)) > 0)
        {
            loop.Read(loopBuf, 0, read);
            for (int i = 0; i < read; i++)
            {
                // Left = mic, Right = loopback
                writer.WriteSample(micBuf[i]);
                writer.WriteSample(loopBuf[i]);
            }
        }
        return stereoPath;
    }
}
```

**Stereo-Concatenation-Tests (~6):**
- Concatenate_ValidMonoFiles_ProducesStereo
- Concatenate_SampleRateMismatch_Throws
- Concatenate_ChannelsMismatch_Throws
- Concatenate_LengthMismatch_Throws
- Concatenate_PreservesAudioContent (Bit-genau-Round-Trip-Test)
- Concatenate_ParallelTasks_NoFilenameCollision (zwei parallele Tasks auf
  unterschiedlichen Meeting-Ordnern, kein File-Lock-Error)

### §5.5 Cross-Channel-Correlation (Local-vs-Remote-Mapping)

Nach Provider-Diarization (S0, S1, S2, ...) wird mit Hilfe des
Cross-Channel-RMS-Verhältnisses der **lokale User** identifiziert und
die übrigen Speaker als `Remote-1`, `Remote-2`, ... gelabelt.

**Grundidee:**
- Lokaler User spricht ins Mikrofon → Mic-Kanal hat **hohe** Energie
- Remote-User sprechen → nur Loopback-Kanal hat **hohe** Energie
- Loopback nimmt System-Audio auf, das alle Stimmen (lokal + remote) enthält

**RMS-Verhältnis-Formel (pro Zeitfenster w, z. B. 100 ms):**

```
RMS(w)         = sqrt( (1/N) · Σ_{i ∈ w} x[i]² )
r(w)           = RMS_mic(w) / ( RMS_mic(w) + RMS_loopback(w) + ε )
```

- `N` = Anzahl Samples im Fenster (1600 bei 16 kHz / 100 ms)
- `ε = 1e-9` als Schutz gegen Division durch 0
- `r ∈ [0, 1]`

**Intuition (Beispiel-Szenarien):**

| Szenario | RMS_mic | RMS_loopback | r | Klassifikation |
| - | - | - | - | - |
| Lokaler User spricht | 0.20 | 0.08 (Playback) | **0.71** | Mic dominiert → Local |
| Remote-User Bob spricht | 0.005 (still) | 0.15 | **0.03** | Loopback dominiert → Remote |
| Beide gleichzeitig | 0.18 | 0.14 | **0.56** | Übergang — Provider-Diarization entscheidet |
| Stille | 0.003 | 0.003 | ~0.5 | undefiniert (VAD-Filter) |

**Schwellwerte (MVP 3):**
- `r > 0.6` → Mic dominiert → Segment von `localSpeaker` als „Local"
- `r < 0.4` → Loopback dominiert → Segment von Remote-Speaker als „Remote-N"
- `0.4 ≤ r ≤ 0.6` → Übergang, Provider-Diarization entscheidet allein

**Algorithmus (Pseudo-Code):**

```csharp
public sealed class SpeakerRoleAssigner
{
    /// <summary>
    /// Mappt Provider-Speaker-IDs auf Local/Remote-N via
    /// Cross-Channel-RMS-Verhältnis.
    /// </summary>
    public SpeakerRoleMap AssignRoles(
        IReadOnlyList<TranscriptionSegment> segments,
        string micPath,
        string loopbackPath)
    {
        // 1. RMS-Verhältnis pro Zeitfenster berechnen (100 ms)
        var ratios = ComputeRmsRatios(micPath, loopbackPath, windowMs: 100);

        // 2. Pro Segment: avg(r) über Segment-Zeitfenster
        var segmentAvgRatio = segments.ToDictionary(
            s => s,
            s => AverageRatioForSegment(ratios, s.Start, s.End));

        // 3. Pro Provider-Speaker: avg(r) über alle seine Segmente
        var speakerAvgRatio = segments
            .GroupBy(s => s.Speaker)
            .ToDictionary(
                g => g.Key,
                g => g.Average(s => segmentAvgRatio[s]));

        // 4. Höchstes avg(r) → Local
        var localSpeakerId = speakerAvgRatio
            .OrderByDescending(kv => kv.Value)
            .First().Key;

        // 5. Andere sortiert nach Häufigkeit → Remote-1, Remote-2, ...
        var remoteSpeakers = speakerAvgRatio
            .Where(kv => kv.Key != localSpeakerId)
            .OrderByDescending(kv => segments.Count(s => s.Speaker == kv.Key))
            .Select((kv, i) => new KeyValuePair<string, string>(kv.Key, $"Remote-{i + 1}"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new SpeakerRoleMap(localSpeakerId, remoteSpeakers);
    }

    private static double[] ComputeRmsRatios(string micPath, string loopPath, int windowMs)
    {
        using var mic = new WaveFileReader(micPath);
        using var loop = new WaveFileReader(loopPath);
        int windowSamples = mic.WaveFormat.SampleRate * windowMs / 1000;
        var micBuf = new float[windowSamples];
        var loopBuf = new float[windowSamples];
        var ratios = new List<double>();

        int read;
        while ((read = mic.Read(micBuf, 0, micBuf.Length)) > 0)
        {
            loop.Read(loopBuf, 0, read);
            double rmsMic = Math.Sqrt(micBuf.Take(read).Average(s => s * s));
            double rmsLoop = Math.Sqrt(loopBuf.Take(read).Average(s => s * s));
            double r = rmsMic / (rmsMic + rmsLoop + 1e-9);
            ratios.Add(r);
        }
        return ratios.ToArray();
    }
}

public sealed record SpeakerRoleMap(
    string LocalSpeakerId,
    IReadOnlyDictionary<string, string> RemoteSpeakerIds  // "S1" → "Remote-1", ...
);
```

**Output-MD-Format (nach Rolle-Lookup):**

```markdown
**[00:00:00 → 00:00:03] Local:** Hallo zusammen
**[00:00:03 → 00:00:06] Remote-1:** Hi, ich bin Bob
**[00:00:06 → 00:00:10] Local:** Schön dich zu sehen
```

**Edge Cases (akzeptiert in MVP 3, v0.4 mit VAD/Frequency-Analysis):**
- Lokaler User auf Mute, Stimme echot im System-Audio → Mic still, Loopback hat Stimme → r ≈ 0, fälschlich als Remote klassifiziert
- Hintergrund-Musik auf Loopback → fälschlich als Remote klassifiziert
- Simultanes Sprechen beider Seiten → r mittig, Provider-Diarization entscheidet

**Cross-Channel-Correlation-Tests (~12):**
- ComputeRmsRatios_SilentAudio_ReturnsMidpoint (r ≈ 0.5)
- ComputeRmsRatios_MicOnly_ReturnsHighRatio (r > 0.7)
- ComputeRmsRatios_LoopbackOnly_ReturnsLowRatio (r < 0.2)
- ComputeRmsRatios_BothActive_ReturnsMidRatio (r ≈ 0.5)
- ComputeRmsRatios_DifferentSampleRates_Throws
- AssignRoles_LocalUserSpeaks_Most_IdentifiesLocal (avg r ≈ 0.7 → Local)
- AssignRoles_RemoteUserSpeaks_Only_IdentifiesRemote (avg r ≈ 0.03 → Remote-N)
- AssignRoles_MultipleSpeakers_Remote1AndRemote2 (3 Speaker, einer lokal, zwei remote)
- AssignRoles_NoSpeech_AllUndefined (alle r undefiniert, kein Local)
- AssignRoles_DurationMapping_CorrectWindowAlignment (Segment vs Window)
- AssignRoles_PersistenceFailure_Throws
- AssignRoles_PerformanceTest_OneHourMeeting (60 min Audio in <5 s verarbeitet)

### Provider-Interface

```csharp
public interface ITranscriptionProvider
{
    string Name { get; }                  // "azure-speech" | "deepgram"
    Task<TranscriptionResult> TranscribeAsync(
        string micPath,
        string loopbackPath,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record TranscriptionOptions(
    string Language,           // ISO-639-2 (deu, eng, ...)
    bool DiarizationRequired,  // immer true in MVP 3
    int MaxSpeakers,           // Default 8
    string ApiKey,             // User-Config (verschlüsselt at rest)
    string? EndpointOverride   // für Sovereign Clouds (Azure China/Germany)
);

public sealed record TranscriptionResult(
    IReadOnlyList<TranscriptionSegment> Segments,
    string ProviderName,
    TimeSpan AudioDuration,
    int SpeakerCount,
    string? LocalSpeakerId,                  // Provider-ID des Local-Users (z. B. "S1"); null wenn unklar
    IReadOnlyList<string> RemoteSpeakerIds,  // sortiert nach Häufigkeit, z. B. ["S2", "S3"]
    SpeakerRoleMap? RoleMap,                 // Cross-Channel-Correlation-Ergebnis (siehe §5.5)
    string? ErrorMessage                     // null bei Erfolg
);

public sealed record TranscriptionSegment(
    string Speaker,            // "S1", "S2", "S3" (kein Mapping in v0.3)
    TimeSpan Start,
    TimeSpan End,
    string Text
);

public sealed record TranscriptionProgress(
    int PercentComplete,       // 0-100
    string? CurrentStep        // "Loading model", "Transcribing mic", "Diarizing", ...
);
```

### Worker-Pattern (analog Spec 0007 `ConversionWorker`)

```csharp
public sealed class TranscriptionWorker : IAsyncDisposable
{
    private readonly Channel<AudioTranscriptionTask> _queue;
    private readonly ITranscriptionProvider _provider;
    private readonly ILogger _logger;

    public void Enqueue(AudioTranscriptionTask task);
    public Task StopAsync(CancellationToken ct);
}

public sealed record AudioTranscriptionTask(
    string MeetingFolder,          // ".../090000-a1b2c3d4/"
    string MicPath,
    string LoopbackPath,
    string MetadataPath,
    TranscriptionOptions Options,
    DateTimeOffset EnqueuedAt
);
```

## 6. Diarization (Pflicht)

**Entscheidung:** Diarization ist in MVP 3 nicht optional. Provider, der kein
Diarization liefert, wird abgelehnt (Worker markiert `transcript_status: failed`
mit Begründung „provider does not support diarization").

**Azure Speech** und **Deepgram** liefern beide Diarization nativ — kein Custom-Modell
nötig. Konfiguration in `TranscriptionConfig.MaxSpeakers`.

### Speaker-Labels

- v0.3: Speaker werden mit `"S1"`, `"S2"`, ... benannt (anonyme IDs)
- Reale Namen (z. B. „Alice", „Bob"): **Out-of-Scope v0.3**, v0.4 über Outlook-Kalender
  + Contact-Match (siehe §1 Ausbaustufe v0.4)

### Max-Speakers-Cap

- Default: `MaxSpeakers = 8`
- Konfigurierbar in `TranscriptionConfig.MaxSpeakers`
- Cap verhindert Endlos-Splits bei Hintergrund-Geräuschen (TV, Musik)

### Output-Format im MD

```markdown
## Transkription

**Provider:** azure-speech
**Speaker:** 3 (S1, S2, S3)
**Sprache:** deu

**[00:00:12 → 00:00:18] S1:** Hallo, können alle mich hören?

**[00:00:19 → 00:00:24] S2:** Ja, ich bin dabei.

**[00:00:25 → 00:00:42] S1:** Gut, dann fangen wir an mit dem Daily Standup.
```

## 7. Transkription in MD-Datei schreiben

**Entscheidung:** Append-Section **nach** dem `## Audio-Files`-Block, **nicht** in
eine separate Datei (analog OCR-Pattern: Content wird in-place in die MD-Datei
geschrieben, siehe Bug-Bash I-17 Spec 0007).

### Update-Logik

```csharp
public sealed class MetadataUpdater
{
    /// <summary>
    /// Appended die Transkriptions-Sektion an meta.md und aktualisiert
    /// das transcript_status-Feld im Frontmatter.
    /// </summary>
    public async Task FinalizeAsync(
        string metadataPath,
        TranscriptionResult result,
        CancellationToken ct);
}
```

### Atomarität

- Frontmatter-Update + Append in **einer** Datei-Operation (single-write)
- Falls Write fehlschlägt → `transcript_status: failed` und Error in Sektion
- Falls MD bereits existiert mit `transcript_status: done` → kein Re-Run
  (Idempotenz-Garantie, analog ConversionWorker Bug-Bash I-17)

### Konflikt mit laufender Aufnahme

Während das Meeting noch läuft, schreibt der Worker **nicht** in `meta.md`.
Erst nach `MeetingEnded` finalisiert der Worker die MD-Datei. Während der
Aufnahme wird nur das initiale Frontmatter geschrieben.

## Komponenten-Plan

```
src/AiRecall.Core/Audio/                    (NEU)
  IAudioRecorder.cs
  WasapiAudioRecorder.cs                    # NAudio-basierte Implementierung
  IAudioDeviceProvider.cs
  AudioDeviceProvider.cs                    # MMDeviceEnumerator-Wrapper
  AudioDeviceInfo.cs                        # record
  StereoConcatenator.cs                     # Mono → Stereo (Update 4)

src/AiRecall.Core/Transcription/            (NEU)
  ITranscriptionProvider.cs
  TranscriptionResult.cs
  TranscriptionSegment.cs
  TranscriptionOptions.cs
  TranscriptionProgress.cs
  TranscriptionQueue.cs                     # Channel<>-Wrapper

src/AiRecall.AppReader.Teams/               (ERWEITERT, Spec 0011)
  TeamsAppReader.cs                         # +MeetingStateChanged-Event + IsActive-Tracking
  MeetingStateChangedEventArgs.cs           # NEU

src/AiRecall.Conversion/Transcription/      (NEU)
  TranscriptionWorker.cs                    # analog ConversionWorker
  AudioTranscriptionTask.cs
  SpeakerRoleAssigner.cs                    # Cross-Channel-Correlation (Update 4)
  SpeakerRoleMap.cs                         # Record Local/Remote-Mapping

src/AiRecall.TrayApp/Audio/                 (NEU)
  AudioRecorderSession.cs                   # Orchestrator: Detection → Recording → MD → Worker
  AudioTab.cs                               # SettingsDialog-Tab
  AudioRecorderSessionFactory.cs            # TriggerSupervisor-Bindeglied

src/AiRecall.Conversion/Providers/          (NEU, beide Implementierungen)
  AzureSpeechTranscriptionProvider.cs       # Microsoft.CognitiveServices.Speech SDK
  DeepgramTranscriptionProvider.cs          # REST + HttpClient

src/AiRecall.TrayApp/Transcription/         (NEU)
  TranscriptionTab.cs                       # SettingsDialog-Tab (Provider-Auswahl)
```

**Verworfen (gegenüber Spec 0013 v0.3 Entwurf):**
- `src/AiRecall.Trigger/MeetingDetector/IMeetingDetector.cs`
- `src/AiRecall.Trigger/MeetingDetector/TeamsMeetingDetector.cs`
- `src/AiRecall.Trigger/MeetingDetector/MeetingEvent.cs`

Diese Funktionalität ist jetzt vollständig in `TeamsAppReader` (Spec 0011).

## Konfiguration — Übersicht

| Section | Feld | Default | Zweck |
| - | - | - | - |
| `audio` | `enabled` | `false` | Master-Switch (Default false = Privacy-First, kein versehentliches Recording beim Erst-Start) |
| `audio` | `micDeviceId` | `""` | Mikrofon (leer = Default) |
| `audio` | `loopbackDeviceId` | `""` | Speaker-Loopback (leer = Default) |
| `audio` | `sampleRate` | `16000` | Hz |
| `audio` | `bitsPerSample` | `16` | PCM-16 |
| `audio` | `storageRoot` | `"audio"` | Relativ zu AppContext.BaseDirectory |
| `transcription` | `enabled` | `false` | Master-Switch |
| `transcription` | `provider` | `"azure-speech"` | azure-speech \| deepgram (Auswahl via Settings-Tab „Transcription") |
| `transcription` | `providerApiKey` | `""` | API-Key für Cloud-Provider (in `%APPDATA%` als Klartext — OS-Verschlüsselung ausreichend, Martin-Direktive 2026-07-07) |
| `transcription` | `defaultLanguage` | `"deu"` | ISO-639-2 |
| `transcription` | `diarizationRequired` | `true` | Pflicht, hart gesetzt |
| `transcription` | `maxSpeakers` | `8` | Cap gegen Endlos-Splits |
| `appReader.teams` | `autoRecordMeetings` | `true` | Trigger-Switch |
| `appReader.teams` | `minMeetingDurationSeconds` | `30` | Mindestlänge, sonst verworfen |

## Tests (TDD-Plan, Ziel MVP 3: +X Tests, aktuell 674)

1. **AudioRecorder-Tests** (~15)
   - Start/Stop, File-Layout, Device-Selection, Cancellation,
     Resource-Disposal, Sample-Rate-Konfiguration
2. **AudioDeviceProvider-Tests** (~8)
   - Enumerate Input/Loopback, Default-Device, Unknown-Device-Handling
3. **Teams-App-Reader-Erweiterung-Tests** (~8)
   - `MeetingStateChanged` Event feuert bei Title-Wechsel `Meeting |` ⇄ anderes
   - `ChatIdShort` ist deterministisch
   - Idempotenz: doppelter `IsActive=true` ignoriert
4. **TriggerSupervisor-Audio-Wiring-Tests** (~6)
   - Event-Subscription, Start/End der AudioRecorderSession,
     Debounce 30 s, Re-Init bei Supervisor-Restart
5. **TranscriptionWorker-Tests** (~12)
   - Queue-Dispatch, Provider-Selection, Cancellation,
     Error-Handling, MD-Finalisierung (Idempotenz)
6. **Transcription-Provider-Stub-Tests** (~6)
   - Fake-Provider mit `ITranscriptionProvider`, Result-Format,
     Diarization-Validation (Provider ohne Diarization → reject)
7. **MD-Generator-Tests** (~8)
   - Initial-Frontmatter, Transcript-Append, Status-Updates,
     Atomarität (Temp-File + Move)
8. **SettingsDialog-Audio-Tab-Tests** (~5)
   - Device-Liste, Test-Button (auch bei `audio.enabled=false`),
     Validation, Save-Reload

Total: ~109 neue Tests (Ziel MVP 3 v0.3: 674 → **~783**)

## TBD / Offene Punkte (Martin-Entscheidung)

**Alle TBDs geklärt.** Siehe §Martin-Entscheidungen 2026-07-07.

Verbleibende externe Abhängigkeit (nicht TBD, sondern Roadmap):
- **Outlook-Kalender-Lookup** → v0.4-Spec, **erst nach v0.3-Abnahme**
  (Martin-Direktive 2026-07-07 Update 3: „V0.4 erst nach 0.3"). v0.3
  befüllt nur `topic`; v0.4 ergänzt `participants`, `description`,
  `calendar_appointment_id`, `organizer` per Outlook-COM-Suche.

## Martin-Entscheidungen 2026-07-07 (geklärt, nicht mehr offen)

| # | Thema | Entscheidung |
| - | - | - |
| 1 | Provider | **Beide implementiert**: Azure Speech **und** Deepgram, Auswahl via `TranscriptionConfig.Provider` im Settings-Dialog |
| 2 | Audio-Encoding | **PCM-16-WAV, Mono, 16 kHz** |
| 3 | Off-Hours-Block | **Immer aufnehmen** wenn Meeting erkannt (kein Nacht-/Wochenende-Skip) |
| 4 | Laptop-Mode (Battery) | **Keine Prüfung** (immer aufnehmen wenn möglich, OS killt ggf. selbst) |
| 5 | Disk-Quota | **Keine Prüfung** (User-Verantwortung, manuelles Aufräumen) |
| 6 | Encryption at Rest | **Keine App-seitige Verschlüsselung** (OS-Bitlocker/EFS ausreichend) |
| 7 | Kalender-Integration | **Ausbaustufe v0.4, erst nach v0.3-Abnahme** (Outlook-Kalender-Suche, Teilnehmer/Title/Description übernehmen) |
| 8 | Trigger-Robustheit | **Akzeptable-Lücken in v0.3** (Teams-Reload / Network-Drop = Recording stoppt, kein Re-Init) |

## Nicht-Ziele (MVP 3 explizit ausschließen)

- **Andere Meeting-Apps** (Zoom, Discord, Webex, Slack Huddles) — Folge-Cluster.
  Architektur erlaubt späteres Hinzufügen via weiterem App Reader.
- **Stereo-Mix-Container** — Diarization-Pipeline braucht separate Spuren.
- **Live-Transkription während Meeting** — nur Post-Meeting in v0.3.
- **Multi-Language Auto-Detect** — Default-Sprache pro Recording (`TranscriptionConfig.DefaultLanguage`).
- **Speaker-Mapping auf reale Namen** — v0.4 (über Outlook-Kalender + Contact-Match).
- **Audio-Pre-Processing** (Noise-Suppression, AGC, Echo-Cancellation) — v0.4.
- **Aufnahme-Indikator** (Tray-LED, Animation) — nice-to-have, nicht MVP.
- **Reine Audio-Spike-Detection** für Trigger — zu viele False-Positives.

## Verwandte Specs

- `specs/0005-trigger-pipeline.md` — TriggerEvent-Infrastruktur (wiederverwendet)
- `specs/0007-async-conversion.md` — ConversionWorker-Pattern (1:1 übernommen)
- `specs/0009-settings-dialog.md` — Settings-Tab-Foundation (AudioConfig-POCOs)
- `specs/0011-teams-app-reader.md` — **Trigger-Quelle** (TeamsChatKind.Meeting + IsMeeting)
- `specs/0012-tessdata-first-run.md` — Modal-Dialog-Stil (nicht direkt relevant)

## Update-Log

- **2026-07-07 (Update 4, nach Martin-Feedback)** — Stereo-Concatenation als
  Pre-Processing vor ASR-Call. `combined-stereo.wav` wird im Meeting-Ordner
  abgelegt (nicht im OS-Temp), pro Task eindeutig — Martin-Direktive
  „parallel multi tasking laufen kann, kein fixer dateiname im temp ordner".
  Cross-Channel-Correlation (RMS-Verhältnis) als Speaker-Role-Mapping nach
  Provider-Diarization, identifiziert lokalen User via
  `r(w) = RMS_mic / (RMS_mic + RMS_loopback + ε)`. Neue Komponenten
  `StereoConcatenator` + `SpeakerRoleAssigner`, Datenmodell erweitert um
  `LocalSpeakerId`/`RemoteSpeakerIds`/`SpeakerRoleMap`, ~18 neue Tests.
- **2026-07-07 (Update 3, nach Martin-Feedback)** — Beide Provider (Azure Speech
  + Deepgram) werden **parallel implementiert**, Auswahl im Settings-Dialog
  (neuer Tab „Transcription"). v0.4 Outlook-Kalender-Integration **explizit
  nach v0.3-Abnahme** verschoben. Provider-Sektion erweitert um
  Implementierungs-Details (Azure SDK vs. Deepgram REST), Settings-Tab-Layout
  und ~23 neue Dual-Provider-Tests.
- **2026-07-07 (Update 2, nach Martin-Feedback)** — Architektur grundlegend
  geändert: Trigger-Quelle ist jetzt der bestehende Teams App Reader (Spec 0011)
  via `TeamsChatKind.Meeting` + neues `MeetingStateChanged`-Event. Separater
  `IMeetingDetector`/`TeamsMeetingDetector` verworfen (Duplikation der
  Title-Parsing-Logik). 6 Martin-Entscheidungen angewandt (Provider-Wahl
  Azure-vs-Deepgram noch offen, Kalender-Integration auf v0.4 verschoben).
- **2026-07-07 (Update 1)** — Erstellt nach Martins Anforderungsliste (8 Punkte).
  Skeleton v0.3.