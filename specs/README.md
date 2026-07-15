# Specs — Index

| #                                | Titel                                | Status                                         |
| -------------------------------- | ------------------------------------ | ---------------------------------------------- |
| [0001](0001-vision.md)           | Vision & Roadmap                     | Draft v0.1                                     |
| [0002](0002-mvp1-scope.md)       | MVP1 Scope & Architektur             | Draft v0.1                                     |
| [0003](0003-active-window.md)    | `recall active-window` Command       | Draft v0.1                                     |
| [0004](0004-app-reader.md)       | App-Reader Architecture              | Iter. 2 abgeschlossen (2026-07-04) — COM + PDF |
| [0005](0005-trigger-pipeline.md) | Trigger-Pipeline (`recall record`)   | **Abgeschlossen v1.0 (2026-07-04)**            |
| [0006](0006-mvp2-tray-exe.md)    | MVP2 Tray-Icon-EXE (Foundation)      | **Abgeschlossen v1.0 (2026-07-04)**            |
| [0007](0007-async-conversion.md) | Async Document Conversion Pipeline   | **v1.1 (2026-07-06) — In-Place-Content**       |
| [0008](0008-live-logviewer.md)   | Live Logviewer Window                | **Abgeschlossen v1.0 (2026-07-04)**            |
| [0009](0009-settings-dialog.md)  | Settings-Dialog (JSON Config Editor) | **Abgeschlossen v1.0 (2026-07-04)**            |
| [0010](0010-onenote-app-reader.md)| OneNote App-Reader                  | **Abgeschlossen v1.0 (2026-07-05)** — Read-only, COM, 4-stufige Active-Page-Strategie |
| [0011](0011-teams-app-reader.md) | Teams App-Reader                     | **Abgeschlossen v1.0 (2026-07-05)** — Modern Teams only, UIA + CDP opt-in |
| [0012](0012-tessdata-first-run.md)| Tessdata First-Run Download         | ✅ **abgeschlossen (2026-07-15)** — Modal-Dialog beim ersten Start, sequentieller Download mit Retry, `osd`-Filter |
| [0013](0013-audio-notes-mvp3.md) | **MVP 3 Audio Notes**                | ✅ **abgeschlossen v1.0 (2026-07-09)** — Teams-Meeting-Detection + 2-Kanal-Audio + Diarization-Transkription |
| [0015](0015-default-credentials-for-downloads.md) | Default-Credentials für HTTP-Downloads | ✅ **abgeschlossen (2026-07-15)** — `HttpClientFactory.CreateDefaultHandler()` für NTLM/Kerberos-Proxy-Auth |
| [0016](0016-first-run-settings-dialog.md) | First-Run Settings-Dialog        | ✅ **abgeschlossen (2026-07-15)** — Settings-Dialog automatisch beim ersten Start (vor Tessdata-Dialog) |

## Konvention

- Dateinamen: `NNNN-slug.md` (4-stellige Nummer + Slug)
- Jede Spec hat oben: **Status**, **Owner**, **Datum**
- Verwandte Specs am Ende verlinkt