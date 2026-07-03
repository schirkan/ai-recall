# 0003 — `recall active-window` Command Spec

> **Status:** Draft v0.1 (2026-07-03)
> **Owner:** Martin
> **Implements:** SR-2, SR-3, SR-4, SR-5 (partial) + P-1, P-2, P-3 (partial) from MVP1

## Zweck

Ein einzelner, expliziter Aufruf erfasst das aktuell aktive Fenster (oder ein
beliebiges Fenster per HWND) als PNG + Markdown mit YAML-Frontmatter.
Dient als Grundlage und Integrations-Test für die spätere kontinuierliche
`recall record`-Pipeline.

## Aufruf

```
recall active-window [options]
```

### Optionen

| Flag | Default | Bedeutung |
|---|---|---|
| `--no-ocr` | aus | OCR überspringen (schneller, kein Text im MD) |
| `--include-ignored` | aus | Auch Fenster capturen, die zur Ignore-Liste passen |
| `--hwnd <hex>` | aus | Bestimmtes Fenster statt `GetForegroundWindow` nutzen |
| `--config <path>` | AppData | Pfad zur Config-JSON überschreiben |
| `-h`, `--help` | — | Hilfe anzeigen |

Aliases: `aw`

## Ablauf

1. Config laden (Reihenfolge: `--config` > `%APPDATA%/AiRecall/config.json` > `default-config.json` neben Exe > Defaults)
2. Serilog initialisieren (Console + Rolling-File in `logs/`)
3. Fenster bestimmen:
   - Mit `--hwnd`: `WindowInfoLookup.Get(hwnd)` (prüft via `IsWindow`)
   - Sonst: `ActiveWindowDetector.GetActive()` (`GetForegroundWindow` + Process-Info + Bounds)
4. Ignore-Check via `IgnoreMatcher` (siehe 0002 §"Trigger-Pipeline")
   - Treffer → Skip + Log + Exit 0 (außer `--include-ignored`)
5. Screenshot via `WindowScreenshot.CapturePng(window)`:
   - Win32 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`
   - PNG-Bytes per `Bitmap.Save(MemoryStream, ImageFormat.Png)`
6. SHA-256-Hash der PNG-Bytes (für spätere Dedup)
7. OCR (außer `--no-ocr`): `OcrEngine.ExtractText(pngBytes)`
   - Fehlt `tessdata/` oder Sprachdateien → Warnung loggen, MD ohne Text schreiben
8. Persistenz via `CaptureWriter.Write(...)`:
   - Pfad: `{rootPath}/{yyyy-MM-dd}/{processName}/{HHmmss-fff}-{titleSlug}.{png,md}`
   - MD enthält YAML-Frontmatter (timestamp, process, pid, hwnd, title, screenshot, hash)
   - MD verlinkt das PNG relativ (P-3)
9. Zusammenfassung auf der Konsole + Serilog-Log

## Exit-Codes

| Code | Bedeutung |
|---|---|
| 0 | Erfolg (auch bei Ignore-Skip) |
| 1 | Allgemeiner Fehler |
| 2 | Usage-Fehler (z. B. unbekanntes Flag, fehlendes Argument) |
| 3 | Kein Fenster gefunden (kein FG-Window / ungültiger HWND) |
| 4 | Screenshot fehlgeschlagen (`PrintWindow` false oder 0-Größe) |

## Ausgabe-Beispiel

```
Active window: chrome (PID 12345) - "GitHub - schirkan/ai-recall: Issues"
  HWND: 0x00090A12, Bounds: 1920x1040 @ (0,23)

Capture written:
  Screenshot: capture/2026-07-03/chrome/142537-481-GitHub - schirkan-ai-recall - Issues.png
  Markdown:   capture/2026-07-03/chrome/142537-481-GitHub - schirkan-ai-recall - Issues.md
  Hash:       8f0abf27bd67249de15d742da3e4d112285704bbf9ebfdb6a250c52072dddacc
  Text chars: 4321
  Took:       612 ms
```

## Persistenz-Schema (P-1, P-2, P-3)

```
capture/
└── 2026-07-03/
    ├── chrome/
    │   ├── 142537-481-GitHub - schirkan-ai-recall - Issues.png
    │   └── 142537-481-GitHub - schirkan-ai-recall - Issues.md
    └── Notepad/
        ├── 143012-005-Readme.txt - Notepad.png
        └── 143012-005-Readme.txt - Notepad.md
```

MD-YAML-Frontmatter:

```yaml
---
timestamp: 2026-07-03T14:25:37.4810000+02:00
process: "chrome"
pid: 12345
hwnd: 0x90A12
title: "GitHub - schirkan/ai-recall: Issues"
screenshot: 142537-481-GitHub - schirkan-ai-recall - Issues.png
hash: 8f0abf27bd67249de15d742da3e4d112285704bbf9ebfdb6a250c52072dddacc
---
```

## Konfiguration

Siehe `default-config.json` (mitgeliefert). Relevante Sektionen:

- `capture.rootPath` — Wurzelpfad für `capture/`
- `screenRecorder.ignoreApps|ignoreUrls|ignoreWindowTitles` — Blacklist
- `ocr.engine` — aktuell nur `"tesseract"`
- `ocr.languages` — Liste der Tesseract-Sprachcodes (z. B. `deu`, `eng`)
- `ocr.tessDataPath` — Pfad zum `tessdata/`-Verzeichnis
- `logging.level` — Serilog-Log-Level (`verbose`/`debug`/`info`/`warn`/`error`/`fatal`)
- `logging.path` — Verzeichnis für Rolling-File-Logs (`null` = aus)

## OCR-Setup (Tessdata)

Tesseract-Sprachdateien sind **nicht** im Repo und nicht im NuGet-Paket.
Laden + ablegen:

1. `tessdata/`-Ordner neben der Exe anlegen (oder absoluten Pfad in Config setzen)
2. Von <https://github.com/tesseract-ocr/tessdata_fast> herunterladen:
   - `deu.traineddata` (~3.8 MB)
   - `eng.traineddata` (~3.0 MB)
   - ggf. weitere Sprachen
3. `recall active-window` (ohne `--no-ocr`) probieren.

## Bekannte Einschränkungen

- **Minimierte Fenster:** `PrintWindow` liefert bei minimierten Fenstern
  leere Bilder (nur Header). Der Capture wird trotzdem geschrieben.
  Künftig: Skip mit Hinweis, wenn `IsIconic(hwnd)`.
- **Kein Dedup:** Jeder Aufruf schreibt eine neue Datei. Dedup ist der
  Trigger-Pipeline (`recall record`) vorbehalten.
- **Kein Multi-Monitor-Aggregat:** Nur das angegebene Fenster, nicht der
  gesamte Bildschirm.
- **OCR-Sprachen:** Die `tessdata/`-Dateien müssen zu den konfigurierten
  Codes passen, sonst Exception beim TesseractEngine-Konstruktor.

## Akzeptanzkriterien

- [x] `recall active-window` schreibt PNG + MD mit YAML-Frontmatter
- [x] Ignore-Liste (Blacklist) wird vor dem Capture geprüft
- [x] `--no-ocr` überspringt Tesseract
- [x] `--include-ignored` umgeht den Skip
- [x] `--hwnd <hex>` fängt ein bestimmtes Fenster (für Skripte)
- [x] Logging (Serilog): Console + `logs/ai-recall-YYYYMMDD.log`
- [x] Tests für Hashing, IgnoreMatcher, ConfigLoader grün
- [ ] OCR mit echtem Bild + tessdata verifiziert (manueller Test auf echtem Desktop)

## Out of Scope (für `active-window`, weiter in MVP1)

- Trigger-Pipeline / kontinuierliche Captures → `recall record`
- Dedup / Hash-State über mehrere Aufrufe
- UIA-basierte Textextraktion (Alternative zu OCR)
- Browser-/Outlook-/Word-/Excel-App-Reader (CLI-3)
