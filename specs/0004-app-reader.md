# 0004 — App Reader Architecture

> **Status:** Iter. 2 abgeschlossen (2026-07-04) — COM-Interop für Office + PDF-Viewer
> **Owner:** Martin
> **Implements:** AR-1, AR-2, AR-3, AR-4 from MVP1 spec

## Zweck

Wenn das aktive Fenster zu einer bekannten App gehört, soll nicht nur der
Pixel-Inhalt via OCR erfasst werden, sondern die App soll nach Möglichkeit
auch **strukturiert** ausgelesen werden — URL + Haupttext im Browser,
Mail-Header + Body in Outlook, Dokument-Text in Word/Excel/PowerPoint,
Buffer-Text in Notepad, aktueller Pfad im Explorer.

Das ist die „smarte" Schicht über dem reinen Screenshot.

## Architektur

Eine **DLL pro Target-App**, geladen via Reflection aus dem CLI-Verzeichnis.
Jede DLL bringt einen `IAppReader` mit, der zu der App passt (oder nicht —
dann liefert `CanRead` false und der nächste Reader wird probiert).

```
AiRecall.Cli
  └─→ AppReaderRegistry
        ├─→ AiRecall.AppReader.Browser      → IAppReader für Edge/Chrome/Firefox
        ├─→ AiRecall.AppReader.Outlook      → IAppReader + Mail-Log für Outlook Classic
        ├─→ AiRecall.AppReader.Documents    → IAppReader für Word/Excel/PowerPoint
        ├─→ AiRecall.AppReader.Notepad      → IAppReader für Notepad
        └─→ AiRecall.AppReader.Explorer     → IAppReader für Windows-Explorer
```

Plugin-Discovery:
- Beim CLI-Start scannt `AppReaderRegistry` alle `AiRecall.AppReader.*.dll`
  im `AppContext.BaseDirectory`.
- Jede DLL wird via `AssemblyLoadContext.Default` geladen.
- Typen, die `IAppReader` implementieren, werden instanziiert und
  registriert.
- DLLs, die nicht geladen werden können (z. B. weil eine native
  Dependency fehlt), werden geloggt und übersprungen — kein Hard-Fail.

Pipeline-Reihenfolge:
1. Window bestimmen (`ActiveWindowDetector` oder `WindowInfoLookup`)
2. **App-Reader** für jede registrierte DLL in fester Reihenfolge (s. u.)
   - Wenn `CanRead(window)` → `ReadAsync(window)` → strukturierter Content
3. **OCR** (Tesseract) als Fallback / zusätzliche Quelle
4. Persistenz: PNG + Capture-MD (wie bisher) **plus** `*.content.md` mit
   dem App-Reader-Output

## IAppReader-Interface

```csharp
namespace AiRecall.AppReader.Base;

public interface IAppReader
{
    /// <summary>Apps, die dieser Reader versteht (z. B. "chrome", "msedge").</summary>
    IReadOnlyCollection<string> SupportedProcesses { get; }

    /// <summary>Erkennt dieser Reader das Fenster? (Process-Match + optional Title-Heuristik)</summary>
    bool CanRead(WindowInfo window);

    /// <summary>Liest den strukturierten Inhalt. Liefert null bei Fehler oder Nicht-Verfügbarkeit.</summary>
    AppReaderResult? Read(WindowInfo window, AppReaderContext context);

    /// <summary>Soll dieser Reader auch außerhalb von active-window laufen? (z. B. Mail-Polling)</summary>
    bool SupportsBackgroundPolling => false;

    /// <summary>Polling-Callback (nur wenn SupportsBackgroundPolling).</summary>
    void OnPoll(AppReaderContext context) { /* default no-op */ }
}

public sealed record AppReaderResult(
    string ContentMarkdown,           // strukturierter Inhalt als MD
    string? ContextLabel,             // z. B. URL, Mail-Subject, Document-Pfad
    string? ContextKind,              // "url", "mail", "document", "buffer"
    IReadOnlyDictionary<string, string>? Extra // z. B. {"EntryID": "..."}
);

public sealed class AppReaderContext
{
    public required AppConfig Config { get; init; }
    public required ILogger Logger { get; init; }
    public CancellationToken CancellationToken { get; init; } = default;
}
```

## Ziel-Apps (MVP1)

| App | Process-Match | Lese-Strategie | Output-Schema |
|---|---|---|---|
| **msedge** | `msedge` | UIA (Address-Bar `ValuePattern` + Document `TextPattern`); optional CDP (siehe Browser-Sektion) | URL + Title + main-Text (gekürzt 50 KB) |
| **chrome** | `chrome` | UIA (Address-Bar `ValuePattern` + Document `TextPattern`); optional CDP (siehe Browser-Sektion) | URL + Title + main-Text |
| **outlook** | `OUTLOOK` | COM `Outlook.Application` → aktives Inspector oder Explorer → MailItem / Selection | Mail-Header + Body (HTML→MD), siehe Outlook-Spezial unten |
| **word** | `WINWORD` | **COM-Interop (late binding, bevorzugt)** — `Word.Application.ActiveDocument` liefert `FullName` + `Range.Text`. **Fallback:** UIA + Titel-Parsing | Vollständiger Pfad + Plain-Text-Inhalt (Word.Range); bei COM-Fehler nur Filename + UIA-Text |
| **excel** | `EXCEL` | **COM-Interop (late binding, bevorzugt)** — `Excel.Application.ActiveWorkbook.ActiveSheet.UsedRange` als Markdown-Tabelle. **Fallback:** UIA + Titel-Parsing | Vollständiger Pfad + UsedRange als Markdown-Tabelle; bei COM-Fehler nur Filename + sichtbare Zellen |
| **powerpoint** | `POWERPNT` | **COM-Interop (late binding, bevorzugt)** — `PowerPoint.Application.ActivePresentation.Slides` als Liste (`### Slide N`). **Fallback:** UIA + Titel-Parsing | Vollständiger Pfad + alle Slide-Text-Frames; bei COM-Fehler nur Filename + sichtbarer Slide |
| **pdf-viewer** | `AcroRd32`, `Acrobat`, `SumatraPDF`, `FoxitReader`, `PDFXEdit`, `msedge`, `chrome` (konfigurierbar) | Fenster-Titel-Parsing (Filename + voller Pfad bei SumatraPDF/PDF-XChange, Page-Info) | Dateiname + ggf. voller Pfad + Page-Nr. **Iter. 1 kein PDF-Inhalt** — Parsing via PdfPig in Iter. 2. |
| **notepad** | `Notepad` | Win32 `EM_GETLINE` / `Edit`-Control-Text + Fenster-Titel | Buffer komplett (UTF-8) + Dateipfad falls in Titel |
| **explorer** | `explorer` | Shell COM `IShellBrowser` → aktueller Pfad, Fallback: Titel-Parsing | Pfad + selektierte Dateien |

### Browser-Reader: CDP-Pfad (opt-in)

Standardstrategie für msedge/chrome ist **UIA** (`ValuePattern` für die
Address-Bar, `TextPattern` für den Document-Bereich). Das liefert ohne
zusätzliche Browser-Konfiguration URL + Plain-Text-Body.

Als reichhaltigere Alternative unterstützt der Browser-Reader
**Chrome DevTools Protocol (CDP)** — per **`appReader.browser.cdp.enabled = true`** opt-in aktivierbar. Der Browser muss dann zwingend mit
`--remote-debugging-port=PORT` gestartet sein (üblich 9222). Bei aktivem
CDP ersetzt ein `Runtime.evaluate("document.documentElement.outerHTML")`
die UIA-Textextraktion; das HTML wird via [ReverseMarkdown](https://github.com/mysticmind/reversemarkdown)
in strukturiertes Markdown konvertiert (Links, Überschriften, Listen,
Code-Blöcke bleiben erhalten). URL stammt dann ebenfalls aus CDP.

**Vorteile gegenüber UIA-only:**
- Reichhaltigerer Content (echte Strukturen statt Plain-Text)
- URL + Body in einem Roundtrip
- Konvertierung über `appReader.browser.markdown` 1:1-konfigurierbar
  (siehe Konfiguration)

**Nachteile / Voraussetzungen:**
- Browser muss mit ` --remote-debugging-port` gestartet werden (manueller Schritt)
- Bei Firefox wäre CDP ebenfalls möglich — wird aber erst erschlossen, wenn
  die Edge/Chrome-Variante etabliert ist (YAGNI).
- Bei mehreren offenen Tabs wird der „erste page-type Target" genommen (nicht zwingend der sichtbare Tab) — Grenzfall, manuell zu prüfen.

## Outlook-Spezial: Mail-Log

Outlook ist nicht nur „aktiver Inspector", sondern wir wollen einen
**kontinuierlichen Mail-Stream** loggen — eingehende + ausgehende Mails
mit Subject, From, To, Date, Body.

### Erfassung

- Beim App-Reader-Init: COM-Verbindung zu laufendem Outlook aufbauen.
- Inbox- und Sent-Folder iterieren (konfigurierbar: `outlook.folders`).
- Pro Mail: `EntryID` als Primärschlüssel, Hashtable in
  `%APPDATA%/AiRecall/outlook-seen.json` (Plain-JSON, gitignored).
- Nur neue Mails (EntryID noch nicht in seen-Set) → MD schreiben.
- Periodisch pollen (Standard: alle 60 s, `outlook.pollIntervalSeconds`).
  Kann in `recall record` mitlaufen, in `recall active-window` reicht
  ein einmaliger Sweep.

### Persistenz-Schema

Pro Mail eine MD-Datei unter:

```
capture/<yyyy-MM-dd>/outlook-mail/<HHmmss>-<direction>-<entryId-short>.md
```

`<direction>` = `in` oder `out`.

YAML-Frontmatter:

```yaml
---
timestamp: 2026-07-03T15:23:11+02:00
direction: "in"
entryId: "00000000ABCDEF1234567890..."
subject: "RE: Angebot Q3"
from: "kunde@example.com"
to: "martin@martin.local"
cc: ""
date: 2026-07-03T15:18:42+02:00
folder: "Inbox"
hasAttachments: false
unread: false
autoRuleSuspect: false
---
```

### Auto-Regel-Mails (Heuristik + Setting)

**Problem:** Outlook-Regeln (z. B. „verschiebe Newsletter in
`Newsletter/`", „lösche Spam") setzen Mails als gelesen oder löschen sie
direkt. Diese „berührungslosen" Mails sind für Recall oft uninteressant
(News-Headlines, Spam) und produzieren viel Rauschen.

**Setting:** `screenRecorder.outlook.ignoreAutoRuleMails` (Default: `false`).

**Heuristik** (wenn Setting = `true`):

Eine Mail gilt als „Auto-Regel-Suspect" (`autoRuleSuspect: true`), wenn
**mindestens zwei** der folgenden Bedingungen zutreffen:

1. `UnRead == false` **und** `LastModificationTime - ReceivedTime < 5 s`
   (Mail wurde nie geöffnet, aber als gelesen markiert → typisch für
   Regel-Aktion „als gelesen markieren")
2. Mail ist im Folder `Junk E-Mail`, `Deleted Items`, oder einem
   Custom-Folder mit Präfix `Newsletter|Notifications|Auto|Rule`
   (case-insensitive)
3. `SenderEmailAddress` enthält keinen lokalen Part mit Personennamen
   (z. B. `noreply@`, `no-reply@`, `notifications@`, `mailer-daemon@`)
4. Subject matched Regex `^(WG:|AW:|Fwd:|TR:)` mit Auto-Reply-Indikatoren
   im Body (`Auto-Reply`, `Automatische Antwort`, `Out of Office`)

Bei `ignoreAutoRuleMails: true` werden Suspect-Mails **nicht** persistiert,
aber im Log (`logs/ai-recall-*.log`) als „skipped" vermerkt mit Begründung.

## Persistenz: zusätzliche `*.content.md`

Pro Capture, bei dem ein App-Reader geliefert hat, zusätzlich:

```
capture/<yyyy-MM-dd>/<process>/<HHmmss-fff>-<title-slug>.content.md
```

Inhalt: das `ContentMarkdown` aus `AppReaderResult`, plus YAML-Frontmatter
mit `kind` (url/mail/document/buffer), `context` (URL/Subject/Pfad),
`reader` (Dll-Name), `readerVersion` (Assembly-Version).

Im Capture-MD (`*.md`) wird ein Link auf das Content-MD gesetzt:

```markdown
## App-Reader
[Structured content](<base>.content.md) (kind=url, reader=AiRecall.AppReader.Browser)
```

So bleibt der `active-window`-Output rückwärtskompatibel.

> **Default-Entscheidung:** zusätzliche Datei. Alternative (ein einzelnes
> MD mit beiden Sektionen) kann später via `appReader.embedInMainMd: true`
> ergänzt werden.

## Konfiguration

Neue Sektion in `default-config.json`:

```json
{
  "appReader": {
    "enabled": true,
    "outlook": {
      "folders": ["Inbox", "Sent Items"],
      "pollIntervalSeconds": 60,
      "ignoreAutoRuleMails": false
    },
    "browser": {
      "maxTextLengthKB": 50,
      "cdp": {
        "enabled": false,
        "endpoint": "http://localhost:9222",
        "timeoutMs": 1500
      },
      "markdown": {
        "unknownTags": "PassThrough",
        "githubFlavored": false,
        "removeComments": true,
        "whitelistUriSchemes": ["http", "https", "ftp", "ftps", "mailto", "tel"],
        "smartHrefHandling": false,
        "tableWithoutHeaderRowHandling": "Default",
        "listBulletChar": "*",
        "defaultCodeBlockLanguage": ""
      }
    },
    "office": {
      "includeTables": true,
      "includeHeaders": true
    },
    "notepad": {
      "maxBufferKB": 256
    }
  }
}
```

Felder:
- `appReader.browser.cdp.enabled` (Default `false`) — Master-Switch. `false`
  ⇒ UIA-Pfad wie bisher, CDP wird nicht angesprochen.
- `appReader.browser.cdp.endpoint` (Default `http://localhost:9222`) — HTTP-Basis-URL.
- `appReader.browser.cdp.timeoutMs` (Default `1500`) — sowohl HTTP-Lookup
  als auch WebSocket-Roundtrip.
- `appReader.browser.markdown.*` — 1:1-Mapping auf `ReverseMarkdown.Config`
  (v3.13). Wirkt unabhängig vom CDP-Gate; alle Felder optional, nicht
  gesetzte Felder lassen die Library-Defaults unangetastet.
  - `unknownTags` (string) — `"PassThrough"` (Default) / `"Drop"` / `"Bypass"` / `"Raise"`
  - `githubFlavored` (bool, Default `false`)
  - `removeComments` (bool, Default `true` in `default-config.json` — Note:
    Library-Default ist `false`; `StripNoise` entfernt HTML-Kommentare
    bereits vorher, daher setzen wir `true`)
  - `whitelistUriSchemes` (List<string>, Default `["http","https","ftp","ftps","mailto","tel"]`)
  - `smartHrefHandling` (bool, Default `false`)
  - `tableWithoutHeaderRowHandling` (string) — `"Default"` / `"EmptyRow"`
  - `listBulletChar` (string, erstes Zeichen gewinnt; Default `*` — Note:
    Library-Default ist `-`)
  - `defaultCodeBlockLanguage` (string, optional)

## Integration in `recall active-window`

1. Nachdem `ActiveWindowDetector` / `WindowInfoLookup` das Fenster
   bestimmt hat, **vor** der OCR, fragt `ActiveWindowCommand` die
   `AppReaderRegistry` nach passenden Readern.
2. Pro passender Reader: `Read(window)` aufrufen, Ergebnis (falls nicht
   null) sammeln.
3. Erster nicht-null-Reader gewinnt — seine `ContentMarkdown` wird
   als `appContext` und Content-Quelle benutzt.
4. OCR läuft **zusätzlich** (Bild-Beweis), wird aber nicht in `## Content`
   gezeigt wenn App-Reader geliefert hat (sonst Duplicate). Stattdessen
   Link auf die OCR-Sektion am Ende.
5. Bei mehreren Readern pro Fenster: nur den ersten nehmen (Best Match).
6. **Browser-Reader Reihenfolge intern** (nur relevant bei aktivem
   CDP-Pfad): CDP-Versuch liefert URL + Body; wenn CDP-`enabled = false`
   oder kein Browser lauscht, geht es ohne Verzögerung auf UIA weiter.

> **Default-Entscheidung:** OCR läuft weiter, wird aber im MD nur
> verlinkt wenn App-Reader Content geliefert hat.

## Integration in `recall record` (später)

- `record` ruft `OnPoll` auf jedem Reader mit `SupportsBackgroundPolling == true`.
- Outlook-Reader pollt alle 60 s, schreibt neue Mails als MDs.
- Andere Reader sind passiv (lesen nur bei aktivem Fenster).

## User Stories

### App Reader

- **AR-1** *(MVP1)* Browser-Reader liefert URL + Title + main-Text für
  Edge/Chrome.
- **AR-2** *(MVP1)* Outlook-Reader liest aktives Inspector-Fenster
  (offene Mail) → MD.
- **AR-3** *(MVP1)* Outlook-Mail-Log persistiert neue Inbox/Sent-Mails
  als MD mit vollem Header + Body.
- **AR-4** *(MVP1)* Word/Excel/PowerPoint-Reader liefert Datei-Pfad +
  sichtbaren Inhalt.
- **AR-5** *(MVP1)* Notepad-Reader liefert Buffer-Text + Dateipfad.
- **AR-6** *(MVP1)* Explorer-Reader liefert aktuellen Pfad.
- **AR-7** *(MVP1)* Auto-Regel-Mails werden per Setting optional ignoriert.

### Trigger-Pipeline (bleibt aus Spec 0002)

- TR-1..6 bleiben unverändert. App-Reader sind eine **horizontale
  Schicht** über der Capture-Pipeline, nicht ein Ersatz.

## Akzeptanzkriterien

- [ ] `AppReaderRegistry` lädt alle `AiRecall.AppReader.*.dll` neben der Exe.
- [ ] Fehlende / nicht-laden-bare DLLs werden geloggt, kein Crash.
- [ ] Browser-Reader liefert für `chrome`/`msedge` mindestens URL + Titel
      (UIA-Pfad ohne CDP).
- [ ] Browser-Reader mit `cdp.enabled = true` und aktivem CDP-Browser
      liefert URL + strukturiertes Markdown.
- [ ] Browser-Reader mit `cdp.enabled = true`, aber ohne CDP-Server,
      fällt lautlos auf UIA zurück (kein Crash, `contentSource = "none"`).
- [ ] Outlook-Reader liefert für aktiven Inspector-Mail mindestens Subject + Body.
- [ ] Outlook-Mail-Log persistiert alle Mails aus Inbox + Sent Items,
      die nicht in `outlook-seen.json` stehen.
- [ ] `ignoreAutoRuleMails: true` filtert anhand der Heuristik und
      markiert im Log.
- [x] Word-Reader liefert Dateiname (aus Fenster-Titel) + optional UIA-Text.
- [x] Excel-Reader liefert Dateiname (aus Fenster-Titel) + UIA-Text der sichtbaren Zellen.
- [x] PowerPoint-Reader liefert Dateiname (aus Fenster-Titel) + UIA-Text der sichtbaren Slide.
- [x] `appReader.documents.{maxTextKB,enableUiaExtraction}` ist konfigurierbar und per Unit-Test abgesichert.
- [x] **Word/Excel/PowerPoint-Reader (COM-Erweiterung, Iter. 2):** COM-Interop liefert `FullName` + Inhalt; bei COM-Fehler Fallback auf UIA+Title. `filePath` wird im Content-MD-Frontmatter emittiert.
- [x] **PDF-Viewer-Reader:** Prozess-Liste konfigurierbar (`appReader.pdf.processes`); Filename + voller Pfad + Page-Nr aus Titel. PDF-Inhalt-Extraktion in Iter. 2 (PdfPig).
- [ ] Notepad-Reader liefert Buffer-Text (max `notepad.maxBufferKB`).
- [ ] Explorer-Reader liefert aktuellen Pfad aus Fenster-Titel oder COM.
- [ ] `*.content.md` wird bei jedem Capture mit App-Reader-Output
      geschrieben.
- [ ] Capture-MD bekommt einen `## App-Reader`-Abschnitt mit Link auf
      Content-MD.
- [ ] Jedes Feld in `appReader.browser.markdown` setzt das gleichnamige
      Feld auf `ReverseMarkdown.Config`; nicht gesetzte Felder bleiben auf
      Library-Default. Per Unit-Test abgesichert (`BuildConverter_*`).

## Iter. 2 (2026-07-04) — COM-Interop für Office + PDF-Viewer (Martin)

### Motivation

UIA-only liefert bei Word/Excel/PowerPoint **keinen** echten Datei-Pfad — nur
den Filename aus dem Fenstertitel. Für „welche Datei war offen?" brauchen
wir den vollen Pfad. Außerdem ist der via COM zugängliche Inhalt
(Word.Range, Excel.UsedRange, PowerPoint.Slides) deutlich reichhaltiger
als das, was UIA aus dem gerenderten Fenster ablesen kann.

PDF-Viewer werden als neue App-Familie ergänzt.

### COM-Strategie

- **Late binding** via ProgID + `Type.InvokeMember`. Keine PIAs / NuGet-Pakete
  nötig. Die COM-Verbindung zur laufenden Instanz läuft über P/Invoke auf
  `oleaut32.dll!GetActiveObject` — `Marshal.GetActiveObject` ist in .NET 8
  SDK 8.0.422 nicht (mehr) direkt verfügbar.
- COM-Lookup nur auf der ersten laufenden Instanz der jeweiligen App
  (typisch genau eine). Pro-Prozess-Disambiguierung wäre möglich, ist für
  Iter. 2 YAGNI.
- **Fallback auf UIA+Title** wenn COM nicht verfügbar (kein Office,
  andere Instanz aktiv, COM-Exception). Nie crashen.
- Office-Prozesse sind auf der Sandbox-Maschine nicht installiert →
  e2e-Smoke-Tests entfallen. COM-spezifische Tests sind als
  `[Trait("Integration", "Office")]` markiert und laufen nur, wenn
  Office installiert ist.

### Inhalts-Format

Der Datei-Inhalt wird in der bestehenden `*.content.md` unter einer
`## Document content (via COM)`-Sektion eingebettet — **kein** separates
File. Der `FullPath` wird als `filePath` in den `Extra`-Dict und damit ins
YAML-Frontmatter der `content.md` geschrieben (über
`CaptureWriter.RenderContentMarkdown`).

| App | Inhalts-Format |
|---|---|
| Word | `Word.Application.ActiveDocument.Range.Text` (Plain) in Code-Block |
| Excel | `UsedRange.Value` (object[,]) als Markdown-Tabelle |
| PowerPoint | Für jede `Slide` Text-Frames sammeln, `### Slide N` Header |

### PDF-Viewer

Neue DLL `AiRecall.AppReader.Pdf` mit `PdfViewerAppReader`:

- Prozess-Liste konfigurierbar (`appReader.pdf.processes`, Default:
  AcroRd32, Acrobat, SumatraPDF, FoxitReader, PDFXEdit, msedge, chrome).
- Fenster-Titel-Parsing: extrahiert Filename, vollen Pfad (Sumatra/PDF-XChange
  zeigen Pfad im Titel) und Page-Nr (SumatraPDF: `" - Page N of M - "`).
- **Kein PDF-Inhalt** in Iter. 1 — `PdfPig` (NuGet) ist Kandidat für Iter. 2.

### Konfiguration

```json
"appReader": {
  "documents": {
    "maxTextKB": 64,
    "enableUiaExtraction": true
  },
  "pdf": {
    "processes": ["AcroRd32", "Acrobat", "SumatraPDF", "FoxitReader", "PDFXEdit", "msedge", "chrome"]
  }
}
```

### Tests

- 17 neue Unit-Tests in `PdfViewerAppReaderTests` (Title-Parsing + Read-Smoke).
- 3 neue Office-COM-Integration-Tests mit `[Trait("Integration", "Office")]`
  (laufen nur bei installiertem Office).
- Bestehende Office-Reader-Tests an COM-Pfad angepasst (Filename-Suche statt
  strikter Markdown-Prefix, COM-liefert-null-Fallback toleriert).
- Test-Count gesamt: 263 / 263 grün (vorher 243).

### Bekannte Einschränkungen

- COM-Lookup nimmt die **erste** laufende Office-Instanz. Bei mehreren
  parallelen Instanzen kann der falsche Pfad geliefert werden. Pro-Prozess-
  Filterung ist Iter.-3-Kandidat.
- Excel `UsedRange` enthält auch leere Zellen am Rand → Markdown-Tabelle
  kann visuelles Padding haben. Trim-Logik in Iter. 3.
- PowerPoint: nur Text-Frames; SmartArt, Tabellen, eingebettete Objekte
  fehlen. COM hat kein einfaches „Inhalt"-Property hier.

## Out of Scope (MVP1)

- Firefox-Reader (UIA zu schwach; CDP-Pfad ist über die Edge/Chrome-Variante erschlossen, Firefox-Erweiterung ist später möglich)
- Weitere Browser (Brave, Opera, Vivaldi) — können über Edge/Chrome-Pfad fallen
- Outlook-**New**-Outlook (PIM-basiert, kein COM)
- Word/Excel-Macros, eingebettete Objekte
- Live-Sync mit Office 365 Cloud
- PDF-Reader (Adobe, Edge-PDF, Sumatra) — spätere Spec
- Terminal-Reader (WindowsTerminal, ConEmu, WezTerm) — nutzlos ohne OCR
- IDEs (VS, Rider, VSCode) — zu komplex, viele Sub-Fenster