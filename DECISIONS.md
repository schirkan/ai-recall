# DECISIONS

Architektur- und Stack-Entscheidungen mit Datum und Begründung. Wird bei
Bedarf von PROJECT.md oder specs/*.md geladen.

---

## 2026-07-04 — Trigger-Pipeline: Implementation-Resultat + nachträgliche Entscheidungen

Spec 0005 (Trigger-Pipeline) ist mit Commits `791161a` … `5d934dc`
abgeschlossen. Die folgenden Entscheidungen wurden während der
Implementation getroffen (teils Martin-Review-Fixes, teils
technische Notwendigkeiten):

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Assembly-Struktur | **`AiRecall.Trigger.dll` als eigenes Projekt** | MVP2-Tray-Icon-EXE soll Trigger-Code wiederverwenden, braucht aber kein ScreenCapture. Zyklusfreie Ref-Kette: Core → AppReader.Base → ScreenCapture → Trigger → Cli. |
| 2 | Trigger-Lifecycle | **`ITriggerService`-Interface + `TriggerService`-Implementierung** | Ermöglicht MVP2-Tray-EXE, denselben Code zu nutzen. `Start`/`Stop`/`Dispose` idempotent. Counter-Properties (`CaptureCount`, `ThrottleCount`, ...) für IPC. |
| 3 | Generisches Throttling | **`Throttle<TKey> where TKey : notnull`** statt separater `ThrottleIntPtr`/`ThrottleString` | DRY, ein Code-Pfad für `Throttle<IntPtr>` (HWND) und `Throttle<string>` (Prozessname). |
| 4 | HWND-Dedup | **`HwndDedup` als eigene Klasse** (nicht in `Dedup` integriert) | HWND-Key muss als Hex-String (`0xDEADBEEF`) in JSON persistiert werden, weil `IntPtr` nicht direkt serialisierbar ist. Dedup nach Prozessname nutzt weiterhin die generische `Dedup`-Klasse. |
| 5 | Channel-Topologie | **`Channel<TriggerEvent>` unbounded, SingleReader (Worker), MultiWriter (WinEventHook + Heartbeat)** | WinEventHook + Heartbeat schreiben parallel, Worker liest sequenziell. Unbounded, damit keine Events verloren gehen (Worker ist schnell genug). |
| 6 | Modal-Dialog-Strategie | **Option (a) — nur Foreground-Capture, Parent-Context als Frontmatter** (Martin-Diskussion 2026-07-04) | Beim modalen Dialog (z. B. Outlook „Neue Nachricht") wird nur das Vordergrund-Fenster aufgenommen, aber `parentHwnd`/`parentTitle`/`parentProcess` werden im YAML-Frontmatter emittiert. Erkennung via `GetAncestor(GA_ROOTOWNER) != rootHwnd`. |
| 7 | Selection-Event | **`EVENT_OBJECT_SELECTION` ist NICHT in den Trigger-Quellen** (Martin 2026-07-04) | Würde bei reinem Caret-Wechsel innerhalb desselben Inhalts Captures auslösen — zu viel Rauschen. Nur Fokus/Name/Value/Scroll sind sinnvolle Trigger. |
| 8 | CLI-Headless-Mode | **`--headless`-Flag** für MVP2-Tray-EXE und CI | Unterdrückt Console-Stats-Output, schreibt nur nach Serilog. Serilog-Output kann von Tray-EXE / NSSM / systemd-logind ausgewertet werden. |
| 9 | CLI-Trigger-Mode | **`--trigger-mode=events\|polling\|both`** (Default: events) | Tests ohne Message-Loop (z. B. Sandbox) können `--trigger-mode=polling` nutzen. Production-Default ist `events` (sparsam). |
| 10 | `recall status` | **Neuer Diagnose-Subcommand** | Liest nur von Disk (Config, heutige Captures nach Prozess, aktive Trigger-Config). `--json` für MVP2-IPC. Vorbereitung: Tray-EXE aktualisiert periodisch eine Status-Datei, die `recall status` anzeigt. |
| 11 | Alte Polling-Pipeline | **`CapturePipeline` + `EventDetector` + `Models.cs` entfernt** | War Dead-Code nach Umstellung auf `TriggerService`. Reduziert Code-Maintenance-Burden. |

### Tests

- 91 neue Tests (Schritte A–G) in `tests/AiRecall.Core.Tests/Trigger/`
  und `tests/AiRecall.Core.Tests/Persistence/`.
- Test-Count gesamt: 189 / 189 grün (vorher 98 / 98).

---

## 2026-07-04 — Documents App-Reader (Spec 0004 Iter. Documents)

Word/Excel/PowerPoint-Reader als eigenständige DLL (`AiRecall.AppReader.Documents`).
Strategie: **UIA statt COM**. Begründung + Entscheidungen:

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Lese-Strategie | **UIA (`System.Windows.Automation`)** statt COM-Interop | COM würde installiertes Office voraussetzen. UIA läuft auch ohne Office, liefert aber nur sichtbaren Inhalt (siehe Punkt 4/5/6). |
| 2 | Assembly-Struktur | **Neue DLL `AiRecall.AppReader.Documents.dll`** (analog zu Browser/Explorer/Notepad) | Eine DLL pro App-Familie — prozessspezifische Logik isoliert, AppReaderRegistry lädt sie automatisch beim Start neben der Exe. |
| 3 | Konfiguration | **`DocumentsConfig` mit `maxTextKB` (Default 64) + `enableUiaExtraction` (Default true)** | maxTextKB analog zu `notepad.maxBufferKB`. enableUiaExtraction erlaubt das Abschalten, falls UIA Probleme macht (dann Title-only). |
| 4 | Word-Spezifika | **Filename-Parsing statt ActiveDocument** | UIA liefert kein `ActiveDocument.Path`. Window-Titel-Format `"Doc.docx - Word"` ist gut dokumentiert und wird robust geparst (Suffix → Flags in beliebiger Reihenfolge → Unsaved-Marker → Untitled-Erkennung „Document1"). |
| 5 | Excel-Spezifika | **Hinweis im MD, dass UIA nur sichtbare Zellen liefert** | Echter Sheet-Inhalt (alle Zellen) erfordert COM. Wir liefern, was sichtbar ist, und dokumentieren die Einschränkung im Output-Markdown. |
| 6 | PowerPoint-Spezifika | **Hinweis im MD, dass UIA nur sichtbare Slide liefert** | Folien-Nummern, Notizen, Layouts erfordern COM. Wir liefern sichtbaren Inhalt + dokumentieren die Einschränkung. |
| 7 | UIA-Verfügbarkeit | **`UseWPF=true` im csproj** | UIA (`AutomationElement`, `TextPattern`, `ValuePattern`) lebt in `UIAutomationClient.dll`, das in .NET 8 nur via `<UseWPF>` automatisch referenziert wird. Alternative wäre explizite `<Reference>`-Tags, die aber in .NET 8 SDK nicht aufgelöst werden können. |
| 8 | Tests | **54 neue Unit-Tests** (`WordAppReaderTests` 18, `ExcelAppReaderTests` 14, `PowerPointAppReaderTests` 14, weitere Smoke-Tests) | Tests prüfen ParseTitle (Normal, Untitled, ReadOnly, SafeMode, Unsaved-Marker, Edge-Cases) und Read-Smoke (IntPtr.Zero → kein UIA-Text, kein Crash). e2e-Tests gegen echtes Office entfallen in der Sandbox (Martin 2026-07-04). |

### Tests

- 54 neue Tests in `tests/AiRecall.Core.Tests/AppReaders/{Word,Excel,PowerPoint}AppReaderTests.cs`.
- Test-Count gesamt: 243 / 243 grün (vorher 189 / 189).

### Verworfen

- **COM-Interop für Word/Excel/PowerPoint** (`Microsoft.Office.Interop.*`): würde Office-Installation
  voraussetzen, ist auf vielen Maschinen nicht vorhanden, und die Bindung an spezifische
  Office-Versionen macht die Pflege teuer. UIA liefert einen akzeptablen Ausschnitt
  ohne diese Abhängigkeit.
- **Folien-Nr / Sheet-Name / Notizen via UIA**: in Tests nicht zuverlässig abrufbar,
  wäre nur über COM oder Office-Add-Ins sinnvoll. Explizit als „nicht implementiert"
  im Output-Markdown dokumentiert.

---

## 2026-07-04 — Office COM-Erweiterung + PDF-Viewer (Spec 0004 Iter. 2)

Martin 2026-07-04: Office-Reader um COM-Komponenten erweitern (echter Pfad + Inhalt),
zusätzlich neue App-Familie PDF-Viewer.

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | COM-Strategie | **Late binding** via ProgID + `Type.InvokeMember` — keine PIAs / NuGet-Pakete | Office ist nicht auf jeder Maschine installiert. Late binding funktioniert, sobald die COM-Server (Office selbst) vorhanden sind. Keine Build-Zeit-Abhängigkeit von Office-Versionen. |
| 2 | `GetActiveObject` | **P/Invoke auf `oleaut32.dll!GetActiveObject`** statt `Marshal.GetActiveObject` | `Marshal.GetActiveObject` ist in .NET 8 SDK 8.0.422 nicht (mehr) direkt verfügbar. P/Invoke ist der robuste Weg, der in allen SDK-Versionen funktioniert. |
| 3 | Inhalt-Speicherung | **In bestehende `*.content.md` integriert** (Sektion `## Document content (via COM)`) — **kein** separates File | Capture hat schon Screenshot + Pfad zur Quelldatei → alles in einer Datei verlinkt. Separate MD-Datei wäre Duplikation ohne Mehrwert. Martin-Default. |
| 4 | `FullPath` im Output | **`filePath` im Extra-Dict** → CaptureWriter rendert es als YAML-Frontmatter-Feld | Bestehende Mechanik: `AppReaderResult.Extra` → `CaptureWriter.RenderContentMarkdown` schreibt jedes KV-Pair als YAML-Zeile. Kein neuer Code noetig. |
| 5 | Excel-Inhalt | **UsedRange als Markdown-Tabelle** (object[,] → Pipe-Syntax) | COM `UsedRange.Value` ist 2D-Array; native Markdown-Tabelle ist die natürlichste Darstellung. Cell-Pipe-Escaping + Length-Truncate bei >60 Zeichen pro Zelle. |
| 6 | PowerPoint-Inhalt | **Slides als `### Slide N`-Liste** mit Text-Frames | COM hat keine „Inhalt"-Property für eine ganze Präsentation; pro Slide die Shapes durchlaufen und `HasTextFrame` + `TextRange.Text` sammeln. SmartArt/Tabellen fehlen in Iter. 2. |
| 7 | Word-Inhalt | **Range.Text (Plain-Text in Code-Block)** | Einfachster Word-Output; Markdown-Konvertierung in Iter. 3 via OOXML oder ReverseMarkdown-Word-Adapter. |
| 8 | COM-Fallback | **Bei jedem COM-Fehler (kein Office, andere Instanz, Exception) → null → Reader fällt auf UIA+Title zurück** | Nie crashen. UIA ist eh schon da; Office ist nur ein Bonus. Reader-Logik: erst COM versuchen, wenn null → Fallback. |
| 9 | COM-Prozess-Disambiguierung | **Nur erste Instanz** (für Iter. 2) | 99% der Fälle ist nur eine Office-Instanz offen. Pro-Prozess-Filterung (PID-Match) ist Iter. 3, YAGNI jetzt. |
| 10 | PDF-Viewer-DLL | **Neue DLL `AiRecall.AppReader.Pdf`** mit `PdfViewerAppReader` | Eine DLL pro App-Familie (analog zu Documents). Process-Liste konfigurierbar (`appReader.pdf.processes`), Default: Adobe/Sumatra/Foxit/PDFXChange/Edge/Chrome. |
| 11 | PDF-Viewer-Inhalt (Iter. 1) | **Nur Title-Parsing** (Filename + voller Pfad + Page-Nr) | PDF-Inhalt-Extraktion braucht eine PDF-Parser-Library (`PdfPig` ist Kandidat). In Iter. 2 mit NuGet-Package. Iter. 1 liefert Pfad-Hinweis im MD, damit der Capture zuordenbar ist. |
| 12 | PDF-Page-Info | **SumatraPDF-Style: `"file.pdf - Page N of M - SumatraPDF"`** | Andere PDF-Viewer zeigen Page-Nr nicht im Titel. Parsing ist robust: Page-Sep erst, dann Pfad-/Filename-Extraktion. |
| 13 | Office-COM-Tests | **`[Trait("Integration", "Office")]`** für COM-spezifische Tests | Sandbox hat kein Office → e2e-Smoke-Tests entfallen. Tests prüfen Struktur (Extra-Dict hat `source: com`, `filePath` gesetzt), laufen aber nur bei installiertem Office. |

### Tests

- 17 neue Unit-Tests in `PdfViewerAppReaderTests`.
- 3 neue Office-COM-Integration-Tests in Word/Excel/PowerPointAppReaderTests.
- Bestehende Office-Tests an COM-Pfad angepasst (Filename statt Markdown-Prefix).
- Test-Count gesamt: **263 / 263 grün** (vorher 243).

### Verworfen

- **Microsoft.Office.Interop.* NuGet-Pakete (PIAs)**: wuerde Office-Versionen ans Build-System binden. Late binding ist version-agnostisch.
- **Separate `*.document.md` pro Capture**: Martin bestaetigt Default „integriert". Falls er doch separate Datei will, ist die Aenderung klein (`CaptureWriter.WriteContent` + Reader rueckgabe).
- **PDF-Inhalt in Iter. 1**: wuerde NuGet-Abhaengigkeit (PdfPig ~5 MB) bedeuten und neue Fehlerquellen. YAGNI; iter. 2 mit NuGet-Evaluierung.
- **COM-Pro-Prozess-Disambiguierung (PID-Match)**: zu 99% nicht noetig; iter. 3 wenn Martin es wirklich braucht.

---

## 2026-07-04 — Office COM Iter. 3: Pro-Instanz-Filename-Match

Martin 2026-07-04 (Folgeanforderung nach Iter. 2): „Ermittle mit COM auch den Pfad zur aktuellen Datei / active document location." Hintergrund: `GetActiveObject("Word.Application")` liefert immer die erste laufende Instanz. Bei mehreren parallelen Office-Instanzen (z. B. zwei Word-Fenster mit unterschiedlichen Dokumenten) liefert COM sonst den falschen Pfad.

Loesung statt Pro-Prozess-COM-Bindung (zu komplex, kein direkter API-Weg in Windows): Filename-Match.

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | Disambiguierung | **Filename-Match statt Pro-Prozess-COM-Bindung** | Es gibt in Windows keinen einfachen Weg, COM an einen bestimmten Prozess zu binden (außer über ROT mit Item-Moniker oder `AccessibleObjectFromWindow`). Filename-Match ist eine pragmatische 95%-Loesung: bei mehreren parallelen Instanzen mit unterschiedlichen Filenames passt der Match nicht → Fallback. |
| 2 | Match-Logik | **`MatchesExpectedFilename(string? fullPath, string? expectedFilename)`** als internal static Helper in `OfficeComInterop` | Eigenstaendig unit-testbar ohne COM. Wird in `TryGet` nach dem Lesen von `FullName` aufgerufen. Bei Mismatch → `null` → Reader faellt auf UIA+Title. |
| 3 | expectedFilename-Quelle | **`ParseTitle(window.Title)` vor COM-Lookup** | Filename aus Window-Titel parsen, an COM durchreichen. Wenn `ParseTitle` "(untitled)" oder den Default-Untitled-Marker (`Document1`/`Book1`/`Presentation1`) liefert, wird `expectedFilename = null` gesetzt (kein Match erzwungen) — sonst wuerde COM bei echtem Untitled-Doc immer mismatchen. |
| 4 | IsLikelyARealFilename | **Heuristik pro Reader** (private static) | Pro App unterschiedliche Untitled-Marker (`Document1` / `Book1` / `Presentation1`). Helper verhindert, dass diese als expectedFilename durchgereicht werden. Drei Zeilen pro Reader; DRY waere overkill. |
| 5 | Fallback-Strategie | **Bei Mismatch → null → Reader-Code faellt auf UIA+Title** | Wichtig: kein falscher Pfad in `content.md`. Im Gegensatz zu Iter. 2 (COM-Fehler) liefert Mismatch trotzdem null; Leser sieht keinen Unterschied. |
| 6 | Tests | **8 neue Unit-Tests** in `OfficeComInteropFilenameMatchTests` | null/empty expected (immer true), match, case-insensitive, mismatch, empty/null fullPath, unsaved-Doc-Sonderfall. |

### Tests

- 8 neue Unit-Tests.
- Test-Count gesamt: **271 / 271 grün** (vorher 263).

### Verworfen

- **PID-basierte COM-Bindung** (z. B. via `AccessibleObjectFromWindow` + `IUnknown::QueryInterface`): zu komplex fuer den Use-Case. Filename-Match deckt 95% der Realfaelle ab (mehrere Office-Instanzen mit identischem Filename sind ein Edge-Case, der in der Praxis selten vorkommt).
- **WindowClass-Match** (z. B. `_WwG` fuer Word): process-name + filename-match reicht aus. WindowClass ist sprachversions-abhaengig.

### Verworfen

- **`EVENT_OBJECT_SELECTION` als Trigger-Quelle**: würde bei Caret-Wechsel
  in Textfeldern jeden Tastendruck als Capture-Event interpretieren.
  Zu viel Rauschen, dedup würde die meisten schlucken.
- **Trigger-Mode „events" mit Heartbeat an**: unnötig, da WinEventHook
  in der Praxis zuverlässig ist. Heartbeat nur als explizit aktivierter
  Fallback oder im `both`-Mode.
- **`getAppContext` mit Modal-Kontext** (Option (b) der Diskussion): würde
  den App-Reader aufrufen, was bei modalen Dialogen oft leer/irrelevant
  ist. Frontmatter-Only (Option a) ist sauberer.

---

## 2026-07-04 — Trigger-Pipeline: WinEventHook statt Polling

`recall record` (Spec 0005) löst das ursprüngliche Polling auf
`GetForegroundWindow` (MVP1-Scope TR-1..6) durch eine event-basierte
Architektur ab.

| Aspekt | Entscheidung | Begründung |
|---|---|---|
| Primäre Trigger-Quelle | **`SetWinEventHook` out-of-context** (systemweit, ohne DLL-Injection) | Events kommen asynchron, granular (FOREGROUND/FOCUS/NAMECHANGE/VALUECHANGE/SCROLL/MENUPOPUP), keine CPU-Last durch Polling, keine Latenz zwischen Ereignis und Capture. |
| Sekundäre Trigger-Quelle | **Heartbeat-Polling** (`trigger.heartbeatIntervalSeconds`, Default 30 s) | Fallback für verschluckte Events (Sleep/Resume, hohe Systemlast). Niedrige Frequenz, reine Foreground-Erkennung, kein Inhalts-Polling. |
| `WH_SHELL` / `WH_CBT`-Hooks | **Verworfen** | Würden DLL-Injection in jeden anderen Prozess erfordern. Zu invasiv (Admin-Rechte, AV-Warnungen, Stabilitätsrisiko). |
| UIA-Event-Handler (`IUIAutomation.AddAutomationEventHandler`) | **Verworfen als primäre Quelle** | App-Coverage dünner als WinEventHook; COM-Interop in C# aufwendig. Kann später als Ergänzung dienen, nicht als Ersatz. |
| Throttle statt Debounce | **`trigger.throttleMs` (Default 500 ms)** — max 1 Capture pro HWND pro Zeitfenster | Klassisches Throttle-Pattern. Debounce („warte bis Ruhe") liefert bei aktivem Scrollen zu lange Pausen. |
| Per-App-Throttle | **`trigger.throttlePerAppSeconds` (Default 2 s)** | Zusätzliche Bremse: verhindert Capture-Bursts bei schneller Tab-Navigation in derselben App. |
| Hash-Dedup | **SHA-256 über `processName + contentText + title`, gespeichert pro HWND in `Dictionary<IntPtr, string>`** | Verhindert redundante Captures bei reinem Titel-Wechsel ohne Inhalts-Wechsel. Nicht über Screenshot-Hash (sonst flackern minimale Pixeländerungen). Verschiedene Fenster derselben App deduplizieren unabhängig voneinander (Diskussion 2026-07-04, Punkt 4). |
| Always-on-Top-Filter | **`WS_EX_TOPMOST` ist kein Ausschlusskriterium** | Viele legitime Apps sind AOT (Sticky Notes, Calculator, Chat). Filtern würde zu Lücken führen. |
| Modale Dialoge | **Eigenes Capture + Parent-Context als Frontmatter** | Bei Word „Speichern unter" o. ä. nur das Vordergrund-Fenster lesen, aber `parentHwnd`/`parentTitle`/`parentProcess` ins Frontmatter. Diskussion 2026-07-04, Option (a). |
| Tooltip/Notification-Filter | **Class-Blacklist** (`trigger.blacklist.windowClasses`) | Default: `tooltips_class32`, `NotifyIconOverflowWindow`. User-erweiterbar via Config. |
| Self-Capture-Filter | **PID-Vergleich** (`pid == Process.GetCurrentProcess().Id`) | Verhindert Aufzeichnung des eigenen Capture-/Konfig-Dialogs. |
| Child-HWND-Normalisierung | **`GetAncestor(hwnd, GA_ROOT)`** vor Throttle-Check | Button-Klick in Word triggert `EVENT_OBJECT_FOCUS` auf Button-HWND; normalisiert wird auf das Word-Fenster. |
| Outlook-Polling | **Bleibt in Spec 0004** unter `appReader.outlook.*` (`pollIntervalSeconds`, Default 60 s) | Mail-Stream ist inhärent polling-basiert (kein OS-Event für „neue Mail"). Konvention: app-spezifische Polling-Configs liegen unter `appReader.<reader>.*`, **nicht** unter `trigger.*` (Diskussion 2026-07-04, Punkt 3). |

### Auswirkungen

- Neue Spec: `specs/0005-trigger-pipeline.md` mit TR-1..9 (TR-1..6 aus
  MVP1-Scope bleiben gültig, +TR-7..9 für Tooltips/Modal/Child-HWND).
- Neue Top-Level-Config-Sektion `trigger.*` in `default-config.json`
  und `%APPDATA%/AiRecall/config.json`.
- Neue Komponente: `TriggerService` (IHostedService) in
  `AiRecall.ScreenCapture/Trigger/`.
- `EventHookThread` + `WorkerThread` + `Channel<TriggerEvent>` als
  Pipeline-Backbone.
- Capture-Writer-Frontmatter wird erweitert um optionale
  `parentHwnd`/`parentTitle`/`parentProcess` (bereits in `AppReaderResult.Extra`
  andeutungsweise vorhanden — wird konkretisiert).
- `recall record` CLI-Subcommand startet den Service und blockiert den
  Hauptthread bis Ctrl+C / SIGTERM.

### Verworfen

- **Stures Polling auf `GetForegroundWindow` (z. B. 1-Hz)**: Latenz,
  CPU-Last, verpasste kurze Events. Nur als Heartbeat-Fallback behalten.
- **Polling + OCR jedes Frames** (Screenpipe-artig): viel zu viel
  Rauschen + CPU/IO-Last für unseren Anwendungsfall (Recall-ähnliche
  semantische Erfassung, nicht Full-Framerate).
- **CDP/WebDriver-Trigger** (z. B. via Chrome Extension): Out of scope,
  Browser-spezifisch, würde Architektur in den Browser ziehen.
- **WPF-/Forms-spezifische Application-Idle-Events**: nur prozess-lokal,
  nicht systemweit.
- **Trigger-Pipeline als separate Library/DLL**: Anfangs in
  `AiRecall.ScreenCapture` geplant, dann doch in eigene
  `AiRecall.Trigger.dll` extrahiert (Martin 2026-07-04, Commit 11dea77),
  weil die MVP2-Tray-Icon-EXE denselben Code nutzen soll und ScreenCapture
  nicht braucht. Ref-Kette: Core → AppReader.Base → ScreenCapture →
  Trigger → Cli (zyklusfrei).

## 2026-07-03 — MVP1 Tech-Defaults

Offene Punkte aus `specs/0002-mvp1-scope.md` durch Martin bestätigt
(oder Default gesetzt):

| # | Thema | Entscheidung | Begründung |
|---|---|---|---|
| 1 | OCR-Engine | **Tesseract** (lokal, mehrsprachig) | Martin: "Build in OCR". Multi-OS-tauglich, kein Microsoft-Cloud-Zwang, MIT-kompatibel. |
| 2 | CLI-Library | **Manueller Switch** (wie vorhanden) | Nur 5 Commands geplant; System.CommandLine/Spectre wären unnötiger Ballast. Switch-Pattern in `Program.cs` ist < 30 Zeilen. |
| 3 | Logging | **Serilog 3.1.1** + Console + File | Strukturiertes Logging, tägliche Rolling-Files, Standard im .NET-Ökosystem. |
| 4 | Tests | **xUnit** (bereits eingerichtet) | Bereits im Skeleton, gut für parallele Tests + VS-Integration. |
| 5 | Ignore-Liste | **Blacklist-Ansatz** mit kleinen Seed-Patterns | Default-Config seeded `1Password`, `KeePass`, `Bitwarden`, ein paar Title-Patterns (`Sign in`/`Anmelden`/`Passwort`/`Fingerprint`) und zwei URL-Patterns (`banking`, `accounts.google.com`). User kann via `%APPDATA%/AiRecall/config.json` erweitern. |

### Auswirkungen

- **Tesseract 5.2.0** als NuGet-Paket in `AiRecall.ScreenCapture`. Tessdata-Dateien sind nicht im Repo, Anleitung in `README.md` und `specs/0003-active-window.md`.
- **SerilogSetup** liegt in `AiRecall.Cli/Logging/` (nicht in Core), damit Core keine Sink-Deps braucht.
- **Default-Config** wird als `default-config.json` ins Output kopiert (`<None CopyToOutputDirectory="PreserveNewest">` im csproj).
- **System.Drawing.Common** braucht `UseWindowsForms=true` in `AiRecall.ScreenCapture.csproj` (für `Bitmap`/`Graphics`).

### Verworfen

- Windows.Media.Ocr — eingeschränkte Sprachunterstützung auf älteren Windows-Versionen, weniger portabel.
- System.CommandLine — Beta, größerer Refactor für 5 Commands unnötig.
- Spectre.Console.Cli — nett, aber ebenfalls Overhead ohne klaren Gewinn bei aktuellem Scope.
- Microsoft.Extensions.Logging — weniger mächtig als Serilog für strukturierte Capture-Pipeline.
- NUnit / MSTest — kein Mehrwert vs. xUnit bei aktuellem Bedarf.

## 2026-07-02 — Initial-Setup-Entscheidungen (aus Spec 0002)

- Lizenz: MIT
- Zielgruppe: Personal (nur Martin)
- Plattform: Windows only (MVP1)
- Solution-Struktur: Hybrid (zentrale `ScreenCapture`-DLL + `AppReader.Base` + separate App-Reader-DLLs)
- Trigger: Window-Activate + Scroll + Click mit Throttle + Dedup (Polling-basiert)
- Persistenz: Files only (MD + PNG, kein SQLite in MVP1)
- Outlook-Variante: Classic (MAPI/COM)
- GitHub-Repo: `schirkan/ai-recall` (public)

## 2026-07-03 — Browser-Reader: CDP als opt-in Pfad

Browser-Reader Iter. 3 führt Chrome DevTools Protocol (CDP) als optionalen
zweiten Pfad ein, zusätzlich zur bestehenden UIA-Strategie.

| Aspekt | Entscheidung | Begründung |
|---|---|---|
| Master-Switch | `appReader.browser.cdp.enabled = false` (Default) | Browser muss mit `--remote-debugging-port` gestartet werden — das ist ein manueller Schritt, den wir per Default nicht erzwingen wollen. UIA-Pfad funktioniert ohne weitere Konfiguration und bleibt Default. |
| Endpoint | `http://localhost:9222` (Default, konfigurierbar) | Standard-Port für Chrome DevTools. Konfigurierbar für Remote-Browser oder Custom-Ports. |
| Timeout | `1500 ms` (Default, konfigurierbar) | Ausreichend für lokales Loopback bei großen Pages; Tests laufen mit 100–200 ms ohne Hänger. |
| HTML → MD | `ReverseMarkdown 3.13.0` (NuGet) | Reichhaltigere Strukturen als UIA-Plain-Text; etabliertes Projekt, MIT-Lizenz. |
| Strategie-Reihenfolge | CDP-Versuch zuerst, UIA-Fallback | Bei aktiviertem CDP liefert ein Roundtrip URL + strukturiertes Markdown; ohne aktiven CDP-Server fällt es ohne Verzögerung auf UIA zurück. |
| Firefox-Support | Bleibt vorerst out of scope | CDP-Pfad ist über Edge/Chrome erschlossen; Firefox-CDP kann später nachgezogen werden, ohne Architekturänderung. |

### Auswirkungen

- `ChromeDevToolsProtocolClient` bleibt `internal static` in `AiRecall.AppReader.Browser` (kein Public-API-Bruch).
- `BrowserConfig.Cdp` ist neu in `AppConfig.cs`; `BrowserAppReader` greift darauf zu und reicht es durch.
- Default-Config (`default-config.json`) hat den Block `appReader.browser.cdp` mit `enabled: false`.
- Spec 0004 wurde entsprechend angepasst: Browser-Strategie-Sektion, Configuration-Sektion, Out-of-Scope-Hinweis zu Firefox relativiert.

### Verworfen

- **CDP hart aktivieren als Default:** Würde bei Usern ohne explizit gestarteten Debugging-Port sofort scheitern oder den Browser-Prozess suchen müssen — UX-Risiko zu hoch für MVP1.
- **Permanente CDP-Instanz pro Capture:** Worker-Lifecycle unnötig; gelegentlicher Roundtrip reicht.
- **CDP in separater DLL (`AiRecall.AppReader.Cdp`):** Overhead für eine einzige Klasse mit klarer Zuordnung zum Browser-Reader; bleibt in `Browser`-DLL.

## 2026-07-03 — Browser-Reader: ReverseMarkdown-Konfiguration 1:1 über JSON

Alle öffentlichen Properties von `ReverseMarkdown.Config` (v3.13) werden über
`appReader.browser.markdown` als JSON konfigurierbar gemacht. Damit lässt
sich das HTML→Markdown-Verhalten des Browser-Readers zur Laufzeit anpassen,
ohne Code-Änderung.

| Aspekt | Entscheidung | Begründung |
|---|---|---|
| Konfigurations-Sektion | `appReader.browser.markdown` (Geschwister zu `cdp`) | Unabhängig vom CDP-Gate; spätere HTML-Quellen (z. B. Reader-Mock oder direkte Page-Quellen) sollen dieselbe Konfiguration nutzen können. |
| POCO-Design | Alle Felder als Nullable (`bool?`, `string?`, `List<string>?`) | Nicht gesetzte Felder werden **nicht** in `ReverseMarkdown.Config` geschrieben → Library-Defaults bleiben unangetastet. |
| Enums | Als JSON-Strings (case-insensitive, `Enum.TryParse`) | JSON hat keine native Enum-Repräsentation; Strings sind lesbar und ändern sich nicht, wenn die Library neue Enum-Werte einführt (alter Wert bleibt Default). |
| `ListBulletChar` (char) | Als String in JSON, nur erstes Zeichen übernommen | JSON hat keinen einzelnen `char`; Strings mit beliebigem Inhalt sind robuster (z. B. `\"->\"` → `'-'`). |
| Converter-Lifecycle | **Per-Call-Build** statt statisches Singleton | Jeder `Read` baut einen frischen `ReverseMarkdown.Converter` mit aktueller Config — vermeidet stale-state, wenn der User die Config zwischen Calls ändert (z. B. via Config-Reload). |
| Defaults in `default-config.json` | `unknownTags: \"PassThrough\"`, `githubFlavored: false`, `removeComments: true`, `smartHrefHandling: false`, `tableWithoutHeaderRowHandling: \"Default\"`, `listBulletChar: \"*\"`, `defaultCodeBlockLanguage: \"\"`, `whitelistUriSchemes: [http, https, ftp, ftps, mailto, tel]` | Setzt sinnvolle Defaults, die von der Library abweichen, wo wir das Verhalten explizit anders wollen (z. B. `listBulletChar: \"*\"` statt Library-Default `-`; `removeComments: true` weil `StripNoise` das sowieso schon macht). |

### Auswirkungen

- `AiRecall.Core/Configuration/AppConfig.cs` bekommt neue Klasse `MarkdownSettings`.
- `BrowserAppReader.cs` verliert den statischen `ReverseMarkdown.Converter`; neue `BuildConverter(MarkdownSettings?)`-Methode baut frischen Converter.
- `ConvertHtmlToMarkdown(html, maxChars, settings)` reicht die Settings durch.
- 11 neue Unit-Tests in `BrowserAppReaderTests` decken Default-Erhalt, alle Felder, Case-Insensitivity für Enums, ungültige Enum-Strings und End-to-End-Konvertierung ab.
- Spec 0004 wurde um den `markdown`-Block im Konfigurations-Abschnitt und ein neues Akzeptanzkriterium erweitert.

### Verworfen

- **Caching des Converters pro Settings-Hash:** Spart Mikrosekunden pro Call; lohnt den Komplexitäts-Aufwand (Hash-Berechnung, Dictionary-Lookup) bei unserer Call-Frequenz nicht. Read ist ohnehin O(HTML-Größe).
- **Converter-Konfiguration über Reflection auf private Felder:** Würde private Implementierungs-Details der Library koppeln; die offizielle `Config`-Property reicht.
- **Automatische Schema-Generierung aus der DLL:** Reflection auf die `ReverseMarkdown.dll` haben wir einmalig zur Verifikation gemacht (siehe `temp/reversemd-inspect/`); für die laufende Konfiguration ist die statische POCO-Definition klarer und typgeprüft.
