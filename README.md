# AI Recall

> Lokales, persönliches „Recall"-Tool für Windows — Screenshot-basiertes
> Memory für Bildschirmarbeit, Mails, Dokumente und (später) Meetings.

⚠️ **Status:** MVP1 + MVP2 v1.0 abgeschlossen. Specs in [`specs/`](./specs/). Kein offizielles Release.

## Vision

AI Recall orientiert sich an [Windows Recall], [Screenpipe] und [rowboat] —
läuft aber **komplett lokal**, ist **Open Source (MIT)** und fokussiert auf
**Windows-Office-Workflows** (Outlook, Word, Excel, Browser).

Details: [`specs/0001-vision.md`](./specs/0001-vision.md)

## Features (Stand 2026-07-05)

- ✅ `recall list-windows` — alle Top-Level-Fenster auflisten
- ✅ `recall active-window` — aktives Fenster (oder per `--hwnd`) capturen
  als PNG + Markdown mit YAML-Frontmatter ([Spec 0003](./specs/0003-active-window.md))
  - Tesseract-OCR (Deutsch/Englisch out-of-the-box konfigurierbar)
  - SHA-256-Hash für spätere Dedup
  - Blacklist-Ignore-Liste (Apps, URLs, Window-Titles)
  - Serilog-Logging (Console + Rolling-File)
- ✅ App-Reader ([Spec 0004](./specs/0004-app-reader.md)) — Plugin-DLLs liefern
  strukturierten Content statt OCR-Fallback:
  - **Browser** (Edge, Chrome, Firefox via CDP): Tab-Titel + URL via UIA,
    optional reichhaltiger via Chrome DevTools Protocol (CDP opt-in),
    HTML→MD via `ReverseMarkdown` mit allen 8 Feldern 1:1-konfigurierbar
  - **Notepad**: Buffer + Dateiname via Win32 `WM_GETTEXT`
  - **Explorer**: aktueller Pfad aus Fenster-Titel (Hyphen/En-Dash/Em-Dash-tolerant)
  - **Outlook** ([Spec 0004 Iter. 3](./specs/0004-app-reader.md#iter-3-2026-07-05--outlook-app-reader--mail-log-martin--pia)):
    aktiver Inspector + Background-Polling für Mail-Stream (Inbox + Sent Items +
    Custom-Folders), EntryID-Dedup, Auto-Regel-Heuristik (4 Bedingungen),
    custom HTML→MD-Konvertierung
  - **OneNote** ([Spec 0010](./specs/0010-onenote-app-reader.md)): aktive OneNote-Page
    via COM-Late-Binding mit 4-stufiger Active-Page-Strategie (Windows.CurrentWindow.CurrentPageId,
    Windows-foreach + Active, GetHierarchy + isCurrentlyViewed="true", null-Fallback);
    Page-XML (xs2013)→MD-Konvertierung mit Tag/Image/Table/InkContent-Mapping;
    Read-only (kein Background-Poll — Page-orientiert)
  - **Word/Excel/PowerPoint** ([Spec 0004 Iter. 2](./specs/0004-app-reader.md#iter-2-2026-07-04--com-interop-für-office--pdf-viewer-martin)):
    UIA + optional COM-Interop (late binding, ProgID + P/Invoke) für Pfad +
    Inhalt; COM-Fallback auf UIA+Title
  - **PDF-Viewer** (Adobe/Sumatra/Foxit/PDF-XChange/Edge/Chrome): Filename +
    voller Pfad + Page-Nr aus Titel; PDF-Inhalt in Iter. 2 (`PdfPig` als Kandidat)
  - Inhalt als zusätzliche `*.content.md` neben dem Capture-MD
  - Reflection-basierte Plugin-Discovery (eine DLL pro App)
- ✅ Trigger-Pipeline ([Spec 0005](./specs/0005-trigger-pipeline.md)) —
  `recall record` mit WinEventHook (out-of-context) + Heartbeat-Fallback,
  Per-HWND-Throttle, Hash-Dedup, modaler Dialog-Frontmatter, `--headless` + `--trigger-mode=events|polling|both`
- ✅ Async Document Conversion Pipeline ([Spec 0007](./specs/0007-async-conversion.md))
  — `DocumentConverter` (OpenXml + PdfPig + ReverseMarkdown), `ConversionWorker`
  (in-process `Channel<string>`), async OCR via `TesseractOcrEngineAdapter`,
  `recall convert` Recovery-Subcommand
- ✅ MVP2 Tray-Icon-EXE ([Spec 0006](./specs/0006-mvp2-tray-exe.md)) — in-process
  `ITriggerService`, Hot-Reload via `TriggerSupervisor.Restart`, SingleInstance-Mutex,
  + Live Logviewer ([Spec 0008](./specs/0008-live-logviewer.md), Ringbuffer 10k + Filter + Auto-Scroll)
  + Settings-Dialog ([Spec 0009](./specs/0009-settings-dialog.md), dynamische Form-Generierung via Reflection auf `AppConfig`)
- MVP3: Auto Knowledge Base / Wiki (Embeddings + LLM-Indexing-Service)

## Quick Start

### Voraussetzungen

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.422 oder höher im 8.0.x-Band)

### Build + erster Smoke-Test

```bash
git clone https://github.com/schirkan/ai-recall.git
cd ai-recall
dotnet build
dotnet run --project src/AiRecall.Cli -- list-windows
```

### Einmal-Capture vom aktiven Fenster (ohne OCR)

```bash
dotnet run --project src/AiRecall.Cli -- active-window --no-ocr
```

Capture landet in `capture/<yyyy-MM-dd>/<process>/<HHmmss-fff>-<title>.{png,md}`.

### OCR aktivieren (einmalig)

1. `tessdata/`-Ordner neben der Exe anlegen (Standard: `src/AiRecall.Cli/bin/Debug/net8.0-windows/tessdata/`)
2. Von <https://github.com/tesseract-ocr/tessdata_fast> herunterladen:
   - `deu.traineddata`
   - `eng.traineddata`
   - (oder weitere Sprachen — `tessdata_fast` ist klein + schnell)

   **PowerShell-One-Liner** für beide Sprachen:

   ```powershell
   $td = 'src\AiRecall.Cli\bin\Debug\net8.0-windows\tessdata'
   New-Item -ItemType Directory -Force -Path $td | Out-Null
   Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/deu.traineddata' -OutFile (Join-Path $td 'deu.traineddata')
   Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/eng.traineddata' -OutFile (Join-Path $td 'eng.traineddata')
   ```

3. `recall active-window` (ohne `--no-ocr`) probieren.

Sprachen und Pfad stehen in `default-config.json` (`ocr.languages`,
`ocr.tessDataPath`).

### Beliebiges Fenster per HWND capturen

```bash
# Fenster aus list-windows picken
dotnet run --project src/AiRecall.Cli -- list-windows
# ...
dotnet run --project src/AiRecall.Cli -- active-window --hwnd 0x0000090068
```

Hilfreich für Skripte und headless Tests.

## Tests

```bash
dotnet test
```

Aktuell **589/589 grün** (MVP1 + Trigger + App-Reader inkl. Outlook + Documents + PDF + OneNote + MVP2-Basis + Conversion + TrayApp-PureLogic).

## Konfiguration

Liefer-Defaults in `src/AiRecall.Cli/default-config.json`. User-Override:

| Pfad | Priorität |
|---|---|
| `--config <path>` (Command-Flag) | 1 (höchste) |
| `%APPDATA%/AiRecall/config.json` | 2 |
| `default-config.json` neben der Exe | 3 |
| Eingebaute C#-Defaults | 4 (niedrigste) |

Wichtige Felder: `capture.rootPath`, `screenRecorder.ignoreApps/Urls/WindowTitles`,
`ocr.languages`, `ocr.tessDataPath`, `logging.level`.

Details: [`specs/0002-mvp1-scope.md` §"Konfiguration"](./specs/0002-mvp1-scope.md)

## Architektur

Siehe [`specs/0002-mvp1-scope.md` §"Architektur"](./specs/0002-mvp1-scope.md)
und [`specs/0004-app-reader.md`](./specs/0004-app-reader.md).
Kurzfassung:

```
AiRecall.Cli → Core + ScreenCapture + AppReader.Base + AppReader.*
ScreenCapture → Core
AppReader.Base → Core
AppReader.{Browser,Notepad,Outlook,Documents} → AppReader.Base
```

Plugin-Discovery: `AppReaderRegistry.LoadFromDirectory(scanDir, logger)`
lädt alle `AiRecall.AppReader.*.dll` neben der Exe via Reflection.

Entscheidungen mit Datum/Begründung: [`DECISIONS.md`](./DECISIONS.md).

## Lizenz

[MIT](./LICENSE) — Copyright © 2026 Martin

## Quellen / Inspiration

- [Windows Recall](https://support.microsoft.com/windows/recall)
- [Screenpipe](https://github.com/screenpipe/screenpipe)
- [rowboat](https://github.com/rowboatlabs/rowboat)
