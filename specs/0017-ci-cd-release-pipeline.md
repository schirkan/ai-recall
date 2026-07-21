# Spec 0017 — CI/CD Release-Pipeline

**Status:** v1.0 (in Umsetzung)
**Datum:** 2026-07-20
**Autor:** Pia (mit Martin-Direktive)
**Repo:** `schirkan/ai-recall` (public, MIT)
**Plattform:** GitHub Actions (alle anderen Optionen wurden explizit ausgeschlossen)
**Trigger:** Tag-Push mit Pattern `v*` (z. B. `v0.1.0-rc1`, `v1.0.0`)

---

## Ziel

Jeder neue Git-Tag, der dem SemVer-Pattern `v*` entspricht, löst eine automatisierte Pipeline aus, die

1. das .NET-Solution-Build auf einer sauberen Windows-Runner-Maschine ausführt (kein lokales Build-Artifact-Risiko),
2. alle xUnit-Tests laufen lässt (Regression-Gate),
3. das Haupt-Binary `AiRecall.TrayApp` (Spec 0006) als Release-Artefakt in Form eines ZIP-Bundles baut und als GitHub-Release-Asset hochlädt,
4. ein GitHub-Release mit Auto-Release-Notes basierend auf den Commits seit dem letzten Tag erstellt.

Erst-Release-Ziel: **`v0.1.0-rc1`** (Spec 0014 v1.0 = stabiles MVP-1+2+3-Feature-Set, Release-Candidate für externe Smoke-Tests).

---

## Architektur-Übersicht

```
┌─────────────────────────────────────────────────────────────────────────┐
│  git push origin v0.1.0-rc1                                            │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  GitHub Actions: workflow "Release" (.github/workflows/release.yml)    │
│  Trigger:    push tags matching 'v*'                                   │
│  Runner:     windows-latest                                            │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                ┌───────────────────┴───────────────────┐
                ▼                                       ▼
┌─────────────────────────────┐         ┌─────────────────────────────┐
│  Job 1: build-and-test      │         │  Job 2: release (needs       │
│                             │         │        build-and-test)       │
│  • Checkout                 │         │                             │
│  • Setup .NET 8 (global.json│         │  • Checkout                  │
│  • Restore dotnet           │         │  • Download Artifact         │
│  • Build Release            │         │  • Create GitHub Release     │
│  • Run xUnit Tests          │         │    - Tag = trigger-tag       │
│  • dotnet publish TrayApp   │         │    - Auto-generated notes    │
│  • ZIP publish-Output       │         │    - Asset: ZIP hochladen    │
│  • Upload Artifact          │ ──────► │                             │
└─────────────────────────────┘         └─────────────────────────────┘
                                                                  │
                                                                  ▼
                                              https://github.com/schirkan/ai-recall/releases/tag/v0.1.0-rc1
```

Job 2 hängt von Job 1 ab (`needs: build-and-test`), damit das Release nur bei grünen Tests erstellt wird.

---

## Trigger-Details

| Event | Filter | Wirkung |
|-------|--------|---------|
| `push` | `tags: 'v*'` (z. B. `v0.1.0`, `v1.2.3-rc1`, `v2.0.0-beta.1`) | Startet Pipeline |
| `push` | `tags: 'something-else'` | **Wird ignoriert** |
| `pull_request` | — | **Wird ignoriert** (kein PR-Build, nur Tag-Releases) |
| `workflow_dispatch` | — | **Manuell ausführbar** für Notfall-Rebuilds |

Begründung Tag-only-Trigger: Pull-Request-Builds sind hier explizit nicht gewünscht (Martin-Direktive: "bei neuen Tags triggert"); ein PR-Build-Workflow kann später in einer separaten Spec nachgerüstet werden, falls Qualitätssicherung das erfordert.

---

## Build-Konfiguration

### Runner

- **`runs-on: windows-latest`** — Pflicht, weil das Projekt `net8.0-windows` targettet und Win32-P/Invoke (u. a. `oleaut32.dll`, `TextRenderer`) Windows-spezifisch ist. `ubuntu-latest` würde das Build mit nativen Win32-PInvoke-Aufrufen brechen.

### .NET-SDK

- **`actions/setup-dotnet@v4`** mit explizitem Pin auf **.NET 8.0.x** (latest patch).
- **Workspace-weites `global.json`** (Commit `2c41fb0`) pinnt auf `8.0.422` mit `latestFeature`-Rollforward → `setup-dotnet` mit `dotnet-version: 8.0.x` ist kompatibel.

### Build- und Test-Schritte

| Schritt | Befehl | Zweck |
|---------|--------|-------|
| Checkout | `actions/checkout@v4` mit `fetch-depth: 0` | Volle Historie für Release-Notes-Generierung |
| Setup .NET | `actions/setup-dotnet@v4` | SDK auf Runner |
| Restore | `dotnet restore AiRecall.sln` | NuGet-Pakete |
| Build | `dotnet build -c Release --no-restore` | Compile, Warnings-as-errors (siehe `.editorconfig` falls vorhanden, sonst nur default) |
| Test | `dotnet test -c Release --no-build --logger trx --logger "console;verbosity=normal"` | xUnit-Suite, **Counter/Async-Regel** bleibt aktiv (Spec 0013 Iter. 4 Counter-Race-Fix) |
| Publish | `dotnet publish src/AiRecall.TrayApp/AiRecall.TrayApp.csproj -c Release -o publish/AiRecall.TrayApp -p:PublishSingleFile=false --no-build` | TrayApp als verteilbares Binary (Multi-File für einfacheres Debuggen, Spec 0006 v1.0) |
| ZIP | PowerShell `Compress-Archive` | `publish/AiRecall.TrayApp/` → `AiRecall-{version}-win-x64.zip` |
| Upload Artifact | `actions/upload-artifact@v4` mit `if-no-files-found: error` | ZIP als Workflow-Artifact (Zwischenspeicher für Job 2) |

### Version-Extraktion

Der ZIP-Dateiname nutzt die Tag-Version direkt:

```yaml
- name: Determine version
  id: version
  shell: pwsh
  run: |
    $tag = "${{ github.ref_name }}"   # z. B. "v0.1.0-rc1"
    $version = $tag.TrimStart('v')    # "0.1.0-rc1"
    "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
```

→ `AiRecall-0.1.0-rc1-win-x64.zip` als Asset-Name.

---

## Release-Job (Job 2)

```yaml
release:
  name: Create GitHub Release
  needs: build-and-test          # ← Tests müssen grün sein
  runs-on: windows-latest
  if: startsWith(github.ref, 'refs/tags/v')
  permissions:
    contents: write               # ← nötig für 'gh release create'
  steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0

    - name: Download build artifact
      uses: actions/download-artifact@v4
      with:
        name: recall-zip
        path: dist

    - name: Create GitHub Release
      uses: softprops/action-gh-release@v2
      with:
        tag_name: ${{ github.ref_name }}
        name: "AI Recall ${{ github.ref_name }}"
        generate_release_notes: true
        fail_on_unmatched_files: true
        files: |
          dist/*.zip
```

**`generate_release_notes: true`** — GitHub generiert die Notes automatisch aus Commits seit dem letzten Tag, gruppiert nach PR/Conventional-Commits-Stil. Spart manuelles Changelog-Schreiben.

**`fail_on_unmatched_files: true`** — schlägt fehl, wenn das ZIP-Artefakt fehlt → verhindert "leere" Releases.

---

## Permissions

```yaml
permissions:
  contents: read    # Default; reicht für checkout + build
```
im Build-Job, plus
```yaml
permissions:
  contents: write   # für softprops/action-gh-release
```
im Release-Job.

Minimal-Permission-Prinzip: kein `packages: write`, kein `id-token: write`. Nur was wirklich gebraucht wird.

---

## Was wird NICHT gebaut / publiziert

| Artefakt | Warum nicht |
|----------|-------------|
| Docker-Image | Spec 0017 v1.0 ist Windows-natives Binary; Docker wäre Spec-Folge (Spec-Kandidat 0016) |
| NuGet-Packages | `AiRecall.*` sind Anwendungs-DLLs, nicht Library-Pakete. Falls später Libraries extrahiert werden, separates Workflow-Feature |
| `*.nupkg` für Tessdata-Modelle | Spec 0012 plant Auto-Download zur Laufzeit, kein Build-Time-Bundling |
| Code-Signierung | Aktuell deaktiviert — Spec-Kandidat (Spec 0017), würde Codesign-Cert + Windows-Secrets erfordern |
| Linux-/macOS-Builds | `net8.0-windows` targettet Windows-only (Spec 0002 v1.0) |

---

## Tag-Schema

Wir verwenden **Semantic Versioning** mit `v`-Prefix:

| Tag | Bedeutung |
|-----|-----------|
| `v0.1.0-rc1` | Erstes Release-Candidate (aktuelles Ziel) |
| `v0.1.0` | Erstes stabiles MVP-1+2+3-Release |
| `v0.2.0-rc1` | Nächste Iteration (z. B. Spec 0017 Folge-Iterations, Spec 0012 Modal-Dialog) |
| `v1.0.0` | Erstes "Production-ready"-Release (alle Specs 0001-0014 stabil) |

Pre-Releases (`-rc1`, `-rc2`, `-beta.1`) werden von GitHub als Pre-Release markiert (GitHub erkennt `-` im Tag automatisch → Pre-Release-Flag im UI gesetzt).

---

## Lokales Nachvollziehen (für Tests)

Wer das Build lokal reproduzieren will, kann auf seinem Windows-Entwicklungsrechner:

```bash
dotnet restore AiRecall.sln
dotnet build -c Release
dotnet test -c Release --no-build
dotnet publish src/AiRecall.TrayApp/AiRecall.TrayApp.csproj -c Release -o publish/AiRecall.TrayApp
# manuelles Zippen:
Compress-Archive -Path publish/AiRecall.TrayApp/* -DestinationPath AiRecall-0.1.0-rc1-win-x64.zip
```

---

## Build-Trim-Targets (Iter. 1.1, 2026-07-21)

**Anlass:** `Microsoft.CognitiveServices.Speech 1.40.0` liefert per Default Runtimes fuer 18 Plattformen → ~197 MB pro Projekt in `bin/Debug/.../runtimes/`. Projekt ist explizit Windows-x64-only (DECISIONS.md 2026-07-02 „Windows only").

**Commits:** `c0070e2` (10:31, PostBuildRuntimesTrim) + `06237dc` (10:54, PostPublishRuntimesTrim).

**Zwei Trim-Targets** (PowerShell-Exec statt MSBuild-ItemGroup, weil `<ItemGroup Include="dir\*">` per Default nur Files matched, keine Directories):

| Target | AfterTargets | Output-Pfad | Workflow |
|---|---|---|---|
| `PostBuildRuntimesTrim` | `Build` | `$(MSBuildProjectDirectory)\$(OutputPath)runtimes\` (also `bin/.../runtimes/`) | Dev/IDE (`dotnet run`, VS-Startprojekt) |
| `PostPublishRuntimesTrim` | `Publish` | `$(PublishDir)runtimes\` (also `publish/AiRecall.TrayApp/runtimes/`) | CI/CD (`dotnet publish` ohne vorgeschalteten `dotnet build`) |

Beide Targets behalten `win-x64` und loeschen den Rest (~17 RID-Ordner).

**Implementierung:** in Cli.csproj + TrayApp.csproj (gespiegelt). Grund: .NET legt RID-Ordner im Output jedes konsumierenden Projekts an, nicht nur im liefernden Projekt (Transcription.csproj). PostBuild-Target allein reicht NICHT fuer CI, weil die Release-Pipeline nur `dotnet publish` aufruft, nicht `dotnet build`.

**Resultat:** ZIP-Groesse 70,86 MiB → 12,10 MiB (83 % kleiner). Lokal verifiziert nach `c0070e2` + `06237dc` (HEAD `26a54c8`).

---

## Bekannte Limitierungen / Spec-Folge-Iterationen

1. **Keine SBOM / Vulnerability-Scan** — Spec-Kandidat 0018 (später, falls Security-Audit gewünscht).
2. **Keine Code-Signierung** — siehe oben, Spec-Kandidat 0017.
3. **Kein automatischer Changelog-Pre-Render** — `generate_release_notes` reicht vorerst; falls strukturiertes `CHANGELOG.md` gewünscht, Spec-Folge.
4. **Build-Zeit nicht optimiert** — kein NuGet-Cache zwischen Jobs, keine Matrix-Strategie für Multi-Project-Build. Akzeptabel bei < 10 min Build-Zeit; falls Pipeline > 15 min, Spec-Folge mit Caching.
5. **Single-File-Publish optional** — aktuell Multi-File-Publish (`PublishSingleFile=false`) für leichteres Debugging. Spec-Folge kann auf Single-File umstellen, falls Größe wichtiger als Debugbarkeit wird.

---

## Referenzen

- **Spec 0006 v1.0** — MVP2 Tray-Icon-EXE (Haupt-Build-Target)
- **Spec 0013 v0.3 Update 8** — MVP3 Audio Notes (im Release enthalten, Iter. 1-4 + Counter-Race-Fix)
- **Spec 0014 v1.0** — Tray Audio Indicator + Manual Audio Control
- **`.gitattributes`** (Commit `77e7293`) — Line-Ending-Strategie (LF für `*.yml`, CRLF für Windows-Scripts)
- **`global.json`** — .NET 8.0.422-Pin
- **`projects/PROJECT-RULES.md`** — Pflicht-Sektionen `## Git` und `## CI/CD` in `PROJECT.md`
