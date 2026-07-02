# 0002 — MVP1 Scope & Architektur

> **Status:** Draft v0.2 (2026-07-02) — Architektur + Trigger präzisiert
> **Owner:** Martin
> **Tech-Stack:** C# / .NET 8 (`net8.0-windows`)
> **Lizenz:** MIT
> **Zielgruppe:** Personal (Martin)

## Bestätigte Entscheidungen

| Thema | Entscheidung |
|---|---|
| Lizenz | MIT |
| Zielgruppe | Personal (nur Martin) |
| Plattform | Windows only (MVP1) |
| Solution-Struktur | Hybrid: zentrale `AiRecall.ScreenCapture`-DLL + `AiRecall.AppReader.Base`-DLL + separate DLL pro App |
| Trigger | Window-Activate + Scroll + Click, mit Throttle + Dedup, optional periodisches Polling |
| Persistenz | Files only (MD + PNG) |
| Outlook-Variante | Classic (MAPI / COM) |
| Ignore-Liste | erstmal leer, später füllbar |

## User Stories

### Screen Recorder (zentrale DLL `AiRecall.ScreenCapture`)

- **SR-1:** Liste alle aktuell geöffneten Top-Level-Fenster (Titel, Prozess,
  PID, sichtbar).
- **SR-2:** Identifiziere das aktive Fenster (Fokus).
- **SR-3:** Mache einen Screenshot des aktiven Fensters.
- **SR-4:** Lies sichtbaren Text via OCR oder Windows UIA (bevorzugt UIA
  wenn sinnvoll).
- **SR-5:** Dedupliziere identische Screenshots (Hash-basiert).

### Trigger-Pipeline

- **TR-1:** Erkenne **Window-Activate**-Events (neues Fenster im Fokus).
- **TR-2:** Erkenne **Scroll**-Events im aktiven Fenster.
- **TR-3:** Erkenne **Click**-Events im aktiven Fenster.
- **TR-4:** **Throttle:** maximal 1 Capture pro konfigurierbares Intervall
  (Default `1000 ms`) — verhindert Spam bei schnellen Events.
- **TR-5:** **Dedup:** wenn Hash identisch zum letzten Capture → skip.
- **TR-6:** **Optional Periodic Capture:** zusätzlich alle N Sekunden, falls
  keine Events kamen.

> **Implementierungs-Hinweis TR-1..3:** Polling-basiert (z. B.
> `GetForegroundWindow` + Cursor-Position + Window-Bounds-Check alle
> 50 ms). Echte systemweite Hooks (`WH_MOUSE_LL`) sind technisch möglich,
> brauchen aber Message-Loop / Hook-DLL → später, falls Polling nicht
> ausreicht.

### App Reader

- **AR-1:** Lies URL + Haupttext der aktiven Browser-Tab
  (Chrome, Edge, Firefox).
- **AR-2:** Erfasse eingehende + ausgehende Outlook-Classic-Mails
  (Subject, From, To, Date, Body) → MD.
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

- **CLI-1:** `recall list-windows`
- **CLI-2:** `recall active-window`
- **CLI-3:** `recall read-browser` / `read-mail` / `read-word` / `read-excel`
- **CLI-4:** `recall capture-once`
- **CLI-5:** `recall record` (kontinuierlich mit Trigger)

## Architektur

```
AiRecall.sln
├── src/
│   ├── AiRecall.Core/                  (net8.0-windows, Klassenbibliothek)
│   │   ├── Models/                     (CaptureItem, WindowInfo, MailItem, …)
│   │   ├── Configuration/              (JsonConfig, IConfigProvider)
│   │   ├── Persistence/                (CaptureWriter, MD-Formatter, Hash)
│   │   └── Util/                       (Logging)
│   │
│   ├── AiRecall.ScreenCapture/         (net8.0-windows, DLL — zentral)
│   │   ├── Windows/                    (EnumWindows, Active Window)
│   │   ├── Screenshot/                 (GDI+ Capture)
│   │   ├── Trigger/                    (EventDetector, Throttle, Dedup)
│   │   └── Text/                       (OCR + UIA)
│   │
│   ├── AiRecall.AppReader.Base/        (net8.0-windows, DLL — Basisklassen)
│   │   ├── IAppReader.cs
│   │   ├── AppReaderBase.cs
│   │   └── ContentExtractor.cs
│   │
│   ├── AiRecall.AppReader.Browser/     (net8.0-windows, DLL — Chrome/Edge/FF)
│   ├── AiRecall.AppReader.Outlook/     (net8.0-windows, DLL — Classic Outlook)
│   ├── AiRecall.AppReader.Documents/   (net8.0-windows, DLL — Word + Excel)
│   │
│   └── AiRecall.Cli/                   (net8.0-windows, Konsolen-App)
│       └── Commands/                   (list-windows, active-window, read-*)
│
└── tests/
    ├── AiRecall.Core.Tests/            (net8.0-windows)
    └── AiRecall.Integration.Tests/     (net8.0-windows)
```

### Abhängigkeiten

```
AiRecall.Cli
  ├─→ AiRecall.Core
  ├─→ AiRecall.ScreenCapture
  └─→ AiRecall.AppReader.* (alle per Reflection geladen → modular)

AiRecall.ScreenCapture → AiRecall.Core

AiRecall.AppReader.Base → AiRecall.Core

AiRecall.AppReader.{Browser,Outlook,Documents} → AiRecall.AppReader.Base → AiRecall.Core
```

### Konfiguration

JSON-Config (Pfad: `%APPDATA%/AiRecall/config.json`, Fallback neben
Executable):

```json
{
  "capture": {
    "rootPath": "capture",
    "dedupStrategy": "sha256",
    "screenshotFormat": "png"
  },
  "screenRecorder": {
    "throttleMs": 1000,
    "periodicCaptureMs": 0,
    "ignoreApps": [],
    "ignoreUrls": []
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
- [ ] Trigger-Pipeline (Activate/Scroll/Click) feuert, Throttle + Dedup greifen
- [ ] Identische Screenshots werden nicht doppelt persistiert
- [ ] `recall read-browser` extrahiert URL + Haupttext → MD
- [ ] `recall read-mail` extrahiert Outlook-Classic-Mails (In/Out) → MD
- [ ] `recall read-word` / `read-excel` extrahiert Office-Content → MD
- [ ] Config wird aus JSON geladen, Defaults verfügbar
- [ ] Tests für Core grün
- [ ] README + USAGE-Doku für CLI

## Out of Scope (MVP1)

Audio-Capture / Meetings (→ MVP2) · Volltext-Index / Embeddings (→ MVP3) ·
SQLite · Multi-Monitor-Aggregation · Global-Hook-DLL · Cloud-Sync