# 0012 — Tessdata First-Run Download

> **Status:** 🟡 **GEPLANT v0.1 (2026-07-06)**
> **Owner:** Martin
> **Abhängig von:** Spec 0006 (Tray-EXE Foundation), Spec 0009 (Settings-Dialog), Tesseract `tessdata_fast` (Apache-2.0)

## Ziel

Wenn die TrayApp startet und Tesseract als OCR-Engine konfiguriert ist, aber die
`tessdata`-Dateien für die vorgegebenen Sprachen fehlen, soll ein modaler Dialog
fragen, ob die Dateien automatisch von GitHub heruntergeladen werden sollen.
Statt eines passiven Balloon-Hinweises (Bug-Bash 2026-07-06 I-14) wird die
Lücke aktiv geschlossen.

## Auslöser

`TrayAppContext` ruft beim Startup `TessdataManager.CheckAsync()` auf.
Voraussetzungen für den Dialog:

1. `AppConfig.Ocr.Engine == "tesseract"`
2. `AppConfig.Ocr.AutoDownloadTessdata == true` (Default: true, neuer Setting-Wert)
3. Mindestens eine konfigurierte Sprache (`AppConfig.Ocr.Languages`) ist auf
   keinem der bekannten Suchpfade vorhanden:
   - `Path.IsPathRooted(TessDataPath)` → direkt
   - `<AppContext.BaseDirectory>/<TessDataPath>` → relativ zum EXE-Verzeichnis
   - `%LOCALAPPDATA%/AiRecall/<TessDataPath>` → User-Daten-Verzeichnis

## UI

Modaler Dialog (`TessdataFirstRunDialog`, ~520 × 240 px, Owner = TrayApp-Hauptfenster):

```
┌─ AiRecall — OCR-Sprachdateien fehlen ───────────────────────┐
│                                                             │
│  Die folgenden Tesseract-Sprachdateien wurden nicht         │
│  gefunden:                                                  │
│                                                             │
│    • eng.traineddata                                        │
│    • deu.traineddata                                        │
│                                                             │
│  Quelle: github.com/tesseract-ocr/tessdata_fast (Apache-2.0)│
│  Gesamtgröße: ca. 6,1 MB                                    │
│                                                             │
│  Jetzt herunterladen und unter                              │
│  %LOCALAPPDATA%\AiRecall\tessdata\ ablegen?                 │
│                                                             │
│  ┌──────────────────────────────────────────────────────┐   │
│  │                                                      │   │
│  │              [ Fortschrittsbalken ]                  │   │
│  │                                                      │   │
│  └──────────────────────────────────────────────────────┘   │
│                                                             │
│         [ Ja, herunterladen ]  [ Später ]  [ Nie fragen ]   │
└─────────────────────────────────────────────────────────────┘
```

### Button-Verhalten

| Button                | Wirkung                                                                                                                                             |
| --------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Ja, herunterladen** | Startet Download (async), schreibt Fortschritt in Progressbar, schließt Dialog nach Erfolg. Bei Fehler → Inline-Fehlertext + Retry-Button.          |
| **Später**            | Schließt Dialog, kein State-Change. Wird beim nächsten Start wieder gefragt.                                                                        |
| **Nie fragen**        | Setzt `Ocr.AutoDownloadTessdata = false` in `%APPDATA%/AiRecall/config.json` (atomic write via bestehende Settings-Persist-Logik), schließt Dialog. |

## Download-Logik

**Service**: `TessdataManager` (testbar, keine UI-Abhängigkeit)

```csharp
public sealed class TessdataManager
{
    public IReadOnlyList<MissingLanguage> FindMissingLanguages(AppConfig config);
    public Task DownloadAsync(
        IReadOnlyList<string> languages,
        string targetDirectory,
        IProgress<TessdataDownloadProgress> progress,
        CancellationToken ct);
}
```

### URL-Schema

```
https://github.com/tesseract-ocr/tessdata_fast/raw/main/{lang}.traineddata
```

- `eng.traineddata`, `deu.traineddata`, …
- **Keine** Script-Modelle (Latin, Devanagari, …) — nur ISO-639-Sprachcodes aus
  der Config.
- **Keine** `osd.traineddata` (Orientation/Script-Detection, siehe Chat-Log 2026-07-06).

### HTTP

- `HttpClient` (singletons via `IHttpClientFactory`-Pattern oder direkt injiziert).
- Timeout: 30 s pro Datei, 3 Retry-Versuche mit exponentiellem Backoff (1 s, 2 s, 4 s).
- SHA-256-Check **nicht** enthalten (Repo veröffentlicht keine Checksums pro File).
  → Risiko: kompromittierter Mirror. Mitigation: User muss explizit zustimmen
  ("Ja, herunterladen"), Dialog zeigt Quell-URL explizit an.

### Persistenz

- Ziel-Verzeichnis: `%LOCALAPPDATA%/AiRecall/tessdata/`
- Bestehendes `Ocr.TessDataPath` wird **nicht** automatisch geändert — der
  Manager versucht, die Dateien in den ersten existierenden Pfad der drei
  Suchpfade zu schreiben. Wenn keiner existiert oder schreibbar ist, fällt er
  auf `%LOCALAPPDATA%/AiRecall/tessdata/` zurück und loggt einen Hinweis, dass
  die User-Config ggf. angepasst werden sollte.
- Nach erfolgreichem Download: **kein** automatischer Hot-Reload. Der bereits
  laufende TriggerSupervisor liest tessdata beim nächsten Capture-Versuch —
  bestehende Pipeline bleibt unverändert.

## Lizenzhinweis

`tessdata_fast` ist Apache-2.0. Beim Download wird **nichts** an
Auto-Update-Mechanismen oder Hintergrund-Syncs installiert. Die LICENSE-Datei
aus dem Repo wird **nicht** automatisch mitkopiert (Limitation v0.1, siehe
Offene Punkte).

## Persistente Settings

Neuer Eintrag in `OcrConfig`:

```csharp
public sealed class OcrConfig
{
    // ... bestehende Felder
    public bool AutoDownloadTessdata { get; set; } = true;
}
```

`Default-Config` (`default-config.json` + `AppConfig` POCO) wird ergänzt.

## Tests

- `TessdataManagerTests`:
  - `FindMissingLanguages`: alle vorhanden → leere Liste
  - `FindMissingLanguages`: `eng` fehlt, `deu` da → nur `eng` zurück
  - `FindMissingLanguages`: `osd` in `Languages` (sollte nie passieren) → wird ignoriert
  - `DownloadAsync`: Mock-HttpClient liefert 200 + 1 KB Stream → Datei wird geschrieben, Progress feuert ≥ 1× mit korrektem `BytesReceived`
  - `DownloadAsync`: 404 nach 3 Retries → wirft `TessdataDownloadException` mit letztem Statuscode
- `FirstRunDetectionTests`:
  - `AutoDownloadTessdata=false` → Manager gibt leere Liste zurück (kein Dialog)
  - `Engine != "tesseract"` → Manager gibt leere Liste zurück
- Keine UI-Tests (WinForms-Dialog manuell verifiziert).

## Verworfen

- **Auto-Download ohne Nachfrage** (Silent): gegen Spec-0002-Prinzip "User gibt Initial-Setup selbst".
- **Download via winget/choco/scoop**: externe Abhängigkeit, Out-of-Scope.
- **Bündelung aller tessdata in EXE** (Spec-Chat 2026-07-06 Lizenz-Frage): +
  MB pro Sprache, Bundle-Größe explodiert. First-Run-Download ist
  benutzerfreundlicher.
- **GitHub-Token / Auth**: tessdata_fast ist öffentliches Repo, Rate-Limit
  reicht für sporadische Downloads.
- **Mirror mit SHA-Checksums**: tessdata_fast veröffentlicht keine pro-File
  Checksums. Würde custom gepflegten Mirror erfordern.

## Offene Punkte

- **LICENSE-File mitkopieren**: Nach Download `LICENSE` und `NOTICE` (falls vorhanden)
  aus dem Repo mit herunterladen und nach `%LOCALAPPDATA%/AiRecall/tessdata/LICENSE`
  ablegen. Wird in v0.2 nachgerüstet (siehe Chat-Log 2026-07-06 Lizenz-Frage).
- **Update-Mechanismus** (neue tessdata-Version): out-of-scope für v0.1, später
  via Trigger-Pipeline vergleichbar zu `0007-async-conversion`.
- **Mehrere Sprachen parallel herunterladen**: aktuell sequentiell. Bei vielen
  Sprachen wäre Parallel-Download sinnvoll — v0.2.
- **"Nie fragen"-Reset via Settings-Dialog**: User soll in Settings den Wert
  wieder auf `true` setzen können (PropertyGrid macht das automatisch, kein
  Extra-UI nötig).