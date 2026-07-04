# 0008 — Live Logviewer Window

> **Status:** 📝 Draft v0.2 (2026-07-04 22:30) — Architektur-Korrektur: in-process Log-Stream
> **Owner:** Martin
> **Abhängig von:** Spec 0006 (Tray-EXE Foundation)

## Ziel

Live-Ansicht der Serilog-Logs (vom `TriggerSupervisor` in TrayApp-Prozess)
in einem eigenen Fenster, aufrufbar über das Tray-Icon-ContextMenu.
Filter, Pause/Resume, Clear, Auto-Scroll.

## UI

- Eigenes WinForms-Fenster (`LogviewerWindow`), non-modal zur TrayApp
- **DataGridView** (Spalten: Timestamp, Level, Logger, Message)
- Virtual-Mode für Performance (kein Full-Paint pro Event)
- **Toolbar** (ToolStrip oben):
  - Filter-Combobox: `All / Verbose / Debug / Information / Warning / Error / Fatal`
  - Search-TextBox (Substring-Filter auf Message)
  - Pause/Resume-Button
  - Clear-Button
  - Auto-Scroll-Checkbox (default: an)
- **Status-Bar** (unten): `Connected | {events} events | filter: {level} | {paused}`
- **Tail-Verhalten**: neue Events werden angehängt, Auto-Scroll wenn am Ende

## Datenquelle (in-process, revidiert v0.2)

**Primär**: `InMemoryLogSink` — Custom-Serilog-Sink, der Events in einen
Ringbuffer (10.000 Einträge) im TrayApp-Prozess schreibt. `LogviewerWindow`
subscribed auf den Sink und liest direkt aus dem Buffer.

**Fallback**: bei Crash oder Sink nicht initialisiert → Tail auf
`logs/trayapp-yyyy-MM-dd.log` (Serilog RollingFile).

### InMemoryLogSink

```csharp
public sealed class InMemoryLogSink : ILogEventSink, IDisposable
{
    private readonly RingBuffer<LogEventEntry> _buffer;   // 10.000 Capacity, FIFO

    public event EventHandler<LogEventEntry>? EventEmitted;

    public void Emit(LogEvent logEvent);
    public IReadOnlyList<LogEventEntry> Snapshot();        // für initiale Population
    public void Clear();
}

public sealed record LogEventEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Logger,
    string Message,
    Exception? Exception);
```

- **Thread-Safety**: lock-basierter Append, lock-freier Read (Copy-on-Read)
- **Subscription**: TrayIconController subscribed auf `EventEmitted` für Status-Updates,
  LogviewerWindow subscribed für Live-Append
- **Persistenz**: nicht persistent — Buffer stirbt mit TrayApp. Für History → File-Tail.

### File-Fallback-Strategie

- Öffne FileStream mit `FileShare.ReadWrite | FileShare.Delete`
- Position am Ende (oder konfigurierbar: von Anfang), lese vorwärts
- Rolling-Detection: bei Datei-Rename (Serilog RollingFile) → neue Datei öffnen

## Log-Format

Plain-Text-Eintrag pro Zeile (`{timestamp-iso}|{level}|{logger}|{message}`),
intern aber typed (LogEventEntry) — Format nur für File-Fallback relevant.

## Performance

- Maximal 10.000 Zeilen im In-Memory-Buffer (Drop oldest, FIFO)
- Append-Operations batched: alle 100 ms flushen via WinForms-Timer
- Virtual-Mode DataGridView (nur sichtbare Zeilen werden gepaintet)
- Color-Coding nach Level: VRB grau, DBG blau, INF schwarz, WRN orange, ERR rot, FTL fett-rot

## Persistenz

- Beim Window-Close → Buffer-Inhalt bleibt (andere Subscriber sehen weiter)
- Optional: `Save View as .log/.txt` (Export-Current-Filtered-View)

## Tests

- `LogviewerWindowTests` (WinForms-Tests mit `Form.Show`-Mock):
  - Filter (Level): nur ERR-Events sichtbar bei Filter=Error
  - Filter (Search): "OCR" → nur OCR-Events
  - Pause: keine neuen Events sichtbar, interne Queue füllt
  - Resume: gepufferte Events werden sichtbar
  - Clear: Buffer leer, Count=0
- `InMemoryLogSinkTests`:
  - Append + Snapshot: korrekte Reihenfolge, Thread-Safety
  - Capacity-Overflow: älteste Einträge werden verworfen (FIFO)
  - Clear: Buffer leer
  - EventEmitted: Subscriber werden benachrichtigt

## Verworfen

- **Memory-Mapped-File als IPC**: durch in-process-Architektur (revidiert v0.2, Martin 22:29) überflüssig.
- **WPF-DataGrid**: Overhead, WinForms-DataGridView ist ausreichend für 10k Zeilen.
- **RichTextBox mit Color-Highlighting**: Performance zu schlecht für Live-Tail mit 10+ Events/Sek.
- **Serilog.Sinks.Seq**: externes Tool, brauchen lokale Lösung ohne Service-Abhängigkeit.
- **Log-Scrollback > 10.000 Zeilen**: Memory-Budget, ältere Logs via File lesen (separater Tab/Spec).
- **Syntax-Highlighting für Stacktraces**: nice-to-have, später.

## Offene Punkte

- Filter-Genauigkeit (Substring vs Regex) — Substring default, Regex optional via Checkbox.
- Export-Funktion (Save current view as .log/.txt) — nice-to-have.
- Theme (Dark/Light) — System-Default erstmal.
- Multi-Tab (verschiedene Logger-Quellen parallel) — YAGNI für MVP2.
- Search-Highlighting in Message-Spalte — nice-to-have.