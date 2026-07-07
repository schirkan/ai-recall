# 0005 — Trigger-Pipeline (`recall record`)

> **Status:** Abgeschlossen v1.0 (2026-07-04, Commits 791161a … 5d934dc)
> **Owner:** Martin
> **Implements:** TR-1..6 from MVP1 spec, integrates App-Reader (0004)

## Zweck

`recall record` zeichnet die Bildschirmarbeit kontinuierlich auf, ohne dass
der Nutzer pro Capture manuell `recall active-window` aufrufen muss.
Auslöser sind **Fokus-Wechsel**, **Inhalts-Updates**, **Scroll-Events**
und **Heartbeat-Polling** — kein stures Polling auf das Vordergrundfenster.

## Trigger-Quellen

### Primär: Win32 Event Hook (`SetWinEventHook`)

Systemweite Hooks ohne DLL-Injection. Flag `WINEVENT_OUTOFCONTEXT` lässt
den Callback im eigenen Prozess laufen; das System injiziert nichts in
andere Prozesse → keine Admin-Rechte, keine AV-Warnungen, keine
Stabilitätsrisiken.

Subskribierte Events:

| Event | Konstante | Zweck |
|---|---|---|
| `EVENT_SYSTEM_FOREGROUND` | `0x0003` | Fensterwechsel (Haupttrigger) |
| `EVENT_OBJECT_FOCUS` | `0x8005` | Fokus innerhalb Fensters (z. B. Tab-Wechsel im Browser, Sub-Control-Fokus) |
| `EVENT_OBJECT_NAMECHANGE` | `0x800C` | Titel/URL geändert (Browser-Tab-Titel, Dokument-Titel) |
| `EVENT_OBJECT_VALUECHANGE` | `0x800E` | Inhalt geändert (TextBox, WebView, Edit-Control) |
| `EVENT_OBJECT_SCROLL` | `0x8015` | Scroll-Bewegung |
| `EVENT_SYSTEM_MENUPOPUPSTART` | `0x0005` | Menü/Kontextmenü geöffnet |

Out-of-context Hooks liefern diese Events systemweit; in-process
OBJECT-Events (z. B. `EVENT_OBJECT_INVOKED`) sind hier nicht relevant.

### Sekundär: Heartbeat-Polling

Ein 30-s-Loop (`trigger.heartbeatIntervalSeconds`) ruft
`GetForegroundWindow` auf. Zweck: Fallback, falls Events verschluckt werden
(z. B. hohe Systemlast, Sleep/Resume, schnelle User-Session-Wechsel).

### Tertiär: Periodisches Capture (Bug-Bash Teil 2, 2026-07-06)

Für Inhalte, die sich visuell ändern, ohne dass `SetWinEventHook`-Events
feuern (Video-Streams, Slideshows, Live-Daten, Browser-Scroll-Churn,
lautlose Updates) gibt es einen optionalen **Periodischen Capture**-Pfad
(`screenRecorder.periodicCaptureMs`, Default `0` = deaktiviert).

Anders als Heartbeat-Polling (Sicherheitsnetz für verlorene Events)
ist Periodic Capture ein **eigenständiger Capture-Auslöser**: er feuert
in festen Intervallen (sinnvoll 3 000–10 000 ms) unabhängig davon, ob
ein WinEventHook-Event kam. Implementiert als `PeriodicCaptureThread`
(`AiRecall.Trigger.dll`), der einen `TriggerKind.Periodic`-Event in den
Worker-Channel schreibt. Konsolidiert mit `HeartbeatThread` über die
gemeinsame interne `PollThread`-Klasse (Bug-Bash I-24).

Wichtige Unterschiede zu Heartbeat:
- Heartbeat ist **Fallback** (selten, ~30 s) — Periodic Capture ist
  **regulärer Trigger** (3–10 s), wenn aktiviert.
- Periodic Capture durchläuft die volle Pipeline (Throttle/Dedup/
  App-Reader/OCR); bei `IgnoreApps`/`IgnoreUrls`/`IgnoreWindowTitles`
  wird der Trigger verworfen, bevor ein Capture entsteht.

### Outlook: Background-Polling (App-Reader-intern)

Outlook-Mail-Stream ist inhärent polling-basiert (kein OS-Event für
„neue Mail") und liegt daher **nicht** unter `trigger.*`. Konfiguration
in Spec 0004 unter `appReader.outlook.*` (`pollIntervalSeconds`,
`folders`, `ignoreAutoRuleMails`).

**Konvention:** App-spezifische Polling-Konfiguration bleibt unter
`appReader.<reader>.*`. Diese Spec behandelt nur Window-basierte Events.

## Architektur

```
┌────────────────────────────────────────────────────────────┐
│ TriggerService (IHostedService, läuft in recall record)   │
│                                                            │
│  ┌──────────────────────┐                                  │
│  │ EventHookThread      │                                  │
│  │ - SetWinEventHook    │                                  │
│  │ - WINEVENT_OUTOFCONTEXT                                    │
│  │ - Message-Loop       │                                  │
│  │   (GetMessage/       │  Channel<TriggerEvent>          │
│  │    DispatchMessage)  │ ─────────────────┐               │
│  │ - WinEventProc →     │                  ▼               │
│  │   Enqueue            │  ┌──────────────────────────────┐ │
│  └──────────────────────┘  │ Worker-Thread                │ │
│                            │ 1. Throttle (per HWND + App) │ │
│  ┌──────────────────────┐  │ 2. AppReaderRegistry.ReadAsync│ │
│  │ HeartbeatThread      │ ─┤ 3. OCR (immer, Bild-Beweis)  │ │
│  │ - 30 s-Loop          │  │ 4. Hash-Dedup                │ │
│  │ - GetForegroundWindow│  │ 5. CaptureWriter             │ │
│  └──────────────────────┘  └──────────────────────────────┘ │
└────────────────────────────────────────────────────────────┘
```

**Warum zwei Threads?** `WinEventProc` läuft auf dem EventHook-Thread
und darf nicht blockieren (sonst hängen andere Apps' UI-Threads,
obwohl out-of-context). Der Worker-Thread entkoppelt Event-Empfang von
Capture-Verarbeitung und nutzt `Channel<TriggerEvent>` (bounded, 1024)
als Thread-sichere Queue.

**Warum drei Quellen?** Heartbeat und Periodic Capture sind optionale
Polling-Quellen, die unabhängig vom WinEventHook laufen. Sie teilen sich
die gemeinsame interne `PollThread`-Klasse (`AiRecall.Trigger.PollThread`),
die mit `triggerKind`, Thread-Name und Log-Prefix parametrisiert wird
(HeartbeatThread und PeriodicCaptureThread sind dünne Wrapper für
Abwärtskompatibilität — Bug-Bash 2026-07-06 I-24).

### TriggerEvent-Datentyp

```csharp
public sealed record TriggerEvent(
    IntPtr Hwnd,
    TriggerKind Kind,
    DateTimeOffset Timestamp
);

public enum TriggerKind
{
    Foreground,        // EVENT_SYSTEM_FOREGROUND
    Focus,             // EVENT_OBJECT_FOCUS
    NameChange,        // EVENT_OBJECT_NAMECHANGE
    ValueChange,       // EVENT_OBJECT_VALUECHANGE
    Scroll,            // EVENT_OBJECT_SCROLL
    MenuPopup,         // EVENT_SYSTEM_MENUPOPUPSTART
    Heartbeat,         // Polling-Fallback (sekundär, ~30s)
    Periodic           // Periodisches Capture (tertiär, 3-10s wenn aktiv)
}
```

### Pipeline-Schritte (Worker)

1. **HWND normalisieren** — `GetAncestor(hwnd, GA_ROOT)` liefert das
   Top-Level-Fenster. Ein Klick auf einen Button in Word triggert
   `EVENT_OBJECT_FOCUS` auf das Button-HWND; normalisiert wird auf das
   Word-Fenster.
2. **Throttle per HWND** — Wenn letzter Trigger für HWND < `trigger.throttleMs`
   (Default 500) zurückliegt → skip.
3. **Throttle per App** — Wenn letzter Trigger für `Process.ProcessName`
   < `trigger.throttlePerAppSeconds` (Default 2 s) zurückliegt → skip.
4. **Self-Capture-Filter** — Wenn HWND.ProcessId == eigene PID → skip.
5. **Class-Blacklist** — Wenn `Win32Class(hwnd)` in
   `trigger.blacklist.windowClasses` → skip.
6. **App-Reader** — `AppReaderRegistry.ReadAsync(window)`. Erster
   nicht-null-Reader liefert `ContentMarkdown` (Spec 0004).
7. **OCR** — Tesseract-Snapshot des Fensters (Bild-Beweis).
8. **Hash berechnen** — SHA-256 über `processName + contentText + title`.
9. **Hash-Dedup pro HWND** — Dictionary `lastHashes[HWND]`. Wenn Hash ==
   letzter Hash **dieses Fensters** → skip Capture (kein Schreiben,
   aber Throttle-Timestamps aktualisieren).
10. **Capture schreiben** — `CaptureWriter.WriteCapture(window, ocrText, appReaderResult)`
    → PNG + Capture-MD + ggf. Content-MD.

## Sonderfälle

### Always-on-Top / Topmost

- `WS_EX_TOPMOST` ist **kein** Ausschlusskriterium (Sticky Notes, Calculator,
  Chat-Fenster, Notification-Clients sind legitim).
- AOT-Fenster werden normal aufgezeichnet.

### Modale Dialoge

- **Strategie (a) aus Diskussion 2026-07-04:** nur Vordergrund-Fenster lesen.
- Parent-Context wird im YAML-Frontmatter dokumentiert:
  ```yaml
  parentHwnd: 0x00040A2E
  parentTitle: "Dokument.docx — Word"
  parentProcess: "WINWORD"
  ```
- Beim Schließen des Dialogs folgt automatisch `EVENT_SYSTEM_FOREGROUND`
  für das Parent-Window → Parent wird wieder aufgezeichnet (sofern sich
  Inhalt geändert hat, sonst greift Hash-Dedup).

### Tooltips / Notifications

Per Win32-Window-Class-Blacklist. Default-Blacklist in `default-config.json`:

- `tooltips_class32`
- `NotifyIconOverflowWindow`

User kann via `%APPDATA%/AiRecall/config.json` erweitern.

### Self-Capture-Filter

TriggerService ignoriert Events für HWNDs des eigenen Prozesses
(`Process.GetCurrentProcess().Id`). Verhindert, dass z. B. ein
Konfigurations-Dialog oder das eigene Terminal-Fenster aufgezeichnet wird.

### Mehrere Tabs im selben Fenster

Edge/Chrome mit mehreren Tabs sind **ein** Top-Level-Fenster
(`GetAncestor(..., GA_ROOT)`). Tab-Wechsel triggern
`EVENT_OBJECT_FOCUS`/`EVENT_OBJECT_NAMECHANGE`, normalisiert auf das
Top-Level → Capture für das gesamte Fenster. Der Browser-Reader nimmt
den aktuell sichtbaren Tab (UIA) bzw. den CDP-Tab-Target. **Out of scope
für MVP1:** Tab-spezifische Trigger-Entscheidung.

## Konfiguration

Neue Top-Level-Sektion in `default-config.json`:

```json
{
  "trigger": {
    "enabled": true,
    "throttleMs": 500,
    "throttlePerAppSeconds": 2,
    "heartbeatIntervalSeconds": 30,
    "winEvents": {
      "foreground": true,
      "focus": true,
      "nameChange": true,
      "valueChange": true,
      "scroll": true,
      "menuPopup": true
    },
    "blacklist": {
      "windowClasses": [
        "tooltips_class32",
        "NotifyIconOverflowWindow"
      ],
      "processes": []
    }
  }
}
```

Felder:
- `trigger.enabled` (Default `true`) — Master-Switch.
- `trigger.throttleMs` (Default `500`) — Min-Intervall zwischen
  Captures für dasselbe HWND. Achtung: Name "throttleMs" — nicht
  Debounce im Frontend-Sinn, sondern klassisches Throttle
  (max 1 Capture pro Zeitfenster).
- `trigger.throttlePerAppSeconds` (Default `2`) — Max 1 Capture pro App
  pro 2 s. Zusätzlich zum HWND-Throttle.
- `trigger.heartbeatIntervalSeconds` (Default `30`) — Polling-Fallback.
  `0` deaktiviert Heartbeat.
- `trigger.winEvents.*` — Granular pro Event-Typ ein-/ausschalten.
- `trigger.blacklist.windowClasses` — Liste von Win32-Window-Klassen
  zum Ignorieren.
- `trigger.blacklist.processes` — Liste von Prozess-Namen zum Ignorieren.

Zusätzlich in `screenRecorder.periodicCaptureMs` (Bug-Bash Teil 2):
- `screenRecorder.periodicCaptureMs` (Default `0`) — Periodischer
  Capture-Trigger in Millisekunden. `0` deaktiviert den periodischen
  Capture komplett (kein Thread wird gestartet). Sinnvolle Werte:
  `3000`–`10000` für Video/Slideshows.
- `screenRecorder.ignoreApps` (Default `[]`) — Prozess-Namen
  (case-insensitive substring), die vom Periodic Capture ignoriert
  werden (HWND-Throttle greift, aber explizit rausfiltern ist billiger).
- `screenRecorder.ignoreUrls` (Default `[]`) — URL-Substrings
  (case-insensitive), die ignoriert werden, wenn ein App-Kontext mit
  URL bekannt ist (z. B. `"about:blank"`).
- `screenRecorder.ignoreWindowTitles` (Default `[]`) — Window-Titel-
  Substrings (case-insensitive), die ignoriert werden.

## Integration in `recall record`

CLI-Subcommand `recall record [--foreground]`:
- Startet `TriggerService` als Hosted Background Service.
- Blockiert den Hauptthread (Ctrl+C / SIGTERM zum Beenden).
- Logs nach `logs/ai-recall-record-<datum>.log`.
- `--foreground`: zusätzlich ein Heartbeat alle 5 s auf stderr
  ("alive") für manuelle Verifikation.
- Bei Windows-Sleep/Resume: nächster `EVENT_SYSTEM_FOREGROUND`
  triggert automatisch einen Capture; kein Service-Restart nötig.

## Integration mit App-Reader (Spec 0004)

- App-Reader wird **vor** OCR gefragt (Spec 0004 §"Integration").
- TriggerService ruft `AppReaderRegistry.ReadAsync(window)`.
- Erster nicht-null-Reader liefert `ContentMarkdown`.
- OCR läuft **zusätzlich** (Bild-Beweis) und wird im Capture-MD
  referenziert, auch wenn App-Reader Content geliefert hat.
- Bei `appReader.enabled = false` → nur OCR als Capture-Quelle.

## User Stories

### Trigger-Pipeline

- **TR-1** *(MVP1)* `recall record` startet einen Service, der auf
  `EVENT_SYSTEM_FOREGROUND` reagiert und für jedes neue Vordergrund-Fenster
  einen Capture erstellt.
- **TR-2** *(MVP1)* Innerhalb desselben HWND wird nicht für jeden
  Fokus-Sub-Wechsel ein Capture erstellt (`throttleMs`).
- **TR-3** *(MVP1)* Wenn der Inhalt eines HWND sich nicht geändert hat
  (gleicher Hash), wird kein neuer Capture geschrieben. Dictionary
  `lastHashes[HWND]` — verschiedene Fenster derselben App deduplizieren
  unabhängig voneinander.
- **TR-4** *(MVP1)* Maximal ein Capture pro App pro `throttlePerAppSeconds`.
- **TR-5** *(MVP1)* Heartbeat-Polling stellt sicher, dass auch bei
  verlorenen Events Captures entstehen.
- **TR-6** *(MVP1)* Self-Capture (eigener Prozess) wird gefiltert.
- **TR-7** *(MVP1)* Tooltips / Notifications werden per Class-Blacklist
  ignoriert.
- **TR-8** *(MVP1)* Modale Dialoge werden aufgezeichnet und mit
  `parentHwnd` / `parentTitle` / `parentProcess` im Frontmatter
  dokumentiert.
- **TR-9** *(MVP1)* Child-HWNDs werden via `GetAncestor(hwnd, GA_ROOT)`
  auf das Top-Level-Fenster normalisiert.

### App-Reader (bleibt Spec 0004)

- AR-1..7 aus Spec 0004 unverändert.

## Akzeptanzkriterien

- [x] `SetWinEventHook` wird mit `WINEVENT_OUTOFCONTEXT` auf eigenem
      Thread gestartet; `WINEVENT_INCONTEXT` (DLL-Injection) wird
      explizit **nicht** verwendet. — `WinEventHookDetector.cs:158`,
      Flag-Kombination `WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS`.
- [x] Event-Callback läuft auf dediziertem Thread mit eigener
      Message-Loop; enqueued `TriggerEvent` in `Channel<TriggerEvent>`. —
      `WinEventHookDetector.Loop()` (private), Message-only window
      (`HWND_MESSAGE = -3`), `GetMessage`/`DispatchMessage`-Loop.
- [x] Worker-Thread liest Channel, wendet Throttle (per HWND + per App)
      → Self-Capture-Filter → Class-Blacklist → App-Reader → OCR →
      Hash-Dedup → Capture an. — `TriggerWorker.ProcessEvent`,
      Pipeline-Schritte 1–12.
- [x] Bei `EVENT_SYSTEM_FOREGROUND` wird ein Capture ausgelöst, sofern
      Throttle OK und Hash != letzter Hash. — `TriggerWorker` Schritte 6–9.
- [x] Bei `EVENT_OBJECT_NAMECHANGE` ohne vorherigen
      `EVENT_SYSTEM_FOREGROUND`: Hash-Dedup **pro HWND** verhindert
      redundantes Capture, wenn der Titel-Wechsel keinen Inhalts-Wechsel bedeutet. —
      `HwndDedup.IsDuplicate(rootHwnd, hash, ts)` (key: HWND-Hex).
- [x] Zwei verschiedene HWNDs derselben App (z. B. zwei Notepad-Fenster)
      deduplizieren unabhängig voneinander — gleicher Hash in Fenster A
      blockt keinen Capture in Fenster B. — Dedup-Key ist HWND, nicht
      Process-Name. Getestet in `HwndDedupTests`.
- [x] `GetAncestor(hwnd, GA_ROOT)` normalisiert Child-HWNDs auf
      Top-Level-Fenster (Test mit Button-in-Word-HWND). —
      `TriggerWorker.ProcessEvent` Schritt 1.
- [x] Heartbeat-Polling läuft alle 30 s, wenn
      `trigger.heartbeatIntervalSeconds > 0`. —
      `HeartbeatThread`, cancellation-aware via `token.WaitHandle.WaitOne`.
      `0` deaktiviert den Heartbeat komplett.
- [x] Self-Capture-Filter ignoriert Events für HWNDs mit
      `pid == Process.GetCurrentProcess().Id`. — Schritt 2 im Worker.
- [x] Class-Blacklist (`trigger.blacklist.windowClasses`) wird auf
      jedes Event angewendet, bevor App-Reader aufgerufen wird. —
      Schritte 3 + 5.
- [x] Modale Dialoge: YAML-Frontmatter enthält `parentHwnd`,
      `parentTitle`, `parentProcess` (ermittelt via `GetAncestor` mit
      `GA_ROOTOWNER`). — Schritt 4b im Worker, `CaptureWriter.Write`
      optionaler `parentWindow: WindowInfo?`-Parameter.
- [x] OCR läuft **immer** zusätzlich, auch wenn App-Reader Content
      geliefert hat (Bild-Beweis im Capture). — Schritt 10.
- [x] Bei `appReader.enabled = false` ist nur OCR die Capture-Quelle. —
      Schritt 11 prüft `_config.AppReader.Enabled`.
- [x] `recall record` läuft kontinuierlich bis Ctrl+C / SIGTERM. —
      CLI `RecordCommand` mit `Console.CancelKeyPress`-Handler.
- [x] Bei Windows-Sleep/Resume triggert der nächste FOREGROUND-Event
      automatisch einen Capture (kein Service-Restart nötig). —
      `SetWinEventHook` registriert sich beim System, OS liefert
      Events nach Resume automatisch.
- [x] Unit-Tests für Throttle-Logik, Hash-Dedup, HWND-Normalisierung,
      Blacklist-Filter, Self-Capture-Filter, Frontmatter-Erzeugung. —
      189 Tests grün, davon 91 neu für Trigger-Pipeline:
      TriggerConfigTests (11), TriggerEventTests (5),
      WinEventHookDetectorTests (8), HeartbeatThreadTests (9),
      ThrottleTests + ThrottleIntPtrTests (12), HwndDedupTests (11),
      TriggerWorkerTests (15), TriggerServiceTests (11),
      CaptureWriterParentTests (5).

### Implementierungs-Resultat (2026-07-04)

- **Assembly:** `AiRecall.Trigger.dll` (neues Projekt, Namespace
  `AiRecall.Trigger`, Dependencies: Core, AppReader.Base, ScreenCapture)
- **Classes:** `ITriggerService`, `TriggerService`,
  `WinEventHookDetector`, `HeartbeatThread`, `TriggerWorker`,
  `TriggerEvent`/`TriggerKind`, `HwndDedup`
- **Generics in Core.Util:** `Throttle<TKey> where TKey : notnull`
- **Test-Count:** 189 / 189 grün (vorher 98)
- **Commits:** 791161a, f570e18, 68b97ea, c6202f3, 45aedf7, 11dea77,
  e65af93, 5d934dc

### Update 2026-07-06 (Bug-Bash Teil 2)

Spec 0005 wurde im Bug-Bash Teil 2 (Commit `d245dd2`, Cluster "Trigger-Pipeline v2") um
**Periodisches Capture** als dritte Trigger-Quelle erweitert.

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Periodisches Capture | Neue Trigger-Quelle `Periodic` für Inhalte ohne WinEventHook-Events | Video-Streams, Slideshows, Live-Daten, Browser-Scroll-Churn ändern sich visuell, ohne dass `EVENT_OBJECT_*`-Events feuern. Periodic Capture ergänzt Heartbeat als eigenständiger Capture-Auslöser (nicht nur Fallback). Sinnvolle Intervalle: 3 000–10 000 ms. |
| 2 | `TriggerKind.Periodic` | Neuer Enum-Wert `Periodic` in `AiRecall.Trigger.TriggerKind` | Konsistent mit `Heartbeat`: gleiche Worker-Pipeline, eigene Semantik für Stats/Logging. Mapping in `TriggerWorker` identisch (Throttle → App-Reader → OCR → Hash-Dedup → Capture). |
| 3 | `PeriodicCaptureThread` | Neue Klasse in `AiRecall.Trigger.dll`, dünner Wrapper um `PollThread` | Periodische Foreground-Window-Erfassung. Erbt von der gemeinsamen `PollThread`-Infrastruktur. `IntervalMs = 0` deaktiviert komplett (kein Thread). |
| 4 | `ScreenRecorderConfig.PeriodicCaptureMs` | Neue Config-Property in `screenRecorder.periodicCaptureMs` (Default `0`) | Konfiguration unter `screenRecorder.*` (nicht `trigger.*`), weil Periodic Capture semantisch ein eigener Capture-Modus ist, nicht ein Trigger-Filter. Default `0` = deaktiviert, kein Backwards-Compat-Risiko. |
| 5 | `PollThread`-Konsolidierung | `HeartbeatThread` + `PeriodicCaptureThread` sind dünne Wrapper um interne `PollThread`-Klasse | Vorher zwei parallel implementierte Klassen mit identischer Loop-Logik (Bug-Bash I-24). Jetzt: `PollThread(intervalMs, writer, triggerKind, threadName, logPrefix, ...)`. Reduziert Code-Duplikation, vereinfacht zukünftige Polling-Quellen. |
| 6 | `screenRecorder.ignoreApps/Urls/WindowTitles` | Drei neue Filter-Listen auf `ScreenRecorderConfig` | Vorab-Filter **vor** Throttle/Dedup: Periodische Captures für `about:blank`, `MsEdge.exe`-URLs ohne Content, oder System-Tools werden gar nicht erst erzeugt. Billiger als HWND-Throttle alleine, weil Periodic Capture bei `0 ms` Idle den HWND-Throttle umgehen würde. |
| 7 | Tests | 86 neue Tests in `PeriodicCaptureThreadTests` | Constructor-Validation (`IntervalMs < 0` → `Max(0, …)`), `Start/Stop`/`Dispose`-Lifecycle, Channel-Writes zählbar, `IntervalMs = 0` startet keinen Thread. |
| 8 | Test-Count | 650 → **673/673 grün** (vorher Teil 1: 416; +73 aus Bug-Bash Teil 2 Trigger-Pipeline v2 + andere Bug-Bash-Items, siehe DECISIONS-Eintrag 2026-07-06) | 86 PeriodicCaptureThreadTests + 187 TessdataManagerTests (Spec 0012-Vorbereitung) + 56 PropertyEditorFactoryTests + weitere aus Bug-Bash Teil 2. |

#### Pipeline-Erweiterung (Schritt 0b im Worker)

Periodic Capture durchläuft die volle Worker-Pipeline, mit folgenden
Erweiterungen in `TriggerWorker.ProcessEvent`:

1. **HWND normalisieren** → identisch zu Foreground/Focus.
2. **Periodic-spezifischer Vorab-Filter** (NEU): Wenn `triggerKind == Periodic`
   und (`processName` ∈ `screenRecorder.ignoreApps`
   oder `windowTitle` ∈ `screenRecorder.ignoreWindowTitles`) → skip.
3. URL-Filter (NEU): Wenn App-Reader eine URL liefert und sie in
   `screenRecorder.ignoreUrls` → skip.
4. **Throttle per HWND** → identisch (greift auch für Periodic).
5. **Throttle per App** → identisch.
6. **Self-Capture-Filter** → identisch.
7. **Class-Blacklist** → identisch.
8. App-Reader → identisch.
9. OCR → identisch.
10. Hash-Dedup → identisch.
11. Capture schreiben → identisch.

#### Verworfen (Bug-Bash Teil 2)

- **Periodic Capture als WinEventHook-Workaround für VALUECHANGE-Churn**: wäre
  semantisch falsch — VALUECHANGE feuert bei jedem Edit-Control-Update.
  Periodic Capture ist für Inhalte **ohne** Events, nicht als VALUECHAVE-Drossel.
- **`trigger.periodicCaptureMs` (in `trigger.*`)**: wäre inkonsistent, weil
  Periodic Capture kein Trigger-Filter ist. Bleibt unter `screenRecorder.*`.
- **Geteilte PollThread-Klasse als `public`**: bleibt `internal sealed`,
  die öffentlichen Klassen sind `HeartbeatThread` (für MVP1-Abwärtskompatibilität)
  und `PeriodicCaptureThread`. So bleibt die Refactoring-API sauber.
- **Periodisches Capture per WMI/Polling auf `_NETWORK_BYTES`**: würde
  Netzwerk-IO messen, nicht Window-Content. Nicht hilfreich für unseren Use-Case.

## Out of Scope (MVP1)

- Browser-Tab-spezifische Trigger-Entscheidung (Edge/Chrome mit
  mehreren Tabs → ein Capture pro Top-Level-Fenster).
- Maus-Klick-Detection als eigener Trigger (überdeckt von ValueChange
  + Selection; Click-only Trigger liefert zu viel Rauschen).
- Fokus-Detection auf spezifische UIA-Elemente (Tooltip-ähnliche
  Sub-Controls) — Blacklist-Ansatz reicht für MVP1.
- Multi-Monitor-spezifische Logik (Capture ist bildschirmkoordinatenbasiert,
  funktioniert auf jedem Monitor; keine Monitor-ID im Frontmatter).
- Persistente Trigger-History (welche Events sind geloggt worden) —
  Hash + Capture reichen für MVP1.
- Hot-Standby / Service-Installation (Windows-Service via SCM). MVP1
  startet `recall record` als User-Prozess.
- **MVP2-Tray-EXE** (Hinweis Martin 2026-07-04): Vollwertige Windows-
  Anwendung mit Notification-Area-Icon zum Steuern von `recall record`
  (Start/Stop/Pause/Status). Aktuell wird `recall record` als CLI-
  gestarteter User-Prozess betrieben. TriggerService wird in MVP2 über
  ein `ITriggerService`-Interface gekapselt, sodass CLI und Tray-EXE
  denselben Code nutzen. CLI bleibt für Scripts erhalten.
- **IPC / Service-Interface:** Aktuell kein Inter-Process-Communication.
  Für die MVP2-Tray-EXE wird ein Mechanismus nötig (Named Pipe, Signal-
  File oder Windows-Service), damit die Tray-Anwendung den
  TriggerService steuern und Status abfragen kann. MVP1 beschränkt sich
  auf CLI-Subcommands; `recall status`-Subcommand liefert bereits
  strukturierte Capture-/Skip-/Duplicate-Counts für externe Konsumenten.
- **CLI-Headless-Mode:** Für MVP2-Tray-EXE ist ein `--headless`-Flag
  an `recall record` vorgesehen, das Console-Output unterdrückt und
  nur in die Serilog-Rolling-Logs schreibt. Status läuft dann
  ausschließlich über `ITriggerService`-Properties.

## Zukunft: MVP2 — Tray-Icon-EXE

MVP1-`recall record` ist nur ein CLI-Einstiegspunkt. MVP2 bringt eine
vollwertige Windows-Anwendung mit Notification-Area-Icon:

- Start/Stop/Pause der Trigger-Pipeline per Klick
- Live-Status: Capture-Count, aktuelles Vordergrund-Fenster, Throttle-Treffer
- Schnellzugriff auf Capture-Verzeichnis (Explorer öffnen)
- Konfigurationsoberfläche (alternativ zur JSON-Datei)
- Optional: Quiet-Hours (Trigger pausiert automatisch)

`TriggerService` wird über ein `ITriggerService`-Interface gekapselt, sodass
CLI und Tray-EXE denselben Code nutzen. Die Schnittstelle wird in
Schritt F (MVP1) bereits als Interface angelegt, auch wenn nur die
CLI-Implementierung in MVP1 ausgeliefert wird — so ist die
Tray-EXE-Anbindung in MVP2 ein reines Wiring-Thema.

Status-Reports laufen bereits jetzt strukturiert über Serilog. Für
die MVP2-IPC wird eine schlanke Variante gewählt (Kandidat: Named Pipe
für bidirektionale Steuerung + Status-Polling, oder Signal-File für
unidirektionale Befehle).

Spec für die Tray-EXE folgt nach MVP1-Abschluss (Spec-Kandidat 0006 oder
Bestandteil von MVP2-Scope).

## Verwandte Specs

- [0001 — Vision & Roadmap](0001-vision.md)
- [0002 — MVP1 Scope](0002-mvp1-scope.md) (TR-1..6)
- [0003 — Active-Window Command](0003-active-window.md)
- [0004 — App-Reader Architecture](0004-app-reader.md)