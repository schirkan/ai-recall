# 0006 — MVP2 Tray-Icon-EXE (Foundation)

> **Status:** 📝 Draft v0.1 (2026-07-04) — Martin-Review ausstehend
> **Owner:** Martin
> **Abhängigkeiten:** Spec 0005 (Trigger-Pipeline, `ITriggerService`), Spec 0007 (Async Conversion, `ConversionWorker`)

## Ziel

Vollwertige Windows-Anwendung mit Notification-Area-Icon zum Steuern
von `recall record` (Start/Stop/Pause/Status). CLI bleibt für Scripts
erhalten, wird aber vom MVP1-Standalone-Mode zum reinen Worker-Mode
(`--headless`) degradiert.

## Martin-Direktiven (2026-07-04)

- **CLI ist nur temporärer Einstiegspunkt** (2026-07-04, Spec 0005-Abnahme). MVP2 wird Tray-Icon-EXE.
- `ITriggerService` ist die Naht-Stelle für Wiederverwendung.
- Specs für zwei neue Features: **Live Logviewer** (Spec 0008) + **Settings-Dialog** (Spec 0009). Diese Spec 0006 definiert die gemeinsame Foundation.

## Architektur

### Neues Projekt

- **`AiRecall.TrayApp`** (WinForms, .NET 8 `net8.0-windows`)
- Output: `bin/Debug/net8.0-windows/AiRecall.TrayApp.exe`
- Startet im Hintergrund, NotifyIcon erscheint im System-Tray
- Beim Quit: sauberer Process-Kill des Subprozesses + Dispose

### Projekt-Struktur

```
src/AiRecall.TrayApp/
├── AiRecall.TrayApp.csproj              (UseWindowsForms=true)
├── Program.cs                            (SingleInstance-Mutex + Application.Run)
├── TrayAppContext.cs                     (ApplicationContext mit NotifyIcon)
├── TrayIconController.cs                 (NotifyIcon + ContextMenu)
├── ProcessSupervisor.cs                  (Start/Stop/Restart `recall record --headless`)
├── MmfLogPipe.cs                         (MemoryMappedFile als Ringbuffer)
├── Windows/
│   ├── LogviewerWindow.cs                (Spec 0008)
│   └── SettingsDialog.cs                 (Spec 0009)
├── Config/
│   ├── UserConfigLocator.cs              (%APPDATA%/AiRecall/config.json)
│   └── ConfigSchemaReflection.cs         (AppConfig POCO → PropertyDescriptor)
└── Resources/
    └── tray-icon.ico                     (16x16 + 32x32)
```

### Process-Management (ProcessSupervisor)

TrayApp startet `recall record --headless` als Subprozess:

```
AiRecall.TrayApp.exe
  └─ Process.Start("AiRecall.Cli.exe", "record --headless")
       ├─ StandardOutput (gestreamed → MmfLogPipe → LogviewerWindow)
       ├─ StandardError  (gestreamed → MmfLogPipe als Level=Error)
       └─ ExitCode       (Watcher, Auto-Restart bei Crash)
```

- **Args**: `recall record --headless --log-format=plain --log-stdout`
- **Restart-Strategie**: bei Exit ≠ 0 → 5 s Verzögerung → Restart (max 3 Versuche, dann Pause)
- **Stop**: TreeKill auf Process + Children
- **Config-Hot-Reload**: Settings-Dialog Save → ProcessSupervisor.Restart()

### Memory-Mapped-File IPC (MmfLogPipe)

- Name: `Global\AiRecall.LogPipe` (Global für Cross-Session falls später Service läuft)
- Größe: 64 KB Ringbuffer (Power-of-Two für mask-modulo)
- Format: Newline-delimited, jedes Event als UTF-8-Text:
  ```
  2026-07-04T21:23:45.123+02:00|INF|AiRecall.Trigger.TriggerService|Started capture for hwnd=0x12345
  2026-07-04T21:23:46.456+02:00|WRN|AiRecall.Conversion.ConversionWorker|OCR failed: tessdata not found
  ```
- **Schreiber**: `recall record --headless` über Custom-Serilog-Sink `MmfSink`
- **Leser**: `LogviewerWindow` über MMF-Stream mit Offset-Tracking
- **Fallback**: wenn MMF nicht verfügbar (z. B. Crash mid-write) → File-Tail auf `logs/recall-yyyy-MM-dd.log`

### NotifyIcon + ContextMenu (TrayIconController)

```
┌──────────────────────────────┐
│ 🟢 Recording                │  ← Status-Zeile (live updated)
│ ──────────────────────────── │
│ ▶ Start Recording      (S)  │  ← Toggle je nach ProcessSupervisor-State
│ ⏸ Stop Recording       (T)  │
│ ──────────────────────────── │
│ 📋 Live Logviewer…     (L)  │  ← Spec 0008
│ ⚙ Settings…           (,)   │  ← Spec 0009
│ ──────────────────────────── │
│ 🚪 Quit               (Q)   │
└──────────────────────────────┘
```

- **Icon-State**: 🟢 = Running, 🔴 = Stopped, 🟡 = Starting/Stopping
- **Tooltip**: `AiRecall — {process} running ({captures_today} captures today)`
- **Doppelklick auf Icon**: Toggle Start/Stop
- **Balloon-Tip**: bei Crash → "Recording stopped, will restart in 5s"

### Single-Instance

- Named-Mutex `Local\AiRecall.TrayApp.SingleInstance`
- Zweiter Start → Bring-To-Front via `FindWindow` + `SetForegroundWindow` (Named-Window-Message IPC)
- Sauberer Exit beim ersten Process

## Dependencies

- `AiRecall.Trigger` (für `ITriggerService` falls in-process-Variante)
- `AiRecall.Core` (Models, AppConfig, CaptureWriter)
- `AiRecall.Cli` (nur als Binary, nicht als Lib)
- `Serilog` + `Serilog.Sinks.Console` für eigenes Logging

## CLI-Änderungen (Begleitend)

- `recall record --headless --log-stdout` (neue Option) → schreibt Log-Events nach stdout (Plain-Format für Pipe-Tail)
- `recall record --log-format=json|plain` (Plain ist Default für Subprozess)
- `recall status` bleibt unverändert (Tray-App kann das auch via HTTP/File auslesen)

## Schritte (Implementierung)

| #   | Commit      | Inhalt                                                                  |
|-----|-------------|-------------------------------------------------------------------------|
| 1   | (tbd)       | `AiRecall.TrayApp`-Projekt + WinForms-NotifyIcon-Skeleton              |
| 2   | (tbd)       | `ProcessSupervisor` (Start/Stop/Restart mit Crash-Recovery)             |
| 3   | (tbd)       | `MmfLogPipe` + Custom-Serilog-Sink `MmfSink` in `AiRecall.Cli`          |
| 4   | (tbd)       | `TrayIconController` + ContextMenu-Wiring + Single-Instance             |
| 5   | (tbd)       | Settings-Hot-Reload (ProcessSupervisor.Restart nach Save)              |
| 6   | (tbd)       | Integration-Tests (Mock-ProcessSupervisor, MMF-Round-Trip)              |
| 7   | (tbd)       | Doku (PROJECT.md + DECISIONS.md + README) + Push                       |

## Verworfen

- **TrayApp in WPF**: Overhead ohne Mehrwert für Notification-Area-Use-Case.
- **Avalonia/MauiUI**: Cross-Platform unnötig (Windows-only per Spec 0001).
- **TrayApp und `recall record` im selben Prozess**: Crashes in der Worker-Pipeline reißen Tray mit runter.
- **TrayApp als reine Wrapper-EXE ohne eigene Logik**: brauchen Settings + Logviewer → eigene Forms-UI notwendig.
- **MS-Terminal-Notifier als Tray-Alternative**: User-Erlebnis ist Tray-Icon, nicht Terminal.
- **Named-Pipe statt MemoryMappedFile**: mehr Setup-Aufwand für bidirektionalen Flow; MMF ist one-way-stream only, reicht für Logs.

## Offene Punkte

- Tray-Icon-Resource (`tray-icon.ico`) — generieren oder aus Vorlage?
- Auto-Start mit Windows (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) — optional, Toggle in Settings?
- Mehrere parallele `recall record`-Instanzen mit unterschiedlichen Configs (Power-User) — out of scope für MVP2.
- Internationalisierung (DE/EN) der ContextMenu-Labels — später.