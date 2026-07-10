# 0014 — Tray Audio-Indikator + Manuelle Audio-Steuerung

> **Status:** 🟡 **GENEHMIGT v0.1 (2026-07-10)** — Scoping beantwortet (Martin 2026-07-10 18:02)
> **Owner:** Martin
> **Abhängig von:** Spec 0006 (Tray-EXE), Spec 0009 (Settings-Dialog), Spec 0013 (Audio Notes / `MeetingTrigger`)

## Scoping-Entscheidungen (Martin 2026-07-10 18:02)

| Frage | Antwort |
|---|---|
| Manuelle Aufnahme ohne Meeting? | **Ja** — auch ohne Meeting aufnehmen (z. B. für Meetings außerhalb von Teams, klassischer Voice-Memo-Modus). |
| Was wird aufgenommen? | **Stereo** (Mic + Speaker-Loopback), analog Meeting-Recording. |
| Manuelle Aufnahme + Transkription? | **Ja** — Transkription läuft analog zu anderen Audio-Recordings im Hintergrund durch den `TranscriptionWorker` (der unterscheidet nicht nach Meeting-Quelle). |
| Bestehende Menu-Items „Start/Stop Recording"? | **Zusätzlich auf gleicher Ebene** — Capture-Items bleiben, Audio-Items kommen daneben. |
| Metadaten bei manueller Aufnahme | **Allgemeine Infos** — kein Meeting-Topic, kein Chat-Id; z. B. `source: manual-audio`, `topic: null`, `chatIdShort: null`, `windowTitle: null`. |

## Motivation

Aktuell zeig t das Tray-Icon nur den **Capture-Pipeline-State** (`tray-recording.ico` = 👁️ / `tray-idle.ico` = ⚫) und die Menu-Items „Start Recording" / „Stop Recording" steuern die **Trigger-Pipeline** (Window-Captures via `_supervisor.Start/Stop`).

**Problem:** Audio-Aufnahmen (Spec 0013, `MeetingTrigger`) laufen komplett im Hintergrund — der User sieht **nicht**, ob gerade aufgenommen wird, und kann **nicht** manuell Audio starten/stoppen (z. B. für Aufnahmen außerhalb von Teams-Meetings).

**Spec-Ziel:**
1. **Tray-Icon zeigt Audio-Recording-Indikator** bei laufender Audio-Aufnahme
2. **Tray-Menu-Items für manuelle Audio-Aufnahme** (Start/Stop)

## Funktionale Anforderungen

| # | Anforderung | Status |
| - | - | - |
| 1 | Tray-Icon zeigt visuell, ob gerade Audio aufgenommen wird (egal ob Meeting-Auto oder manuell) | Spezifiziert |
| 2 | Audio-Aufnahme kann manuell via Tray-Menu gestartet werden (auch ohne Meeting) | Spezifiziert |
| 3 | Audio-Aufnahme kann manuell via Tray-Menu gestoppt werden | Spezifiziert |
| 4 | Manuelle Aufnahme ohne Meeting-Kontext möglich (z. B. Meetings außerhalb von Teams) | **ENTSCHIEDEN: Ja** |
| 5 | Manuelle Aufnahme: Stereo (Mic + Speaker-Loopback), analog Meeting-Recording | **ENTSCHIEDEN: Stereo** |
| 6 | Manuelle Aufnahme triggert Transkription via `TranscriptionWorker` (analog Meeting-Recording) | **ENTSCHIEDEN: Ja** |
| 7 | Manuelle Aufnahme-Metadaten: allgemeine Infos (`source: manual-audio`, `topic: null`) | **ENTSCHIEDEN** |
| 8 | Audio-Controls als zusätzliche Menu-Items auf gleicher Ebene (Capture-Items bleiben daneben) | **ENTSCHIEDEN** |

## Architektur-Skizze

### Recording-State-Quelle

`MeetingTrigger` (Spec 0013) wird um Recording-State-API erweitert:

```csharp
public enum RecordingSource { MeetingAuto, Manual }

public sealed record RecordingStateChangedEventArgs(
    bool IsRecording,
    RecordingSource Source,
    string? Key,           // ChatIdShort bei MeetingAuto, "manual-{guid}" bei Manual
    string? Topic,         // null bei Manual
    DateTimeOffset At);

public interface IRecordingControl
{
    bool IsRecording { get; }
    event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;

    /// <summary>
    /// Startet eine manuelle Aufnahme (auch ohne Meeting-Kontext).
    /// Erzeugt einen neuen eindeutigen Key, startet die Recording-Session,
    /// feuert RecordingStateChanged(Source=Manual, IsRecording=true).
    /// </summary>
    Task<string> StartManualAsync(CancellationToken ct);

    /// <summary>
    /// Stoppt eine konkrete Aufnahme per Key (oder alle aktiven Sessions
    /// bei key=null). Enqueued den resultierenden Transkriptions-Task im
    /// TranscriptionWorker (analog Auto-Recording).
    /// </summary>
    Task StopAsync(string? key = null);
}

public sealed class MeetingTrigger : IRecordingControl
{
    // bestehend: Polling → Trigger → Recording
    // NEU:
    public bool IsRecording => !_active.IsEmpty;
    public event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;
    public async Task<string> StartManualAsync(CancellationToken ct) { ... }
    public async Task StopAsync(string? key = null) { ... }
}
```

**Aufteilung:**
- `MeetingTrigger.IsRecording` = `_active.Count > 0` (egal ob Auto oder Manual)
- `MeetingTrigger.StartManualAsync()` startet eine neue Recording-Session mit Key `"manual-{guid}"`, Topic=null, WindowTitle=null (generische Metadaten)
- `MeetingTrigger.StopAsync(key=null)` stoppt alle aktiven Sessions; `StopAsync(key=specific)` stoppt eine bestimmte
- Nach Stop: Transkription wird via `TranscriptionWorker.Enqueue` getriggert — der Worker unterscheidet nicht nach Quelle

**Key-Verwaltung im `_active`-Dictionary:**
- Auto-Recording: Key = `chatIdShort` (z. B. `"abc12345"`)
- Manual-Recording: Key = `"manual-{guid:N}"` (z. B. `"manual-3f4a5b6c7d8e9f0a"`)
- Beide Quellen koexistieren ohne Konflikt (unterschiedliche Präfixe)

### Tray-Icon-Update

`TrayIconController` abonniert `MeetingTrigger.RecordingStateChanged` und schaltet das Icon:

```csharp
private Icon ResolveTrayIcon()
{
    if (_meetingTrigger?.IsRecording == true)
        return _menuImages.GetOrAddEmbeddedIcon("tray-audio-recording.ico");
    if (_supervisor.State == TriggerState.Running)
        return _menuImages.GetOrAddEmbeddedIcon("tray-recording.ico");
    return _menuImages.GetOrAddEmbeddedIcon("tray-idle.ico");
}
```

**Icon-Priorität:** Audio-Recording > Capture-Running > Idle.

**Neue Icon-Resource:** `Resources\Icons\tray-audio-recording.ico` (🎤 Mikrofon oder 🔴 mit Notiz). Generierung via bestehendes `EmojiIconGen`-Tool (siehe Bug-Bash 2026-07-06).

### Tray-Menu-Items

Zusätzliche Items auf gleicher Ebene (ENTSCHIEDEN 2026-07-10). Capture-Items bleiben unverändert:

```
─── Status: Running — 12 captures ───
─────────────────────────
▶ Start Recording       (Ctrl+S)
■ Stop Recording        (Ctrl+T)
🎙 Audio aufnehmen      (Ctrl+Shift+R)
🎙 Audio stoppen        (Ctrl+Shift+T)
─────────────────────────
Live Logviewer…
Settings…
─────────────────────────
Quit
```

Items „Audio aufnehmen" / „Audio stoppen" nur sichtbar/aktiv wenn `Audio.Enabled = true` (Privacy-First-Gate aus Spec 0013). Bei `IsRecording == false`: „Audio aufnehmen" enabled, „Audio stoppen" disabled. Bei `IsRecording == true`: umgekehrt.

## Iter.-Plan

### Iter. 1 — Recording-State-API in MeetingTrigger
- `RecordingSource` Enum + `RecordingStateChangedEventArgs` POCO (in `AiRecall.Trigger`)
- `IRecordingControl` Interface (in `AiRecall.Trigger`)
- `MeetingTrigger.IsRecording` property + `RecordingStateChanged` event
- `MeetingTrigger.StartManualAsync(CancellationToken)` Methode (manuell, ohne Meeting)
- `MeetingTrigger.StopAsync(string? key)` Methode (per Key oder alle)
- Key-Prefix `manual-` für manuelle Aufnahmen
- Tests: ~10 Tests (State-Transitions, Event-Reihenfolge, Stop-Semantik, Key-Konfliktfreiheit)

### Iter. 2 — Tray-Icon-Indikator
- `tray-audio-recording.ico` generieren (EmojiIconGen-Tool, 🎙-Emoji)
- `TrayIconController` abonniert `RecordingStateChanged`
- Icon-Priorität: Audio > Capture > Idle
- `_statusRefreshTimer` triggert auch Icon-Refresh bei Audio-State-Change
- Tests: ~5 Tests (Icon-State-Machine, Edge-Cases: Audio-Start während Capture-Running)

### Iter. 3 — Tray-Menu-Items für manuelle Audio-Steuerung
- Neue Menu-Items `_startAudioItem` / `_stopAudioItem` (zusätzlich auf gleicher Ebene)
- Privacy-First-Gate: nur sichtbar wenn `Audio.Enabled = true`
- Click-Handler ruft `MeetingTrigger.StartManualAsync()` / `StopAsync()`
- Disabled-State basierend auf `IsRecording` (analog Capture-Items)
- Tests: ~6 Tests (Click-Handler, Gate-Verhalten, Idempotenz)

### Iter. 4 — Doku-Cluster
- `PROJECT.md`: Punkt 8 erweitern (MVP 3 Audio Notes → Audio Indicator)
- `DECISIONS.md`: Neue Top-Level-Eintrag „2026-07-10 — MVP 3 Audio Indicator & Manual Control"
- `README.md`: Feature-Block „Tray audio indicator + manual recording control"
- Spec 0014 v0.1 → v1.0

## Offene Fragen für Martin

**Status 2026-07-10 18:02:** Alle 4 Hauptfragen beantwortet. Verbleibende Detail-Frage:

1. **Persistenz manueller Aufnahmen — gleiche Ordnerstruktur oder separat?**
   - [ ] **Vorschlag:** Gleiche Ordnerstruktur (`capture/yyyy-MM-dd/audio/{key}/`) — vereinfacht TranscriptionWorker-Logik (kein Sonderpfad), nur Unterschied in den MD-Frontmatter-Feldern.
   - [ ] Separater Ordner `capture/yyyy-MM-dd/manual-audio/{key}/` — klarere Trennung, aber zusätzlicher Pfad in `TranscriptionWorker`-Scans.
   - Martin-Entscheidung ausstehend.

## Privacy-First-Gates (aus Spec 0013)

Alle Iter. respektieren die existierenden Gates:
- `Audio.Enabled = false` (Default) → keine Audio-Features sichtbar
- `AppReader.Teams.Enabled = false` → `MeetingTrigger` wird nicht erzeugt (Spec 0013 Iter. 4)
- `AppReader.Teams.AutoRecordMeetings = false` → keine Auto-Aufnahmen, **aber** manuelle Aufnahme via `StartAsync()` soll weiterhin möglich sein (das ist ja der Sinn manueller Steuerung)

## Test-Strategie

- Bestehende 777 Tests bleiben grün
- Neue Tests: ~19 (geschätzt: 8 + 5 + 6)
- Mind. 5 Test-Runs vor Commit (Counter/Async-Konvention, siehe DECISIONS.md 2026-07-09)

## Change-History

- **v0.1** (2026-07-10): Skelett angelegt nach Martin-Direktive „Bei laufender Audio-Aufzeichnung soll sich das Icon ändern. Steuerung für Audio-Aufzeichnung im Tray-Menu (Start, Stop)". Status: GENEHMIGT nach Martin-Antwort (alle 4 Scoping-Fragen beantwortet).
  - Manuelle Aufnahme JA (auch ohne Meeting)
  - Stereo (Mic + Speaker-Loopback)
  - Transkription via `TranscriptionWorker` im Hintergrund
  - Audio-Items ZUSÄTZLICH auf gleicher Ebene (Capture-Items bleiben)
  - Metadaten bei manueller Aufnahme: nur allgemeine Infos
  - Verbleibend: Ordnerstruktur für manuelle Aufnahmen (Vorschlag: gleiche wie Meeting-Aufnahmen).