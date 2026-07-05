# 0010 — OneNote App-Reader

> **Status:** ✅ **abgeschlossen (Cluster 1–6, 2026-07-05)** — Test-Count 589/589 grün, gepusht als Commits `c02d861`, `fd03b7b`, `ce10dec`, `1081ece`, `b8a3e20` (+ Docs in Cluster 6).
> **Implements:** ON-1 .. ON-7 (App-Reader-Erweiterung für OneNote)
> **Pattern:** analog Outlook App-Reader (Spec 0004 Iter. 3)
> **Owner:** Martin + Pia
> **Branch:** `main`
> **Started:** 2026-07-05

## Motivation

Microsoft OneNote ist auf Windows-Workstations allgegenwärtig für freie Notizen,
Meeting-Notes, Skizzen und gesammelte Recherchen. Im Gegensatz zu Mail-Work­flows
(Outlook) hat OneNote einen anderen Charakter: der Anwender arbeitet mit
einer **sichtbaren Page**, kein konstanter Daten­strom.

Wir wollen die aktuell sichtbare OneNote-Page als strukturierten Content loggen
(analog Outlook-Mail-Log):

- **Strukturierte Erfassung** statt OCR-only (solange die Page Text enthält).
- **Notebook/Section/Page-Hierarchie** im Frontmatter für spätere Suche.
- **Read-only** in Iter. 1 — kein automatisches Update/Insert in OneNote.

Aktive Page **nur** (kein Background-Polling). Wenn kein OneNote-Fenster offen
oder keine Page selektiert ist, liefert der Reader `null` → Trigger fällt
zurück auf OCR / UIA-Capture.

## Architektur

Eine neue DLL analog Outlook:

```
AiRecall.AppReader.OneNote.dll
├─ OneNoteAppReader           : AppReaderBase (Read only, kein OnPoll)
├─ OneNoteComInterop          : internal static — late binding via ProgID
├─ OneNotePageXmlToMarkdown   : Pure-Function XML→MD (stateless)
├─ OneNoteHierarchyInfo       : internal sealed record (Notebook/Section/Page)
├─ OneNoteComException        : Exception-Wrapper für COM-Codes
└─ OneNoteContentResult       : internal sealed record (PageContent + Meta)
```

`AppReaderRegistry` scannt automatisch via `AiRecall.AppReader.*.dll`-Pattern.

## Active-Page-Strategie (4-stufig)

OneNote COM `Application.Windows.CurrentWindow.CurrentPageId` ist die offizielle
API (bestätigt via Microsoft Learn + OneMore-AddIn production code). Wir bauen
eine 4-stufige Fallback-Kette:

1. **Stufe 1 — Windows.CurrentWindow.CurrentPageId**: direkter Zugriff auf den
   aktuell fokussierten Tab. Property, kein Iter.
2. **Stufe 2 — Windows-Collection + Active-Property**: Fallback wenn
   `CurrentWindow` nicht verfügbar (ältere Builds, Edge-Cases).
3. **Stufe 3 — GetHierarchy(hsPages) + isCurrentlyViewed="true"**: Fallback
   wenn COM-Fehler oder kein Window offen. Robust, wenn `Windows`-Collection
   leer.
4. **Stufe 4 — null → Trigger-OCR-Fallback**: Reader liefert `null` →
   TriggerSupervisor nimmt UIA-Capture-Pfad.

Konfigurierbar via `OneNoteConfig.ActivePageStrategy` (Default: `WindowsApi`).

## Komponenten

### OneNoteComInterop (internal static)

Late-Binding-Wrapper für ProgID `OneNote.Application`. Initialisierung via
`GetActiveObject` (P/Invoke `oleaut32.dll`) statt `new OneNote.Application()`
(letzteres würde eine zweite Instanz starten).

Public APIs:
- `IsOneNoteRunning()` → bool (Process-Check `OneNote.exe`, cached 5s)
- `GetActivePageId(out OneNoteHierarchyInfo? info)` → bool
  → bevorzugt `Windows.CurrentWindow.CurrentPageId`
- `GetActivePageIdByHierarchy(out OneNoteHierarchyInfo? info)` → bool
  → Fallback via `GetHierarchy(hsPages)` + XPath `.//one:Page[@isCurrentlyViewed='true']`
- `GetPageContentXml(string pageId, out string xml)` → bool
  → `GetPageContent(pageId, out xml, XMLSchema.xs2013)` (immer `xs2013` für
   Versions-Kompatibilität, nie `xsCurrent`)
- `GetPageHierarchyXml(string pageId, out string xml)` → bool
  → `GetHierarchy(hsSelf, pageId)` für Parent-Info (Notebook/Section)
- `TryGetActiveSectionId(out string? id)` / `TryGetActiveNotebookId(out string? id)`
  → Konsistenz mit `CurrentPageId` (manche Builds liefern andere Werte)

Private Helper:
- `MarshalRelease(object? comObject)` → RCW-Cleanup (mehrere try-finally)
- `IsRetryableComError(Exception ex)` → bool (siehe Retry-Logik unten)
- `ReplaceApplication()` → frisches `OneNote.Application`-Objekt für Retry
- `XPathFindIsCurrentlyViewedPage(string hierarchyXml)` → internal (testbar)

### Retry-Logik (inspiriert von OneMore-AddIn)

COM-Late-Binding ist fragil. Wrapper implementiert Retry-Semantik:

- **Fatal-Errors ohne Retry** (`IsRetryableComError` = false):
  - `0x80042001` (`hrXmlIsInvalid`) — fehlerhaftes XML-Schema, kein Recovery
  - `0x800706BA` (`hrRpcFailed2`) — Server-Crash
- **Retryable-Errors mit Retry** (max. 3 Versuche, je 500ms Backoff):
  - `InvalidComObjectException` — RCW wurde orphaned
  - `COMException` mit `hrRpcFailed`, `hrRpcUnavailable`, `hrCOMBusy`,
    `hrObjectMissing`
- Pro Retry: `ReplaceApplication()` (frisches COM-Objekt).

### OneNotePageXmlToMarkdown (Pure Function)

Stateless XML→MD-Konverter. Verarbeitet `one:Page`-XML.

Mapping:

| OneNote-XML | Markdown | Bedingung |
|---|---|---|
| `one:OE` (Outline-Element, Top-Level) | Absatz (`\n\n` getrennt) | immer |
| `one:T` (CDATA) | Plain-Text, `&amp;`/`&lt;`/`&gt;`/`&nbsp;` decodiert | immer |
| `one:Image` | `![alt](filePath)` | `IncludeImages=true` |
| `one:Tag` (To-Do) | `[ ]` oder `[x]` | `IncludeTags=true` |
| `one:Tag` (andere) | `#tag-name` | `IncludeTags=true` |
| `one:InkContent` | `*(handschriftlich)*` Hinweis | immer (Handschrift nicht in MD abbildbar) |
| `one:Table` | Markdown-Tabelle (Header-Zeile + Pipes) | immer |
| `one:InsertedFile` | Datei-Referenz im Frontmatter | immer (keine Binär-Persistenz) |

HTML-Entities werden via `System.Web.HttpUtility.HtmlDecode` dekodiert
(.NET 8 built-in).

Output-Schema:
```
# <pageTitle>

*Source: OneNote (COM + Active Page)*
*Notebook: <notebookTitle> (<notebookId>)*
*Section: <sectionTitle> (<sectionId>)*
*Last-Modified: <yyyy-MM-dd HH:mm:ss>*

---

<markdown content>
```

### OneNoteAppReader (Read only)

Erbt von `AppReaderBase`:

```csharp
public sealed class OneNoteAppReader : AppReaderBase
{
    public override string ReaderKey => "onenote";
    public override IReadOnlyList<string> SupportedProcesses => new[] { "OneNote" };
    public override string DisplayName => "OneNote (COM + Active Page)";

    // Read only — OneNote ist Page-orientiert, kein Stream
    public override bool SupportsBackgroundPolling => false;

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context);
}
```

**Read-Pipeline:**

1. `EnsureInitialized(context)` — lazy Setup (OneNoteConfig + Logger).
2. `OneNoteComInterop.IsOneNoteRunning()` → wenn false → return null.
3. `OneNoteComInterop.GetActivePageId(out var info)`:
   - **Stufe 1** (`Windows.CurrentWindow.CurrentPageId`) wenn aktiviert.
   - **Stufe 2** (`Windows-Collection.Active` Property) als Fallback.
   - **Stufe 3** (`isCurrentlyViewed="true"` Hierarchy) als Fallback.
   - **Stufe 4** — null zurückgeben wenn alle Stages scheitern.
4. `OneNoteComInterop.GetPageContentXml(pageId)` → XML-String.
5. `OneNotePageXmlToMarkdown.Convert(xml, OneNoteConfig)` → Markdown-String.
6. Persist via `CaptureWriter.WriteContent(window, "onenote", md, frontmatter)`.
7. Return `AppReaderResult.Success(window, info.PageId, ReadKind.ActivePage)`.

**Throttle:** Nicht relevant (kein OnPoll). TriggerSupervisor ruft `Read`
nur bei explizitem Trigger (Ctrl+Shift+R oder Auto-Trigger alle `n` Sek.).

### Persistenz-Schema (analog Outlook)

```
capture/<yyyy-MM-dd>/onenote/<HHmmss>-<pageIdShort>.md
```

`<pageIdShort>` = erste 8 Zeichen der Page-GUID (ohne Bindestriche).
Beispiel: `capture/2026-07-05/onenote/191823-AB12CD34.md`

**Frontmatter** (YAML):

```yaml
kind: onenote-page
pageId: AB12CD34-1234-5678-90AB-CDEF12345678
notebook: "Mein Notebook"
notebookId: "..."
section: "Meine Section"
sectionId: "..."
pageTitle: "Mein Seitentitel"
lastModified: "2026-07-05T18:34:12"
strategy: WindowsApi          # oder HierarchyXml / null
includeImages: false
includeTags: true
source: onenote-com
reader: AiRecall.AppReader.OneNote
readerVersion: "0.1.0"
```

## Konfiguration (`OneNoteConfig`)

POCO in `src/AiRecall.Core/Configuration/AppConfig.cs`:

```csharp
public sealed class OneNoteConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxContentKB { get; set; } = 256;
    public bool IncludeImages { get; set; } = false;   // Default: aus (Base64-Inflation)
    public bool IncludeTags { get; set; } = true;
    public string HierarchyDepth { get; set; } = "PageAndSection";  // PageOnly|PageAndSection|PageAndSectionAndNotebook
    public string ActivePageStrategy { get; set; } = "WindowsApi";  // WindowsApi|HierarchyXml|Auto
    public int PollIntervalSeconds { get; set; } = 0;                // 0 = Read-only, kein OnPoll
    public List<string> SkipNotebookPatterns { get; set; } = new();   // z. B. "*.deleted"
}
```

Integration in `AppConfig`-Wurzel:

```csharp
public OneNoteConfig OneNote { get; set; } = new();
```

`DefaultConfigLoader` liest `appReader.onenote.*`-Sektion aus
`default-config.json` + User-Config (analog zu `appReader.outlook.*`).

## Tests (~50 neue)

| File | Tests | Inhalt |
|---|---|---|
| `OneNoteComInteropTests.cs` | 8 | `IsOneNoteRunning`, Page-ID-Strategien, Hierarchy-Fallback, Retry-Logik, RCW-Release, Hierarchy-XPath |
| `OneNotePageXmlToMarkdownTests.cs` | 25 | XML-Fixtures für `OE`/`T`/`Image`/`Tag`/`Table`/`InkContent`/`InsertedFile`/`Entities`/`CDATA`/nested-OE |
| `OneNoteAppReaderTests.cs` | 12 | `Read`-Pfad mit Mock-ComInterop, alle 4 Stages, Frontmatter-Validation, CaptureWriter-Roundtrip, Disabled-Config |
| `OneNoteConfigTests.cs` | 5 | Defaults + JSON-Deserialisierung + Missing-Section (rückwärts-kompatibel) |
| **Σ** | **50** | analog Outlook-Pattern |

Test-Trait-Marker analog Outlook:
- `[Trait("Integration", "OneNote")]` für Tests, die installiertes OneNote
  voraussetzen — werden im CI skipped wenn OneNote nicht installiert.
- Tests laufen lokal auf Martins Workstation (OneNote ist installiert).

## Bekannte Einschränkungen

- **OneNote UWP** wird NICHT unterstützt (kein COM-Interface, sandbox).
- **OneNote Web** ist nicht erreichbar (nur lokal via COM).
- **Handschrift** (`one:InkContent`) wird zu MD-Hinweis `*(handschriftlich)*`
  konvertiert — kein Pixel-OCR.
- **Attachments** (`one:InsertedFile`) werden nur als Filename-Liste im
  Frontmatter geloggt, **keine** Binär-Persistenz (zu groß für Capture-MD).
- **Mehrere offene OneNote-Fenster**: aktuell nur die `CurrentWindow` wird
  gelesen (User-Verhalten: typischerweise ein Window).
- **Multi-Page-Selection**: pro Capture nur 1 Page (User wählt via
  OneNote-Tab — wir lesen das aktive Tab).
- **Keine Cross-Reference**: `window.WindowHandle` wird aktuell nicht für
  `WindowInfo`-Matching verwendet (eine zukünftige Iteration könnte hier
  genauer filtern).
- **COM-RPC-Errors**: Trotz Retry kann OneNote COM hängen. Fail-safe: Read
  gibt `null` zurück → TriggerSupervisor-OCR-Fallback.

## Out-of-Scope (für Iter. 1)

- Schreibzugriff auf Pages (`UpdatePageContent` nicht implementiert).
- Page-History via `GetPageHistory`.
- Search/Query via OneNote API.
- OneNote Mobile sync.
- Embedding-basierte Page-Ähnlichkeit (Spec 0007/Index).

## Referenzen

- Microsoft Learn: [Application Interface](https://learn.microsoft.com/en-us/office/onenote/application-interface)
- OneMore AddIn: <https://github.com/stevencohn/OneMore>
- Outlook App-Reader (Spec 0004 Iter. 3): `specs/0004-app-reader.md`
- Trigger-Pipeline: `specs/0005-trigger-pipeline.md`

## Acceptance Criteria (gegen Code verifiziert, 2026-07-05)

Stand nach Cluster 1–6 (5 Commits + Docs). Verifikation gegen die tatsächliche Implementation
in `src/AiRecall.AppReader.OneNote/`:

### ON-1 — OneNote-Prozess-Erkennung
- [x] `OneNoteComInterop.IsOneNoteRunning()` via `Process.GetProcessesByName("OneNote")` — implementiert in `OneNoteComInterop.cs` (Cluster 2).
- [x] `OneNoteAppReader.IsOneNoteProcessRunning()` (public static) fuer externe Diagnostik — implementiert in `OneNoteAppReader.cs` (Cluster 4).
- [x] Pre-Filter in `OneNoteAppReader.Read()` vermeidet COM-Aufrufe wenn OneNote nicht laeuft — verifiziert im Source.

### ON-2 — 4-stufige Active-Page-Strategie
- [x] **Stage 1**: `onenote.Windows.CurrentWindow.CurrentPageId` (offizielle API) — implementiert in `OneNoteComInterop.TryStage1Or2`.
- [x] **Stage 2**: `Windows`-foreach + `window.Active == true` (Fallback) — implementiert in `OneNoteComInterop.TryStage1Or2`.
- [x] **Stage 3**: `GetHierarchy(hsPages)` + XPath `//one:Page[@isCurrentlyViewed='true']` (Robust-Fallback) — implementiert in `OneNoteComInterop.TryStage3`.
- [x] **Stage 4**: `null`-Rueckgabe (Caller faellt auf OCR zurueck) — implementiert in `OneNoteComInterop.TryGetActivePage` (Strategy "Auto" endet mit null).
- [x] Konfigurierbar via `OneNoteConfig.ActivePageStrategy` (WindowsApi / HierarchyXml / Auto) — implementiert.

### ON-3 — Page-Content-XML
- [x] `OneNoteComInterop.TryGetPageContentXml(pageId)` mit `xs2013`-Schema — implementiert in `OneNoteComInterop.cs`.
- [x] Private Konstante `XmlSchema2013 = "xs2013"` in `OneNoteComInterop` — implementiert.
- [x] Fallback-Pfad: bei `null`-XML wird `BuildFallbackResult(info, hint)` aufgerufen — implementiert in `OneNoteAppReader.cs`.

### ON-4 — XML→MD-Konvertierung (OneNotePageXmlToMarkdown)
- [x] `ConvertBody(xml, config)` als Pure-Function (zustandslos, IO-frei) — implementiert in `OneNotePageXmlToMarkdown.cs`.
- [x] `one:OE` → Absatz mit `\n\n`-Separator — implementiert in `AppendOutline` + `AppendOE`.
- [x] `one:T` (CDATA) → Plain-Text via `HttpUtility.HtmlDecode` — implementiert in `DecodeHtml`.
- [x] `one:Image` → `![alt](filename)` wenn `IncludeImages=true` — implementiert in `FormatImageInline` + `AppendImage`.
- [x] `one:Tag` (to-do:empty) → `[ ]` — implementiert in `FormatTagInline`.
- [x] `one:Tag` (to-do:complete) → `[x]` — implementiert in `FormatTagInline`.
- [x] `one:Tag` (custom) → `#tag-name` — implementiert in `FormatTagInline`.
- [x] `one:Table` → Markdown-Tabelle mit Header + Pipes — implementiert in `AppendTable`.
- [x] `one:InkContent` → `*(handschriftlich)*` Hinweis + optionaler OCR-Text — implementiert in `AppendInkContent`.
- [x] `one:InsertedFile` → `*Attached File:* filename` am Page-Level (Pfad getruncated auf Filename) — implementiert in `AppendInsertedFile`.
- [x] Bullet-Indent mit `style="list..."`-Substring-Match + 2-Space-Indent pro Ebene — implementiert in `IsBulletStyle` + `AppendOE`.
- [x] `ExtractPageTitle(xml)` → `page.name`-Attribut — implementiert.
- [x] `ExtractLastModified(xml)` → `page.lastModifiedTime`-Attribut — implementiert.
- [x] `ExtractInsertedFileNames(xml)` → deduped Liste der Filenames (ohne Pfad) — implementiert.

### ON-5 — Frontmatter + Hierarchy-Info
- [x] `OneNoteHierarchyInfo` record mit `PageId`, `PageTitle`, `SectionId`, `SectionTitle`, `NotebookId`, `NotebookTitle`, `LastModified` — implementiert in `OneNoteHierarchyInfo.cs` (Cluster 2).
- [x] `PageIdShort`-Property (erste 8 Zeichen ohne Bindestriche) — implementiert in `OneNoteHierarchyInfo`.
- [x] `HasMinimumInfo`-Property (PageId gesetzt?) — implementiert in `OneNoteHierarchyInfo`.
- [x] `OneNoteAppReader.BuildFullMarkdown(info, body, xml, cfg)` produziert YAML-Frontmatter + Header + Body — implementiert (internal, fuer Tests via `InternalsVisibleTo`).
- [x] `HierarchyDepth`-konfigurierbar (PageOnly / PageAndSection / PageAndSectionAndNotebook) — implementiert via `ShouldIncludeSection`/`ShouldIncludeNotebook`.

### ON-6 — Persistenz-Schema
- [x] Schema: `capture/yyyy-MM-dd/onenote/HHmmss-{pageIdShort}.md` — implementiert durch `CaptureWriter.WriteContent` (TriggerWorker-Pfad).
- [x] YAML-Frontmatter-Felder: timestamp, kind=onenote-page, pageId, pageTitle, [section/sectionId], [notebook/notebookId], lastModified, strategy, includeImages, includeTags, attachments, source=onenote-com, reader, readerVersion — implementiert in `BuildFullMarkdown`.
- [x] YAML-Escape (Backslash/Double-Quote/Newline) — implementiert in `EscapeYaml`-Helper.

### ON-7 — Tests + Plugin-Discovery
- [x] `AppReaderRegistry.LoadFromDirectory` scannt automatisch `AiRecall.AppReader.OneNote.dll` — durch Pattern-Match.
- [x] Parameterloser `OneNoteAppReader()`-Konstruktor fuer `Activator.CreateInstance(type)` — implementiert (Cluster 4).
- [x] `internal OneNoteAppReader(ILogger logger, string captureRoot)` fuer Test-Injection — implementiert.
- [x] `[InternalsVisibleTo("AiRecall.Core.Tests")]` in `AiRecall.AppReader.OneNote.csproj` — implementiert (Cluster 2).
- [x] Tests: 64 neue (5 Config + 8 ComInterop + 30 XML→MD + 21 AppReader) — **589/589 grün** (verifiziert in Cluster 5, Commit `b8a3e20`).
- [x] Truncate-Logik (`OneNoteAppReader.TruncateBody` + Config `MaxContentKB`) — implementiert (Cluster 4).
- [x] `SkipNotebookPatterns`-Filter (case-insensitive Substring gegen `NotebookTitle`) — implementiert in `OneNoteAppReader.Read()` (Cluster 4).
- [x] `IsOneNoteProcessRunning`-Helper fuer UI-Hinweise (TrayApp-Integration vorbereitet) — implementiert (Cluster 4).

### Test-Trait-Marker
- [x] ComInterop-Pure-Function-Tests laufen ohne installiertes OneNote (alle 64 Tests auf CI gruen) — verifiziert.
- [ ] **OFFEN (für Iter. 2):** Integration-Tests gegen echtes OneNote mit `[Trait("Integration", "OneNote")]`. Werden auf Martins Workstation zusaetzlich ausgefuehrt; in CI skipped wenn OneNote nicht installiert.
