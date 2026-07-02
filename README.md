# AI Recall

> Lokales, persönliches „Recall"-Tool für Windows — Screenshot-basiertes
> Memory für Bildschirmarbeit, Mails, Dokumente und (später) Meetings.

⚠️ **Status:** Aktiver MVP1-Entwicklungsstart. Specs liegen in
[`specs/`](./specs/). Noch kein Release.

## Vision

AI Recall orientiert sich an [Windows Recall], [Screenpipe] und [rowboat] —
läuft aber **komplett lokal**, ist **Open Source (MIT)** und fokussiert auf
**Windows-Office-Workflows** (Outlook, Word, Excel, Browser).

Details: [`specs/0001-vision.md`](./specs/0001-vision.md)

## Features (geplant)

- **MVP1 — Screen Recorder + App Reader**
  - Fenster-Liste, aktives Fenster, Screenshot, OCR/UIA, Deduplizierung
  - Trigger: Activate + Scroll + Click (mit Throttle + Dedup)
  - Browser- / Outlook- / Word- / Excel-Extraktion → Markdown
  - Persistenz als MD + PNG in `capture/`
- **MVP2** — Auto-Meeting-Recording (Audio + Transcription)
- **MVP3** — Auto Knowledge Base / Wiki

## Quick Start (geplant)

```bash
# Noch nicht implementiert
dotnet run --project src/AiRecall.Cli -- list-windows
```

## Architektur

Siehe [`specs/0002-mvp1-scope.md`](./specs/0002-mvp1-scope.md).

## Lizenz

[MIT](./LICENSE) — Copyright © 2026 Martin

## Quellen / Inspiration

- [Windows Recall](https://support.microsoft.com/windows/recall)
- [Screenpipe](https://github.com/screenpipe/screenpipe)
- [rowboat](https://github.com/rowboatlabs/rowboat)