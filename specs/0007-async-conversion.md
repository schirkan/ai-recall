# 0007 — Async Document Conversion Pipeline

> **Status:** Draft v0.2 (2026-07-04) — Pandoc raus (Martin-Direktive: Performance wichtiger), nur .NET-Konverter
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

**Conversion (asynchron):**

**Option A: `ConversionWorker` als Background-Task im selben Prozess** (`recall record`):

- `FileSystemWatcher` auf `capture/`-Verzeichnis
- Filter: `*.md` mit `conversion: pending` (Frontmatter-Parse)
- Event: neue Datei → in Queue
- Worker: `DocumentConverter.Convert(filePath)` → schreibt `*.content.md` → updated Frontmatter `conversion: done`
- Throttle: max. N Konvertierungen parallel (Default 2)

**Option B: Manueller `recall convert` Subcommand:**

- Scannt `capture/`-Verzeichnis nach pending-MD-Files
- Konvertiert alle in einem Batch
- Für Tests, CI und manuelle Recovery

**Beide parallel.** FileSystemWatcher für Live-Updates in der
Capture-Session, `recall convert` für Batch/CI.

### Status-Tracking

**Frontmatter:**

```yaml
conversion: pending | done | failed
conversionError: "..."                   # nur bei failed
conversionTimestamp: 2026-07-04T18:00:00 # bei done/failed
converterUsed: openxml | pdfpig | reversemarkdown | textfile | none
```

**Log:** `logs/conversion.log` (Serilog) für Diagnose:

```
2026-07-04T18:00:00 [INF] Conversion started: Doc.docx (openxml)
2026-07-04T18:00:01 [INF] Conversion succeeded: Doc.docx (12 KB MD)
2026-07-04T18:00:02 [ERR] Conversion failed: broken.pdf (pdfpig: corrupted header)
2026-07-04T18:00:03 [ERR] Conversion failed: notes.odt (no-converter-for-odt)
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
| `maxTextKB` | `64` | Maximale MD-Länge (analog zu `appReader.documents.maxTextKB`). |
| `batchSize` | `2` | Max. parallele Konvertierungen im Worker. |
| `conversionTimeoutSeconds` | `30` | Pro-Datei-Timeout. |
| `watchDirectory` | leer | Leer = `capture.rootPath`. Anderer Pfad möglich für Test-Setups. |

**Pandoc-Felder entfernt.** `pandocPath` und `fallbackConverters` sind in v0.2 raus,
da Pandoc nicht mehr unterstützt wird.

## Datenfluss

```
Window-Event
  ↓
TriggerWorker (Pipeline-Schritte 1-12)
  ↓
AppReader.Read(window)  ← nur Rohdaten (Title, FilePath, UIA)
  ↓
CaptureWriter.Write
  ↓
PNG + MD (mit conversion: pending)
  ↓
FileSystemWatcher (in recall record)
  ↓
ConversionWorker
  ↓
DocumentConverter.Convert(filePath)
  ↓
*.content.md + Frontmatter conversion: done
```

## Akzeptanzkriterien

- [ ] AppReader lesen nur Rohdaten, kein MD-String mehr (außer Platzhalter)
- [ ] `DocumentConverter.Convert(filePath)` liefert MD-String für alle unterstützten Formate (docx/xlsx/pptx/pdf/html/txt/md/csv/log)
- [ ] Edge-Cases (odt, latex, epub, doc/xls/ppt alt) liefern `null` + Log-Hinweis `no-converter-for-<ext>`; Capture wird mit `conversion: failed` markiert
- [ ] `recall record` schreibt PNG + MD mit `conversion: pending`, **kein** `*.content.md` initial
- [ ] Hintergrund-Worker konvertiert `*.md` mit `conversion: pending` asynchron zu `*.content.md`
- [ ] `recall convert` Subcommand konvertiert alle pending-MD-Files in einem Batch
- [ ] Frontmatter wird nach erfolgreicher Konvertierung auf `conversion: done` gesetzt
- [ ] Fehlerhafte Konvertierungen markieren `conversion: failed` + `conversionError: …`
- [ ] `logs/conversion.log` dokumentiert alle Konvertierungen
- [ ] Tests: DocumentConverter (alle Formate), Frontmatter-Update, Worker-Lifecycle

## Out of Scope

- **OCR (Bild-PDF → Text)**: separates Spec, würde Tesseract brauchen
- **Live-Vorschau der konvertierten MD-Inhalte**: Web-UI, MVP3
- **Multi-File-Container** (zip, tar, …): zu komplex für MVP
- **Konvertierung von Audio/Video**: separates Spec
- **Bidirektionale Sync** (MD-Änderungen → Original-Datei): zu komplex, read-only

## Migration

- **Bestehende Captures ohne `conversion:`-Feld** = `legacy`
- `recall convert` überspringt Legacy-Files (kein Nachträglich-Konvertieren)
- Optionaler Flag `--include-legacy` in `recall convert` zum Nachträglich-Konvertieren

## Offene Fragen (Martin-Review)

1. ~~**Pandoc als externe Dependency OK?**~~ **Erledigt (Martin 2026-07-04 19:12):** Pandoc raus, Performance wichtiger. Edge-Cases (odt, latex, epub, alt-Formate) liefern `null` + Log.
2. **NuGet-Packages OK?**
   - `DocumentFormat.OpenXml` (MIT, Microsoft) für docx/xlsx/pptx
   - `UglyToad.PdfPig` (Apache 2.0, MIT-kompatibel) für pdf
   - `ReverseMarkdown` (MIT, vorhanden) für html
3. **FileSystemWatcher im selben Prozess** (in `recall record`) ODER
   separater Service? Empfehlung: im selben Prozess (einfacher, kein
   separater Lifecycle).
4. **`recall convert` als CLI-Subcommand OK?** Plus optionaler `--include-legacy`-Flag.
5. **Bestehende Captures:** Legacy-Status akzeptabel oder nachträglich
   konvertieren? Empfehlung: Legacy akzeptabel, kein Nachträglich-Konvertieren
   (würde massenhaft Files ändern).

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
| `ConversionWorkerTests` | Lifecycle (Start/Stop/Dispose), Throttle, Error-Recovery |
| `CaptureWriterConversionTests` | Frontmatter `conversion: pending` wird korrekt geschrieben |
| `AppReaderRefactorTests` | Reader liefert nur Rohdaten, ContentMarkdown = Platzhalter |
| `RecallConvertCommandTests` | CLI-Parsing, Batch-Verarbeitung, Exit-Codes |

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