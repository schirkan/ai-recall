# AI Recall — PROJECT

> Projekt-Workspace: `projects/ai-recall/`
> GitHub: `schirkan/ai-recall` (public, MIT)
> Branch: `main`
> Tech-Stack MVP1: C# / .NET 8 (`net8.0-windows`)

## Aktueller Status

**MVP1 — `active-window` + App-Reader Foundation + erste Reader (Browser, Notepad).**

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
- [x] Tests: 98/98 grün (+35 AppReader-Tests seit `Browser-Reader Tests (App-Reader Iteration 2)`: +20 inkl. CDP-Iter. 3, +11 für ReverseMarkdown-Config-Mapping in Iter. 4)
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
- [ ] App-Reader: Outlook (mit Mail-Log + Auto-Regel-Setting), Word/Excel/PowerPoint
- [ ] Trigger-Pipeline (`recall record`) — naechste Iteration
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
| `src/` | .NET-Solution-Projekte |
| `src/AiRecall.Core/` | Models, Configuration, Persistence, Util |
| `src/AiRecall.ScreenCapture/` | Win32 Window/Screenshot/OCR, Trigger-Logik |
| `src/AiRecall.AppReader.Base/` | `IAppReader`-Interface + Basisklassen (leer, kommt mit 0004) |
| `src/AiRecall.AppReader.{Browser,Outlook,Documents}/` | App-Reader-DLLs (Stubs, kommen mit 0004) |
| `src/AiRecall.Cli/` | `recall`-Kommando + Serilog-Setup + Default-Config |
| `tests/AiRecall.Core.Tests/` | xUnit-Tests für Core (Hashing, IgnoreMatcher, ConfigLoader) |
| `capture/` | (Laufzeit, gitignored) Screenshots + MD-Extraktionen |
| `logs/` | (Laufzeit, gitignored) Serilog Rolling-Logs |
| `tessdata/` | (Laufzeit, gitignored) Tesseract-Sprachdateien (manuell) |

## Konventionen

Folgen `projects/PROJECT-RULES.md`:
- Specs in `specs/`
- Externe Doku in `context/`
- Decisions in `DECISIONS.md`
- Current Status hier aktuell halten

## Offene Punkte (für MVP1 nach `active-window`)

1. **Spec 0004 (App-Reader) — Martin-Review ausstehend**
   - IAppReader-Interface-Design
   - Eine DLL pro App (8 Stück): Browser, Outlook, Documents (Word/Excel/PowerPoint), Notepad, Explorer
   - Outlook-Mail-Log mit `ignoreAutoRuleMails`-Setting
   - Persistenz als zusätzliche `*.content.md` neben dem Capture-MD
   - Plugin-Discovery via Reflection
   - **Browser-Reader CDP opt-in erledigt (Iter. 3)** — siehe DECISIONS.md-Eintrag 2026-07-03
2. **Trigger-Pipeline (`recall record`):** SetWinEventHook (out-of-context) als
   Hauptquelle + Heartbeat-Polling-Fallback + Throttle (per HWND + per App) +
   HWND-Dedup + Parent-Context für modale Dialoge (Spec 0005, TR-1..9).
   Iteration läuft (Schritte A-C committed, D-H offen).
3. **UIA-Fallback:** Wenn OCR zu schlecht, Windows UIA als zusätzliche Textquelle (oder als App-Reader-Default)
4. **OCR-Tessdata-Doku:** README-Schritt-für-Schritt-Anleitung zum Download ✅ (PS-One-Liner drin)
5. **State-File:** Letzter Hash pro HWND für Dedup, evtl. SQLite in MVP2/3
6. **MVP2: Tray-Icon-EXE** (Hinweis Martin 2026-07-04): Vollwertige
   Windows-Anwendung mit Notification-Area-Icon zum Steuern von
   `recall record` (Start/Stop/Pause/Status-Anzeige). CLI bleibt für
   Scripts erhalten. `TriggerService` wird über ein
   `ITriggerService`-Interface gekapselt, damit CLI und Tray-EXE
   denselben Code nutzen. Spec folgt nach MVP1-Abschluss.