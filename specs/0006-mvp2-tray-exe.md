# 0006 — MVP2 Tray-Icon-EXE (Foundation)

> **Status:** 📝 Draft v0.2 (2026-07-04 22:30) — Architektur-Korrektur: in-process statt Subprozess
> **Owner:** Martin
> **Abhängigkeiten:** Spec 0005 (Trigger-Pipeline, `ITriggerService`), Spec 0007 (Async Conversion, `ConversionWorker`)

## Ziel

Vollwertige Windows-Anwendung mit Notification-Area-Icon zum Steuern
der Trigger-Pipeline (Start/Stop/Pause/Status). CLI bleibt für Scripts
erhalten, wird aber vom MVP1-Standalone-Mode zum reinen Worker-Mode
(`--headless`) degradiert.

## Martin-Direktiven (2026-07-04)

- **CLI ist nur temporärer Einstiegspunkt** (2026-07-04, Spec 0005-Abnahme). MVP2 wird Tray-Icon-EXE.
- `ITriggerService` ist die Naht-Stelle für Wiederverwendung.
- **In-Process-Worker statt Subprozess** (Martin 22:29): `ITriggerService` direkt aus `AiRecall.Trigger` referenzieren. TrayApp und CLI nutzen dieselbe Library, aber TrayApp hostet den Service selbst. CLI startet den Service ebenfalls in-process (Standalone-Support).
- Specs für zwei neue Features: **Live Logviewer** (Spec 0008) + **Settings-Dialog** (Spec 0009). Diese Spec 0006 definiert die gemeinsame Foundation.

## Architektur (in-process, revidiert v0.2)

### Neues Projekt

- **`AiRecall.TrayApp`** (WinForms, .NET 8 `net8.0-windows`)
- Output: `bin/Debug/net8.0-windows/AiRecall.TrayApp.exe`
- Startet im Hintergrund, NotifyIcon erscheint im System-Tray
- **In-process**: referenziert `AiRecall.Trigger` und instanziiert `ITriggerService` direkt
- Beim Quit: `ITriggerService.Dispose()` sauber

### Projekt-Struktur

```
src/AiRecall.TrayApp/
├── AiRecall.TrayApp.csproj              (UseWindowsForms=true, ProjectRef → Core+Trigger)
├── Program.cs                            (SingleInstance-Mutex + Application.Run)
├── TrayAppContext.cs                     (ApplicationContext mit NotifyIcon + TriggerSupervisor)
├── TrayIconController.cs                 (NotifyIcon + ContextMenu, Status aus Supervisor)
├── TriggerSupervisor.cs                  (Wraps ITriggerService: Start/Stop/Restart + Hot-Reload)
├── Windows/
│   ├── LogviewerWindow.cs                (Spec 0008, liest Serilog-Events direkt)
│   └── SettingsDialog.cs                 (Spec 0009, ruft TriggerSupervisor.Restart() nach Save)
├── Config/
│   ├── UserConfigLocator.cs              (%APPDATA%/AiRecall/config.json)
│   └── ConfigSchemaReflection.cs         (AppConfig POCO → PropertyDescriptor)
└── Resources/
    └── tray-icon.ico                     (16x16 + 32x32, später)
```

### TriggerSupervisor (in-process Wrapper)

`TriggerSupervisor` ist ein dünner Wrapper um `ITriggerService`:

```csharp
public sealed class TriggerSupervisor : IDisposable
{
    public ITriggerService? Service { get; private set; }
    public TriggerState State { get; private set; }  // Stopped, Starting, Running, Stopping, Crashed

    public Task StartAsync(AppConfig config, CancellationToken ct = default);
    public Task StopAsync(CancellationToken ct = default);
    public Task RestartAsync(AppConfig newConfig, CancellationToken ct = default);
    public event EventHandler<TriggerEventArgs>? TriggerEventReceived;
    public event EventHandler<SupervisorStateChangedEventArgs>? StateChanged;

    // Counter-Properties für IPC (z. B. Status-Tooltip)
    public int CaptureCount { get; }
    public int ThrottleCount { get; }
    public int CrashCount { get; private set; }
    public DateTime? LastCrashAt { get; private set; }

    public void Dispose();   // idempotent
}

public enum TriggerState { Stopped, Starting, Running, Stopping, Crashed }
```

- **Start**: erstellt `TriggerService` mit Config, ruft `StartAsync()`. Setzt State = Starting → Running.
- **Stop**: ruft `StopAsync()`, setzt State = Stopping → Stopped.
- **Restart**: Stop → Start mit neuer Config. Atomic auf UI-Thread.
- **Crash-Recovery**: `TriggerService` exposet `OnError`-Event. Bei unbehandeltem Fehler → State = Crashed → automatischer Restart nach 5 s (max 3 Versuche, dann manueller Re-Start nötig).
- **Hot-Reload**: Settings-Dialog Save → `RestartAsync(newConfig)`.
- **Logging**: `TriggerSupervisor` reicht alle `TriggerEvent`s per Event-Handler an Interessenten (TrayIcon für Tooltip, Logviewer für Live-View) weiter.

### NotifyIcon + ContextMenu (TrayIconController)

```
┌──────────────────────────────┐
│ 🟢 Running — 42 captures    │  ← Status-Zeile (live updated)
│ ──────────────────────────── │
│ ⏸ Stop Recording       (T)  │  ← Toggle je nach TriggerSupervisor.State
│ ──────────────────────────── │
│ 📋 Live Logviewer…     (L)  │  ← Spec 0008
│ ⚙ Settings…           (,)   │  ← Spec 0009
│ ──────────────────────────── │
│ 🚪 Quit               (Q)   │
└──────────────────────────────┘
```

- **Icon-State**: 🟢 = Running, 🔴 = Stopped, 🟡 = Starting/Stopping, ⚠ = Crashed
- **Tooltip**: `AiRecall — {state} ({captures_today} captures today)`
- **Doppelklick auf Icon**: Toggle Start/Stop
- **Start-Item fehlt** wenn Running; **Stop-Item fehlt** wenn Stopped (single Item "Stop" oder "Start")

### Single-Instance

- Named-Mutex `Local\AiRecall.TrayApp.SingleInstance`
- Zweiter Start → SendMessage via `WM_COPYDATA` an erstes Fenster ("ShowLogviewer" / "ShowSettings" / "ExitSecondInstance")
- Sauberer Exit beim ersten Process

### Datenfluss (in-process, kein IPC)

```
┌──────────────────────────────────────────────────────────────┐
│                      AiRecall.TrayApp.exe                     │
│                                                              │
│  ┌─────────────────┐    events    ┌───────────────────────┐  │
│  │ TriggerSupervisor├────────────►│ TrayIconController    │  │
│  │  (ITriggerService) │            │ (Status, Tooltip)     │  │
│  └────────┬─────────┘            └───────────────────────┘  │
│           │ events                                          │
│           │                                                  │
│  ┌────────▼─────────┐                                       │
│  │ Serilog Logger   ├───► LogviewerWindow (Spec 0008)       │
│  │  (subscribes via  │     liest direkt aus Serilog-Buffer  │
│  │   custom sink)    │                                       │
│  └──────────────────┘                                       │
│                                                              │
│  ┌─────────────────┐   reload   ┌───────────────────────┐  │
│  │ SettingsDialog   ├───────────►│ TriggerSupervisor     │  │
│  │  (Spec 0009)     │            │ .RestartAsync()       │  │
│  └─────────────────┘            └───────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

**Vorteil gegenüber Subprozess**: keine Serialisierung, keine Latenz, keine Cold-Start, kein MMF.

## Dependencies

- `AiRecall.Trigger` (für `ITriggerService`)
- `AiRecall.Core` (Models, AppConfig, CaptureWriter)
- `AiRecall.Conversion` (für `ConversionWorker`-Property, falls TrayApp `TriggerService` mit Conversion-Worker erstellt)
- `Serilog` + `Serilog.Sinks.File` für eigenes Logging

## CLI-Änderungen (minimal)

- `recall record --headless` startet `ITriggerService` weiterhin in-process (wie in Spec 0005).
- `recall status` bleibt unverändert.
- TrayApp ist **kein Wrapper** mehr — sie ist eine alternative UI für denselben Code.

## Schritte (Implementierung, revidiert)

| #   | Commit      | Inhalt                                                                  |
|-----|-------------|-------------------------------------------------------------------------|
| 1   | `cff2b50`   | `AiRecall.TrayApp`-Projekt + WinForms-NotifyIcon-Skeleton ✅ DONE       |
| 2   | (tbd)       | `TriggerSupervisor` (in-process `ITriggerService`-Wrapper, Start/Stop/Restart, Crash-Recovery) |
| 3   | (tbd)       | `InMemoryLogSink` (Serilog-Sink, in-process Buffer für Logviewer)        |
| 4   | (tbd)       | `TrayIconController` aktiviert Start/Stop, Status-Subscriptions          |
| 5   | (tbd)       | `LogviewerWindow` (Spec 0008)                                            |
| 6   | (tbd)       | `SettingsDialog` (Spec 0009)                                              |
| 7   | (tbd)       | Integration-Tests (Mock-TriggerSupervisor, Hot-Reload-Round-Trip)        |
| 8   | (tbd)       | Doku (PROJECT.md + DECISIONS.md + README) + Push                         |

## Verworfen

- **Subprozess-Spawn** (revidiert v0.2, Martin 22:29): TrayApp ist ohnehin tot ohne Worker — Isolation bringt nichts. Cold-Start, MMF-IPC und Process-Supervision sind unnötige Komplexität.
- **TrayApp in WPF**: Overhead ohne Mehrwert für Notification-Area-Use-Case.
- **Avalonia/MauiUI**: Cross-Platform unnötig (Windows-only per Spec 0001).
- **MS-Terminal-Notifier als Tray-Alternative**: User-Erlebnis ist Tray-Icon, nicht Terminal.
- **MemoryMappedFile als IPC**: durch in-process-Architektur überflüssig.
- **Named-Pipe für Log-Streaming**: durch in-process-Architektur überflüssig.

## Offene Punkte

- Tray-Icon-Resource (`tray-icon.ico`) — generieren oder aus Vorlage?
- Auto-Start mit Windows (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) — optional, Toggle in Settings?
- Trigger-Service und Conversion-Worker gleichzeitig starten — `TriggerService` hat bereits `ConversionWorker`-Property (Spec 0007), wird in `TriggerSupervisor.StartAsync` mit-gewired.
- Mehrere parallele `TriggerService`-Instanzen mit unterschiedlichen Configs (Power-User) — out of scope für MVP2.