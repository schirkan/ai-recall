# 0002 — MVP1 Scope & Architektur

> **Status:** Draft v0.1 (2026-07-02)
> **Owner:** Martin
> **Tech-Stack:** C# / .NET 8 (vorgegeben)

## User Stories

### Screen Recorder

- **SR-1:** Liste alle aktuell geöffneten Top-Level-Fenster (Titel, Prozess,
  PID, sichtbar).
- **SR-2:** Identifiziere das aktive Fenster (Fokus).
- **SR-3:** Mache einen Screenshot des aktiven Fensters.
- **SR-4:** Lies sichtbaren Text via **OCR oder Windows UIA** (bevorzugt
  UIA wenn verfügbar/sinnvoll).
- **SR-5:** Dedupliziere identische Screenshots (Hash-basiert).

### App Reader

- **AR-1:** Lies URL + Haupttext der aktiven Browser-Tab
  (Chrome, Edge, Firefox).
- **AR-2:** Erfasse eingehende + ausgehende Outlook-Mails (Subject, From,
  To, Date, Body) → MD.
- **AR-3:** Erfasse offene Word-Dokumente (Titel, Pfad, sichtbarer Text)
  → MD.
- **AR-4:** Erfasse offene Excel-Arbeitsmappen (Dateiname, Sheets, sichtbare
  Zellen) → MD.

### Persistenz

- **P-1:** Schreibe Screenshots als PNG in `capture/<yyyy-MM-dd>/`.
- **P-2:** Schreibe MD-Extraktionen mit YAML-Frontmatter (Quelle, App,
  Timestamp, URL/Pfad) in `capture/<yyyy-MM-dd>/<source>/`.
- **P-3:** Verlinke MD ↔ Screenshot über relativen Pfad.

### CLI

- **CLI-1:** `recall list-windows` — Fenster-Liste
- **CLI-2:** `recall active-window` — aktives Fenster + Screenshot + Text
- **CLI-3:** `recall read-browser` / `read-mail` / `read-word` / `read-excel`
- **CLI-4:** `recall capture-once` — eine vollständige Capture-Runde
- **CLI-5:** `recall record` — kontinuierliches Recording

## Architektur (Vorschlag — Entscheidung offen)

```
AiRecall.sln
├── src/
│   ├── AiRecall.Core/             (Klassenbibliothek)
│   │   ├── Models/                (CaptureItem, WindowInfo, MailItem, …)
│   │   ├── Configuration/         (JsonConfig, IConfigProvider)
│   │   ├── Persistence/           (CaptureWriter, MD-Formatter)
│   │   └── Util/                  (Hash, Logging)
│   ├── AiRecall.Capture.Screen/   (DLL — Fenster-Enum, Screenshot)
│   ├── AiRecall.Capture.Text/     (DLL — OCR + UIA)
│   ├── AiRecall.Reader.Browser/   (DLL — Browser)
│   ├── AiRecall.Reader.Outlook/   (DLL — Outlook/Mail)
│   ├── AiRecall.Reader.Documents/ (DLL — Word + Excel)
│   └── AiRecall.Cli/              (Konsolen-App)
└── tests/
    └── AiRecall.Core.Tests/
```

JSON-Config (Pfad: Vorschlag `%APPDATA%/AiRecall/config.json`,
Fallback neben Executable):

```json
{
  "capture": {
    "rootPath": "capture",
    "dedupStrategy": "sha256",
    "screenshotFormat": "png"
  },
  "screenRecorder": {
    "intervalMs": 5000,
    "trigger": "polling",
    "ignoreApps": [],
    "ignoreUrls": []
  },
  "appReader": {
    "browsers": ["chrome", "edge", "firefox"],
    "outlookVariant": "classic",
    "officeMode": "com-interop"
  },
  "ocr": {
    "engine": "tesseract",
    "languages": ["deu", "eng"]
  },
  "logging": {
    "level": "info",
    "path": "logs"
  }
}
```

## Akzeptanzkriterien MVP1

- [ ] `recall list-windows` listet alle Top-Level-Fenster korrekt
- [ ] `recall active-window` macht Screenshot + Text in `capture/`
- [ ] Identische Screenshots werden dedupliziert
- [ ] `recall read-browser` extrahiert URL + Haupttext → MD
- [ ] `recall read-mail` extrahiert Outlook-Mails (In/Out) → MD
- [ ] `recall read-word` / `read-excel` extrahiert Office-Content → MD
- [ ] Config wird aus JSON geladen, Defaults verfügbar
- [ ] Tests für Core grün
- [ ] README + USAGE-Doku für CLI

## Out of Scope (MVP1)

Audio-Capture / Meetings (→ MVP2) · Volltext-Index / Embeddings (→ MVP3) ·
Multi-Monitor-Aggregation · Background-Service / Tray-Icon · Cloud-Sync

## Offene Architektur-Entscheidungen

→ Frage-Runde an Martin (separate Nachricht)