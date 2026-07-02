# 0001 — Vision & Roadmap

> **Status:** Draft v0.1 (2026-07-02)
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

### MVP 1 — Screen Recorder + App Reader

- **Screen Recorder:** Fenster-Liste, aktives Fenster, Screenshot, OCR/UIA,
  Deduplizierung
- **App Reader:** Browser-Content, Outlook-Mails, Word/Excel → MD
- **Persistenz:** `capture/`-Ordner (Screenshots + MD mit YAML-Frontmatter)
- **Tech:** C# / .NET 8, DLL-Kern + CLI
- **Konfig:** JSON

### MVP 2 — Auto-Meetings

- Audio-Capture (Mikrofon + System-Audio)
- Lokale Transcription (Whisper.cpp o. ä.)
- Kalender-Integration (Outlook)
- Auto-Start/Stop bei Meetings

### MVP 3 — Auto Knowledge Base / Wiki

- Volltext-Index + Embeddings
- Auto-Tagging, Wiki-Links zwischen Einträgen
- Semantische Suche
- Obsidian-kompatibles Vault-Format (optional)

## Out of Scope (alle MVP)

Cloud-Sync · Multi-User · Mobile-Companion · Real-Time-Collaboration

## Quellen

- Windows Recall: https://support.microsoft.com/windows/recall
- Screenpipe: https://github.com/screenpipe/screenpipe
- rowboat: https://github.com/rowboatlabs/rowboat

## Siehe auch

- `0002-mvp1-scope.md` — MVP1-Details, User Stories, Architektur-Vorschlag
- `../PROJECT.md` — Aktueller Projektstatus