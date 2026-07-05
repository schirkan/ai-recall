# 0004 — App Reader Architecture

> **Status:** Iter. 3 abgeschlossen (2026-07-05) — Outlook App-Reader + Mail-Log (Martin + Pia)
> **Implements:** AR-1, AR-2, AR-3, AR-4, AR-5, AR-6, AR-7 from MVP1 spec
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
- [x] Outlook-Reader liefert für aktiven Inspector-Mail mindestens Subject + Body.
- [x] Outlook-Mail-Log persistiert alle Mails aus Inbox + Sent Items,
      die nicht in `outlook-seen.json` stehen.
- [x] `ignoreAutoRuleMails: true` filtert anhand der 4-Bedingungen-Heuristik
      und markiert im Log (OutlookAutoRuleDetector).
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

## Iter. 3 (2026-07-05) — Outlook Classic Reader + Mail-Log (Martin + Pia)

### Motivation

Outlook Classic ist auf Windows-Workstations allgegenwärtig — berufliche
E-Mails, Kalender-Einladungen, Newsletter, Auto-Regel-Weiterleitungen
landen hier. Wir wollen den **Mail-Stream** als strukturierten Content
loggen, nicht nur den Screenshot-OCR (der bei Mail-Bodies oft arm an Text ist).

Dabei ist „aktives Inspector-Fenster" nur die Spitze des Eisbergs: die
Hauptarbeit ist der **periodische Sweep** über Inbox + Sent Items +
beliebige Custom-Folder, mit EntryID-basierter Dedup, sodass jede Mail
genau einmal persistiert wird.

### Komponenten (neu in Iter. 3)

- **`AiRecall.AppReader.Outlook`** (neue DLL, `net8.0-windows`).
  - `OutlookAppReader : AppReaderBase` — Dual-Modus:
    - `Read(window)` → aktives Inspector-Fenster oder Explorer-Selektion,
      Fallback auf Fenster-Titel-Parsing.
    - `OnPoll(context)` → Background-Sweep über konfigurierte Folders,
      intern throttled auf `outlook.pollIntervalSeconds` (Default 60 s).
  - `OutlookComInterop` (`internal static`) — late binding via ProgID
    `Outlook.Application` + P/Invoke auf `oleaut32.dll!GetActiveObject`
    (`Marshal.GetActiveObject` ist in .NET 8 SDK 8.0.422 nicht direkt
    verfügbar).
    - `TryGetActiveInspectorMail()` — `Application.ActiveInspector().CurrentItem`
    - `TryGetExplorerSelection(maxItems)` — `Application.ActiveExplorer().Selection`
    - `TryGetRecentMails(folderName, maxItems)` — Folder-Iteration via
      `Session.GetDefaultFolder(olFolderInbox)` o. ä.
    - Alle Methoden geben bei Fehler `null`/`leere Liste` zurück — nie werfen.
  - `OutlookEntryStore` — EntryID-Dedup, State in
    `%APPDATA%/AiRecall/outlook-seen.json`, atomic via `File.Replace`.
  - `OutlookAutoRuleDetector` — Pure-Function-Heuristik (4 Bedingungen,
    ≥2 Hits = suspect): Marked-Read-Fast / Junk-Folder / NoReply-Sender /
    Auto-Reply-Subject+Body.
  - `OutlookTitleParser` — Parsing von Outlook-Fenster-Titeln
    (FolderView vs. InspectorSubject, Read-Only-Marker, Unsaved-Marker,
    Unread-Counter).
  - `OutlookBodyToMarkdown` — Custom HTML→MD-Konvertierung für
    Outlook-spezifisches HTML (Conditional-Comments, Word-Style-Klassen).

### Konfiguration

```json
"outlook": {
  "folders": ["Inbox", "Sent Items"],
  "pollIntervalSeconds": 60,
  "ignoreAutoRuleMails": false,
  "maxItemsPerSweep": 200,
  "bodyTruncateKB": 256,
  "htmlToMarkdown": {
    "preserveLinks": true,
    "preserveLineBreaks": true,
    "stripImages": true
  }
}
```

### Persistenz-Schema

Pro Mail eine MD-Datei unter
`capture/<yyyy-MM-dd>/outlook-mail/<HHmmss>-<direction>-<entryIdShort>.md`
mit YAML-Frontmatter: `timestamp`, `kind=mail`, `direction` (`in`/`out`),
`entryId`, `subject`, `from`, `folder`, `date`, `unread`,
`autoRuleSuspect`, `source=outlook-com`, `reader`, `readerVersion`.

### Tests

- 109 neue Tests (419 → **525 grün**):
  - `OutlookEntryStoreTests` (26): IsSeen, MarkSeen, MarkSweepCompleted,
    Save/Load, Corruption-Tolerance, Atomic-Write, Count, LastSweepAt.
  - `OutlookAutoRuleDetectorTests` (14): IsSuspect ≥2-Hit-Threshold,
    jede der 4 Bedingungen einzeln + Kombinationen, Explain-Format.
  - `OutlookTitleParserTests` (13): FolderView, InspectorSubject,
    ReadOnly-Marker, Unsaved-Marker, Unread-Counter, Fallback.
  - `OutlookBodyToMarkdownTests` (27): FromHtml Stripping
    (Style/Script/Comments/Conditional), Links, Block-Tags,
    Whitespace-Normalisierung (Block-Boundaries, `\u00A0`→space),
    Plain-Passthrough, Truncate.
  - `OutlookAppReaderTests` (26): Public-Surface, ShortId (9 Fälle),
    Read Fallbacks (FolderView/Inspector/Empty), OnPoll-Throttle,
    Store-Init, DefaultCaptureRoot.
- COM-spezifische Tests als `[Trait("Integration", "Outlook")]` markiert
  (in Sandbox skipped, da Outlook nicht installiert).

### Bekannte Einschränkungen

- **Outlook New** (PIM-basiert) wird **nicht** unterstützt — kein COM.
  Spec-Out-of-Scope weiterhin.
- Folder-Iteration läuft über `Session.GetDefaultFolder()` für Standard-
  Folder und `Session.Folders(name).Items` für Custom-Folder. Letzteres
  ist case-sensitive auf manchen Outlook-Versionen.
- Polling-Logik ist single-threaded pro AppReader-Instanz. Bei mehreren
  parallelen Outlook-Instanzen (selten) kann der Sweep sich gegenseitig
  überholen — EntryID-Dedup fängt das ab.
- BodyTruncateKB schneidet bei `maxKB*1024` Zeichen, nicht Bytes.
  Ausreichend für Volltext-Indexierung.
- `ignoreAutoRuleMails` Heuristik kann False-Positives haben (eine
  No-Reply-Transaktionsmail + Unread-Counter passt zu zwei Bedingungen).
  User-Tuning der Bedingungen ist YAGNI in Iter. 3.

## Out of Scope (MVP1)

- Firefox-Reader (UIA zu schwach; CDP-Pfad ist über die Edge/Chrome-Variante erschlossen, Firefox-Erweiterung ist später möglich)
- Weitere Browser (Brave, Opera, Vivaldi) — können über Edge/Chrome-Pfad fallen
- Outlook-**New**-Outlook (PIM-basiert, kein COM)
- Word/Excel-Macros, eingebettete Objekte
- Live-Sync mit Office 365 Cloud
- PDF-Reader (Adobe, Edge-PDF, Sumatra) — spätere Spec
- Terminal-Reader (WindowsTerminal, ConEmu, WezTerm) — nutzlos ohne OCR
- IDEs (VS, Rider, VSCode) — zu komplex, viele Sub-Fenster

## Iter. 3 (2026-07-05) — Outlook App-Reader + Mail-Log (Martin + Pia)

### Motivation

Outlook ist der wichtigste Mail-Workflow in MVP1 und war als einzige
App-Familie in Iter. 2 noch **nicht** implementiert (Spec 0004 §„Outlook-
Spezial: Mail-Log“). Iter. 3 schließt AR-2 (aktiver Inspector) und AR-3
(Mail-Stream-Log) ab. AR-7 (Auto-Regel-Heuristik) kommt mit.

Außerdem wird der Outlook-Reader auch in der Pipeline-Wiring benötigt
(Trigger-Pipeline Spec 0002 + Trigger-Pipeline Spec 0006 Iter. 2):
ein Outlook-Fenster ohne Reader würde nur OCR-Text liefern, was bei
HTML-Mails praktisch unbrauchbar ist.

### Architektur

Neue DLL **`AiRecall.AppReader.Outlook`** mit folgenden Klassen:

| Datei | Verantwortlichkeit |
|---|---|
| `OutlookAppReader.cs` | Hauptklasse. `Read` = aktiver Inspector, `OnPoll` = Mail-Log-Sweep. Erbt von `AppReaderBase`. |
| `OutlookComInterop.cs` | Late-binding COM-Wrapper (ProgID `Outlook.Application`, `ActiveExplorer()`, `ActiveInspector()`, Folder/MailItem-Properties). P/Invoke `oleaut32!GetActiveObject` wie `OfficeComInterop`. |
| `OutlookEntryStore.cs` | Persistente EntryID-Verwaltung. State in `%APPDATA%/AiRecall/outlook-seen.json` (Plain-JSON, atomar via `File.Replace`). Threadsafe. |
| `OutlookAutoRuleDetector.cs` | Pure-Function-Heuristik (4 Bedingungen) für „berührungslose“ Auto-Regel-Mails. |
| `OutlookTitleParser.cs` | Parst Outlook-Fenster-Titel (z. B. `"Inbox - foo@bar.com - Outlook"` oder `"Subject - Outlook"`). Liefert `Folder`, `Subject`, ggf. `Sender`. |
| `OutlookBodyToMarkdown.cs` | Konvertiert Mail-Body (HTML oder Plain) zu Markdown. Plain → direkt; HTML → einfache Tag-Strip-Logik (kein ReverseMarkdown — zu fett für Mail-Bodies). |

### `OutlookAppReader` API

```csharp
public sealed class OutlookAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses => new[] { "OUTLOOK" };
    public override string DisplayName => "Outlook (COM + Mail-Log)";

    // AR-2: Aktiver Inspector / Explorer → offene Mail
    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context);

    // AR-3 + AR-7: Mail-Log-Sweep
    public override bool SupportsBackgroundPolling => true;
    public override void OnPoll(AppReaderContext context);
}
```

### `Read` (AR-2 — aktiver Inspector)

1. **COM-Lookup** via `OutlookComInterop.TryGetActiveMail(window.Title)`:
   - ProgID `Outlook.Application` → `ActiveExplorer()` oder `ActiveInspector()`.
   - Wenn `Inspector` aktiv: `CurrentItem` als `MailItem` casten, Header + Body lesen.
   - Wenn `Explorer` aktiv (Hauptfenster mit Folder-Liste): `Selection[1]` casten,
     sonst null.
2. **Fallback auf Title-Parsing** wenn COM nicht verfügbar:
   `OutlookTitleParser` extrahiert Subject aus dem Titel.
3. **Body-Extraktion**:
   - `MailItem.Body` (Plain) bevorzugt.
   - Falls leer und `MailItem.HTMLBody` nicht leer: `OutlookBodyToMarkdown.FromHtml(...)`.
4. **Persistenz**: liefert `AppReaderResult` mit `IsThinReader = false` (kein
   `ConversionWorker` nötig — Mail-Content ist vollständig im MD).

### `OnPoll` (AR-3 + AR-7 — Mail-Log)

Pro Sweep:

1. **COM-Verbindung** zu Outlook aufbauen (gleiche Lookup-Logik wie `Read`).
2. **Folder-Iteration**: für jeden konfigurierten Folder (`outlook.folders`,
   Default `["Inbox", "Sent Items"]`):
   - `MAPI-Folder.Items` restriktiv sortieren nach `ReceivedTime` DESC.
   - Erste N (Default 200, Cap gegen riesige Postfächer) durchlaufen.
3. **EntryID-Dedup** via `OutlookEntryStore.IsSeen(entryId)`.
   - Neue Mails → MD schreiben + `MarkSeen(entryId)`.
   - Bereits gesehene → skip (kein Log-Eintrag, sonst Spam).
4. **Auto-Regel-Heuristik** wenn `ignoreAutoRuleMails: true`:
   - `OutlookAutoRuleDetector.IsSuspect(mail)` → wenn true, skip mit
     `context.Logger.Information("Outlook: skipped auto-rule mail {Subject} ({EntryIdShort})", ...)`.
   - **Trotzdem** in `outlook-seen.json` markieren, damit die Mail nicht
     beim nächsten Sweep nochmal geprüft wird.
5. **Persistenz-Schema** (siehe unten).
6. **Threading**: `OutlookAppReader` ist NICHT thread-safe; der zukünftige
   `AppReaderPollService` (Iter. 4) serialisiert Aufrufe. Für Iter. 3 ist
   OnPoll single-threaded (manuell oder via Timer im Tests).

### Konfiguration

Bestehende `OutlookConfig` (Spec 0004 Iter. 1) wird erweitert:

```json
{
  "appReader": {
    "outlook": {
      "folders": ["Inbox", "Sent Items"],
      "pollIntervalSeconds": 60,
      "ignoreAutoRuleMails": false,
      "maxItemsPerSweep": 200,
      "bodyTruncateKB": 256,
      "htmlToMarkdown": {
        "preserveLinks": true,
        "preserveLineBreaks": true,
        "stripImages": true
      }
    }
  }
}
```

Felder:
- `folders` *(Default `["Inbox", "Sent Items"]`)* — MAPI-Folder-Namen, die
  gepollt werden. Case-insensitive Match gegen `Folder.Name`.
- `pollIntervalSeconds` *(Default `60`)* — Hinweis für den späteren
  Poll-Service; Iter. 3 triggert `OnPoll` manuell oder per Test.
- `ignoreAutoRuleMails` *(Default `false`)* — Master-Switch für die
  Heuristik (Spec 0004 §„Auto-Regel-Mails“).
- `maxItemsPerSweep` *(Default `200`)* — Cap gegen riesige Postfächer; nur
  die N neuesten Mails je Folder werden geprüft. Verhindert stundenlange
  First-Run-Sweeps.
- `bodyTruncateKB` *(Default `256`)* — Maximale Body-Länge im MD. Längere
  Bodies werden bei `bodyTruncateKB * 1024` Zeichen mit Hinweis
  `_(... truncated, original size: NNN KB)_` abgeschnitten.
- `htmlToMarkdown.preserveLinks` *(Default `true`)* — `<a href="...">X</a>` → `[X](...)`.
- `htmlToMarkdown.preserveLineBreaks` *(Default `true`)* — `<br>`, `</p>` → `\n\n`.
- `htmlToMarkdown.stripImages` *(Default `true`)* — `<img>`-Tags komplett
  entfernen (kein Markdown-Image, da Mails oft Tracking-Pixel haben).

### Persistenz-Schema

Pro Mail eine MD-Datei unter:

```
capture/<yyyy-MM-dd>/outlook-mail/<HHmmss>-<direction>-<entryId-short>.md
```

`<direction>` = `in` (Inbox) oder `out` (Sent Items). `<entryId-short>` =
erste 12 Zeichen der EntryID (Outlook-EntryIDs sind 70+ Zeichen, eindeutig
über MAPI-Session, reicht für Filename).

**Filename-Kollisionen**: Wenn im selben Sweep zwei Mails denselben
`<HHmmss>` haben, wird ein `-N`-Suffix angehängt (`-1`, `-2`, ...). Datei-
namen-Kollisionen über Sweeps hinweg sind ausgeschlossen, weil Outlook
EntryIDs eindeutig sind.

YAML-Frontmatter (siehe Spec 0004 §„Outlook-Spezial: Mail-Log“):

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
bodyLengthKB: 12
---
```

Body folgt als Plain-Markdown (entweder direkt Plain-Text oder HTML
konvertiert).

### EntryStore-Format (`outlook-seen.json`)

```json
{
  "version": 1,
  "lastSweepAt": "2026-07-05T10:00:00+02:00",
  "entryIds": [
    "00000000ABCDEF1234567890...",
    "0000000012345678ABCDEF12..."
  ]
}
```

- Plain-JSON (System.Text.Json), gitignored, **nicht** in der
  `%APPDATA%/AiRecall/`-Hierarchie.
- Atomar via `File.Replace` (siehe `OutlookEntryStore.MarkSeen`).
- `OutlookEntryStore.IsSeen` ist O(1) (`HashSet<string>` im Memory).
- Load beim `AppReader`-Init, Save nach jedem Sweep.
- `lastSweepAt` wird für Diagnostics geloggt, hat aber keine
  Filter-Funktion (EntryID-Dedup ist robuster).

### Auto-Regel-Heuristik (`OutlookAutoRuleDetector`)

Eine Mail ist „Auto-Regel-Suspect" wenn **mindestens 2** der folgenden
Bedingungen zutreffen (Spec 0004 §„Auto-Regel-Mails“):

1. `UnRead == false` **und** `LastModificationTime - ReceivedTime < 5 s`
   — Mail wurde nie geöffnet, aber als gelesen markiert.
2. Folder-Name ist `Junk E-Mail`, `Deleted Items`, oder matcht Regex
   `^(Newsletter|Notifications|Auto|Rule)` (case-insensitive).
3. `SenderEmailAddress` matched Regex `^(noreply|no-reply|notifications|mailer-daemon)@`.
4. Subject matched Regex `^(WG:|AW:|Fwd:|TR:)` **und** Body enthält
   `Auto-Reply`, `Automatische Antwort`, oder `Out of Office`
   (case-insensitive).

**Pure Function**: `OutlookAutoRuleDetector.IsSuspect(MailSnapshot snap)`. Die
Heuristik operiert auf einem `MailSnapshot`-Record (`Subject`, `From`,
`FolderName`, `UnRead`, `ReceivedTime`, `LastModificationTime`, `Body`) —
nicht direkt auf `MailItem` (COM). Damit ist die Heuristik unit-testbar
ohne Outlook.

### HTML-zu-Markdown-Konvertierung

`OutlookBodyToMarkdown.FromHtml(string html, HtmlToMarkdownOptions opts)`:

- Verwendet **NICHT** ReverseMarkdown (zu fett für Mails, viele Edge-Cases
  bei Outlook-spezifischem HTML).
- Eigene simple Tag-Strip-Logik:
  - `<style>`, `<script>`, `<!-- -->` → komplett entfernen.
  - `<a href="X">Y</a>` → `[Y](X)` (wenn `preserveLinks`), sonst `Y`.
  - `<br>`, `</p>`, `</div>` → `\n\n`.
  - `<img>` → komplett entfernen (wenn `stripImages`), sonst
    `![alt](src)`.
  - Alle anderen Tags → innerText.
- HTML-Entities (`&amp;`, `&lt;`, `&gt;`, `&quot;`, `&nbsp;`) werden
  decoded.
- Output ist Plain-Text mit Markdown-Link-Syntax.

### Tests

- **`OutlookEntryStoreTests`** (≥ 6 Tests):
  - `IsSeen_NewEntry_ReturnsFalse`
  - `MarkSeen_Persists_LoadReadsIt`
  - `MarkSeen_Idempotent`
  - `MarkSeen_Multiple_OrderIndependent`
  - `Load_MissingFile_StartsEmpty`
  - `Load_CorruptedJson_StartsEmpty_WithLog`
- **`OutlookAutoRuleDetectorTests`** (≥ 5 Tests):
  - `IsSuspect_FreshUnreadMail_ReturnsFalse`
  - `IsSuspect_MarkedReadImmediately_ReturnsTrue` (Bedingung 1)
  - `IsSuspect_InNewsletterFolder_ReturnsTrue` (Bedingung 2)
  - `IsSuspect_NoreplySender_ReturnsTrue` (Bedingung 3)
  - `IsSuspect_AutoReplySubjectAndBody_ReturnsTrue` (Bedingung 4)
  - `IsSuspect_SingleCondition_ReturnsFalse` (≥ 2 erforderlich)
- **`OutlookTitleParserTests`** (≥ 4 Tests):
  - `Parse_InboxFolderName`
  - `Parse_SubjectOnly`
  - `Parse_SentItems`
  - `Parse_UnknownFolder_FallsBackToSubject`
- **`OutlookBodyToMarkdownTests`** (≥ 5 Tests):
  - `FromHtml_StripsStyleAndScript`
  - `FromHtml_PreservesLinks`
  - `FromHtml_DropsImages_ByDefault`
  - `FromHtml_ConvertsLineBreaks`
  - `FromPlain_PassesThrough`
  - `FromHtml_DecodesEntities`
- **`OutlookAppReaderTests`** (≥ 3 Tests):
  - `Read_NoOutlook_ReturnsNull`
  - `Read_UnsupportedProcess_ReturnsNull`
  - `OnPoll_NoOutlook_DoesNotThrow`
  - `OnPoll_DetectsSeenEntryIds_DoesNotPersist` (Mock-Store)
- Test-Count Ziel: **+23 neue Tests**, gesamt **≥ 449 / 449 grün**.

### Bekannte Einschränkungen (Iter. 3)

- **OnPoll wird in Iter. 3 nicht automatisch aufgerufen.** Der Trigger-
  Worker kennt aktuell `OnPoll` noch nicht. Spec 0004 Iter. 4 wird einen
  `AppReaderPollService` einführen, der `OnPoll` periodisch aufruft. Bis
  dahin ist der Outlook-Reader manuell via Test oder Custom-CLI-Sub-
  Command testbar.
- **Nur Outlook Classic** (COM-basiert). Outlook New (PIM-basiert) hat
  keine COM-Schnittstelle und bleibt Out-of-Scope (siehe Out-of-Scope-
  Liste).
- **COM-Lookup nimmt die erste laufende Outlook-Instanz.** Bei mehreren
  parallelen Outlook-Profilen kann der falsche Folder gelesen werden.
  Pro-Profil-Filterung ist Iter.-4-Kandidat.
- **HTML-zu-Markdown ist simpel.** Outlook produziert oft verschachteltes
  HTML mit Conditional-Comments (`<!--[if gte mso 9]>...<![endif]-->`),
  Word-spezifische CSS-Klassen und Inline-Styles. Unsere Konvertierung
  strippt alles, was sie nicht versteht — d. h. das Ergebnis ist Plain-
  Text mit Markdown-Links, nicht „schönes Markdown". Für Spec 0004
  Iter. 3 ist das ausreichend (Ziel ist Volltext-Indexierung, nicht
  Rendering).
- **Attachments werden nicht persistiert.** Spec 0004 erwähnt das nicht
  explizit, aber `hasAttachments: true` im Frontmatter zeigt an, dass
  welche da waren. Attachment-Speicherung ist Iter.-4-Kandidat.
- **Race Condition zwischen mehreren Outlook-Sweeps:** Wenn `OnPoll`
  parallel aufgerufen wird (z. B. von Tests), kann `outlook-seen.json`
  inkonsistent werden. Iter. 4 serialisiert via Poll-Service. Für
  Iter. 3 ist `OutlookAppReader.OnPoll` mit `lock (_gate)` geschützt.