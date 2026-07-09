# 0013 — Audio Notes (MVP 3)

> **Status:** 🟢 **ABGESCHLOSSEN v0.3 (2026-07-09, Update 8 — TriggerSupervisor-Integration)**
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
  `PresenceChanged(IsActive=false)` innerhalb von 30 s kommt, wird
  der Recording-Ordner verworfen (kein WAV, keine MD, kein Worker-Task).

### Meeting-Anwesenheitserkennung — Polling (Update 8)

**Martin-Direktive 2026-07-07 Update 8:** _„Es muss auch automatisch erkannt
werden, wann ein Meeting endet, um die Aufnahme zu stoppen … Um das Meeting
ende zu erkennen muss regelmäßig nach dem Meeting Fenster gesucht werden."_

**Architektur:** Der **Polling-basierte `MeetingPresencePoller`** ist die
**alleinige Quelle** für `Start`/`Stop`-Signale in v0.3. Er ruft periodisch
`TeamsAppReader.TryGetActiveMeetingAsync(ct)` auf und vergleicht das Ergebnis
mit dem letzten bekannten Zustand (Edge-Detection: false→true = Started,
true→false = Ended). Nur bei **Zustandswechsel** wird ein Event gefeuert.

| Eigenschaft | Wert |
| - | - |
| Intervall | **5 s** (Default, konfigurierbar via `appReader.teams.presencePollIntervalSeconds`) |
| Thread | **Dedizierter Polling-Thread** (`Task.Run` beim `StartAsync`) |
| Quelle | `TeamsAppReader.TryGetActiveMeetingAsync(ct)` — liest CDP / UIA / Title |
| Trigger | **Edge-Detection** — Event feuert nur bei Zustandswechsel (nicht alle 5 s) |
| Logging | Jeder Poll + jeder Edge-Wechsel auf `Debug`-Level |

**Warum Polling und nicht Event-driven:**
- Events vom App Reader können verloren gehen (Teams-Reload, Network-Drop,
  UI-Crash, App-Hang)
- Polling ist **selbst-heilend** — nächster Poll sieht den aktuellen Zustand
- Polling-Intervall 5 s ist kurz genug, um max. 5 s Verzögerung beim Stop
  zu haben (akzeptabel für Audio-Aufnahme)
- Konfigurierbar, falls User höhere Latenz toleriert und CPU-Last reduzieren
  will (z. B. 10–30 s)

**Wiring in `TriggerSupervisor` (ersetzt Event-driven Detection):**

```csharp
public sealed class TriggerSupervisor : ITriggerService
{
    private readonly MeetingPresencePoller _poller;   // NEU (Update 8)
    private readonly AudioRecorderSessionFactory _audioFactory;

    public Task StartAsync(CancellationToken ct)
    {
        _poller.PresenceChanged += OnPresenceChanged;  // NICHT _teamsReader.MeetingStateChanged
        await _poller.StartAsync(ct);
        // ... existing trigger wiring
    }

    private void OnPresenceChanged(object? sender, MeetingPresenceStateChangedEventArgs e)
    {
        if (e.IsActive) _audioFactory.StartOrAttach(e);
        else _audioFactory.EndSession(e.ChatIdShort);
    }
}
```

**Neue Komponenten:**

```csharp
public sealed class MeetingPresencePoller : IAsyncDisposable
{
    private readonly TeamsAppReader _reader;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<MeetingPresenceStateChangedEventArgs>? PresenceChanged;

    public Task StartAsync(CancellationToken ct);
    public async Task StopAsync(CancellationToken ct);
}

public sealed record MeetingPresenceStateChangedEventArgs(
    bool IsActive,             // false -> true = Started, true -> false = Ended
    string? Topic,             // z. B. "Daily Standup" (aus Title-Parser)
    string? WindowTitle,       // Voll-Title für Diagnose
    string? ChatIdShort,       // SHA256(Title+Process+StartedAt) -> 8 hex
    DateTimeOffset DetectedAt
);
```

**`TeamsAppReader`-Erweiterung (Spec 0011):**

```csharp
public sealed class TeamsAppReader : IAppReader, IDisposable
{
    /// <summary>
    /// Synchroner Snapshot der aktuellen Meeting-Anwesenheit.
    /// true = Meeting-Fenster ist aktiv (Title hat "Meeting |"-Prefix ODER
    /// CDP zeigt aktiven Call), false = kein Meeting erkannt.
    /// </summary>
    public Task<MeetingPresenceSnapshot> TryGetActiveMeetingAsync(CancellationToken ct);
}

public sealed record MeetingPresenceSnapshot(
    bool IsActive,
    string? Topic,
    string? WindowTitle,
    string? ChatIdShort
);
```

**Status v0.3 vs. v0.4:**
- `MeetingStateChanged`-Event aus Spec 0011/Update 2 bleibt im Code, wird
  aber in v0.3 **nicht** vom `TriggerSupervisor` abonniert (Polling-only)
- v0.4 kann Event-driven Detection zusätzlich nutzen (für sofortige Reaktion,
  < 100 ms statt 5 s Polling-Latenz), Polling bleibt als Fallback

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

    /// <summary>Polling-Intervall fuer MeetingPresencePoller in Sekunden (Update 8).</summary>
    [Description("Polling-Intervall (Sekunden), in dem der MeetingPresencePoller das Teams-Fenster prueft. Niedrigere Werte = schnellere Stop-Erkennung, hoehere Werte = weniger CPU-Last. Default 5.")]
    [JsonPropertyName("presencePollIntervalSeconds")]
    public int PresencePollIntervalSeconds { get; set; } = 5;
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

### Recording-Lifecycle (Eigener Thread + Stop-Signal, Update 8)

**Martin-Direktive 2026-07-07 Update 8:** _„Generell sollte die Aufnahme in
einem eigenen thread laufen, bis das stopp Signal kommt. Dann werden die
Audio Dateien geschrieben und die md datei verlinkt beide. Der background
worker liest diese dann später ein und startet das transkript."_

**Threading-Modell:**

| Thread | Verantwortlich |
| - | - |
| UI-Thread | TrayApp, Settings, User-Interaktion |
| **Polling-Thread** (dediziert) | `MeetingPresencePoller` Polling-Loop (5 s) |
| **NAudio-Capture-Thread Mic** | `WasapiCapture` intern (Audio-Callback) |
| **NAudio-Capture-Thread Loopback** | `WasapiLoopbackCapture` intern (Audio-Callback) |
| **Recording-Coordinator-Thread** | `RecordingSession.RecordLoopAsync` (Buffer-Sammlung) |
| Background-Worker-Thread | `TranscriptionWorker` (eine Task pro Recording) |

Die Audio-Aufnahme läuft in **drei voneinander entkoppelten Threads**:
- NAudio erzeugt pro Capture-Stream einen eigenen Thread (Audio-Callback)
- `RecordingSession` läuft in eigenem Background-Task und sammelt Buffer

**Stop-Signal-Flow (Polling → Recording-Ende):**

```
Polling-Thread (alle 5 s)
  ↓ TeamsAppReader.TryGetActiveMeetingAsync()
  ↓ → IsActive = false
  ↓ PresenceChanged-Event feuert
  ↓
TriggerSupervisor.OnPresenceChanged
  ↓ _audioFactory.EndSession(chatIdShort)
  ↓
RecordingSession.StopAsync(ct)
  ↓ CancellationToken.Cancel()
  ↓
Recording-Coordinator-Thread
  ↓ Task.Delay wirft OperationCanceledException
  ↓ → NAudio StopRecording()
  ↓ → Buffer-Snapshots erstellen
  ↓ → WAV-Files schreiben (mic.wav, loopback.wav)
  ↓ → meta.md Status: recorded
  ↓
TranscriptionWorker.Enqueue(AudioTranscriptionTask)
  ↓ (Worker liest meta.md + Audio-Files asynchron)
```

**Wichtige Eigenschaften:**

- **Eigener Thread:** Aufnahme läuft NIEMALS auf UI-Thread oder Polling-Thread
- **Stop-Signal via CancellationToken:** sauber, kein Thread.Abort, keine Race-Conditions
- **File-Write beim Stop, nicht live:** keine Fragmentierung, eine WAV pro Meeting
- **MD-Stub wird beim Stop erzeugt:** verlinkt beide Audio-Files (`mic.wav`, `loopback.wav`)
- **Background-Worker liest MD-Stub später:** entkoppelt Recording von Transkription

**File-Layout beim Recording-Stop:**

```
%APPDATA%/AiRecall/audio/yyyy-MM-dd/HHmmss-{meetingIdShort}/
  mic.wav                       # erzeugt beim Stop, dauerhaft persistiert
  loopback.wav                  # erzeugt beim Stop, dauerhaft persistiert
  meta.md                       # erzeugt beim Stop mit Status "recorded"
  # combined-stereo.wav wird HIER transient erzeugt (Update 4),
  # NICHT im OS-Temp (Martin-Direktive: "kein fixer dateiname im temp ordner")
```

**RecordingSession-API:**

```csharp
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _recordingTask;

    public string MeetingIdShort { get; }
    public string? Folder { get; private set; }
    public RecordingState State { get; private set; }   // Created | Recording | Recorded | Failed

    /// <summary>
    /// Startet die Aufnahme im eigenen Background-Thread.
    /// Schreibt initiales meta.md mit Status "recording".
    /// </summary>
    public void Start(DateTimeOffset startedAt, string topic, string windowTitle);

    /// <summary>
    /// Stoppt die Aufnahme (CancellationToken-basiert).
    /// Schreibt finale WAV-Files (mic.wav, loopback.wav) +
    /// aktualisiert meta.md auf Status "recorded".
    /// Gibt die Pfade zurück für TranscriptionWorker.
    /// </summary>
    public async Task<MeetingRecordingPaths> StopAsync();

    /// <summary>
    /// Force-Stop (User-Tray-Menu "Stop Recording" oder Crash).
    /// Wie StopAsync, aber ohne 30-s-Debounce.
    /// </summary>
    public async Task<MeetingRecordingPaths> ForceStopAsync();
}
```

**Buffer-Strategie:**

- NAudio `DataAvailable`-Event sammelt Bytes in `List<byte>` (in-Memory)
- Bei kurzen Meetings (< 1 h): komplett in RAM (~120 MB × 2 Streams = 240 MB max)
- Bei langen Meetings: ggf. auf Ring-Buffer mit Disk-Spilling ausweichen (v0.4)
- Beim Stop: `List<byte>.ToArray()` → `WaveFileWriter` schreibt einmalig WAV

**Idempotenz:**
- Doppelter `Start()` für gleiche `chatIdShort` → no-op (return existing Session)
- `StopAsync()` ohne `Start()` → wirft `InvalidOperationException`
- `ForceStopAsync()` während laufender Aufnahme → wie `StopAsync`, ohne Debounce

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

### Initialer Inhalt (direkt nach Meeting-Start, Update 8)

**Martin-Direktive 2026-07-07 Update 8:** _„Dann werden die Audio Dateien geschrieben
und die md datei verlinkt beide."_

`meta.md` wird beim **Recording-Start** (Status `recording`) und beim
**Recording-Stop** (Status `recorded`) **zweistufig** geschrieben:

**Stufe 1 — beim Start (Status `recording`):**

```markdown
---
schema: ai-recall/meeting-recording/v1
type: audio-meeting
status: recording              # recording → recorded → transcribed (Update 8)
meeting_id_short: a1b2c3d4
started_at: 2026-07-07T22:00:00+02:00
ended_at: null
duration_seconds: null
topic: Daily Standup
trigger_source: polling         # Update 8 (Polling als Trigger-Quelle)
mic_device: Microphone (Realtek High Definition Audio)
loopback_device: Speakers (Realtek High Definition Audio)
sample_rate: 16000
bits_per_sample: 16
audio_files: []                  # leer waehrend recording, befuellt beim Stop
transcript_status: pending       # pending | partial | done | failed
diarization: required
provider: ""                     # azure-speech | deepgram (nach Worker-Auswahl)
language: deu
participants: []                 # leer in v0.3, gefuellt in v0.4 via Outlook-Kalender
calendar_appointment_id: null    # null in v0.3, gefuellt in v0.4
worker_task_enqueued: false      # Update 8: wird beim Recording-Stop auf true gesetzt
---

# Meeting: Daily Standup

**Aufnahme-Status:** läuft seit 2026-07-07T22:00:00+02:00

## Teilnehmer

- (Liste wird in v0.4 per Outlook-Kalender-Lookup befüllt)

## Audio-Files

- *(Audio-Files werden beim Meeting-Ende geschrieben und hier verlinkt)*

## Transkription

Wird im Hintergrund verarbeitet, sobald das Meeting beendet ist.
```

**Stufe 2 — beim Stop (Status `recorded`, Audio-Links verlinkt):**

```markdown
---
schema: ai-recall/meeting-recording/v1
type: audio-meeting
status: recorded                # recording → recorded → transcribed (Update 8)
meeting_id_short: a1b2c3d4
started_at: 2026-07-07T22:00:00+02:00
ended_at: 2026-07-07T22:45:00+02:00
duration_seconds: 2700
topic: Daily Standup
trigger_source: polling
mic_device: Microphone (Realtek High Definition Audio)
loopback_device: Speakers (Realtek High Definition Audio)
sample_rate: 16000
bits_per_sample: 16
audio_files:                     # Update 8: verlinkt beim Recording-Stop
  - mic.wav
  - loopback.wav
transcript_status: pending       # Aenderung folgt durch Worker
diarization: required
provider: ""                     # wird durch Worker befuellt
language: deu
participants: []
calendar_appointment_id: null
worker_task_enqueued: true       # Update 8: true nach Enqueue im TranscriptionWorker
---

# Meeting: Daily Standup

**Aufnahme-Status:** beendet um 2026-07-07T22:45:00+02:00 (Dauer 45m)

## Teilnehmer

- (Liste wird in v0.4 per Outlook-Kalender-Lookup befüllt)

## Audio-Files

- Mikrofon: [`mic.wav`](mic.wav) (PCM 16 kHz Mono 16-bit, ~51,5 MB)
- Speaker-Loopback: [`loopback.wav`](loopback.wav) (PCM 16 kHz Mono 16-bit, ~51,5 MB)

## Transkription

*(wird vom Background-Worker verarbeitet — diese MD-Datei wird automatisch gefunden und aktualisiert)*
```

### `status`-Lifecycle (Update 8)

**Martin-Direktive 2026-07-07 Update 8:** _„Der background worker liest diese dann
später ein und startet das transkript."_

| Status | Wer setzt | Wann | Bedeutung |
| - | - | - | - |
| `recording` | `RecordingSession.Start` | Direkt nach Folder-Create + Initial-MD | Aufnahme läuft |
| `recorded` | `RecordingSession.StopAsync` | Nach File-Write (mic.wav + loopback.wav) | Audio persistiert, bereit für Worker |
| `transcribed` | `TranscriptionWorker` | Nach erfolgreicher Transkription | Transkriptions-Sektion vollständig |
| `failed` | `TranscriptionWorker` | Bei Worker-Fehler (Provider-Down, File-Corruption) | Fehlertext in `## Transcription`-Sektion |

**`worker_task_enqueued` (Update 8):**
- `false` während `recording`
- `true` ab `RecordingSession.StopAsync` nach Enqueue im `TranscriptionWorker`
- `TranscriptionWorker` sucht beim Start nach allen `meta.md` mit
  `status=recorded AND worker_task_enqueued=false` und enqueued diese als
  Recovery nach Crash/Restart (Defensive Idempotenz)

### Worker-Discovery (Update 8)

Der `TranscriptionWorker` findet seine Tasks auf zwei Wegen:

**Pfad 1 — Live-Enqueue (Hauptpfad):**
- `RecordingSession.StopAsync` ruft direkt `TranscriptionWorker.Enqueue(task)`
- Task wird sofort verarbeitet (wenn Worker-Slot frei)

**Pfad 2 — Recovery-Scan (Crash-Recovery):**
- Beim `TranscriptionWorker.StartAsync` (z. B. nach App-Restart)
- Scannt `audio/yyyy-MM-dd/*/meta.md` rekursiv
- Filter: `status=recorded AND worker_task_enqueued=false`
- Enqueued alle gefundenen Tasks (Idempotenz über `worker_task_enqueued`)

```csharp
public sealed class TranscriptionWorker : IAsyncDisposable
{
    public async Task ScanForPendingRecordingsAsync(CancellationToken ct)
    {
        var audioRoot = _config.StorageRoot;     // "audio"
        var pendingDirs = Directory.EnumerateDirectories(audioRoot, "*", SearchOption.AllDirectories)
            .Where(dir => File.Exists(Path.Combine(dir, "meta.md")))
            .Where(dir =>
            {
                var meta = ParseFrontmatter(Path.Combine(dir, "meta.md"));
                return meta.Status == "recorded" && meta.WorkerTaskEnqueued == false;
            });

        foreach (var dir in pendingDirs)
        {
            var meta = ParseFrontmatter(Path.Combine(dir, "meta.md"));
            Enqueue(new AudioTranscriptionTask(
                Folder: dir,
                MicPath: Path.Combine(dir, "mic.wav"),
                LoopbackPath: Path.Combine(dir, "loopback.wav"),
                MetadataPath: Path.Combine(dir, "meta.md"),
                Options: BuildOptionsFromConfig(),
                EnqueuedAt: DateTimeOffset.Now
            ));
            // Setze worker_task_enqueued=true (atomar mit Task-Enqueue)
            await SetWorkerTaskEnqueuedFlagAsync(Path.Combine(dir, "meta.md"), ct);
        }
    }
}
```

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
   ├─ StereoConcatenator.Concatenate(mic, loopback) ──▶ combined-stereo.wav  (transient)
   │
   ├─ ITranscriptionProvider.TranscribeAsync(combined-stereo.wav, ...)
   │       (Azure Speech oder Deepgram, Diarization im Provider)
   │
   ├─ MetadataUpdater.FinalizeAsync(meta.md, result)
   │
   ▼
meta.md (transcript_status: done + Transcript appended, rohe Provider-Speaker-IDs S0/S1/...)
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
- **Stereo-Handling** (Update 7): verarbeitet Stereo nativ, **kein interner Downmix**.
  Response enthält `ChannelId` pro Segment zusätzlich zu `SpeakerId`
  (Channel 0 = mic, Channel 1 = loopback). Wir mergen Channel+Speaker
  zu einem kombinierten Label im Output (z. B. „C0-S1" für Channel 0 / Speaker 1)
- **Response-Format**: `SpeechRecognitionResult` mit `ChannelId` + `SpeakerId` pro Word
- **Segmentierung**: Gruppierung aufeinanderfolgender Words mit gleichem
  `(ChannelId, SpeakerId)`-Tupel
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

### §5.4 Stereo-Concatenation (Multi-Task-tauglich, beide Kanäle erhalten)

**Martin-Direktive 2026-07-07 Update 4:** _„Beachte, dass der background worker
auch parallel multi tasking laufen kann. Also kein fixer dateiname im temp ordner."_
**Martin-Direktive 2026-07-07 Update 5:** _„Streiche das rms. Diarization macht der Provider."_
**Martin-Direktive 2026-07-07 Update 6:** _„Nutze weiterhin stereo mit beiden Kanälen."_

**Architektur:** Vor dem ASR-Call werden `mic.wav` (Mono) und `loopback.wav`
(Mono) zu **einem Stereo-WAV kombiniert** (links = mic, rechts = loopback).
Der kombinierte Stereo-Stream ist der einzige Audio-Input für den Provider;
**Diarization läuft komplett im Provider** (Azure Speech oder Deepgram),
wir machen **keine eigene Speaker-Analyse**.

**Begründung Stereo (statt Mono-Mix):**
- **Beide Kanäle bleiben erhalten** für Storage-Flexibilität (z. B. späteres
  Per-Channel-Re-Transkribieren, Audio-Pre-Processing auf Mic vs. Loopback,
  Debugging wenn Diarization schlecht ist)
- **Beide Provider (Azure Speech + Deepgram) bekommen das gleiche
  `combined-stereo.wav` als Input** — keine Provider-spezifischen
  Pre-Processing-Pfade, keine Mono-Konvertierung auf unserer Seite
- **Deepgram** verarbeitet Stereo mit `multichannel=true`-Parameter
  (pro Channel separate Diarization, kanalspezifische Speaker-IDs)
- **Azure Speech** verarbeitet Stereo nativ (kein interner Downmix);
  Response enthält `ChannelId` pro Segment zusätzlich zu `SpeakerId`
- v0.4-Mitigationen (VAD, Echo-Cancellation) können auf Stereo-Daten
  aufsetzen, ohne Recording-Format zu ändern

**File-Layout pro Meeting:**

```
%APPDATA%/AiRecall/audio/yyyy-MM-dd/HHmmss-{meetingIdShort}/
  mic.wav                       # dauerhaft persistiert
  loopback.wav                  # dauerhaft persistiert
  combined-stereo.wav           # transient, gelöscht nach Transkription
  meta.md                       # dauerhaft persistiert
```

**Lifecycle von `combined-stereo.wav`:**
- **Create** im Meeting-Ordner vor ASR-Call (Worker concat Mono → Stereo)
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
- Concatenate_ValidMonoFiles_ProducesStereo (Format-Validation: 2 Kanäle, korrekte Sample-Rate)
- Concatenate_SampleRateMismatch_Throws
- Concatenate_ChannelsMismatch_Throws
- Concatenate_LengthMismatch_Throws
- Concatenate_PreservesAudioContent (Bit-genau-Round-Trip-Test: Left-Channel = mic, Right-Channel = loopback)
- Concatenate_ParallelTasks_NoFilenameCollision (zwei parallele Tasks auf
  unterschiedlichen Meeting-Ordnern, kein File-Lock-Error)

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
    int SpeakerCount,                        // Anzahl unterschiedlicher Provider-Speaker-IDs
    IReadOnlyList<string> SpeakerLabels,     // z. B. ["S0", "S1", "S2"] — rohe Provider-IDs
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

## 6. Diarization (Pflicht, durch Provider)

**Martin-Direktive 2026-07-07 Update 5:** _„Streiche das rms. Diarization macht der Provider."_

**Architektur:** Diarization läuft **komplett im Provider** (Azure Speech oder
Deepgram). Wir machen **keine eigene Speaker-Analyse** auf unserer Seite —
kein RMS-Cross-Channel-Correlation, keine Local/Remote-Mapping.

**Provider-Auswahl-Kriterium:** Diarization ist nicht optional. Provider, der
kein Diarization liefert, wird abgelehnt (Worker markiert
`transcript_status: failed` mit Begründung „provider does not support
diarization"). **Azure Speech** und **Deepgram** liefern beide Diarization
nativ — kein Custom-Modell nötig.

### Speaker-Labels (aus Provider, roh)

- Provider liefert Speaker-IDs direkt: `S0`, `S1`, `S2`, ... (anonyme IDs)
- Wir geben diese **roh** in der MD aus — keine Local/Remote-N Unterscheidung
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
**Speaker:** 3 (S0, S1, S2)
**Sprache:** deu

**[00:00:12 → 00:00:18] S0:** Hallo, können alle mich hören?

**[00:00:19 → 00:00:24] S1:** Ja, ich bin dabei.

**[00:00:25 → 00:00:42] S0:** Gut, dann fangen wir an mit dem Daily Standup.
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
src/AiRecall.Trigger/                       (NEU, Update 8)
  MeetingPresencePoller.cs                  # Polling-Loop (5s) auf TeamsAppReader
  MeetingPresenceStateChangedEventArgs.cs   # Polling-Edge-Event

src/AiRecall.Core/Audio/                    (NEU)
  IAudioRecorder.cs
  WasapiAudioRecorder.cs                    # NAudio-basierte Implementierung
  IAudioDeviceProvider.cs
  AudioDeviceProvider.cs                    # MMDeviceEnumerator-Wrapper
  AudioDeviceInfo.cs                        # record
  RecordingSession.cs                       # NEU (Update 8): eigener Thread + Stop + File-Write
  MeetingRecordingPaths.cs                  # NEU (Update 8): Folder + WAV-Pfade
  StereoConcatenator.cs                     # Mono + Mono → Stereo (Update 6, ersetzt MonoMixer aus Update 5)

src/AiRecall.Core/Transcription/            (NEU)
  ITranscriptionProvider.cs
  TranscriptionResult.cs
  TranscriptionSegment.cs
  TranscriptionOptions.cs
  TranscriptionProgress.cs
  TranscriptionQueue.cs                     # Channel<>-Wrapper

src/AiRecall.AppReader.Teams/               (ERWEITERT, Spec 0011 + Update 8)
  TeamsAppReader.cs                         # +TryGetActiveMeetingAsync (Update 8) + MeetingStateChanged-Event
  MeetingPresenceSnapshot.cs                # NEU (Update 8): Sync-Snapshot fuer Polling
  MeetingStateChangedEventArgs.cs           # bestehend aus Spec 0011/Update 2

src/AiRecall.Conversion/Transcription/      (NEU)
  TranscriptionWorker.cs                    # analog ConversionWorker + ScanForPendingRecordings (Update 8)
  AudioTranscriptionTask.cs
  MetaMdLifecycleManager.cs                 # NEU (Update 8): status-Updates + Audio-Links
  MetaMdFrontmatter.cs                      # NEU (Update 8): record
  # SpeakerRoleAssigner und SpeakerRoleMap ENTFALLEN (Update 5: Provider macht Diarization)

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

Diese Funktionalität ist jetzt vollständig in `TeamsAppReader` (Spec 0011) +
`MeetingPresencePoller` (Update 8).

**Verworfen (gegenüber Update 2):**
- Event-driven `MeetingStateChanged` als v0.3-Trigger → ersetzt durch Polling
  (Update 8), Event bleibt im Code, wird in v0.3 NICHT vom `TriggerSupervisor`
  abonniert

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
| `appReader.teams` | `presencePollIntervalSeconds` | `5` | Update 8: Polling-Intervall für MeetingPresencePoller |

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
9. **MeetingPresencePoller-Tests (Update 8)** (~6)
   - Polling-Loop feuert `PresenceChanged` bei Edge-Detection (false→true, true→false)
   - Kein Event bei stabilem Zustand (alle 5 s „true" → kein erneutes Event)
   - Polling-Intervall wird korrekt eingehalten (FakeTimer)
   - Exception in `TeamsAppReader` wird gefangen, Loop läuft weiter
   - StopAsync bricht Loop sauber ab
   - Mehrere Edge-Wechsel in Folge feuern mehrere Events
10. **RecordingSession-Tests (Update 8)** (~7)
    - Start erstellt Meeting-Folder + initiale `meta.md` mit Status `recording`
    - Stop schreibt finale WAV-Files + setzt Status `recorded` + `audio_files`-Liste
    - Buffer-Sammlung: simulierte Audio-Bytes landen in korrekten Files
    - Force-Stop ohne Debounce (auch innerhalb 30 s)
    - Doppelter Start für gleiche `chatIdShort` → no-op (Idempotenz)
    - StopAsync ohne Start → `InvalidOperationException`
    - Dispose bricht laufende Aufnahme ab und gibt Resourcen frei
11. **MetaMdLifecycleManager-Tests (Update 8)** (~5)
    - Status-Übergang `recording` → `recorded` schreibt korrektes Frontmatter
    - Audio-Links werden beim `recorded`-Übergang eingefügt
    - `worker_task_enqueued`-Flag wird atomar mit Task-Enqueue gesetzt
    - Recovery-Scan findet alle `status=recorded AND worker_task_enqueued=false` Tasks
    - Idempotenz: doppeltes Enqueue für gleichen Task wird verhindert

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
| 9 | Meeting-Ende-Erkennung (Update 8) | **Polling alle 5 s** (`MeetingPresencePoller`) — Edge-Detection, Event nur bei Zustandswechsel |
| 10 | Recording-Lifecycle (Update 8) | **Eigener Thread + Stop-Signal via CancellationToken + MD-Stub beim Stop mit Audio-Links + Worker liest MD-Stub später** |

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

- **2026-07-07 (Update 8, nach Martin-Feedback)** — **„Es muss auch automatisch
  erkannt werden, wann ein Meeting endet, um die Aufnahme zu stoppen.
  Generell sollte die Aufnahme in einem eigenen thread laufen, bis das
  stopp Signal kommt. Dann werden die Audio Dateien geschrieben und die
  md datei verlinkt beide. Der background worker liest diese dann später
  ein und startet das transkript. Um das Meeting ende zu erkennen muss
  regelmäßig nach dem Meeting Fenster gesucht werden."**

  Architektonische Erweiterung um **Polling-basierte Anwesenheitserkennung**
  + **Recording-Lifecycle mit eigenem Thread + Stop-Signal + MD-Stub-Pattern**.

  **Was neu ist:**
  - **§1 erweitert um „Polling-basierte Meeting-Anwesenheitserkennung"** —
    Neuer `MeetingPresencePoller` in `src/AiRecall.Trigger/` ruft alle 5 s
    `TeamsAppReader.TryGetActiveMeetingAsync()` auf und feuert `PresenceChanged`
    bei Edge-Detection (false→true = Started, true→false = Ended). Polling
    ist **alleinige Quelle** für Start/Stop in v0.3; Event-driven
    `MeetingStateChanged` aus Spec 0011/Update 2 bleibt im Code, wird aber
    NICHT vom `TriggerSupervisor` abonniert. Selbst-heilend, robust gegen
    verlorene Events (Teams-Reload, Network-Drop, UI-Crash).
  - **§2 erweitert um „Recording-Lifecycle (Eigener Thread + Stop-Signal)"** —
    Threading-Modell dokumentiert: NAudio-Capture-Thread ×2 +
    Recording-Coordinator-Thread (eigener Background-Task via `Task.Run`).
    Stop-Signal via `CancellationToken`, kein Thread.Abort. Beim Stop:
    Buffer-Snapshots erstellen → `mic.wav` + `loopback.wav` schreiben →
    `meta.md` Status `recorded` mit `audio_files`-Links setzen.
    Neue Komponente `RecordingSession` in `src/AiRecall.Core/Audio/`.
  - **§4 erweitert um Status-Lifecycle** — Neue Status-Werte
    `recording` → `recorded` → `transcribed` → `failed`. `meta.md` wird
    zweistufig geschrieben: Stufe 1 beim Start (`recording`, leere
    `audio_files`), Stufe 2 beim Stop (`recorded`, `audio_files` befüllt
    mit `mic.wav` + `loopback.wav`). Neues Frontmatter-Feld
    `worker_task_enqueued` für Recovery-Scan nach Crash.
  - **§5 erweitert um „Worker-Discovery"** — TranscriptionWorker findet
    seine Tasks auf zwei Wegen: Pfad 1 Live-Enqueue beim Stop,
    Pfad 2 Recovery-Scan beim Worker-Start für `status=recorded AND
    worker_task_enqueued=false`. Idempotenz via atomarem Flag-Set.
    Neue Komponente `MetaMdLifecycleManager` in `src/AiRecall.Conversion/Transcription/`.
  - **Komponenten-Plan erweitert** — `MeetingPresencePoller`,
    `MeetingPresenceStateChangedEventArgs`, `RecordingSession`,
    `MeetingRecordingPaths`, `MetaMdLifecycleManager`, `MetaMdFrontmatter`,
    `MeetingPresenceSnapshot`.
  - **Tests-Plan erweitert** — ~18 neue Tests (Polling-Loop, Recording-Session,
    MetaMdLifecycle), gesamt jetzt ~109 neue Tests, Ziel 674 → ~783.

  **Was bleibt unverändert:**
  - Stereo-Concatenation (Update 6/7)
  - RMS-Analyse gestrichen (Update 5)
  - Rohe Provider-Speaker-IDs im Output (Update 5)
  - Beide Provider Azure Speech + Deepgram parallel (Update 3)
  - v0.4 Outlook-Kalender nach v0.3-Abnahme (Update 3)

  **Martin-Direktiven-Status:** 10 Direktiven (1-10) abgehakt.

- **2026-07-07 (Update 7, nach Martin-Feedback)** — **„Azure speech auch mit stereo nutzen."**
  Annahme „Azure Speech downmixt intern auf Mono" aus Update 6 entfernt.
  Azure Speech verarbeitet das Stereo-File nativ (kein Downmix auf
  unserer Seite), Response enthält `ChannelId` zusätzlich zu `SpeakerId`.
  Beide Provider (Azure + Deepgram) bekommen das gleiche
  `combined-stereo.wav` — keine Provider-spezifischen Pre-Processing-Pfade.
  Output-MD-Format nutzt jetzt kombinierte Channel-Speaker-Labels
  (z. B. „C0-S1" für Channel 0 / Speaker 1) für Azure-Response.
- **2026-07-07 (Update 6, nach Martin-Feedback)** — **„Nutze weiterhin stereo mit beiden Kanälen."**
  §5.4 von Mono-Mix (Update 5) zurück auf Stereo-Concatenation.
  `MonoMixer` ersetzt durch `StereoConcatenator`, `combined-mono.wav`
  ersetzt durch `combined-stereo.wav`. Beide Kanäle bleiben erhalten
  (Deepgram kann Multi-Channel-Diarization nutzen, Azure Speech downmixt
  intern). RMS-Analyse bleibt gestrichen (Update 5 unverändert gültig).
  Storage-Flexibilität für v0.4 (Per-Channel-Re-Transkription,
  Audio-Pre-Processing) ohne Recording-Format-Change.
- **2026-07-07 (Update 5, nach Martin-Feedback)** — **„Streiche das rms. Diarization macht der Provider."**
  Komplette Vereinfachung: §5.5 Cross-Channel-Correlation (RMS) ersatzlos
  gestrichen. §5.4 von Stereo-Concatenation auf Mono-Mix reduziert
  (`combined-mono.wav` statt `combined-stereo.wav`). Datenmodell
  `TranscriptionResult` schlanker (kein `LocalSpeakerId`, kein
  `RemoteSpeakerIds`, kein `RoleMap` mehr — nur `SpeakerLabels`). Output-MD
  zeigt rohe Provider-Speaker-IDs (S0, S1, S2). Komponenten
  `SpeakerRoleAssigner`/`SpeakerRoleMap`/`StereoConcatenator` entfernt,
  ersetzt durch `MonoMixer`. Test-Plan von ~109 zurück auf ~91 Tests.
  **§5.4-Mono-Mix-Teil in Update 6 zurückgenommen auf Stereo.**
- **2026-07-07 (Update 4, nach Martin-Feedback)** — Stereo-Concatenation als
  Pre-Processing vor ASR-Call. `combined-stereo.wav` wird im Meeting-Ordner
  abgelegt (nicht im OS-Temp), pro Task eindeutig — Martin-Direktive
  „parallel multi tasking laufen kann, kein fixer dateiname im temp ordner".
  Cross-Channel-Correlation (RMS-Verhältnis) als Speaker-Role-Mapping nach
  Provider-Diarization, identifiziert lokalen User via
  `r(w) = RMS_mic / (RMS_mic + RMS_loopback + ε)`. Neue Komponenten
  `StereoConcatenator` + `SpeakerRoleAssigner`, Datenmodell erweitert um
  `LocalSpeakerId`/`RemoteSpeakerIds`/`SpeakerRoleMap`, ~18 neue Tests.
  **Komplett durch Update 5 ersetzt.**
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
- **2026-07-09 (Update 8, Iter. 4)** — TriggerSupervisor-Integration:
  `MeetingTrigger` wird von `TriggerService` initialisiert/beendet
  (analog zum `ConversionWorker`-Pattern aus Spec 0007). Neue Factory
  `MeetingTriggerFactory.TryCreateDefault(config, logger)` baut die
  Production-Default-Composition (`MeetingPresencePoller` +
  `TeamsAppReaderProbe` + `TranscriptionWorker` mit Default-Provider
  (Azure Speech / Deepgram, je nach `TranscriptionConfig.Provider`) +
  `WasapiAudioRecorderFactory` + `AudioDeviceProvider` +
  StorageRoot-Fallback auf `%APPDATA%\AiRecall\audio`). Privacy-First-Gate
  (Audio.Enabled / Teams.AutoRecordMeetings / AppReader.Teams.Enabled) →
  `null` anstatt Composition. `MeetingTrigger` ist jetzt
  `IDisposable + IAsyncDisposable`; `Dispose()` ruft `DisposeAsync()`
  synchron. `TranscriptionWorker`-Bug-Fix: Counter (`_failedCount`)
  wird jetzt **nach** `MarkFailedAsync` inkrementiert (vorher: Test sah
  Counter steigen bevor `meta.md` mit `transcript_status: failed`
  geschrieben war → 40 % Flake-Rate).
  4 neue `TriggerService`Tests (Audio.Disabled / Teams.AutoRecord.Off /
  Teams.Reader.Disabled / External-Injection-Is-Exposed-As-Is),
  777/777 grün in 5/5 Runs stabil.
- **2026-07-07 (Update 1)** — Erstellt nach Martins Anforderungsliste (8 Punkte).
  Skeleton v0.3.