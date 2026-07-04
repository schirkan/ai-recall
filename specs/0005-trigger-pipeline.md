# 0005 — Trigger-Pipeline (`recall record`)

> **Status:** Draft v0.1 (2026-07-04)
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
| `EVENT_OBJECT_SELECTION` | `0x8006` | Text-Selektion (optional) |
| `EVENT_SYSTEM_MENUPOPUPSTART` | `0x0005` | Menü/Kontextmenü geöffnet |

Out-of-context Hooks liefern diese Events systemweit; in-process
OBJECT-Events (z. B. `EVENT_OBJECT_INVOKED`) sind hier nicht relevant.

### Sekundär: Heartbeat-Polling

Ein 30-s-Loop (`trigger.heartbeatIntervalSeconds`) ruft
`GetForegroundWindow` auf. Zweck: Fallback, falls Events verschluckt werden
(z. B. hohe Systemlast, Sleep/Resume, schnelle User-Session-Wechsel).

### Outlook: Background-Polling (App-Reader-intern)

Bereits in Spec 0004 dokumentiert: Outlook-Reader pollt alle 60 s
(`outlook.pollIntervalSeconds`) und persistiert neue Mails.

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
    Selection,         // EVENT_OBJECT_SELECTION
    MenuPopup,         // EVENT_SYSTEM_MENUPOPUPSTART
    Heartbeat          // Polling-Fallback
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
9. **Hash-Dedup** — Wenn Hash == letzter Hash dieser App → skip Capture
   (kein Schreiben, aber Throttle-Timestamps aktualisieren).
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
      "selection": false,
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
  `selection: false` per Default (Selektionen liefern für MVP1 zu viel
  Rauschen; kann später aktiviert werden).
- `trigger.blacklist.windowClasses` — Liste von Win32-Window-Klassen
  zum Ignorieren.
- `trigger.blacklist.processes` — Liste von Prozess-Namen zum Ignorieren.

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
- **TR-3** *(MVP1)* Wenn der Inhalt eines Fensters sich nicht geändert
  hat (gleicher Hash), wird kein neuer Capture geschrieben.
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

- [ ] `SetWinEventHook` wird mit `WINEVENT_OUTOFCONTEXT` auf eigenem
      Thread gestartet; `WINEVENT_INCONTEXT` (DLL-Injection) wird
      explizit **nicht** verwendet.
- [ ] Event-Callback läuft auf dediziertem Thread mit eigener
      Message-Loop; enqueued `TriggerEvent` in `Channel<TriggerEvent>`.
- [ ] Worker-Thread liest Channel, wendet Throttle (per HWND + per App)
      → Self-Capture-Filter → Class-Blacklist → App-Reader → OCR →
      Hash-Dedup → Capture an.
- [ ] Bei `EVENT_SYSTEM_FOREGROUND` wird ein Capture ausgelöst, sofern
      Throttle OK und Hash != letzter Hash.
- [ ] Bei `EVENT_OBJECT_NAMECHANGE` ohne vorherigen
      `EVENT_SYSTEM_FOREGROUND`: Hash-Dedup verhindert redundantes
      Capture, wenn der Titel-Wechsel keinen Inhalts-Wechsel bedeutet.
- [ ] `GetAncestor(hwnd, GA_ROOT)` normalisiert Child-HWNDs auf
      Top-Level-Fenster (Test mit Button-in-Word-HWND).
- [ ] Heartbeat-Polling läuft alle 30 s, wenn
      `trigger.heartbeatIntervalSeconds > 0`.
- [ ] Self-Capture-Filter ignoriert Events für HWNDs mit
      `pid == Process.GetCurrentProcess().Id`.
- [ ] Class-Blacklist (`trigger.blacklist.windowClasses`) wird auf
      jedes Event angewendet, bevor App-Reader aufgerufen wird.
- [ ] Modale Dialoge: YAML-Frontmatter enthält `parentHwnd`,
      `parentTitle`, `parentProcess` (ermittelt via `GetAncestor` mit
      `GA_ROOTOWNER`).
- [ ] OCR läuft **immer** zusätzlich, auch wenn App-Reader Content
      geliefert hat (Bild-Beweis im Capture).
- [ ] Bei `appReader.enabled = false` ist nur OCR die Capture-Quelle.
- [ ] `recall record` läuft kontinuierlich bis Ctrl+C / SIGTERM.
- [ ] Bei Windows-Sleep/Resume triggert der nächste FOREGROUND-Event
      automatisch einen Capture (kein Service-Restart nötig).
- [ ] Unit-Tests für Throttle-Logik, Hash-Dedup, HWND-Normalisierung,
      Blacklist-Filter, Self-Capture-Filter, Frontmatter-Erzeugung.

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

## Verwandte Specs

- [0001 — Vision & Roadmap](0001-vision.md)
- [0002 — MVP1 Scope](0002-mvp1-scope.md) (TR-1..6)
- [0003 — Active-Window Command](0003-active-window.md)
- [0004 — App-Reader Architecture](0004-app-reader.md)