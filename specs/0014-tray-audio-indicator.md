# 0014 — Tray Audio-Indikator + Manuelle Audio-Steuerung

> **Status:** 🟡 **GENEHMIGT v0.1 (2026-07-10)** — Scoping beantwortet (Martin 2026-07-10 18:02); **API-Vereinfachung** (Martin 2026-07-10 19:11)
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

## API-Vereinfachung: Single-Active-Recording (Martin 2026-07-10 19:11)

**Martin-Direktive:** Es kann immer nur **eine** aktive Audio-Aufnahme geben. Auto-Recording (Meeting) und Manual-Recording schließen sich gegenseitig aus.

**Konsequenzen für die API:**
- `StopAsync()` braucht keinen Key-Parameter mehr — es gibt immer nur eine Session zu stoppen.
- `StartManualAsync()` muss die Single-Active-Invariante garantieren: wenn schon eine Aufnahme läuft (egal ob Auto oder Manual), wird der Start abgelehnt.
- `IsRecording` reicht als Single-State.
- `RecordingStateChangedEventArgs.Key` ist nur noch für Diagnostics (Log), nicht für API-Steuerung.
- Iter. 1 muss refactored werden: `StopAsync(string?)` → `StopAsync()` ohne Parameter; Tests entsprechend anpassen.

**Verhalten von `StartManualAsync` bei laufender Aufnahme (Martin-Entscheidung ausstehend):**
- [ ] **Vorschlag A (Default):** `InvalidOperationException` — User muss erst Stop klicken (klar, deterministisch; Tray-Menu-Items sorgen für korrekten Enabled/Disabled-State).
- [ ] Alternative B: Auto-Stop + Manual-Start (Auto verliert Priorität — User-Manual-Wins).
- [ ] Alternative C: Silent no-op (User klickt doppelt, nur eine Aufnahme läuft).

## Motivation

Aktuell zeigt das Tray-Icon nur den **Capture-Pipeline-State** (`tray-recording.ico` = 👁️ / `tray-idle.ico` = ⚫) und die Menu-Items „Start Recording" / „Stop Recording" steuern die **Trigger-Pipeline** (Window-Captures via `_supervisor.Start/Stop`).

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
| 9 | Single-Active-Recording-Constraint: maximal 1 Aufnahme gleichzeitig (Auto+Manual schließen sich aus) | **ENTSCHIEDEN** |

## Architektur-Skizze

### Recording-State-Quelle

`MeetingTrigger` (Spec 0013) wird um Recording-State-API erweitert:

```csharp
public enum RecordingSource { MeetingAuto, Manual }

public sealed record RecordingStateChangedEventArgs(
    bool IsRecording,
    RecordingSource Source,
    string Key,           // ChatIdShort bei MeetingAuto, "manual-{guid}" bei Manual (nur Diagnostics)
    string? Topic,        // null bei Manual
    DateTimeOffset At);

public interface IRecordingControl
{
    bool IsRecording { get; }
    event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;

    /// <summary>
    /// Startet eine manuelle Aufnahme (auch ohne Meeting-Kontext).
    /// Wirft InvalidOperationException, wenn schon eine Aufnahme laeuft
    /// (Single-Active-Recording-Constraint, Martin 2026-07-10 19:11).
    /// </summary>
    Task<string> StartManualAsync(CancellationToken ct);

    /// <summary>
    /// Stoppt die aktive Aufnahme (genau eine, da Single-Active).
    /// Enqueued den resultierenden Transkriptions-Task im TranscriptionWorker.
    /// </summary>
    Task StopAsync();
}

public sealed class MeetingTrigger : IRecordingControl
{
    // bestehend: Polling → Trigger → Recording
    // NEU:
    public bool IsRecording => _active.Count > 0;
    public event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;
    public async Task<string> StartManualAsync(CancellationToken ct) { ... }
    public async Task StopAsync() { ... }
}
```

**Aufteilung:**
- `MeetingTrigger.IsRecording` = `_active.Count > 0` (genau 0 oder 1 durch Single-Active-Constraint)
- `MeetingTrigger.StartManualAsync()` wirft `InvalidOperationException` wenn `IsRecording == true` — User muss erst stoppen
- `MeetingTrigger.StopAsync()` stoppt die eine aktive Session (egal ob Auto oder Manual)
- Nach Stop: Transkription wird via `TranscriptionWorker.Enqueue` getriggert — der Worker unterscheidet nicht nach Quelle

**Key-Verwaltung im `_active`-Dictionary:**
- Auto-Recording: Key = `chatIdShort` (z. B. `"abc12345"`)
- Manual-Recording: Key = `"manual-{guid-N-32}"` (z. B. `"manual-3f4a5b6c7d8e9f0a1234567890abcdef"`)
- Dictionary bleibt intern `Dictionary<string, ActiveRecording>` (auch wenn max. 1 Eintrag), damit das Pattern zu Auto-Recording symmetrisch bleibt und Iter. 3+ problemlos Multi-Session aktivieren könnte (z. B. mehrere parallele Meetings)

### Tray-Icon-Update

`TrayIconController` abonniert `IRecordingControl.RecordingStateChanged` und schaltet das Icon:

```csharp
private Icon ResolveTrayIcon()
{
    if (_recordingControl?.IsRecording == true)
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

### Iter. 1 — Recording-State-API in MeetingTrigger (TEILWEISE GEPUSHT als `a8a70e3`)

**Status:** Committed, muss refactored werden für Single-Active-Constraint (siehe `IRecordingControl.StopAsync(string?)` → `StopAsync()`).

- `RecordingSource` Enum + `RecordingStateChangedEventArgs` POCO (in `AiRecall.Trigger`) — ✅ gepusht
- `IRecordingControl` Interface (in `AiRecall.Trigger`) — ✅ gepusht, muss refactored werden
- `MeetingTrigger.IsRecording` property + `RecordingStateChanged` event — ✅ gepusht
- `MeetingTrigger.StartManualAsync(CancellationToken)` Methode — ✅ gepusht, muss um Single-Active-Check erweitert werden
- `MeetingTrigger.StopAsync()` Methode (parameterlos) — ❌ noch nicht implementiert (ist noch `StopAsync(string? key = null)`)
- Key-Prefix `manual-` für manuelle Aufnahmen — ✅ gepusht
- Tests: 11 Tests gepusht, müssen für neues API angepasst werden

### Iter. 1b — Refactor für Single-Active-Constraint (NEU, klein)

- `IRecordingControl.StopAsync(string? key = null)` → `StopAsync()`
- `MeetingTrigger.StartManualAsync()`: Single-Active-Check (`if (IsRecording) throw InvalidOperationException`)
- `MeetingTrigger.StopAsync()`: parameterlos, stoppt die eine aktive Session
- Tests anpassen: `StopAsync_WithSpecificKey_*` → `StopAsync_StopsActiveSession_*`, `StopAsync_WithoutActiveRecording_NoOp`
- Neuer Test: `StartManualAsync_WhileAutoRecording_Throws`
- Tests: 11 → ~12 Tests (1 hinzu, ~3 umbenannt/angepasst)

### Iter. 2 — Tray-Icon-Indikator
- `tray-audio-recording.ico` generieren (EmojiIconGen-Tool, 🎙-Emoji)
- `TrayIconController` abonniert `IRecordingControl.RecordingStateChanged`
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

**Status 2026-07-10 19:11:** Single-Active-Constraint entschieden. Verbleibend:

1. **Verhalten von `StartManualAsync` bei laufender Aufnahme?**
   - [ ] **Vorschlag A (Default):** `InvalidOperationException` — User muss erst Stop klicken.
   - [ ] Alternative B: Auto-Stop + Manual-Start.
   - [ ] Alternative C: Silent no-op.
2. **Persistenz manueller Aufnahmen — gleiche Ordnerstruktur oder separat?**
   - [ ] **Vorschlag:** Gleiche Ordnerstruktur (`capture/yyyy-MM-dd/audio/{key}/`).

## Privacy-First-Gates (aus Spec 0013)

Alle Iter. respektieren die existierenden Gates:
- `Audio.Enabled = false` (Default) → keine Audio-Features sichtbar
- `AppReader.Teams.Enabled = false` → `MeetingTrigger` wird nicht erzeugt (Spec 0013 Iter. 4)
- `AppReader.Teams.AutoRecordMeetings = false` → keine Auto-Aufnahmen, **aber** manuelle Aufnahme via `StartManualAsync()` soll weiterhin möglich sein (das ist ja der Sinn manueller Steuerung)

## Test-Strategie

- Bestehende 788 Tests bleiben grün
- Neue Tests: ~25 (geschätzt: 12 für Iter. 1b + 5 für Iter. 2 + 6 für Iter. 3 + ~2 für Iter. 4)
- Mind. 5 Test-Runs vor Commit (Counter/Async-Konvention, siehe DECISIONS.md 2026-07-09)

## Change-History

- **v0.1** (2026-07-10): Skelett angelegt nach Martin-Direktive „Bei laufender Audio-Aufzeichnung soll sich das Icon ändern. Steuerung für Audio-Aufzeichnung im Tray-Menu (Start, Stop)". Status: GENEHMIGT nach Martin-Antwort.
  - Iter. 1 teilweise gepusht (Commit `a8a70e3`), muss für Single-Active-Constraint refactored werden (Iter. 1b).
- **v0.1 Update 2** (2026-07-10 19:11): Martin-Direktive Single-Active-Recording-Constraint.
  - `StopAsync()` parameterlos (kein Key mehr).
  - `StartManualAsync` muss Single-Active-Invariante garantieren.
  - Iter. 1b (Refactor) hinzugefügt zwischen Iter. 1 und Iter. 2.