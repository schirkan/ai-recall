# AI Recall — PROJECT

> Projekt-Workspace: `projects/ai-recall/`
> GitHub: `schirkan/ai-recall` (public, MIT)
> Branch: `main`
> Tech-Stack MVP1: C# / .NET 8 (`net8.0-windows`)

## Aktueller Status

**MVP1 — `active-window` implementiert** (Spec 0003).

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
- [x] Tech-Defaults final (Tesseract, Serilog, xUnit, manueller CLI-Switch, Blacklist) — siehe `DECISIONS.md`
- [x] Tests: 18/18 grün (Hashing, IgnoreMatcher, ConfigLoader)
- [ ] OCR mit echten Tessdata-Dateien manuell auf Desktop verifiziert
- [ ] `recall record` (kontinuierlich mit Trigger-Pipeline)
- [ ] App-Reader: Browser, Outlook, Word, Excel
- [ ] Push auf `origin/main` (nach Tests + manueller Verifikation)

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
| `README.md` | GitHub-Readme (Status, Features, Quick Start, Architektur) |
| `DECISIONS.md` | Architektur- und Stack-Entscheidungen mit Datum/Begründung |
| `.gitignore` | Generische + .NET + Capture/Laufzeit-Ausschlüsse (inkl. `tessdata/`, `*.traineddata`) |
| `PROJECT.md` | Diese Datei — Current Status, Project Files |
| `AiRecall.sln` | Solution mit 8 Projekten |
| `global.json` | .NET SDK-Pin (8.0.422, `latestFeature`) |
| `specs/` | Spezifikationen, Roadmaps |
| `specs/0001-vision.md` | Vision + Roadmap MVP1/MVP2/MVP3 |
| `specs/0002-mvp1-scope.md` | MVP1-Scope, User Stories, Architektur, Config |
| `specs/0003-active-window.md` | `recall active-window` Command-Spec |
| `src/` | .NET-Solution-Projekte |
| `src/AiRecall.Core/` | Models, Configuration, Persistence, Util |
| `src/AiRecall.ScreenCapture/` | Win32 Window/Screenshot/OCR, Trigger-Logik |
| `src/AiRecall.AppReader.Base/` | IAppReader-Basis (leer, für App-Reader-DLLs) |
| `src/AiRecall.AppReader.{Browser,Outlook,Documents}/` | App-Reader (Stubs, kommen nach `active-window`) |
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

1. **Trigger-Pipeline (`recall record`):** Polling auf `GetForegroundWindow` + Scroll/Click-Detection + Throttle + Dedup (TR-1..6)
2. **App-Reader:** Browser (Chrome/Edge/FF URL+Text), Outlook Classic (MAPI), Word, Excel → MD
3. **UIA-Fallback:** Wenn OCR zu schlecht, Windows UIA als zusätzliche Textquelle
4. **State-File:** Letzter Hash pro Prozess für dedup, evtl. SQLite in MVP2/3
5. **OCR-Tessdata-Doku:** README-Schritt-für-Schritt-Anleitung zum Download
