# AI Recall

> Lokales, persönliches „Recall"-Tool für Windows — Screenshot-basiertes
> Memory für Bildschirmarbeit, Mails, Dokumente und (später) Meetings.

⚠️ **Status:** Aktive MVP1-Entwicklung. Erste CLI-Commands lauffähig.
Specs in [`specs/`](./specs/). Noch kein Release.

## Vision

AI Recall orientiert sich an [Windows Recall], [Screenpipe] und [rowboat] —
läuft aber **komplett lokal**, ist **Open Source (MIT)** und fokussiert auf
**Windows-Office-Workflows** (Outlook, Word, Excel, Browser).

Details: [`specs/0001-vision.md`](./specs/0001-vision.md)

## Features (Stand MVP1-Start)

- ✅ `recall list-windows` — alle Top-Level-Fenster auflisten
- ✅ `recall active-window` — aktives Fenster (oder per `--hwnd`) capturen
  als PNG + Markdown mit YAML-Frontmatter ([Spec 0003](./specs/0003-active-window.md))
  - Tesseract-OCR (Deutsch/Englisch out-of-the-box konfigurierbar)
  - SHA-256-Hash für spätere Dedup
  - Blacklist-Ignore-Liste (Apps, URLs, Window-Titles)
  - Serilog-Logging (Console + Rolling-File)
- ⏳ Trigger-Pipeline (`recall record`, geplant)
- ⏳ App-Reader: Browser / Outlook / Word / Excel (Stubs vorhanden)
- MVP2: Auto-Meeting-Recording (Audio + Transcription)
- MVP3: Auto Knowledge Base / Wiki

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

Aktuell 18/18 grün (Hashing, IgnoreMatcher, ConfigLoader).

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

Siehe [`specs/0002-mvp1-scope.md` §"Architektur"](./specs/0002-mvp1-scope.md).
Kurzfassung:

```
AiRecall.Cli → Core + ScreenCapture + AppReader.*
ScreenCapture → Core
AppReader.Base → Core
AppReader.{Browser,Outlook,Documents} → AppReader.Base → Core
```

Entscheidungen mit Datum/Begründung: [`DECISIONS.md`](./DECISIONS.md).

## Lizenz

[MIT](./LICENSE) — Copyright © 2026 Martin

## Quellen / Inspiration

- [Windows Recall](https://support.microsoft.com/windows/recall)
- [Screenpipe](https://github.com/screenpipe/screenpipe)
- [rowboat](https://github.com/rowboatlabs/rowboat)
