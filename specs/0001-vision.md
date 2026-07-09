# 0001 — Vision & Roadmap

> **Status:** Draft v0.2 (2026-07-09) — Roadmap aktualisiert nach MVP-3-Audio-Notes-Abnahme
  und 2026-07-06 Reshuffle (MVP4=Auto Wiki statt MVP3); siehe DECISIONS.md.
> **Owner:** Martin

## Konzept

**AI Recall** ist ein lokales Windows-Tool, das die Arbeit am Bildschirm
kontinuierlich aufzeichnet, strukturiert und durchsuchbar macht — als
„persönliches Gedächtnis" für Recherchen, Mails, Dokumente, Meetings.

Funktional orientiert an:

- **Windows Recall** (Microsoft) — Screenshot-basiertes Memory
- **Screenpipe** (https://github.com/screenpipe/screenpipe) — 24/7 lokales
  Screen + Audio Capture, OCR, SQLite-Index
- **rowboat** (https://github.com/rowboatlabs/rowboat) — AI Coworker mit
  Memory- und Context-Engineering

## Differenzierung

Anders als Windows Recall läuft AI Recall **vollständig lokal**, ist
**Open Source** und fokussiert auf **Windows-Office-Workflows** (Outlook,
Word, Excel, Browser).

| Feature                  | Win Recall | Screenpipe | rowboat | AI Recall   |
|--------------------------|:----------:|:----------:|:-------:|:-----------:|
| Plattform                | Win11+     | Cross      | Cross   | Windows     |
| Open Source              | ❌         | ✅ MIT     | ✅      | ✅          |
| Outlook/Mail Extract     | ❌         | ❌         | ❌      | ✅          |
| Word/Excel → MD          | ❌         | ❌         | ❌      | ✅          |
| Browser Content Extract  | ❌         | ✅         | ✅      | ✅          |
| Auto-Meeting Record      | ❌         | ✅ (audio) | ✅      | ✅ (MVP2)   |
| Auto-Wiki / Knowledge    | ❌         | ❌         | ✅      | ✅ (MVP3)   |

## Roadmap

### MVP 1 — Screen Recorder + App Reader ✅

- **Screen Recorder:** Fenster-Liste, aktives Fenster, Screenshot, OCR/UIA,
  Deduplizierung
- **App Reader:** Browser-Content, Outlook-Mails, OneNote-Pages,
  Teams-Chats, Word/Excel → MD
- **Persistenz:** `capture/`-Ordner (Screenshots + MD mit YAML-Frontmatter)
- **Tech:** C# / .NET 8, DLL-Kern + CLI
- **Konfig:** JSON

### MVP 2 — Tray-Icon-EXE ✅ (vor Reshuffle „Auto-Meetings")

- Notification-Area-Icon zum Steuern von `ITriggerService`
- in-process Architektur (kein Subprozess-MMF-IPC)
- Trigger Hot-Reload via `TriggerSupervisor.Restart(newConfig)`
- Live-Logviewer (Ringbuffer + Filter)
- Settings-Dialog (Reflection auf `AppConfig` POCOs)
- **Audio-Meeting-Aufzeichnung wurde 2026-07-06 aus MVP 2 herausgelöst
  und wandert in MVP 3** (Roadmap-Reshuffle, siehe DECISIONS.md).

### MVP 3 — Audio Notes ✅

- Teams-Meeting-Anwesenheitserkennung via `MeetingPresencePoller`
- Zweikanaliges Audio-Recording (Mic + Speaker-Loopback)
  via NAudio.Wasapi 2.2.1
- Stereo-Concatenation als Pre-Processing
- Transkription mit Diarization (Azure Speech + Deepgram, parallel
  implementiert per Martin-Direktive Update 3)
- MD-Stub-Pattern: `transcript_status` in Frontmatter (`recording` →
  `done`/`failed`)
- Trigger-Wiring: `TriggerService` integriert `MeetingTrigger` analog
  zum `ConversionWorker`-Pattern aus Spec 0007
- **v0.3 Update 8** abgeschlossen + gepusht 2026-07-09, 777/777 Tests
  grün. Spec-Detail: `0013-audio-notes-mvp3.md`.

### MVP 4 — Auto Knowledge Base / Wiki ⏳

- Volltext-Index + Embeddings
- Auto-Tagging, Wiki-Links zwischen Einträgen
- Semantische Suche
- Obsidian-kompatibles Vault-Format (optional)

*Ehemals MVP 3, nach 2026-07-06 Roadmap-Reshuffle auf MVP 4 verschoben
(siehe DECISIONS.md). Spec-Detail (`0013-auto-wiki.md` Kandidat) folgt
in eigenem Cluster, sobald Anforderungen klar sind.*

## Out of Scope (alle MVP)

Cloud-Sync · Multi-User · Mobile-Companion · Real-Time-Collaboration

## Quellen

- Windows Recall: https://support.microsoft.com/windows/recall
- Screenpipe: https://github.com/screenpipe/screenpipe
- rowboat: https://github.com/rowboatlabs/rowboat

## Siehe auch

- `0002-mvp1-scope.md` — MVP1-Details, User Stories, Architektur-Vorschlag
- `../PROJECT.md` — Aktueller Projektstatus