# 0008 — Live Logviewer Window

> **Status:** 📝 Draft v0.1 (2026-07-04) — Martin-Review ausstehend
> **Owner:** Martin
> **Abhängig von:** Spec 0006 (Tray-EXE Foundation)

## Ziel

Live-Ansicht der Serilog-Logs (von `recall record --headless`) in einem
eigenen Fenster, aufrufbar über das Tray-Icon-ContextMenu. Filter,
Pause/Resume, Clear, Auto-Scroll.

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
- **Status-Bar** (unten): `Connected to {pid} | {events} events | filter: {level} | {paused}`
- **Tail-Verhalten**: neue Events werden angehängt, Auto-Scroll wenn am Ende

## Datenquelle

**Primär**: Memory-Mapped-File `Global\AiRecall.LogPipe` (Spec 0006 §MmfLogPipe).
**Fallback**: Tail auf `logs/recall-yyyy-MM-dd.log` (Serilog RollingFile).
**Bei beiden Pfaden gleich**: einfacher `StreamReader`-Reader mit Offset-Tracking.

### MMF-Reader-Strategie

- Polling alle 100 ms (kein Push-Event, MMF ist nicht signal-fähig)
- Lese neuen Offset seit letztem Read → parse Lines → append
- Wrap-Around bei Ringbuffer: bei Offset < letzterOffset → Resync von vorne (kleiner Datenverlust akzeptabel)
- Buffer-Size-Check: wenn > 80% voll → Warning "log pipe saturating, consider reducing verbosity"

### File-Fallback-Strategie

- Öffne FileStream mit `FileShare.ReadWrite | FileShare.Delete`
- Position am Ende, lese vorwärts
- Rolling-Detection: bei Datei-Rename → neue Datei öffnen

## Log-Format

Plain-Text, Pipe-delimited (vom `MmfSink` geschrieben):

```
{timestamp-iso}|{level}|{logger}|{message}
{timestamp-iso}|{level}|{logger}|{message-with-newlines-escaped}
```

- Timestamp: ISO-8601 mit Offset (`2026-07-04T21:23:45.123+02:00`)
- Level: `VRB / DBG / INF / WRN / ERR / FTL`
- Logger: vollqualifizierter Klassen-Name
- Message: kann `|` enthalten, dann escaped als `\|`
- Newlines in Message: escaped als `\n` (Literal)

## Performance

- Maximal 10.000 Zeilen im In-Memory-Buffer (Drop oldest, FIFO)
- Append-Operations batched: alle 100 ms flushen
- Virtual-Mode DataGridView (nur sichtbare Zeilen werden gepaintet)
- Color-Coding nach Level: VRB grau, DBG blau, INF schwarz, WRN orange, ERR rot, FTL fett-rot

## Persistenz

- Beim Window-Close → Buffer verwerfen (kein Persistieren)
- Optional: `Save View as .log/.txt` (Export-Current-Filtered-View)

## Tests

- `LogviewerWindowTests` (WinForms-Tests mit `Form.Show`-Mock):
  - Filter (Level): nur ERR-Events sichtbar bei Filter=Error
  - Filter (Search): "OCR" → nur OCR-Events
  - Pause: keine neuen Events sichtbar, interne Queue füllt
  - Resume: gepufferte Events werden sichtbar
  - Clear: Buffer leer, Count=0
  - MMF-Wrap-Around: simulierter Wrap → Resync-Warning-Log
  - File-Fallback: MMF nicht verfügbar → File-Tail-Modus, Read vom Test-Log

## Verworfen

- **WPF-DataGrid**: Overhead, WinForms-DataGridView ist ausreichend für 10k Zeilen.
- **RichTextBox mit Color-Highlighting**: Performance zu schlecht für Live-Tail mit 10+ Events/Sek.
- **Serilog.Sinks.Seq**: externes Tool, brauchen lokale Lösung ohne Service-Abhängigkeit.
- **Log-Scrollback > 10.000 Zeilen**: Memory-Budget, ältere Logs via File lesen (separater Tab/Spec).
- **Syntax-Highlighting für Stacktraces**: nice-to-have, später via AvsAnsiColorParser.
- **Remote-Logviewer (TCP)**: nur lokale MVP2-Anforderung.

## Offene Punkte

- Filter-Genauigkeit (Substring vs Regex) — Substring default, Regex optional via Checkbox.
- Export-Funktion (Save current view as .log/.txt) — nice-to-have.
- Theme (Dark/Light) — System-Default erstmal.
- Multi-Tab (verschiedene Logger-Qullen parallel) — YAGNI für MVP2.
- Search-Highlighting in Message-Spalte — nice-to-have.