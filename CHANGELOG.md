# Versionshinweise

## v0.2.1 – 2026-11-21

- **Windows-Paket:** Das x64-ZIP enthält genau eine Datei: `AiRecall.exe` (vorher `AiRecall.TrayApp.exe`). Die .NET 10 Desktop Runtime (x64) bleibt erforderlich. Die Release-Pipeline prüft die EXE isoliert, bevor sie das ZIP veröffentlicht.
- **Aufnahmen:** Kommandozeile und Hintergrundaufnahme teilen die Ausschlussregeln für Apps, Fenstertitel und erkannte URLs sowie die Aufbereitung der Reader-Ergebnisse.
- **Hintergrundaufträge:** Beim Beenden nehmen Konvertierung und Transkription keine neuen Aufträge mehr an. Laufende und bereits angenommene Arbeit darf auslaufen; bei Zeitüberschreitung wird ein Abbruch angefordert.
- **Dateien und Einstellungen:** Tray-Einstellungen und OCR-Dialog schreiben die Konfiguration atomar und bewahren unbekannte JSON-Felder. YAML-Frontmatter erhält Windows-Pfade und mehrzeilige Inhalte.
- **Dokumentation:** Die README trennt den Start mit der fertigen Tray-App vom Build aus Quellcode und erklärt die Datenschutzoptionen.

[Änderungen seit v0.2.0](https://github.com/schirkan/ai-recall/compare/v0.2.0...v0.2.1)

## v0.2.0 – 2026-11-21

Umstieg auf .NET 10 und ein einzelnes Windows-x64-Binary; Korrekturen für Capture-Filter und -Stopp, Tray-Einstellungen, Teams-Polling sowie Audio-Stopp und WAV-Finalisierung. [GitHub-Release v0.2.0](https://github.com/schirkan/ai-recall/releases/tag/v0.2.0)
