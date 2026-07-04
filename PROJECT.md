# AI Recall — PROJECT

> Projekt-Workspace: `projects/ai-recall/`
> GitHub: `schirkan/ai-recall` (public, MIT)
> Branch: `main`
> Tech-Stack MVP1: C# / .NET 8 (`net8.0-windows`)

## Aktueller Status

**MVP1 — `active-window` + App-Reader Foundation + erste Reader (Browser, Notepad) + Trigger-Pipeline (Spec 0005) + Async Document Conversion Pipeline (Spec 0007 v1.0 abgeschlossen).**

- [x] Projektordner angelegt
- [x] Lokales Git-Repo initialisiert (`main`)
- [x] `.gitignore` angelegt
- [x] GitHub-Repo erstellt (`schirkan/ai-recall`, public)
- [x] Vision dokumentiert (`specs/0001-vision.md`)
- [x] MVP1-Scope dokumentiert (`specs/0002-mvp1-scope.md`)
- [x] Architektur-Entscheidungen bestätigt (MIT, Windows-only, Hybrid-DLLs, Trigger-Pipeline)
- [x] `LICENSE` (MIT) angelegt
- [x] `README.md` angelegt
- [x] `dotnet-8`-Solution-Skeleton (8 Projekte)
- [x] `recall list-windows` — lauffähig
- [x] `recall active-window` (Spec 0003) — lauffähig mit Ignore-Liste, OCR (Tesseract), SHA-256, YAML-Frontmatter
- [x] OCR mit echten Tessdata-Dateien manuell auf WindowsTerminal verifiziert (2882 Zeichen, 6 s)
- [x] Tech-Defaults final (Tesseract, Serilog, xUnit, manueller CLI-Switch, Blacklist) — siehe `DECISIONS.md`
- [x] **App-Reader-Architektur** (Spec 0004) implementiert:
  - `IAppReader` + `AppReaderResult` + `AppReaderContext` + `AppReaderRegistry` (Reflection-Loader)
  - `CaptureWriter.WriteContent()` für `*.content.md` neben Capture-MD
  - **Browser-Reader** (msedge, chrome): Tab-Titel + URL via UIA `ValuePattern`, Body via `TextPattern` (Smoke-Test steht aus, kein Browser in der Sandbox-Session)
    - **Browser-Reader Iter. 3 — CDP als Opt-in:** zusätzlich Chrome DevTools Protocol
      (`appReader.browser.cdp.enabled`, Default `false`). Erfordert Browser-Start mit
      `--remote-debugging-port`. HTML → MD via `ReverseMarkdown 3.13.0`.
      DisplayName aktualisiert: „Browser (UIA; CDP opt-in via config)".
      Default-Verhalten bleibt UIA — bestehende Smoke-Tests laufen weiter grün.
  - **Notepad-Reader**: Buffer via Win32 `WM_GETTEXT` + rekursive Edit-Control-Suche via `EnumChildWindows`, Filename-Parsing (En-Dash/Em-Dash-tolerant) — Smoke-Test grün (15 Zeilen, 363 Zeichen aus echtem Notepad)
  - **Explorer-Reader** (neu): aktueller Pfad aus Fenster-Titel, Hyphen/En-Dash/Em-Dash-tolerant, Special-Folder-Liste (Desktop/Dieser PC/Schnellzugriff/…) → null — Smoke-Test grün (echtes Explorer-Fenster liefert Content-MD)
- [x] Tests: 271/271 grün (98 MVP1-Basis + 11 ReverseMarkdown-Iter-4 + 11 TriggerConfig Schritt A + 5 TriggerEvent + 8 WinEventHookDetector + 9 HeartbeatThread + 12 Throttle/HwndDedup Schritt D + 15 TriggerWorker Schritt E + 11 TriggerService Schritt F-Kern + 5 CaptureWriter-Parent Schritt F-Kern + 54 Documents-Reader Iter. 1 + 17 PDF-Viewer + 3 Office-COM-Integration + 8 Office-COM-Filename-Match Iter. 3)
- [x] **Documents-Reader Iter. 2 (Martin 2026-07-04) — COM-Interop:**
  - Neue Klasse `OfficeComInterop` (late binding via ProgID + P/Invoke
    auf `oleaut32.dll!GetActiveObject` — `Marshal.GetActiveObject` ist
    in .NET 8 SDK 8.0.422 nicht verfuegbar).
  - Word: `ActiveDocument.FullName` + `Range.Text` (Plain-Text in Code-Block)
  - Excel: `ActiveWorkbook.ActiveSheet.UsedRange` als Markdown-Tabelle
  - PowerPoint: alle `Slides` mit Text-Frames als `### Slide N`-Liste
  - Bei COM-Erfolg: `filePath` im Frontmatter + Inhalt unter
    `## Document content (via COM)` in `content.md`.
  - Fallback: bisherige Title+UIA-Logik. COM-Tests als
    `[Trait("Integration", "Office")]` (in Sandbox skipped).
- [x] **PDF-Viewer-Reader** (Martin 2026-07-04, neue DLL `AiRecall.AppReader.Pdf`):
  - Process-Liste konfigurierbar (`appReader.pdf.processes`, Default:
    Adobe/Sumatra/Foxit/PDFXChange/Edge/Chrome).
  - Title-Parsing: Filename + voller Pfad + Page-Nr (Sumatra/PDF-XChange-Style).
  - **Iter. 1**: kein PDF-Inhalt — `PdfPig` (NuGet) ist Kandidat fuer Iter. 2.
- [x] **Office-COM Iter. 3 — Pro-Instanz-Filename-Match** (Martin 2026-07-04):
  - `OfficeComInterop.MatchesExpectedFilename(fullPath, expectedFilename)`
    als internal static Helper (eigenständig unit-testbar).
  - Reader ruft `ParseTitle(window.Title)` zuerst und übergibt den erwarteten
    Filename an COM. Bei Mismatch (`ActiveDocument` einer anderen Instanz)
    → `null` → Fallback auf UIA+Title. **Verhindert falschen Pfad bei
    mehreren parallelen Office-Instanzen** (z. B. zwei Word-Fenster mit
    unterschiedlichen Dokumenten).
  - 8 neue Unit-Tests (`OfficeComInteropFilenameMatchTests`):
    null/empty expected, match, case-insensitive, mismatch, empty/null fullPath,
    unsaved-Doc-Sonderfall.
- [x] **Browser-Reader Iter. 4 — ReverseMarkdown 1:1 via JSON:** neue Sektion
  `appReader.browser.markdown` mappt alle 8 öffentlichen `ReverseMarkdown.Config`-Felder
  (`unknownTags`, `githubFlavored`, `removeComments`, `whitelistUriSchemes`,
  `smartHrefHandling`, `tableWithoutHeaderRowHandling`, `listBulletChar`,
  `defaultCodeBlockLanguage`) per POCO. Per-Call `BuildConverter()` statt statischem
  Converter — so greifen Reload-Änderungen sofort. Tests 98/98 grün
  (+11: `BuildConverter_NullSettings_*`, `_EmptySettings_PreservesLibraryDefaults`,
  `_AllSettings_AppliesAllValues`, `_UnknownTags_IsCaseInsensitive`,
  `_UnknownTags_InvalidString_LeavesDefault`, `_TableWithoutHeaderRow_*`,
  `_ListBulletChar_TakesFirstCharOnly`, `_WhitelistUriSchemes_EmptyList_*`,
  `ConvertHtmlToMarkdown_*` End-to-End, `AppConfig_BrowserConfig_HasMarkdownSettings`).
- [x] **Trigger-Pipeline (Spec 0005) komplett implementiert:**
  - `AiRecall.Trigger.dll` (eigene Assembly, Namespace `AiRecall.Trigger`):
    `ITriggerService`, `TriggerService` (Orchestrator), `WinEventHookDetector`
    (`SetWinEventHook` out-of-context, Message-Loop auf eigenem Thread),
    `HeartbeatThread` (periodisches `GetForegroundWindow`, Fallback),
    `TriggerWorker` (Pipeline-Schritte 1–12), `TriggerEvent`/`TriggerKind`,
    `HwndDedup` (Hex-Persistenz, `0xDEADBEEF`).
  - `AiRecall.Core.Util.{Throttle<TKey>, Dedup}` (generisch, gemeinsam
    genutzt).
  - `AiRecall.Core.Windows.WindowInfoLookup` (HWND → WindowInfo).
  - `CaptureWriter.Write` um `parentWindow: WindowInfo?` erweitert
    (Spec 0005 §Modale Dialoge, Option (a): nur Foreground-Capture +
    `parentHwnd`/`parentTitle`/`parentProcess` im Frontmatter).
  - `TriggerWorker` Modal-Dialog-Detection via
    `GetAncestor(GA_ROOTOWNER) != rootHwnd`.
  - CLI: `recall record` mit `--headless` (MVP2-Tray-EXE) und
    `--trigger-mode=events|polling|both`. `recall status` (neu):
    Diagnose (Config-Pfade, heutige Captures nach Prozess, aktive
    Trigger-Config), `--json` für MVP2-IPC.
  - Alte `CapturePipeline`/`EventDetector`/`Models.cs` (Polling-basiert)
    entfernt.
  - 91 neue Tests (Schritte A–G).
- [ ] App-Reader: Outlook (mit Mail-Log + Auto-Regel-Setting)
- [x] App-Reader: Word/Excel/PowerPoint (Spec 0004 Iter. Documents — UIA-only, Office nicht erforderlich; Tests grün, e2e-Smoke gegen Office ausstehend)
- [x] Trigger-Pipeline (`recall record`) — **komplett, Spec 0005 abgeschlossen**
- [x] **Async Document Conversion Pipeline (Spec 0007 v1.0 abgeschlossen)**
  - Neue DLL `AiRecall.Conversion` mit `DocumentConverter` (OpenXml/PdfPig/ReverseMarkdown/Plain)
  - `ConversionWorker` (in-process `Channel<string>`, Background-Task) — async OCR + DocumentConverter
  - `IOcrEngine` Interface + `TesseractOcrEngineAdapter` (async via `Task.Run`) + `NullOcrEngine`
  - `CaptureWriter.WritePending` + `UpdateConversionStatus` Frontmatter-Update-Pattern
  - `recall convert` Subcommand (Recovery für gecrashte Sessions)
  - Word/Excel/PowerPoint-Reader refactored zu **dünn** (`IsThinReader=true`, `ContentMarkdown=Platzhalter`)
  - `TriggerService` integriert `ConversionWorker` als Sink
  - **Test-Count: 331/331 grün** (vorher 271, +60)
  - Commits: `3a98e04`, `f176bea`, `9c7d9b5`, `de83a7e`, `84afab7`
  - Martin-Direktiven umgesetzt: Pandoc raus (Performance > Format-Coverage), `Channel<string>` statt FileSystemWatcher, OCR ebenfalls async, kein Legacy-Handling
- [x] **MVP2 Tray-Icon-EXE (Spec 0006 v1.0 abgeschlossen — inkl. 0008 Logviewer + 0009 Settings-Dialog)**
  - Neue DLL `AiRecall.TrayApp` (WinForms, .NET 8 windows) mit `AiRecall.TrayApp.exe`
  - SingleInstance-Mutex + `NotifyIcon` + `ContextMenuStrip` (Start/Stop, Live Logviewer, Settings, Quit)
  - `TriggerSupervisor` (in-process `ITriggerService`-Wrapper, Start/Stop/Restart, StateChanged-Event, CrashCount)
  - `InMemoryLogSink` (Serilog-Custom-Sink mit Ringbuffer 10.000 + EventEmitted) + `LogviewerSession` (Filter + Snapshot)
  - `LogviewerWindow` (DataGridView mit Level/Search-Filter, Pause/Clear/Auto-Scroll, Color-Coding)
  - `SettingsDialog` (TreeView + dynamische Form-Generierung: bool/int/string/enum/List-Editoren)
  - `ConfigSchemaReflection` (Reflection auf `AppConfig` POCOs) + `ConfigSerializer` (atomic write mit .bak-Backup) + `PropertyEditorFactory`
  - Hot-Reload: Settings Save → `TriggerSupervisor.Restart(newConfig)` (kein Process-Kill, kein Cold-Start)
  - **Test-Count: 416/416 grün** (vorher 331, +85)
  - Commits: `5ab077a`, `cff2b50`, `d9ffd11`, `12ced87`, `dc14dc0`, `da6586d`, `c23d3ca`, `e80d8fc`, `875ae98`
  - Architektur-Korrektur (Martin 22:29): in-process statt Subprozess — ProcessSupervisor + MMF-IPC entfallen komplett
  - Martin-Direktive umgesetzt: WinForms (kein WPF), in-process `ITriggerService`, Hot-Reload via Restart
- [x] Push auf `origin/main`

## Projektziel (Kurzfassung)

Lokales Windows-Tool, das die Bildschirmarbeit kontinuierlich aufzeichnet
und in durchsuchbare Markdown-Extraktionen + Screenshots überführt.
Vergleichbar mit Windows Recall / Screenpipe / rowboat, aber
Open Source, MIT, lokal-only und mit Fokus auf Office-Workflows
(Outlook Classic, Word, Excel, Browser).

Ausführlich: `specs/0001-vision.md`

## Project Files

| Datei/Ordner | Zweck |
|---|---|
| `LICENSE` | MIT-Lizenztext |
| `README.md` | GitHub-Readme (Status, Features, Quick Start, Architektur, OCR-Setup) |
| `DECISIONS.md` | Architektur- und Stack-Entscheidungen mit Datum/Begründung |
| `.gitignore` | Generische + .NET + Capture/Laufzeit-Ausschlüsse |
| `PROJECT.md` | Diese Datei — Current Status, Project Files |
| `AiRecall.sln` | Solution mit 8 Projekten |
| `global.json` | .NET SDK-Pin (8.0.422, `latestFeature`) |
| `specs/` | Spezifikationen, Roadmaps |
| `specs/0001-vision.md` | Vision + Roadmap MVP1/MVP2/MVP3 |
| `specs/0002-mvp1-scope.md` | MVP1-Scope, User Stories, Architektur, Config |
| `specs/0003-active-window.md` | `recall active-window` Command-Spec |
| `specs/0004-app-reader.md` | App-Reader-Architektur (eine DLL pro App, Outlook-Mail-Log) |
| `specs/0005-trigger-pipeline.md` | Trigger-Pipeline (WinEventHook + Heartbeat + Worker) |
| `specs/0006-mvp2-tray-exe.md` | MVP2 Tray-Icon-EXE (v1.0 abgeschlossen, inkl. 0008+0009) |
| `specs/0007-async-conversion.md` | Async Document Conversion Pipeline (v1.0 abgeschlossen) |
| `specs/0008-live-logviewer.md` | Live Logviewer Window (v1.0 abgeschlossen) |
| `specs/0009-settings-dialog.md` | Settings-Dialog JSON-Editor (v1.0 abgeschlossen) |
| `src/` | .NET-Solution-Projekte |
| `src/AiRecall.Core/` | Models, Configuration, Persistence, Util, Windows |
| `src/AiRecall.ScreenCapture/` | Win32 Window/Screenshot/OCR (kein Trigger mehr) |
| `src/AiRecall.Trigger/` | **Trigger-Pipeline-DLL (Spec 0005): WinEventHook + Heartbeat + Worker + Service** |
| `src/AiRecall.Conversion/` | **Async Document Conversion (Spec 0007)**: `DocumentConverter` + `ConversionWorker` + `IOcrEngine`/`TesseractOcrEngineAdapter`/`NullOcrEngine` |
| `src/AiRecall.AppReader.Documents/` | **Word/Excel/PowerPoint-Reader** (Spec 0004 Iter. Documents) — UIA-only |
| `src/AiRecall.TrayApp/` | **MVP2 Tray-Icon-EXE (Spec 0006 v1.0)**: `NotifyIcon` + `TriggerSupervisor`-Wiring + `LogviewerWindow` (Spec 0008) + `SettingsDialog` (Spec 0009) |
| `src/AiRecall.AppReader.Base/` | `IAppReader`-Interface + Basisklassen |
| `src/AiRecall.AppReader.{Browser,Outlook,Documents,Notepad,Explorer}/` | App-Reader-DLLs |
| `src/AiRecall.Cli/` | `recall`-Kommando + Serilog-Setup + Default-Config |
| `tests/AiRecall.Core.Tests/` | xUnit-Tests für Core + Trigger + App-Reader + Conversion (416 Tests) |
| `capture/` | (Laufzeit, gitignored) Screenshots + MD-Extraktionen |
| `logs/` | (Laufzeit, gitignored) Serilog Rolling-Logs |
| `tessdata/` | (Laufzeit, gitignored) Tesseract-Sprachdateien (manuell) |

## Konventionen

Folgen `projects/PROJECT-RULES.md`:
- Specs in `specs/`
- Externe Doku in `context/`
- Decisions in `DECISIONS.md`
- Current Status hier aktuell halten

## Offene Punkte (für MVP1)

1. **Spec 0004 (App-Reader) — Martin-Review ausstehend**
   - Eine DLL pro App: Browser ✅, Notepad ✅, Explorer ✅, Outlook ⏳ (mit Mail-Log + Auto-Regel-Setting), Documents (Word/Excel/PowerPoint) ⏳
   - Persistenz als zusätzliche `*.content.md` neben dem Capture-MD ✅
   - **Browser-Reader CDP opt-in erledigt (Iter. 3)** — siehe DECISIONS.md-Eintrag 2026-07-03
   - **Browser-Reader ReverseMarkdown 1:1 erledigt (Iter. 4)** — siehe DECISIONS.md
2. **Trigger-Pipeline (Spec 0005) — abgeschlossen (Schritte A–G):** ✅
   - `AiRecall.Trigger.dll`, `ITriggerService`/`TriggerService`
   - `WinEventHookDetector` (out-of-context) + `HeartbeatThread` (Fallback)
   - `TriggerWorker` (Pipeline 1–12) + `HwndDedup` + `Throttle<TKey>`
   - CLI: `recall record --headless --trigger-mode=events|polling|both`
   - CLI: `recall status [--json]` (Diagnose + MVP2-IPC-Vorbereitung)
   - Modal-Dialog-Frontmatter (`parentHwnd`/`parentTitle`/`parentProcess`)
3. **UIA-Fallback:** Wenn OCR zu schlecht, Windows UIA als zusätzliche Textquelle (oder als App-Reader-Default)
4. **OCR-Tessdata-Doku:** README-Schritt-für-Schritt-Anleitung zum Download ✅ (PS-One-Liner drin)
5. **MVP2: Tray-Icon-EXE** ✅ **abgeschlossen (Spec 0006 v1.0)**: Vollwertige
   Windows-Anwendung mit Notification-Area-Icon zum Steuern von
   `ITriggerService` (Start/Stop/Status). In-process-Architektur, kein Subprozess.
   Live Logviewer (Spec 0008) + Settings-Dialog (Spec 0009) inkludiert.
   CLI bleibt für Scripts erhalten.
6. **Spec 0007 Folge-Iterationen (nach v1.0)**:
   - PDF-Verschlüsselung-Handling + mehrseitige Dokumente
   - OCR-Preprocessing (Binarization/Deskew) optional
   - `*.conversion.md` ↔ `*.content.md` Vereinheitlichung (Schritt 7 nutzt `*.conversion.md` als Trennung zum App-Reader)