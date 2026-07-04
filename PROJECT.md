# AI Recall — PROJECT

> Projekt-Workspace: `projects/ai-recall/`
> GitHub: `schirkan/ai-recall` (public, MIT)
> Branch: `main`
> Tech-Stack MVP1: C# / .NET 8 (`net8.0-windows`)

## Aktueller Status

**MVP1 — `active-window` + App-Reader Foundation + erste Reader (Browser, Notepad) + Trigger-Pipeline (Spec 0005).**

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
- [x] Tests: 243/243 grün (98 MVP1-Basis + 11 ReverseMarkdown-Iter-4 + 11 TriggerConfig Schritt A + 5 TriggerEvent + 8 WinEventHookDetector + 9 HeartbeatThread + 12 Throttle/HwndDedup Schritt D + 15 TriggerWorker Schritt E + 11 TriggerService Schritt F-Kern + 5 CaptureWriter-Parent Schritt F-Kern + 54 Documents-Reader Iter. 1)
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
| `src/` | .NET-Solution-Projekte |
| `src/AiRecall.Core/` | Models, Configuration, Persistence, Util, Windows |
| `src/AiRecall.ScreenCapture/` | Win32 Window/Screenshot/OCR (kein Trigger mehr) |
| `src/AiRecall.Trigger/` | **Trigger-Pipeline-DLL (Spec 0005): WinEventHook + Heartbeat + Worker + Service** |
| `src/AiRecall.AppReader.Base/` | `IAppReader`-Interface + Basisklassen |
| `src/AiRecall.AppReader.{Browser,Outlook,Documents,Notepad,Explorer}/` | App-Reader-DLLs |
| `src/AiRecall.AppReader.Documents/` | **Word/Excel/PowerPoint-Reader** (Spec 0004 Iter. Documents) — UIA-only |
| `src/AiRecall.Cli/` | `recall`-Kommando + Serilog-Setup + Default-Config |
| `tests/AiRecall.Core.Tests/` | xUnit-Tests für Core + Trigger + App-Reader (243 Tests) |
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
5. **MVP2: Tray-Icon-EXE** (Hinweis Martin 2026-07-04): Vollwertige
   Windows-Anwendung mit Notification-Area-Icon zum Steuern von
   `recall record` (Start/Stop/Pause/Status-Anzeige). CLI bleibt für
   Scripts erhalten. `ITriggerService` ist die Schnittstelle dafür.
   Spec folgt nach MVP1-Abschluss.