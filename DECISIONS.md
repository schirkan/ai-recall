# DECISIONS

Architektur- und Stack-Entscheidungen mit Datum und Begründung. Wird bei
Bedarf von PROJECT.md oder specs/*.md geladen.

---

## 2026-07-03 — MVP1 Tech-Defaults

Offene Punkte aus `specs/0002-mvp1-scope.md` durch Martin bestätigt
(oder Default gesetzt):

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | OCR-Engine | **Tesseract** (lokal, mehrsprachig) | Martin: "Build in OCR". Multi-OS-tauglich, kein Microsoft-Cloud-Zwang, MIT-kompatibel. |
| 2 | CLI-Library | **Manueller Switch** (wie vorhanden) | Nur 5 Commands geplant; System.CommandLine/Spectre wären unnötiger Ballast. Switch-Pattern in `Program.cs` ist < 30 Zeilen. |
| 3 | Logging | **Serilog 3.1.1** + Console + File | Strukturiertes Logging, tägliche Rolling-Files, Standard im .NET-Ökosystem. |
| 4 | Tests | **xUnit** (bereits eingerichtet) | Bereits im Skeleton, gut für parallele Tests + VS-Integration. |
| 5 | Ignore-Liste | **Blacklist-Ansatz** mit kleinen Seed-Patterns | Default-Config seeded `1Password`, `KeePass`, `Bitwarden`, ein paar Title-Patterns (`Sign in`/`Anmelden`/`Passwort`/`Fingerprint`) und zwei URL-Patterns (`banking`, `accounts.google.com`). User kann via `%APPDATA%/AiRecall/config.json` erweitern. |

### Auswirkungen

- **Tesseract 5.2.0** als NuGet-Paket in `AiRecall.ScreenCapture`. Tessdata-Dateien sind nicht im Repo, Anleitung in `README.md` und `specs/0003-active-window.md`.
- **SerilogSetup** liegt in `AiRecall.Cli/Logging/` (nicht in Core), damit Core keine Sink-Deps braucht.
- **Default-Config** wird als `default-config.json` ins Output kopiert (`<None CopyToOutputDirectory="PreserveNewest">` im csproj).
- **System.Drawing.Common** braucht `UseWindowsForms=true` in `AiRecall.ScreenCapture.csproj` (für `Bitmap`/`Graphics`).

### Verworfen

- Windows.Media.Ocr — eingeschränkte Sprachunterstützung auf älteren Windows-Versionen, weniger portabel.
- System.CommandLine — Beta, größerer Refactor für 5 Commands unnötig.
- Spectre.Console.Cli — nett, aber ebenfalls Overhead ohne klaren Gewinn bei aktuellem Scope.
- Microsoft.Extensions.Logging — weniger mächtig als Serilog für strukturierte Capture-Pipeline.
- NUnit / MSTest — kein Mehrwert vs. xUnit bei aktuellem Bedarf.

## 2026-07-02 — Initial-Setup-Entscheidungen (aus Spec 0002)

- Lizenz: MIT
- Zielgruppe: Personal (nur Martin)
- Plattform: Windows only (MVP1)
- Solution-Struktur: Hybrid (zentrale `ScreenCapture`-DLL + `AppReader.Base` + separate App-Reader-DLLs)
- Trigger: Window-Activate + Scroll + Click mit Throttle + Dedup (Polling-basiert)
- Persistenz: Files only (MD + PNG, kein SQLite in MVP1)
- Outlook-Variante: Classic (MAPI/COM)
- GitHub-Repo: `schirkan/ai-recall` (public)
