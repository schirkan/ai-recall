# 0007 — Async Document Conversion Pipeline

> **Status:** ✅ **v1.0 ABGESCHLOSSEN (2026-07-04)** — Alle 7 Schritte implementiert, getestet, gepusht
> **Owner:** Martin
> **Branches/Commits:** `fbce705` (v0.1) → `fc732ba` (v0.2 Pandoc raus) → `348b732` (v0.3 OCR+Channel) → `704155e` (v0.4 kein Legacy) → `3a98e04` (Schritt 1+2) → `f176bea` (Schritt 3) → `9c7d9b5` (Schritt 4+5) → `de83a7e` (Schritt 6) → `84afab7` (Schritt 7)

## Ziel

App-Reader entkoppeln von MD-Generierung. App-Reader liefern nur **strukturierte
Metadaten** (Title, FilePath, ggf. UIA-Content). Eine zentrale, **async**
Conversion-Pipeline assemblet daraus das finale `*.conversion.md`.

## Martin-Direktiven (chronologisch)

| Zeit       | Direktive                                                                                              |
|------------|--------------------------------------------------------------------------------------------------------|
| 19:12      | „Pandoc ist Performance-mäßig raus" — Konverter bleiben in-process (.NET-Libraries)                   |
| 19:25      | „OCR ebenfalls async in der Conversion-Pipeline" — keine separaten OCR-Pass                           |
| 19:25      | „In-process `Channel<string>` statt FileSystemWatcher" — keine Disk-Polling                            |
| 20:01      | „Das Tool ist neu — kein Legacy-Handling" — `--include-legacy`-Flag entfällt                          |

## Ziel-Architektur (erreicht)

### App-Reader (dünn)

`AppReader.Read(window)` liefert nur:

- **Title** aus Window-Titel (ParseTitle)
- **FilePath** aus COM-Interop (Office) oder nicht verfügbar
- **UIA-Content** optional als Roh-Text (best effort)
- **IsThinReader = true** → keine Content-MD-Erzeugung mehr im Reader

**Output:** `AppReaderResult` mit:

- `ContextLabel`: Filename oder Window-Titel
- `ContextKind`: `"document"`, `"spreadsheet"`, `"presentation"`, …
- `Extra`: `{ filePath, fileName, uiaContent, source, hasContent }`
- `ContentMarkdown`: **nur Platzhalter** „_(siehe .content.md)_"
- `IsThinReader = true`

### Zentrale `DocumentConverter`-Klasse

In neuer DLL **`AiRecall.Conversion`** (Namespace `AiRecall.Conversion`):

```csharp
public static class DocumentConverter
{
    public static string? Convert(string filePath, int maxChars = 64*1024, ILogger? logger = null);
    public static string? GetConverterForFile(string filePath);
    public static bool HasConverter(string filePath);
}
```

Strategie: **Format-Extension-basiert**, alle Konverter in-process (kein Spawn).

| Extension                          | Konverter                                       |
|------------------------------------|-------------------------------------------------|
| `.txt`/`.md`/`.log`/`.csv`         | Plain-Read                                      |
| `.docx`/`.doc`                     | DocumentFormat.OpenXml Wordprocessing           |
| `.xlsx`/`.xls`                     | DocumentFormat.OpenXml Spreadsheet (MD-Tabelle) |
| `.pptx`/`.ppt`                     | DocumentFormat.OpenXml Presentation (Slide-Liste) |
| `.pdf`                             | UglyToad.PdfPig                                 |
| `.html`/`.htm`                     | ReverseMarkdown                                 |
| unbekannt (odt, latex, epub, rtf)  | `null` + Log                                    |

### Async `ConversionWorker`

In `AiRecall.Conversion`:

```csharp
public sealed class ConversionWorker : IDisposable
{
    public ConversionWorker(AppConfig config, ILogger logger, IOcrEngine? ocrEngine = null);
    public ValueTask EnqueueAsync(string captureMdPath, CancellationToken ct = default);
    public bool TryEnqueue(string captureMdPath);
    public int PendingCount, CompletedCount, FailedCount, PartialCount, OcrSkippedCount, OcrErrorCount;
    public void Stop();
    public void Dispose();
}
```

- **In-process `Channel<string>`** (SingleReader/MultiWriter, unbounded)
- **Background-Task** liest Channel und verarbeitet
- Pipeline pro Capture:
  1. Parse Frontmatter (`filePath`, `screenshot`, `uiaContent`)
  2. **DocumentConverter** wenn `filePath` → `## Document content (via {converter})` Section
  3. **OCR** (async) wenn `screenshot` → `## OCR Content (via {engine})` Section
  4. **App-Reader-UIA** wenn `uiaContent` → `## App Reader Content (UIA)` Section
  5. Schreibt `*.conversion.md` (nur wenn mindestens eine Section da)
  6. Updated MD-Frontmatter: `conversion: done|partial|failed`, `conversionSteps: doc=ok,…;ocr=ok,…`

### OCR-Engine-Interface (Schritt 4)

```csharp
public interface IOcrEngine
{
    string Name { get; }
    Task<string> ExtractTextAsync(byte[] pngBytes, CancellationToken ct = default);
}
```

Implementierungen:

- `TesseractOcrEngineAdapter` — wrappt bestehenden `OcrEngine` via `Task.Run`
- `NullOcrEngine` — Null-Object (Default), gibt immer leeren String

### TriggerWorker-Integration (Schritt 6)

`TriggerWorker` ruft App-Reader **vor** `CaptureWriter.WritePending` auf, übergibt
`filePath`/`uiaContent` aus dem `Extra`-Dict ins Pending-MD-Frontmatter. Bei
`IsThinReader=true` wird **kein** `CaptureWriter.WriteContent` mehr aufgerufen.

`TriggerService` besitzt den `ConversionWorker` (Default: `TesseractOcrEngineAdapter`,
Fallback `NullOcrEngine` bei tessdata-Init-Fehler). `ConversionWorker` ist public
Property auf `TriggerService`, IDisposable-Pattern mit Ownership-Flag.

## Persistenz-Layout (erreicht)

Pro Capture entstehen 2–3 Dateien:

```
{root}/yyyy-MM-dd/{process}/{HHmmss-fff}-{title-slug}.png          ← Screenshot (immer)
{root}/yyyy-MM-dd/{process}/{HHmmss-fff}-{title-slug}.md           ← Capture-MD mit Frontmatter
{root}/yyyy-MM-dd/{process}/{HHmmss-fff}-{title-slug}.content.md   ← nur nicht-dünne App-Reader (Browser/Notepad/Explorer)
{root}/yyyy-MM-dd/{process}/{HHmmss-fff}-{title-slug}.conversion.md ← async Conversion-Output (Document + OCR + AppReader-UIA)
```

**Wichtig:** Bei dünnen Readern (Word/Excel/PowerPoint) gibt es **kein**
`*.content.md`. Der ConversionWorker schreibt das `*.conversion.md`.

## Schritte (chronologisch implementiert)

| #   | Commit      | Inhalt                                                                                              |
|-----|-------------|-----------------------------------------------------------------------------------------------------|
| v0.1 | `fbce705`  | Initiale Spec + Schnittstellen-Skizzen                                                              |
| v0.2 | `fc732ba`  | Pandoc raus (Martin-Direktive Performance)                                                          |
| v0.3 | `348b732`  | OCR ebenfalls in Pipeline, `Channel<string>` statt FileSystemWatcher (Martin-Direktive)              |
| v0.4 | `704155e`  | Kein Legacy-Handling (Martin-Direktive Tool ist neu)                                                |
| 1+2  | `3a98e04`  | `AiRecall.Conversion`-DLL + `DocumentConverter` (37 Tests)                                          |
| 3    | `f176bea`  | `CaptureWriter.WritePending`/`UpdateConversionStatus` + `ConversionWorker` (15 Tests)                |
| 4+5  | `9c7d9b5`  | `IOcrEngine`/`TesseractOcrEngineAdapter`/`NullOcrEngine` + `recall convert` Subcommand (5 Tests)     |
| 6    | `de83a7e`  | `TriggerService`-Integration mit `ConversionWorker` (6 Tests)                                       |
| 7    | `84afab7`  | Word/Excel/PowerPoint-Reader dünn (`IsThinReader=true` + `ContentMarkdown`-Platzhalter, 1 neuer Test)|

## Konfiguration (final)

```json
{
  "ocr": {
    "engine": "tesseract",
    "languages": ["deu", "eng"],
    "tessDataPath": "tessdata"
  },
  "conversion": {
    "enabled": true,
    "maxTextKB": 64,
    "batchSize": 2,
    "conversionTimeoutSeconds": 30
  },
  "appReader": {
    "documents": {
      "maxTextKB": 64,
      "enableUiaExtraction": true
    }
  }
}
```

`OcrConfig` bleibt separat am Root (`ocr.*`) — Backward-Compat mit bestehender
Config. Conversion-spezifische Felder unter `conversion.*`.

## NuGet-Packages (final)

| Package                       | Version            | Lizenz      | Zweck                          |
|-------------------------------|--------------------|-------------|--------------------------------|
| DocumentFormat.OpenXml        | 3.5.1              | MIT         | docx/xlsx/pptx (MS, 700M+)     |
| UglyToad.PdfPig               | 1.7.0-custom-5     | Apache 2.0  | PDF (21M+)                     |
| ReverseMarkdown               | 3.13.0             | MIT         | HTML → MD (bereits vorhanden)  |

Pandoc, ClosedXML, NPOI, iText7 explizit verworfen — siehe DECISIONS.md.

## Test-Statistik

| Stand        | Count | Delta |
|--------------|-------|-------|
| vor Spec 0007 (nach Schritt 7 App-Reader dünn wäre falsch hier) | 271   | –     |
| nach Schritt 1+2 (DocumentConverter)                            | 308   | +37   |
| nach Schritt 3 (ConversionWorker)                               | 323   | +15   |
| nach Schritt 4+5 (OcrWorker + ConvertCommand)                   | 328   | +5    |
| nach Schritt 6 (TriggerService-Integration)                     | 334   | +6    |
| nach Schritt 7 (App-Reader dünn, Tests umgeschrieben)           | 331   | -3    |
| **Stand 2026-07-04 nach Schritt 8 (final)**                     | **331** | –   |

> Hinweis: Schritt 7 hat Tests **reduziert** weil die alten App-Reader-Tests
> (COM-Text, Tabellen-Asserts) durch neue dünnere Tests (Placeholder, Extra-Keys)
> ersetzt wurden. Die `ConversionWorkerOcrTests` haben +1 neuen Test für die
> UIA-Content-Section.

## CLI

| Befehl                            | Zweck                                                                 |
|-----------------------------------|-----------------------------------------------------------------------|
| `recall record --headless`        | Trigger-Pipeline laufen lassen (Spec 0005)                            |
| `recall record --trigger-mode=…`  | `events` (WinEventHook), `polling` (Heartbeat), `both` (Default)       |
| `recall status [--json]`          | Diagnose + MVP2-IPC-Vorbereitung                                     |
| `recall convert [--path …]`       | Recovery: scannt Capture-Root, enqueued `pending` Captures in ConversionWorker (Schritt 5) |
| `recall active-window`            | Einmal-Snapshot (Sync-Variante)                                       |
| `recall list-windows`             | Listet sichtbare Fenster                                              |

## Verworfen (YAGNI)

- **Eigener Office-OpenXML-Writer** zum Reverse-Konvertieren (MD → docx): nicht im Scope.
- **Pandoc-Integration** (Martin 2026-07-04 19:12): Performance wichtiger als Format-Coverage.
- **Streaming-Konvertierung** (Pipe zu externem Tool ohne Temp-File): unnötig, da in-process.
- **Worker als Windows-Service**: zu komplex. Background-Task im selben Prozess reicht.
- **Legacy-Handling** (Martin 2026-07-04 20:01): Tool ist neu, keine alten Captures zu konvertieren.
- **FileSystemWatcher** (Martin 2026-07-04 19:25): in-process `Channel<string>` reicht.

## Offene Punkte (für nach Spec 0007)

1. **OCR-Tessdata-Doku** in README ausführlicher (welche Sprachen, Download-Links)
2. **Outlook App-Reader** (Spec 0004 — noch offen)
3. **MVP2 Tray-Icon-EXE** (Spec 0006) — nutzt `ITriggerService` und `ConversionWorker` direkt
4. **Performance-Tuning** Tesseract: Preprocessing (Binarization, Deskew) optional
5. **PDF-Inhalt via DocumentConverter** (`*.pdf` → `## Document content (via pdfpig)`) — bereits implementiert, weitere Tests für Edge-Cases (verschlüsselte PDFs, mehrseitige Dokumente)
