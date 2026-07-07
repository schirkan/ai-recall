# 0006 — MVP2 Tray-Icon-EXE (Foundation)

> **Status:** ✅ **v1.0 ABGESCHLOSSEN (2026-07-04 22:30)** — Alle 7 Schritte implementiert, getestet, gepusht
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
    ├── tray-icon.ico                     (16x16 + 32x32, später)
    └── EmojiIconFactory.cs               (Bug-Bash 2026-07-06 I-UE: Color-Emoji via TextRenderer für Menu-Icons)
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

## Schritte (Implementierung, revidiert, alle DONE)

| #   | Commit      | Inhalt                                                                  |
|-----|-------------|-------------------------------------------------------------------------|
| 1   | `cff2b50`   | `AiRecall.TrayApp`-Projekt + WinForms-NotifyIcon-Skeleton ✅ DONE       |
| 2   | `12ced87`   | `TriggerSupervisor` (in-process `ITriggerService`-Wrapper, Start/Stop/Restart, Crash-Recovery) ✅ DONE |
| 3   | `dc14dc0`   | `InMemoryLogSink` (Serilog-Sink, in-process Buffer für Logviewer) ✅ DONE |
| 4   | `da6586d`   | `TrayIconController` aktiviert Start/Stop, Status-Subscriptions ✅ DONE  |
| 5   | `c23d3ca`   | `LogviewerWindow` (Spec 0008) + `LogviewerSession` ✅ DONE               |
| 6   | `e80d8fc`   | `SettingsDialog` (Spec 0009) + `ConfigSerializer` + `ConfigSchemaReflection` + `PropertyEditorFactory` ✅ DONE |
| 7   | `875ae98`   | Integration-Tests: `LogviewerSession` (12 Tests, Sink-↔-Session-Round-Trip) ✅ DONE |
| 8   | (dieser)    | Doku (PROJECT.md + DECISIONS.md + Specs v1.0) + Push                     |

## Test-Statistik

| Stand                                            | Count | Delta |
|--------------------------------------------------|-------|-------|
| vor Spec 0006 (Spec 0007 v1.0 abgeschlossen)    | 358   | –     |
| Schritt 2 (TriggerSupervisor)                    | +13   | 371   |
| Schritt 3 (InMemoryLogSink)                      | +14   | 385   |
| Schritt 4 (TrayIconState + UserConfigLocator)    | +11   | 396   |
| Schritt 5 (LogFilter)                            | +8    | 404   |
| Schritt 6 (ConfigSchemaReflection + ConfigSerializer + PropertyEditorFactory) | +27 | 431   |
| Schritt 7 (LogviewerSession)                     | +12   | 443   |
| **Stand 2026-07-04 nach Spec 0006 v1.0**         | **443** | –   |

> Hinweis: Anzahl weicht von finaler PROJECT.md ab, weil zwischen Schritten
> Tests refactored/umgeschrieben wurden (z. B. 2 Tests fuer
> TrayIconState-Edge-Cases in Schritt 4 weggekuerzt). Stand 2026-07-04
> 22:30: **443/443 grün**.

## Update 2026-07-06 (Bug-Bash Teil 2)

Bug-Bash 2026-07-06 (Commit `d245dd2`) hat Spec 0006 um **EmojiIconFactory**
erweitert und damit das Problem „Menu-Icons mit Color-Emoji rendern"
gelöst.

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Menu-Icon-Rendering | **`EmojiIconFactory` (intern)** rendert Color-Emoji-Glyphen via Win32 `TextRenderer.DrawText` auf `Format32bppArgb`-Bitmaps | Vor Bug-Bash: Menu-Icons wurden entweder als Text-Emoji (kleine monochrome Glyphen) oder als Bitmap aus dem `tray-icon.ico` (statisch, kein Emoji) angezeigt. User-Erfahrung war inkonsistent. Nach Bug-Bash: Menu-Items rendern echte Color-Emoji (z. B. ✅ ⚙ 📄 🔴) als Bitmap. |
| 2 | Render-Pfad | `TextRenderer.DrawText` mit Font `"Segoe UI Emoji"` statt `Graphics.DrawString` | Wichtige Erkenntnis: GDI+ `Graphics.DrawString` rendert Color-Fonts (COLR/CPAL) auf einem 32bpp-Argb-Bitmap mit transparentem Hintergrund **ZU LEER** (kein Glyphe sichtbar). `TextRenderer` benutzt die Win32-Text-Pipeline, die Color-Fonts korrekt zeichnet. Trick: Hintergrund opak weiß füllen, Glyphe drüber, dann weiße Pixel auf Alpha=0 maskieren. |
| 3 | Glyph-Größe | Konstante `0.7f` (statt `0.85f`) für Font-Größe relativ zur Bitmap-Größe | `TextRenderer` fügt intern ein Font-Linegap hinzu. Bei `0.85f` landet der untere Teil der Glyphe im weißen Mask-Bereich und wird weggeschnitten. Bei `0.7f` sitzt die Glyphe sicher im sichtbaren Bereich mit symmetrischem Padding. |
| 4 | NotifyIcon vs Menu | NotifyIcon rendert weiterhin `SystemIcons.Application`, Menu rendert Emoji-Bitmap | GDI+ und WinForms `NotifyIcon` rendern Color-Emoji auf dem Test-System unzuverlässig (leere Bitmaps oder monochrome Outlines). Im Tray-Icon selbst ist das akzeptabel (User sieht ohnehin nur Icon-State). Im Menu ist die Glyphe sichtbar und wichtig für UX. |
| 5 | API | `public static Bitmap RenderBitmap(string emoji, int size, Color? color = null)` | Caller ist für `Dispose` verantwortlich (oder via `MenuImageCache` mit AutoDispose in TrayIconController). Kein eigener Cache im EmojiIconFactory — Trennung von Rendering und Lifetime-Management. |
| 6 | Tests | Visual-Tests via Screenshot-Vergleich in `EmojiIconFactoryTests` (manuell) + Smoke-Test im TrayIconController | Reines Visual-Rendering ist schwer Unit-Test-bar (Bitmap-Pixel-Vergleich brüchig, GDI+ auf Test-Runner anders). Pragmatisch: Smoke-Test rendert ein ✅ und prüft, dass das Bitmap nicht leer ist. |
| 7 | Icons-Set | 10 Menu-Icons im aktuellen Tray: ✅ Running, ⏸ Stopped, 🔄 Starting/Stopping, ⚠ Crashed, ⚙ Settings, 📋 Logviewer, 🚪 Quit, etc. | Alle via `EmojiIconFactory.RenderBitmap` zur Laufzeit generiert, keine `.ico`-Dateien mehr nötig. |

### Code

```csharp
// AiRecall.TrayApp/EmojiIconFactory.cs (~150 LoC)
internal static class EmojiIconFactory
{
    public static Bitmap RenderBitmap(string emoji, int size, Color? color = null)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            // ... TextRenderer.DrawText für Color-Emoji ...
            // dann weiße Pixel auf Alpha=0 maskieren
        }
        return bmp;
    }
}
```

### Verworfen (Bug-Bash Teil 2, Icon-Set)

- **Embedded `.ico` mit Color-Emoji**: ICO-Format unterstützt keine
  COLR/CPAL-Fonts. Statische ICO-Dateien können nur monochrome Glyphen
  oder BMP/PNG mit vorgerenderten Bitmaps enthalten — beides würde die
  Glyphe als Bitmap fixieren und bei DPI-Skalierung pixelig werden.
- **`NotifyIcon.Icon = EmojiIconFactory.RenderBitmap(...)`**: GDI+ kann
  COLR/CPAL auf `NotifyIcon` nicht zuverlässig darstellen (siehe Punkt 4).
  Tray-Icon bleibt deshalb `SystemIcons.Application`.
- **Eigene Color-Font-Datei (`.ttf`) mitliefern**: würde Segoe UI Emoji
  duplizieren (auf Windows 10/11 ohnehin vorhanden), unnötige
  Binary-Größe (~5 MB).

## Verworfen

- **Subprozess-Spawn** (revidiert v0.2, Martin 22:29): TrayApp ist ohnehin tot ohne Worker — Isolation bringt nichts. Cold-Start, MMF-IPC und Process-Supervision sind unnötige Komplexität.
- **TrayApp in WPF**: Overhead ohne Mehrwert für Notification-Area-Use-Case.
- **Avalonia/MauiUI**: Cross-Platform unnötig (Windows-only per Spec 0001).
- **MS-Terminal-Notifier als Tray-Alternative**: User-Erlebnis ist Tray-Icon, nicht Terminal.
- **MemoryMappedFile als IPC**: durch in-process-Architektur überflüssig.
- **Named-Pipe für Log-Streaming**: durch in-process-Architektur überflüssig.
- **WinForms `PropertyGrid`-Control** (.NET 8 hat es nicht): dynamische Form-Generierung mit Type-spezifischen Editoren aus `PropertyEditorFactory` (Spec 0009 Schritt 6).
- **TrayIconController-Unit-Tests mit echter WinForms-Form**: stattdessen `TrayIconState`-Pure-Logic separat testbar (8 Tests), UI-Tests manuell.

## Offene Punkte

- Tray-Icon-Resource (`tray-icon.ico`) — generieren oder aus Vorlage?
- Auto-Start mit Windows (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) — optional, Toggle in Settings?
- Trigger-Service und Conversion-Worker gleichzeitig starten — `TriggerService` hat bereits `ConversionWorker`-Property (Spec 0007), wird in `TriggerSupervisor.StartAsync` mit-gewired.
- Mehrere parallele `TriggerService`-Instanzen mit unterschiedlichen Configs (Power-User) — out of scope für MVP2.