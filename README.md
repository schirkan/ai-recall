# AI Recall

AI Recall ist ein Windows-Tool für Menschen, die Bildschirmarbeit in Browsern und Office-Anwendungen später nachvollziehen möchten: Es speichert Fensteraufnahmen und extrahierte Inhalte als PNG- und Markdown-Dateien.

## Schnellstart: Tray-App

**Voraussetzungen:** Windows 10 oder 11 (x64) und die [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0). Für die fertige App ist kein SDK nötig.

1. Das Windows-x64-ZIP unter [GitHub Releases](https://github.com/schirkan/ai-recall/releases/latest) herunterladen und in einen beschreibbaren Ordner entpacken.
2. `AiRecall.exe` starten. Falls du noch das ZIP von `v0.2.0` verwendest: Dort heißt die Datei `AiRecall.TrayApp.exe`.
3. Beim ersten Start die Einstellungen prüfen, besonders den Speicherordner und die Ausschlussregeln. Wenn du OCR nutzen möchtest, kannst du fehlende Tesseract-Sprachdaten über den angebotenen Dialog herunterladen.
4. Im Windows-Infobereich das AI-Recall-Menü öffnen und **Start Recording** wählen. Zu einem unkritischen Fenster wechseln, etwa Notepad mit dem Text `Hallo AI Recall`, und danach **Stop Recording** wählen.

Im konfigurierten Capture-Ordner findest du nach einer erfolgreichen Aufnahme ein PNG und eine Markdown-Datei mit Fenstermetadaten. Standardmäßig liegt der Ordner `capture` neben der Tray-EXE. Je nach Anwendung und Einstellungen kommt extrahierter Text hinzu.

## Was AI Recall erfasst

- **Fenster:** Aufnahmen bei Fenster- und Fokusereignissen; ein periodisches Capture-Intervall ist optional. [Trigger und Filter](./specs/0005-trigger-pipeline.md)
- **Text und Metadaten:** Tesseract-OCR und spezialisierte Reader, etwa für Edge, Chrome, Notepad, Explorer, Office, Outlook Classic, OneNote und Teams. Welche Inhalte ausgelesen werden können, hängt von der Anwendung und der verfügbaren Schnittstelle ab. [App-Reader](./specs/0004-app-reader.md) · [Dokumentkonvertierung](./specs/0007-async-conversion.md)
- **Audio, optional:** Manuelle Aufnahmen oder automatische Aufnahmen in Teams-Meetings. Mikrofon und Wiedergabe werden separat als WAV gespeichert. [Audio-Notizen](./specs/0013-audio-notes-mvp3.md) · [Tray-Audiosteuerung](./specs/0014-tray-audio-indicator.md)

## Datenschutz und Einstellungen

Screenshots, Markdown und WAV-Dateien werden lokal gespeichert. Audioaufnahme und Cloud-Transkription sind standardmäßig aus. Nur bei aktivierter Cloud-Transkription werden Audiodaten an den konfigurierten Anbieter (Azure Speech oder Deepgram) übermittelt. Der Download von OCR-Sprachdaten benötigt eine Internetverbindung, überträgt aber keine Aufnahmen.

- **Ausschlüsse:** `screenRecorder.ignoreApps`, `screenRecorder.ignoreWindowTitles` und `screenRecorder.ignoreUrls`. URL-Regeln greifen nur, wenn ein Reader die URL ermitteln kann. Prüfe die Regeln vor der Aufnahme sensibler Inhalte und kläre bei Meetings, ob du aufnehmen darfst.
- **Speicherort:** `capture.rootPath` für Fensteraufnahmen, `audio.storageRoot` für Audio. Einstellungen der Tray-App liegen in `%APPDATA%\AiRecall\config.json`.
- **Teams und Cloud:** Manuelles Audio benötigt `audio.enabled`. Automatische Teams-Aufnahmen benötigen zusätzlich `appReader.teams.enabled` und `appReader.teams.autoRecordMeetings`. Cloud-Transkription hat mit `transcription.enabled` einen separaten Schalter.

[Tray-Einstellungen](./specs/0009-settings-dialog.md) · [OCR-Erststart](./specs/0012-tessdata-first-run.md) · [CLI-Beispielkonfiguration](./src/AiRecall.Cli/default-config.json)

## Aus Quellcode bauen und testen

Auf Windows mit Git und dem [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (ab `10.0.402`, siehe [`global.json`](./global.json)). Die Befehle sind für PowerShell; nach `Set-Location` werden sie im Repository-Stamm ausgeführt.

```powershell
git clone https://github.com/schirkan/ai-recall.git
Set-Location ai-recall
dotnet build AiRecall.sln -c Release
dotnet run --project src/AiRecall.Cli -c Release --no-build -- list-windows --help
```

Die CLI zeigt `Usage: recall list-windows [options]`. Ohne `--help` listet sie offene Fenster mit Prozessname, Titel und `HWND` auf. Für eine einzelne Testaufnahme einen Wert aus der Spalte `HWND` eines unkritischen Fensters übernehmen:

```powershell
$hwnd = '0x00001234' # Beispielwert; durch die eigene Fenster-ID ersetzen.
dotnet run --project src/AiRecall.Cli -c Release --no-build -- active-window --hwnd $hwnd --no-ocr
```

Bei Erfolg gibt die CLI die Pfade zu PNG und Markdown aus. Die CLI speichert standardmäßig unter `capture` im Arbeitsverzeichnis; wenn Ausschlussregeln greifen, wird der Capture übersprungen. Die CLI ist ein separates Projekt und nicht Teil der heruntergeladenen Tray-EXE. [CLI-Optionen](./specs/0003-active-window.md)

```powershell
dotnet test AiRecall.sln -c Release
dotnet publish src/AiRecall.TrayApp/AiRecall.TrayApp.csproj -c Release -p:PublishProfile=WinX64SingleFile -o publish/AiRecall
```

Der Publish erzeugt `publish/AiRecall/AiRecall.exe` als einzelne Windows-x64-Datei. Die .NET 10 Desktop Runtime bleibt erforderlich. Die [Release-Pipeline](./.github/workflows/release.yml) baut und testet bei gepushten `v*`-Tags und veröffentlicht das ZIP erst nach erfolgreicher Paketprüfung.

## Dokumentation und Beiträge

- [Versionshinweise](./CHANGELOG.md), [Spezifikationen](./specs/README.md), [Roadmap](./specs/0001-vision.md) und [Architekturentscheidungen](./DECISIONS.md)
- [GitHub Issues](https://github.com/schirkan/ai-recall/issues) für Fehler und Vorschläge. Bitte Version, Windows-Version und Reproduktionsschritte angeben; keine privaten Aufnahmen, Mitschriften oder Zugangsdaten anhängen.
- [MIT-Lizenz](./LICENSE). Inspiriert von [Windows Recall](https://support.microsoft.com/windows/recall), [Screenpipe](https://github.com/screenpipe/screenpipe) und [rowboat](https://github.com/rowboatlabs/rowboat).
