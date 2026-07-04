# 0007 — Async Document Conversion Pipeline

> **Status:** v0.4 final (2026-07-04) — Alle offenen Fragen geklärt, kein Legacy-Handling (Martin 2026-07-04 20:01: Tool ist neu, keine Legacy-Captures)
> **Owner:** Martin
> **Refactored:** Spec 0004 Iter. 4 — App-Reader → DocumentConverter + async pipeline

## Motivation

Aktuell ist die MD-Konvertierung in den App-Readern verteilt:

- `WordAppReader.Read()` baut Word-MD direkt im StringBuilder
- `ExcelAppReader.Read()` baut Excel-MD direkt im StringBuilder
- `PowerPointAppReader.Read()` baut PowerPoint-MD direkt im StringBuilder
- `OfficeComInterop.Convert2DArrayToMarkdownTable()` ist der einzige zentrale Helper (Excel-spezifisch)

**Probleme:**

1. **Schema-Duplikation.** Ein neues Feld im Document-Block (z. B.
   Dateigröße, Hash, letzte Änderung) muss in 3 Readern gepflegt werden.
2. **Format-Limit.** Aktuell nur die in Office geöffneten Formate
   (Word/Excel/PowerPoint). Andere (txt, html, csv, …) müssen in jedem
   Reader einzeln behandelt werden.
3. **Sync im Capture-Pfad.** Die Konvertierung läuft synchron in
   `recall record`, kann den Capture-Loop verlangsamen (Office-COM,
   OCR-Engines, …).
4. **COM-Abhängigkeit.** Für Word/Excel/PowerPoint setzt der Reader
   Office voraus. Eine txt- oder pdf-Datei braucht das nicht.
5. **Keine Beliebig-Datei-Strategie.** Wenn Martin `C:\path\file.pdf`
   konvertieren will, muss er es erst in einem Office-kompatiblen
   Programm öffnen — oder wir brauchen eine separate Funktion.

## Ziel-Architektur

### Reader (dünn)

`AppReader.Read(window)` liest nur:

- **Title**: aus Window-Titel (ParseTitle oder AppReader-spezifisch)
- **FilePath**: aus COM (Office) oder nicht verfügbar (Browser, Notepad, …)
- **UIA-Content**: optionaler Rohtext (best effort)
- **ProcessName, HWND, PID**: aus WindowInfo

**Output:** `AppReaderResult` mit:

- `ContextLabel`: Filename oder Window-Titel
- `ContextKind`: wie bisher (`document`, `spreadsheet`, `mail`, `url`, …)
- `Extra`: `{ filePath, uiaContent, conversion: "pending" }`
- `ContentMarkdown`: **nur Platzhalter** „_(see .content.md)_"

Der Reader entscheidet NICHT mehr über das finale MD-Format.
Er liefert nur die Rohdaten + Metadaten.

### Zentrale `DocumentConverter`-Klasse

In neuer DLL **`AiRecall.Conversion`** (Namespace `AiRecall.Conversion`):

```csharp
public static class DocumentConverter
{
    /// <summary>
    /// Konvertiert eine Datei nach Markdown.
    /// Liefert null bei Fehler oder unbekanntem Format.
    /// Strategie: Format-Extension-basiert, alle Konverter in-process (kein Spawn).
    /// </summary>
    public static string? Convert(
        string filePath,
        int maxChars = 64 * 1024,
        ILogger? logger = null);

    /// <summary>Liefert den Konverter-Namen, der fuer die Datei benutzt wuerde.</summary>
    public static string GetConverterForFile(string filePath);
}
```

**Strategie (rein .NET, keine externen Tools):**

| Extension | Konverter | NuGet-Package |
|---|---|---|
| `.txt` / `.md` / `.log` / `.csv` | Plain-Read (`File.ReadAllText`) | — |
| `.docx` / `.doc` | `DocumentFormat.OpenXml` | `DocumentFormat.OpenXml` (MIT, Microsoft) |
| `.xlsx` / `.xls` | `DocumentFormat.OpenXml` (Spreadsheet) | dito |
| `.pptx` / `.ppt` | `DocumentFormat.OpenXml` (Presentation) | dito |
| `.pdf` | `PdfPig` | `UglyToad.PdfPig` (Apache 2.0) |
| `.html` / `.htm` | `ReverseMarkdown` | `ReverseMarkdown` (MIT, vorhanden) |
| `.odt` / `.odp` / `.ods` | **kein Konverter** → `null` + Log | — |
| `.latex` / `.tex` / `.epub` / `.rtf` / `.docbook` | **kein Konverter** → `null` + Log | — |

**Nie crashen.** Fehler → `null` + Log mit `filePath`, `extension`, `errorMessage`.

**Pandoc wird NICHT unterstützt.** Performance ist wichtiger als Format-Coverage
(Martin 2026-07-04 19:12). Für Edge-Cases (odt, latex, epub, alte doc/xls/ppt)
liefert der Konverter `null` und der Capture wird mit
`conversion: failed` + `conversionError: "no-converter-for-<ext>"` markiert.

### Async-Pipeline

**Initial-Capture (synchron in `recall record`):**

1. AppReader liest Rohdaten
2. `CaptureWriter.Write` schreibt PNG + MD mit Frontmatter:

   ```yaml
   ---
   timestamp: 2026-07-04T18:00:00+02:00
   process: "WINWORD"
   pid: 1234
   hwnd: 0xABCDEF
   title: "Doc.docx - Word"
   filePath: "C:\Users\...\Doc.docx"
   conversion: pending
   screenshot: 180000000-Doc.png
   hash: ...
   ---
   ```

3. **KEIN `*.content.md`** wird initial geschrieben.

**Conversion (asynchron, Martin 2026-07-04 19:25 — OCR ebenfalls async):**

**Architektur (Martin-Vorschlag, v0.3):** in-process `Channel<string>` als
Producer-Consumer-Queue. Kein FileSystemWatcher — TriggerWorker enqueued
direkt nach CaptureWriter.

**Pipeline (alle async im ConversionWorker):**

1. **OCR** (wenn Screenshot vorhanden): Tesseract liest PNG → Plain-Text → `## OCR Content`-Sektion
2. **DocumentConverter** (wenn `filePath` vorhanden): Format-Extension-basiert → MD-String → `## Document Content (via <name>)`-Sektion
3. **Persist**: `CaptureWriter.WriteContent` schreibt `*.content.md`, updated Frontmatter
4. **Status**: `conversion: done` wenn beide erfolgreich, `partial` wenn einer failed, `failed` wenn beide failed

**TriggerWorker → ConversionWorker:**

```
TriggerWorker
  → WindowScreenshot.CapturePng(window)   // synchron, schnell
  → CaptureWriter.Write (PNG + *.md mit conversion: pending, KEIN OCR)
  → Channel<string>.Enqueue(captureMdPath)
                                          ↓
ConversionWorker (Background-Task)
  → liest aus Channel
  → Parallel:
     [OCR-Worker]   Tesseract.ExtractText(screenshot) → OCR-Text
     [Doc-Worker]   DocumentConverter.Convert(filePath) → Doc-MD
  → CaptureWriter.WriteContent + Frontmatter update
```

**`recall convert` Subcommand (Martin-Punkt 4):**

- Recovery-Mechanismus: scannt Disk nach `*.md` mit `conversion: pending`
- Füllt Channel → ConversionWorker verarbeitet asynchron
- **Nicht blockierend**: gibt Stats zurück (N enqueued), läuft nicht selbst
- Plus `--include-legacy`: entfällt — Tool ist neu, keine Legacy-Captures (Martin 2026-07-04 20:01)

**Vorteile ggü. FileSystemWatcher (Martin 2026-07-04 19:25):**

- ✓ Direkt (kein Disk-Roundtrip zwischen Trigger und Worker)
- ✓ Testbar (Channel< string> kann gemockt werden)
- ✓ Deterministisch (Queue im Code sichtbar)
- ✓ Plattform-neutral (kein Win32-FileSystemWatcher)
- ✓ OCR + Document in einem Worker-Pool → Lastbalancierung

### Status-Tracking

**Frontmatter:**

```yaml
conversion: pending | done | partial | failed   # overall status
conversionError: "..."                          # bei partial/failed, semikolon-getrennt
conversionTimestamp: 2026-07-04T18:00:00        # bei done/partial/failed
conversionSteps: "ocr=ok,tesseract;doc=ok,openxml"  # strukturierter Status pro Schritt
```

**Log:** `logs/conversion.log` (Serilog) für Diagnose:

```
2026-07-04T18:00:00 [INF] Conversion started: Doc.docx (ocr=tesseract,doc=openxml)
2026-07-04T18:00:01 [INF] Conversion done: Doc.docx (12 KB MD, steps=2/2)
2026-07-04T18:00:02 [ERR] Conversion failed: broken.pdf (doc=pdfpig: corrupted header)
2026-07-04T18:00:03 [ERR] Conversion partial: notepad.png (ocr=tesseract: empty image, no doc)
2026-07-04T18:00:04 [ERR] Conversion failed: notes.odt (doc=no-converter-for-odt)
```

### `recall convert` Subcommand

```
recall convert [--path <capture-root>] [--max-parallel N] [--timeout S]
```

- Default `--path`: aus Config (`capture.rootPath`)
- Default `--max-parallel`: aus Config (`conversion.batchSize`)
- Default `--timeout`: aus Config (`conversion.conversionTimeoutSeconds`)
- Output: Stats (N pending, N done, N failed)
- Exit-Code: 0 wenn alles done, 1 wenn failed-Marker

## Konfiguration

```json
{
  "conversion": {
    "enabled": true,
    "ocr": {
      "enabled": true,
      "engine": "tesseract",
      "language": "deu+eng",
      "timeoutSeconds": 15
    },
    "maxTextKB": 64,
    "batchSize": 2,
    "conversionTimeoutSeconds": 30,
    "watchDirectory": ""
  }
}
```

| Feld | Default | Bedeutung |
|---|---|---|
| `enabled` | `true` | Globaler Toggle. Wenn `false`: keine Async-Conversion, Capture-MD enthält nur Rohdaten ohne `*.content.md`. |
| `ocr.enabled` | `true` | OCR-Schritt aktiv. Wenn `false`: nur DocumentConverter, kein OCR-Text. |
| `ocr.engine` | `"tesseract"` | OCR-Engine. Aktuell nur Tesseract. |
| `ocr.language` | `"deu+eng"` | Tesseract-Sprachcodes. |
| `ocr.timeoutSeconds` | `15` | OCR-Timeout pro Bild. |
| `maxTextKB` | `64` | Maximale MD-Länge (analog zu `appReader.documents.maxTextKB`). |
| `batchSize` | `2` | Max. parallele Conversion-Tasks im Worker-Pool. |
| `conversionTimeoutSeconds` | `30` | Pro-Capture-Timeout (gesamt). |
| `watchDirectory` | leer | Leer = `capture.rootPath`. Anderer Pfad möglich für Test-Setups. |

**Pandoc-Felder entfernt.** `pandocPath` und `fallbackConverters` sind in v0.2 raus,
da Pandoc nicht mehr unterstützt wird.

## Datenfluss (v0.3, mit OCR in der Pipeline)

```
Window-Event
  ↓
TriggerWorker (Pipeline-Schritte 1-12)
  ↓
AppReader.Read(window)  ← nur Rohdaten (Title, FilePath, UIA)
  ↓
WindowScreenshot.CapturePng(window)  ← synchron, schnell (kein OCR hier mehr)
  ↓
CaptureWriter.Write (PNG + MD mit conversion: pending)
  ↓
Channel<string>.Enqueue(captureMdPath)
                                          ↓
ConversionWorker (Background-Task)
  ├─ OCR-Sub-Task: Tesseract.ExtractText(screenshot) → "## OCR Content"-Sektion
  └─ Document-Sub-Task: DocumentConverter.Convert(filePath) → "## Document Content"-Sektion
                                          ↓
CaptureWriter.WriteContent + Frontmatter update
  ↓
*.content.md + conversion: done | partial | failed
```

**OCR-Hinweis:** OCR ist **nicht mehr im synchronen Capture-Pfad** (Martin
2026-07-04 19:25). Das macht den Capture-Loop deutlich schneller, weil
Tesseract (typisch 100–500ms pro Bild) erst nachgelagert läuft.

## Akzeptanzkriterien

- [ ] AppReader lesen nur Rohdaten, kein MD-String mehr (außer Platzhalter)
- [ ] `DocumentConverter.Convert(filePath)` liefert MD-String für alle unterstützten Formate (docx/xlsx/pptx/pdf/html/txt/md/csv/log)
- [ ] Edge-Cases (odt, latex, epub, doc/xls/ppt alt) liefern `null` + Log-Hinweis `no-converter-for-<ext>`; Capture wird mit `conversion: failed` markiert
- [ ] `recall record` schreibt PNG + MD mit `conversion: pending`, **kein** OCR-Text, **kein** `*.content.md` initial
- [ ] Hintergrund-Worker (Channel-Queue) konvertiert `*.md` mit `conversion: pending` asynchron
- [ ] OCR läuft ebenfalls async (Martin 2026-07-04 19:25) — nicht synchron im Capture-Pfad
- [ ] Worker-Pool macht parallel: OCR + DocumentConverter (für denselben Capture)
- [ ] `recall convert` Subcommand enqueued pending-Captures in die Channel-Queue
- [ ] Frontmatter nach erfolgreicher Konvertierung: `conversion: done` (oder `partial` wenn ein Schritt failed)
- [ ] Fehlerhafte Konvertierungen markieren `conversion: failed|partial` + `conversionError: …` + `conversionSteps: ...`
- [ ] `logs/conversion.log` dokumentiert alle Konvertierungen
- [ ] Tests: DocumentConverter (alle Formate), Frontmatter-Update, Worker-Lifecycle, Channel-Queue

## Out of Scope

- **OCR (Bild-PDF → Text)**: separates Spec, würde Tesseract brauchen
- **Live-Vorschau der konvertierten MD-Inhalte**: Web-UI, MVP3
- **Multi-File-Container** (zip, tar, …): zu komplex für MVP
- **Konvertierung von Audio/Video**: separates Spec
- **Bidirektionale Sync** (MD-Änderungen → Original-Datei): zu komplex, read-only

## Migration

**Keine Migration nötig** — das Tool ist neu (Martin 2026-07-04 20:01),
es existieren keine Legacy-Captures vor Spec 0007. Alle Captures ab
Release werden im neuen Format (`conversion: pending|done|partial|failed`,
OCR + DocumentConverter asynchron) erstellt.

`recall convert` ist ein reiner **Recovery-Subcommand** für gecrashte
`recall record`-Sessions: scannt Disk nach `pending`-Captures und füllt
die Channel-Queue. Kein `--include-legacy`-Flag.

## Offene Fragen — alle geklärt (Martin-Review 2026-07-04)

1. ~~**Pandoc als externe Dependency OK?**~~ **Erledigt:** Pandoc raus.
2. ~~**NuGet-Packages OK?**~~ **Erledigt (Martin 2026-07-04 20:01):** DocumentFormat.OpenXml + PdfPig + ReverseMarkdown bestätigt.
3. ~~**FileSystemWatcher im selben Prozess?**~~ **Erledigt:** Channel-Queue.
4. ~~**`recall convert` als CLI-Subcommand OK?**~~ **Erledigt:** ja, ohne `--include-legacy`-Flag.
5. ~~**Bestehende Captures:** Legacy-Migration?~~ **Erledigt (Martin 2026-07-04 20:01):** Kein Legacy-Handling — Tool ist neu.
6. ~~**OCR-Sprache-Default `"deu+eng"`**~~ **Erledigt (Martin 2026-07-04 19:52):** OK, konfigurierbar.
7. ~~**OCR-Engine-Erweiterung (Azure/Google Vision)?**~~ **Erledigt:** aktuell nur Tesseract, YAGNI.

**Status: Spec final.** Bereit für Implementation.

## Implementierungs-Reihenfolge

1. **Spec-Review** mit Martin (offene Fragen klären)
2. **`AiRecall.Conversion.dll` anlegen** mit `DocumentConverter` (alle Formate)
3. **Tests** für `DocumentConverter` (alle Formate mit Sample-Files)
4. **`ConversionWorker` + FileSystemWatcher** als eigene Klasse in `AiRecall.Trigger` oder neu in `AiRecall.Conversion`
5. **`recall convert` Subcommand** im CLI
6. **`CaptureWriter` erweitern** um `conversion: pending` als Default
7. **`AppReader` refactoren** zu dünnen Readern (Word/Excel/PowerPoint zuerst)
8. **`TriggerService` integriert** den `ConversionWorker` (Start/Stop)
9. **Tests**: Worker-Lifecycle, Frontmatter-Update, Async-Verhalten
10. **Doku**: PROJECT.md + DECISIONS.md + README.md (NuGet-Hinweise: DocumentFormat.OpenXml, UglyToad.PdfPig)
11. **Commit + Push**

## Tests-Plan

| Test-Klasse | Tests |
|---|---|
| `DocumentConverterTests` | Pro Format 1 Happy-Path + 1 Error-Path. txt/md/log/csv + docx/xlsx/pptx + pdf + html + Unbekannt (null). |
| `ConversionWorkerTests` | Lifecycle (Start/Stop/Dispose), Throttle, Error-Recovery, Channel-Integration |
| `OcrWorkerTests` | Tesseract-Integration, Empty-Image-Handling, Timeout, Language-Switch |
| `CaptureWriterConversionTests` | Frontmatter `conversion: pending` wird korrekt geschrieben |
| `AppReaderRefactorTests` | Reader liefert nur Rohdaten, ContentMarkdown = Platzhalter |
| `RecallConvertCommandTests` | CLI-Parsing, Batch-Verarbeitung, Exit-Codes |
| `ChannelQueueTests` | Producer-Consumer-Pattern, Backpressure, Cancellation |

Geschätzt: **~30-40 neue Tests**, Test-Count gesamt: **~300-310**.

## Verworfen (YAGNI)

- **Eigener Office-OpenXML-Writer** zum Reverse-Konvertieren (MD → docx):
  nicht im Scope.
- **Pandoc-Integration** (Martin 2026-07-04 19:12): Performance wichtiger als
  Format-Coverage. Edge-Cases (odt, latex, epub, alt-Formate) liefern `null`.
- **Streaming-Konvertierung** (Pipe zu externem Tool ohne Temp-File): Komplexität
  vs. Nutzen ungünstig. Mit rein .NET-Libraries sowieso irrelevant (in-process).
- **Worker als Windows-Service**: eigenständiger Lifecycle zu komplex
  für jetzt. Background-Task im selben Prozess reicht.