# AI Recall — PROJECT

> Projekt-Workspace: `projects/ai-recall/`
> GitHub: `schirkan/ai-recall` (public, MIT)
> Branch: `main`
> Tech-Stack MVP1: C# / .NET 8 (`net8.0-windows`)

## Aktueller Status

**Spec-Phase** — Vision + MVP1-Scope bestätigt, .NET-Solution-Skeleton in Arbeit.

- [x] Projektordner angelegt
- [x] Lokales Git-Repo initialisiert (`main`)
- [x] `.gitignore` angelegt
- [x] GitHub-Repo erstellt (`schirkan/ai-recall`, public)
- [x] Vision dokumentiert (`specs/0001-vision.md`)
- [x] MVP1-Scope dokumentiert (`specs/0002-mvp1-scope.md`)
- [x] Architektur-Entscheidungen bestätigt (MIT, Windows-only, Hybrid-DLLs, Trigger-Pipeline)
- [x] `LICENSE` (MIT) angelegt
- [x] `README.md` angelegt
- [ ] dotnet-8-Solution-Skeleton (`AiRecall.sln` + 7 Projekte)
- [ ] Erstes CLI-Command (`recall list-windows`) lauffähig
- [ ] Push auf `origin/main`

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
| `.gitignore` | Generische Ausschlüsse (wird um .NET-Spezifika ergänzt) |
| `PROJECT.md` | Diese Datei — Current Status, Project Files |
| `specs/` | Spezifikationen, Roadmaps |
| `specs/0001-vision.md` | Vision + Roadmap MVP1/MVP2/MVP3 |
| `specs/0002-mvp1-scope.md` | MVP1-Scope, User Stories, Architektur, Config |
| `src/` | (geplant) .NET-Solution `AiRecall.sln` + Projekte |
| `tests/` | (geplant) Unit-/Integration-Tests |
| `capture/` | (Laufzeit, gitignored) Screenshots + MD-Extraktionen |
| `DECISIONS.md` | (geplant) Architekturentscheidungen |

## Konventionen

Folgen `projects/PROJECT-RULES.md`:
- Specs in `specs/`
- Externe Doku in `context/`
- Decisions in `DECISIONS.md`
- Current Status hier aktuell halten

## Offene Punkte (für MVP1 noch zu klären)

1. **Ignore-Liste:** Apps/URLs/Pattern, die nie erfasst werden sollen
   (z. B. Banking, 1Password, Inkognito-Browser)
2. **OCR-Engine:** Tesseract (lokal, mehrsprachig) oder Windows.Media.Ocr
   (built-in, nur en-US auf älteren Windows-Versionen)?
3. **CLI-Library:** System.CommandLine vs. Spectre.Console.Cli
4. **Logging:** Serilog oder Microsoft.Extensions.Logging
5. **Tests:** xUnit, NUnit, oder MSTest?