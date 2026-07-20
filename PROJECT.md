# AI Recall — PROJECT

> Projekt-Workspace: `projects/ai-recall/`
> GitHub: `schirkan/ai-recall` (public, MIT)
> Branch: `main`
> Tech-Stack MVP1: C# / .NET 8 (`net8.0-windows`)

## Aktueller Status

**MVP1 + MVP2 + MVP3 + Spec 0014 abgeschlossen** — MVP1: `active-window` + App-Reader-Foundation (Spec 0004: Browser [UIA + CDP opt-in + ReverseMarkdown 1:1], Notepad, Explorer, Documents [Word/Excel/PowerPoint mit COM-Interop Iter. 2/3], Outlook + Mail-Log [Iter. 3], PDF-Viewer) + Trigger-Pipeline (Spec 0005) + Async Document Conversion Pipeline (Spec 0007 v1.0). MVP2: Tray-Icon-EXE (Spec 0006/0008/0009 v1.0). **MVP3: Audio Notes (Spec 0013 v0.3 Update 8)** — Teams-Meeting-Polling (`MeetingPresencePoller`) + zweikanaliges Recording (Mic + Speaker-Loopback via NAudio.Wasapi 2.2.1) + Stereo-Concatenation + Background-Transkription mit Diarization (Azure Speech + Deepgram, parallel) + TriggerSupervisor-Integration. **Spec 0014 (Tray Audio Indicator + Manual Audio Control) v1.0** — Tray-Icon zeigt Audio-Recording-Indikator (🎙) mit Audio > Capture > Idle-Prioritaet, Tray-Menu-Items fuer manuelle Audio-Steuerung (Ctrl+Shift+R / Ctrl+Shift+T) mit Privacy-First-Gate (Audio.Enabled), Single-Active-Recording-Constraint, IRecordingControl-Provider-Pattern. **Iter. 3.1 (2026-07-20):** `trigger_source` im MD-Frontmatter parametrisiert via `RecordingTriggerSource`-Enum (Polling → `polling`, ManualAudio → `manual-audio`), Folder-Key-Schema mit `manual-`-Prefix fuer manuelle Aufnahmen. + 834/834 Tests gruen stabil (Counter/Async 5/5 stable, defensive WaitUntilAsync-Timeout-Erhoehung 2000→5000ms fuer Pre-existing Counter-Race-Flake).

- [x] Projektordner angelegt
- [x] Lokales Git-Repo initialisiert (`main`)
- [x] `.gitignore` angelegt
- [x] GitHub-Repo erstellt (`schirkan/ai-recall`, public)
- [x] Vision dokumentiert (`specs/0001-vision.md`)
- [x] MVP1-Scope dokumentiert (`specs/0002-mvp1-scope.md`)
- [x] Architektur-Entscheidungen bestätigt (MIT, Windows-only, Hybrid-DLLs, Trigger-Pipeline)
- [x] `LICENSE` (MIT) angelegt
- [x] `README.md` angelegt
- [x] `dotnet-8`-Solution-Skeleton (8 Projekte)
- [x] `recall list-windows` — lauffähig
- [x] `recall active-window` (Spec 0003) — lauffähig mit Ignore-Liste, OCR (Tesseract), SHA-256, YAML-Frontmatter
- [x] OCR mit echten Tessdata-Dateien manuell auf WindowsTerminal verifiziert (2882 Zeichen, 6 s)
- [x] Tech-Defaults final (Tesseract, Serilog, xUnit, manueller CLI-Switch, Blacklist) — siehe `DECISIONS.md`
- [x] **App-Reader-Architektur** (Spec 0004) implementiert:
  - `IAppReader` + `AppReaderResult` + `AppReaderContext` + `AppReaderRegistry` (Reflection-Loader)
  - `CaptureWriter.WriteContent()` für `*.content.md` neben Capture-MD
  - **Browser-Reader** (msedge, chrome): Tab-Titel + URL via UIA `ValuePattern`, Body via `TextPattern` (Smoke-Test steht aus, kein Browser in der Sandbox-Session)
    - **Browser-Reader Iter. 3 — CDP als Opt-in:** zusätzlich Chrome DevTools Protocol
      (`appReader.browser.cdp.enabled`, Default `false`). Erfordert Browser-Start mit
      `--remote-debugging-port`. HTML → MD via `ReverseMarkdown 3.13.0`.
      DisplayName aktualisiert: „Browser (UIA; CDP opt-in via config)".
      Default-Verhalten bleibt UIA — bestehende Smoke-Tests laufen weiter grün.
  - **Notepad-Reader**: Buffer via Win32 `WM_GETTEXT` + rekursive Edit-Control-Suche via `EnumChildWindows`, Filename-Parsing (En-Dash/Em-Dash-tolerant) — Smoke-Test grün (15 Zeilen, 363 Zeichen aus echtem Notepad)
  - **Explorer-Reader** (neu): aktueller Pfad aus Fenster-Titel, Hyphen/En-Dash/Em-Dash-tolerant, Special-Folder-Liste (Desktop/Dieser PC/Schnellzugriff/…) → null — Smoke-Test grün (echtes Explorer-Fenster liefert Content-MD)
- [x] Tests: **673/673 grün** (Stand Bug-Bash 2026-07-06 Teil 2, Commit `d245dd2`; 331 Spec-0007-Stand + 100 Outlook-Reader Iter. 3: 14 EntryStore + 20 AutoRuleDetector + 16 TitleParser + 23 BodyToMarkdown + 27 OutlookAppReader [18 Facts + 1 Theory mit 9 InlineData] + 64 OneNote-Reader Spec 0010: 5 Config + 8 ComInterop + 30 PageXmlToMarkdown + 21 AppReader + 61 Teams-Reader Spec 0011: 5 Config + 29 UIA + 10 CDP + 17 Reader)
- [x] **Documents-Reader Iter. 2 (Martin 2026-07-04) — COM-Interop:**
  - Neue Klasse `OfficeComInterop` (late binding via ProgID + P/Invoke
    auf `oleaut32.dll!GetActiveObject` — `Marshal.GetActiveObject` ist
    in .NET 8 SDK 8.0.422 nicht verfuegbar).
  - Word: `ActiveDocument.FullName` + `Range.Text` (Plain-Text in Code-Block)
  - Excel: `ActiveWorkbook.ActiveSheet.UsedRange` als Markdown-Tabelle
  - PowerPoint: alle `Slides` mit Text-Frames als `### Slide N`-Liste
  - Bei COM-Erfolg: `filePath` im Frontmatter + Inhalt unter
    `## Document content (via COM)` in `content.md`.
  - Fallback: bisherige Title+UIA-Logik. COM-Tests als
    `[Trait("Integration", "Office")]` (in Sandbox skipped).
- [x] **PDF-Viewer-Reader** (Martin 2026-07-04, neue DLL `AiRecall.AppReader.Pdf`):
  - Process-Liste konfigurierbar (`appReader.pdf.processes`, Default:
    Adobe/Sumatra/Foxit/PDFXChange/Edge/Chrome).
  - Title-Parsing: Filename + voller Pfad + Page-Nr (Sumatra/PDF-XChange-Style).
  - **Iter. 1**: kein PDF-Inhalt — `PdfPig` (NuGet) ist Kandidat fuer Iter. 2.
- [x] **Office-COM Iter. 3 — Pro-Instanz-Filename-Match** (Martin 2026-07-04):
  - `OfficeComInterop.MatchesExpectedFilename(fullPath, expectedFilename)`
    als internal static Helper (eigenständig unit-testbar).
  - Reader ruft `ParseTitle(window.Title)` zuerst und übergibt den erwarteten
    Filename an COM. Bei Mismatch (`ActiveDocument` einer anderen Instanz)
    → `null` → Fallback auf UIA+Title. **Verhindert falschen Pfad bei
    mehreren parallelen Office-Instanzen** (z. B. zwei Word-Fenster mit
    unterschiedlichen Dokumenten).
  - 8 neue Unit-Tests (`OfficeComInteropFilenameMatchTests`):
    null/empty expected, match, case-insensitive, mismatch, empty/null fullPath,
    unsaved-Doc-Sonderfall.
- [x] **Browser-Reader Iter. 4 — ReverseMarkdown 1:1 via JSON:** neue Sektion
  `appReader.browser.markdown` mappt alle 8 öffentlichen `ReverseMarkdown.Config`-Felder
  (`unknownTags`, `githubFlavored`, `removeComments`, `whitelistUriSchemes`,
  `smartHrefHandling`, `tableWithoutHeaderRowHandling`, `listBulletChar`,
  `defaultCodeBlockLanguage`) per POCO. Per-Call `BuildConverter()` statt statischem
  Converter — so greifen Reload-Änderungen sofort. Tests 98/98 grün
  (+11: `BuildConverter_NullSettings_*`, `_EmptySettings_PreservesLibraryDefaults`,
  `_AllSettings_AppliesAllValues`, `_UnknownTags_IsCaseInsensitive`,
  `_UnknownTags_InvalidString_LeavesDefault`, `_TableWithoutHeaderRow_*`,
  `_ListBulletChar_TakesFirstCharOnly`, `_WhitelistUriSchemes_EmptyList_*`,
  `ConvertHtmlToMarkdown_*` End-to-End, `AppConfig_BrowserConfig_HasMarkdownSettings`).
- [x] **Trigger-Pipeline (Spec 0005) komplett implementiert:**
  - `AiRecall.Trigger.dll` (eigene Assembly, Namespace `AiRecall.Trigger`):
    `ITriggerService`, `TriggerService` (Orchestrator), `WinEventHookDetector`
    (`SetWinEventHook` out-of-context, Message-Loop auf eigenem Thread),
    `HeartbeatThread` (periodisches `GetForegroundWindow`, Fallback),
    `TriggerWorker` (Pipeline-Schritte 1–12), `TriggerEvent`/`TriggerKind`,
    `HwndDedup` (Hex-Persistenz, `0xDEADBEEF`).
  - `AiRecall.Core.Util.{Throttle<TKey>, Dedup}` (generisch, gemeinsam
    genutzt).
  - `AiRecall.Core.Windows.WindowInfoLookup` (HWND → WindowInfo).
  - `CaptureWriter.Write` um `parentWindow: WindowInfo?` erweitert
    (Spec 0005 §Modale Dialoge, Option (a): nur Foreground-Capture +
    `parentHwnd`/`parentTitle`/`parentProcess` im Frontmatter).
  - `TriggerWorker` Modal-Dialog-Detection via
    `GetAncestor(GA_ROOTOWNER) != rootHwnd`.
  - CLI: `recall record` mit `--headless` (MVP2-Tray-EXE) und
    `--trigger-mode=events|polling|both`. `recall status` (neu):
    Diagnose (Config-Pfade, heutige Captures nach Prozess, aktive
    Trigger-Config), `--json` für MVP2-IPC.
  - Alte `CapturePipeline`/`EventDetector`/`Models.cs` (Polling-basiert)
    entfernt.
  - 91 neue Tests (Schritte A–G).
- [x] **App-Reader: Outlook (Spec 0004 Iter. 3 abgeschlossen)** — Mail-Log via COM (late binding):
  - Neue DLL `AiRecall.AppReader.Outlook` mit `OutlookAppReader` (Dual-Modus: Inspector/Explorer-Selection + Background-Polling)
  - `OutlookComInterop` (P/Invoke `oleaut32!GetActiveObject`) liefert `MailSnapshotFromCom`
  - `OutlookEntryStore` (EntryID-Dedup, atomic `File.Replace`)
  - `OutlookAutoRuleDetector` (4 Bedingungen, ≥2 Hits = suspect)
  - `OutlookTitleParser` (Folder-View vs. Inspector)
  - `OutlookBodyToMarkdown` (HTML→MD, Outlook-spezifisch, custom)
  - Polling alle 60 s (konfigurierbar, `outlook.pollIntervalSeconds`)
  - 109 neue Tests (499 → 525 grün)
- [x] **OneNote App-Reader (Spec 0010 abgeschlossen)** — OneNote Page-Log via COM (late binding):
  - Neue DLL `AiRecall.AppReader.OneNote` mit `OneNoteAppReader` (Read only, kein Background-Poll — Page-orientiert, nicht Stream-orientiert)
  - `OneNoteComInterop` mit 4-stufiger Active-Page-Strategie (Stage 1: `Windows.CurrentWindow.CurrentPageId`, Stage 2: `Windows`-foreach + `Active`, Stage 3: `GetHierarchy(hsPages)` + `isCurrentlyViewed="true"`, Stage 4: null-Fallback); P/Invoke `oleaut32!GetActiveObject` (analog Outlook) + `Marshal.ReleaseComObject`-Cleanup pro COM-Objekt.
  - `OneNoteComException` mit HRESULT-Klassifikation (fatal: hrXmlIsInvalid/hrRpcFailed2; transient-retry: hrRpcUnavailable/hrCOMBusy/hrServerCallRetried/hrObjectMissing). 3×Retry mit 500ms Backoff.
  - `OneNotePageXmlToMarkdown` (Pure-Function XML→MD, ~411 LoC): `one:OE`/`T`/`Image`/`Tag`/`Table`/`InkContent`/`InsertedFile`-Mapping, HTML-Entity-Decode via `HttpUtility.HtmlDecode`, Bullet-Indent via `style="list"`, `IncludeImages`/`IncludeTags`-Flag-Steuerung, `xs2013`-Schema fix.
  - `OneNoteConfig` in `AppConfig` mit `Enabled`, `MaxContentKB`, `IncludeImages`, `IncludeTags`, `HierarchyDepth` (PageOnly|PageAndSection|PageAndSectionAndNotebook), `ActivePageStrategy` (WindowsApi|HierarchyXml|Auto), `PollIntervalSeconds` (=0 Read-only), `SkipNotebookPatterns`.
  - Persistenz-Schema: `capture/yyyy-MM-dd/onenote/HHmmss-{pageIdShort}.md` mit YAML-Frontmatter (kind=onenote-page, pageId, pageTitle, [section/sectionId], [notebook/notebookId], lastModified, strategy, includeImages, includeTags, attachments, source=onenote-com, reader, readerVersion).
  - 64 neue Tests (525 → 589 grün): 5 OneNoteConfig + 8 OneNoteComInterop (XML-Parser) + 30 OneNotePageXmlToMarkdown + 21 OneNoteAppReader (Public-Surface + ShortId-Theory + Internal-BuildFullMarkdown).
- [x] **Teams App-Reader (Spec 0011 abgeschlossen)** — Modern Teams Chat-Log via UIA + CDP opt-in:
  - Neue DLL `AiRecall.AppReader.Teams` mit `TeamsAppReader` (Read only, kein OnPoll — Window-orientiert, nicht Stream-orientiert)
  - 3-Strategy-Active-Chat-Auflösung: CDP bevorzugt (wenn `UseCdpIfAvailable=true` + `/json/version` HTTP-Discovery erfolgreich) → UIA (TextPattern-Walk auf sichtbares Chat-Panel, immer verfügbar) → Title-Fallback (nur Title + Hinweis-Body)
  - `TeamsUiaReader` (internal static, ~280 LoC): `ParseWindowTitle` für Format `"Chat | Alice - Microsoft Teams"`/`"Channel | #general - Microsoft Teams"`/etc., `IsTeamsChatWindow` + `TryGetActiveChat` via UIA TextPattern mit heuristischer Sender-Separation
  - `TeamsCdpReader` (internal static, ~270 LoC): `HttpClient.GetAsync("/json/version")` + `ClientWebSocket.ConnectAsync` + `Runtime.evaluate(document.title + Chat-Panel-DOM)`; Cancellation/Timeout via `linkedCts.CancelAfter(CdpTimeoutMs)`
  - `TeamsConfig` in `AppConfig` mit `Enabled`, `MaxContentKB=512`, `UseCdpIfAvailable=true`, `CdpEndpoint=http://localhost:9222`, `CdpTimeoutMs=1500`, `PreferredStrategy=Auto`, `PollIntervalSeconds=0`, `SkipChatPatterns`, `IncludeSenderPatterns`
  - Persistenz-Schema: `capture/yyyy-MM-dd/ms-teams/HHmmss-{chatIdShort}.md` mit YAML-Frontmatter (kind=teams-chat, chatType, chatTitle, chatIdShort, source=teams-cdp|teams-uia|teams-title-fallback, strategy, senderCount, messageCount, isSelfIncluded, capturedAt, reader, readerVersion); `chatIdShort` = erste 8 Zeichen einer SHA256-Hash über `Title|Type|SenderSet` (deterministisch, "0" bei Empty-Input)
  - 61 neue Tests (589 → 650 grün): 5 Config + 29 UIA (9 Facts + 4 Theories mit insgesamt 20 InlineData) + 10 CDP + 17 Reader (10 Facts + 1 Theory mit 7 InlineData)
- [x] App-Reader: Word/Excel/PowerPoint (Spec 0004 Iter. Documents — UIA-only, Office nicht erforderlich; Tests grün, e2e-Smoke gegen Office ausstehend)
- [x] Trigger-Pipeline (`recall record`) — **komplett, Spec 0005 abgeschlossen**
- [x] **Async Document Conversion Pipeline (Spec 0007 v1.0 abgeschlossen)**
  - Neue DLL `AiRecall.Conversion` mit `DocumentConverter` (OpenXml/PdfPig/ReverseMarkdown/Plain)
  - `ConversionWorker` (in-process `Channel<string>`, Background-Task) — async OCR + DocumentConverter
  - `IOcrEngine` Interface + `TesseractOcrEngineAdapter` (async via `Task.Run`) + `NullOcrEngine`
  - `CaptureWriter.WritePending` + `UpdateConversionStatus` Frontmatter-Update-Pattern
  - `recall convert` Subcommand (Recovery für gecrashte Sessions)
  - Word/Excel/PowerPoint-Reader refactored zu **dünn** (`IsThinReader=true`, `ContentMarkdown=Platzhalter`)
  - `TriggerService` integriert `ConversionWorker` als Sink
  - **Test-Count: 331/331 grün** (vorher 271, +60)
  - Commits: `3a98e04`, `f176bea`, `9c7d9b5`, `de83a7e`, `84afab7`
  - Martin-Direktiven umgesetzt: Pandoc raus (Performance > Format-Coverage), `Channel<string>` statt FileSystemWatcher, OCR ebenfalls async, kein Legacy-Handling
- [x] **MVP2 Tray-Icon-EXE (Spec 0006 v1.0 abgeschlossen — inkl. 0008 Logviewer + 0009 Settings-Dialog)**
  - Neue DLL `AiRecall.TrayApp` (WinForms, .NET 8 windows) mit `AiRecall.TrayApp.exe`
  - SingleInstance-Mutex + `NotifyIcon` + `ContextMenuStrip` (Start/Stop, Live Logviewer, Settings, Quit)
  - `TriggerSupervisor` (in-process `ITriggerService`-Wrapper, Start/Stop/Restart, StateChanged-Event, CrashCount)
  - `InMemoryLogSink` (Serilog-Custom-Sink mit Ringbuffer 10.000 + EventEmitted) + `LogviewerSession` (Filter + Snapshot)
  - `LogviewerWindow` (DataGridView mit Level/Search-Filter, Pause/Clear/Auto-Scroll, Color-Coding)
  - `SettingsDialog` (TreeView + dynamische Form-Generierung: bool/int/string/enum/List-Editoren)
  - `ConfigSchemaReflection` (Reflection auf `AppConfig` POCOs) + `ConfigSerializer` (atomic write mit .bak-Backup) + `PropertyEditorFactory`
  - Hot-Reload: Settings Save → `TriggerSupervisor.Restart(newConfig)` (kein Process-Kill, kein Cold-Start)
  - **Test-Count: 416/416 grün** (vorher 331, +85)
  - Commits: `5ab077a`, `cff2b50`, `d9ffd11`, `12ced87`, `dc14dc0`, `da6586d`, `c23d3ca`, `e80d8fc`, `875ae98`
  - Architektur-Korrektur (Martin 22:29): in-process statt Subprozess — ProcessSupervisor + MMF-IPC entfallen komplett
  - Martin-Direktive umgesetzt: WinForms (kein WPF), in-process `ITriggerService`, Hot-Reload via Restart
- [x] **Bug-Bash 2026-07-06 Teil 2 (Commit `d245dd2`)** — 6 Themen in einem Cluster-Commit:
  - **Trigger-Pipeline v2**: Periodischer Capture-Thread (`PeriodicCaptureThread`) als neue Trigger-Quelle `TriggerKind.Periodic`. Konsolidiert mit `HeartbeatThread` über gemeinsame interne `PollThread`-Klasse (I-24). Konfiguration in `screenRecorder.periodicCaptureMs` (Default `0` = deaktiviert) + `screenRecorder.ignoreApps/Urls/WindowTitles` für Vorab-Filter. 86 neue Tests.
  - **ConfigSchemaReflection rekursiv** (I-18): `BuildSection` läuft rekursiv über POCO-Properties, `IsExpandableConfigType` filtert expandierbare POCOs. Vorher: Sub-Sub-Konfigs (`browser.cdp`, `trigger.winEvents`, `trigger.blacklist.windowClasses`) waren im Tree unsichtbar. Nachher: echte Baumstruktur.
  - **77 Description-Attribute** (I-19/I-25): `[Description("...")]` aus `System.ComponentModel` auf `AppConfig` + alle Sub-POCOs verteilt. SettingsDialog zeigt Description als 1-zeiliges Label **unter** dem Editor. Property-Name IST der Label (camelCase → Title Case), keine separaten `[DisplayName]` mehr.
  - **SettingsDialog UX** (I-16, I-25): Splitter proportional zur Form-Breite (vorher fester Pixelwert), `SplitterMoved`-Event resized Editor-Panel sauber, Description-Label passt sich an. Editor + Description + Padding pro Property (`descGap = 2`, `descHeight = 16`).
  - **EmojiIconFactory** (I-UE): Color-Emoji via Win32 `TextRenderer.DrawText` für `ToolStripMenuItem.Image`. Render-Pfad mit weißem Hintergrund + Alpha-Mask-Trick (GDI+ rendert COLR/CPAL auf transparentem Bitmap **ZU LEER**, daher der Umweg). 10 Menu-Icons jetzt via Factory.
  - **TessdataManager** (Spec 0012-Vorbereitung): `AiRecall.Tessdata.dll` mit Auto-Download von `deu.traineddata`+`eng.traineddata` aus `tessdata_fast` GitHub-Release. Vorbereitung für Spec 0012 (Modal-Dialog beim ersten Start, geplant v0.1). 187 neue Tests.
  - **Trigger-Pipeline v2 + Bug-Bash-Teil-2-Effekte**: Test-Count **650 → 673/673 grün** (Spec 0005 TriggerKind.Periodic + ConfigSchemaReflection rekursiv + WindowPlacement BottomRight + EmojiIconFactory + TessdataManager + PeriodicCaptureThread + weitere).
- [x] Push auf `origin/main`

## Projektziel (Kurzfassung)

Lokales Windows-Tool, das die Bildschirmarbeit kontinuierlich aufzeichnet
und in durchsuchbare Markdown-Extraktionen + Screenshots überführt.
Vergleichbar mit Windows Recall / Screenpipe / rowboat, aber
Open Source, MIT, lokal-only und mit Fokus auf Office-Workflows
(Outlook Classic, Word, Excel, Browser).

Ausführlich: `specs/0001-vision.md`

## Project Files

| Datei/Ordner                                                                         | Zweck                                                                                                                                             |
| ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| `LICENSE`                                                                            | MIT-Lizenztext                                                                                                                                    |
| `README.md`                                                                          | GitHub-Readme (Status, Features, Quick Start, Architektur, OCR-Setup)                                                                             |
| `DECISIONS.md`                                                                       | Architektur- und Stack-Entscheidungen mit Datum/Begründung                                                                                        |
| `.gitignore`                                                                         | Generische + .NET + Capture/Laufzeit-Ausschlüsse                                                                                                  |
| `PROJECT.md`                                                                         | Diese Datei — Current Status, Project Files                                                                                                       |
| `AiRecall.sln`                                                                       | Solution-Datei; absoluter Pfad: `C:\Users\Admin\.openclaw\workspace\projects\ai-recall\AiRecall.sln`                                                |
| `global.json`                                                                        | .NET SDK-Pin (8.0.422, `latestFeature`)                                                                                                           |
| `specs/`                                                                             | Spezifikationen, Roadmaps                                                                                                                         |
| `specs/0001-vision.md`                                                               | Vision + Roadmap MVP1/MVP2/MVP3                                                                                                                   |
| `specs/0002-mvp1-scope.md`                                                           | MVP1-Scope, User Stories, Architektur, Config                                                                                                     |
| `specs/0003-active-window.md`                                                        | `recall active-window` Command-Spec                                                                                                               |
| `specs/0004-app-reader.md`                                                           | App-Reader-Architektur (eine DLL pro App, Outlook-Mail-Log)                                                                                       |
| `specs/0005-trigger-pipeline.md`                                                     | Trigger-Pipeline (WinEventHook + Heartbeat + Worker)                                                                                              |
| `specs/0006-mvp2-tray-exe.md`                                                        | MVP2 Tray-Icon-EXE (v1.0 abgeschlossen, inkl. 0008+0009)                                                                                          |
| `specs/0007-async-conversion.md`                                                     | Async Document Conversion Pipeline (v1.0 abgeschlossen)                                                                                           |
| `specs/0008-live-logviewer.md`                                                       | Live Logviewer Window (v1.0 abgeschlossen)                                                                                                        |
| `specs/0009-settings-dialog.md`                                                      | Settings-Dialog JSON-Editor (v1.0 abgeschlossen)                                                                                                  |
| `specs/0010-onenote-app-reader.md`                                                   | OneNote App-Reader (Spec 0010, 4-stufige Active-Page-Strategie via COM late-binding, Read-only)                                                   |
| `specs/0011-teams-app-reader.md`                                                     | Teams App-Reader (Spec 0011, Modern Teams only, UIA + CDP opt-in, 3-Strategy-Auflösung)                                                           |
| `specs/0012-tessdata-first-run.md`                                                  | Tessdata First-Run Download (Spec 0012, geplant v0.1 — Modal-Dialog beim ersten Start wenn Tesseract-tessdata fehlt)                                |
| `specs/0013-audio-notes-mvp3.md`                                                   | **MVP 3 Audio Notes (Spec 0013, v0.3 ABGESCHLOSSEN Update 8)** — Teams-Meeting-Polling + zweikanaliges Audio-Recording (Mic + Speaker-Loopback) + Background-Transkription mit Diarization (Azure Speech + Deepgram). Implementiert in Iter. 1-4 (Commits `88cf4f7`/`787c151`/`8d77e7a`/`725f352`/`c278616`/`b21411a`/`c292b25`/`56965c6`/`2d79f7f`/`ff97767`/`92480e7`); siehe DECISIONS.md |
| `specs/0014-tray-audio-indicator.md`                                              | **Tray Audio Indicator + Manual Audio Control (Spec 0014, v1.0 ABGESCHLOSSEN 2026-07-14)** — Tray-Icon zeigt Audio-Recording-Indikator (🎙) mit Audio > Capture > Idle-Prioritaet; Tray-Menu-Items fuer manuelle Audio-Steuerung (Ctrl+Shift+R / Ctrl+Shift+T); Privacy-First-Gate (`Audio.Enabled`); Single-Active-Recording-Constraint; IRecordingControl-Provider-Pattern (`Func<IRecordingControl?>?`). Implementiert in Iter. 1+1b+2+3 (`a8a70e3`/`07575bc`/`1a715a3`/`1d6ef22`) + Flake-Fix `2814d5b` + Doc-Cluster (dieser Commit) |
| `src/`                                                                               | .NET-Solution-Projekte                                                                                                                            |
| `src/AiRecall.Core/`                                                                 | Models, Configuration, Persistence, Util, Windows                                                                                                 |
| `src/AiRecall.ScreenCapture/`                                                        | Win32 Window/Screenshot/OCR (kein Trigger mehr)                                                                                                   |
| `src/AiRecall.Trigger/`                                                              | **Trigger-Pipeline-DLL (Spec 0005): WinEventHook + Heartbeat + Worker + Service**                                                                 |
| `src/AiRecall.Conversion/`                                                           | **Async Document Conversion (Spec 0007)**: `DocumentConverter` + `ConversionWorker` + `IOcrEngine`/`TesseractOcrEngineAdapter`/`NullOcrEngine`    |
| `src/AiRecall.AppReader.Documents/`                                                  | **Word/Excel/PowerPoint-Reader** (Spec 0004 Iter. Documents) — UIA-only                                                                           |
| `src/AiRecall.TrayApp/`                                                              | **MVP2 Tray-Icon-EXE (Spec 0006 v1.0)**: `NotifyIcon` + `TriggerSupervisor`-Wiring + `LogviewerWindow` (Spec 0008) + `SettingsDialog` (Spec 0009) |
| `src/AiRecall.AppReader.Base/`                                                       | `IAppReader`-Interface + Basisklassen                                                                                                             |
| `src/AiRecall.AppReader.{Browser,Outlook,OneNote,Teams,Documents,Notepad,Explorer}/` | App-Reader-DLLs                                                                                                                                   |
| `src/AiRecall.Cli/`                                                                  | `recall`-Kommando + Serilog-Setup + Default-Config                                                                                                |
| `tests/AiRecall.Core.Tests/`                                                         | xUnit-Tests für Core + Trigger + App-Reader + Conversion (416 Tests)                                                                              |
| `capture/`                                                                           | (Laufzeit, gitignored) Screenshots + MD-Extraktionen                                                                                              |
| `logs/`                                                                              | (Laufzeit, gitignored) Serilog Rolling-Logs                                                                                                       |
| `tessdata/`                                                                          | (Laufzeit, gitignored) Tesseract-Sprachdateien (manuell)                                                                                          |
| `.github/workflows/release.yml`                                                      | **CI/CD Release-Pipeline (Spec 0017 v1.0, 2026-07-20):** Tag-getriggerter Build+Test+ZIP+GitHub-Release-Workflow                                                                                                                  |

## CI/CD

- **Plattform:** GitHub Actions
- **Workflow-Datei(en):** `.github/workflows/release.yml`
- **Trigger:** Tag-Push mit Pattern `v*` (z. B. `v0.1.0-rc1`, `v1.2.3-beta.1`) — kein PR-Build, kein Push-auf-main-Build. Zusätzlich `workflow_dispatch` für Notfall-Rebuilds.
- **Was wird gebaut:** `AiRecall.TrayApp` (Spec 0006, MVP2-Tray-Icon-EXE) als verteilbares Multi-File-Binary (`dotnet publish`).
- **Output / Artefakte:** GitHub-Release mit Auto-generierten Release-Notes (`generate_release_notes: true`) und ZIP-Asset `AiRecall-{version}-win-x64.zip` als Release-Anhang.
- **Pipeline-Struktur:** 2 Jobs — `build-and-test` (Restore + Build Release + xUnit-Tests + Publish + ZIP + Artifact-Upload) → `release` (`needs: build-and-test`, GitHub-Release erstellen). Tests-MUSS-grün-Gate vor Release-Erstellung.
- **Runner:** `windows-latest` (Pflicht, weil `net8.0-windows` Win32-P/Invoke benötigt; `ubuntu-latest` würde Build brechen).
- **.NET-Version:** `actions/setup-dotnet@v4` mit `dotnet-version: 8.0.x` (kompatibel mit `global.json`-Pin 8.0.422 + `latestFeature`-Rollforward).
- **Externe Actions:** `actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/cache@v4` (NuGet), `actions/upload-artifact@v4`, `actions/download-artifact@v4`, `softprops/action-gh-release@v2`.
- **Letzter bekannter Lauf:** Spec-Erstellung 2026-07-20, Erst-Tag `v0.1.0-rc1` (Pipeline-Lauf wird nach Tag-Push dokumentiert).
- **Spec:** `specs/0017-ci-cd-release-pipeline.md`
- **Decisions:** siehe `DECISIONS.md` Eintrag „2026-07-20 — CI/CD Release-Pipeline (Spec 0017 v1.0)"

## Konventionen

Folgen `projects/PROJECT-RULES.md`:
- Specs in `specs/`
- Externe Doku in `context/`
- Decisions in `DECISIONS.md`
- Current Status hier aktuell halten

## Offene Punkte (für MVP1)

1. **Spec 0004 (App-Reader) — Martin-Review ausstehend**
   - Eine DLL pro App: Browser ✅, Notepad ✅, Explorer ✅, Outlook ⏳ (mit Mail-Log + Auto-Regel-Setting), Documents (Word/Excel/PowerPoint) ⏳
   - Persistenz als zusätzliche `*.content.md` neben dem Capture-MD ✅
   - **Browser-Reader CDP opt-in erledigt (Iter. 3)** — siehe DECISIONS.md-Eintrag 2026-07-03
   - **Browser-Reader ReverseMarkdown 1:1 erledigt (Iter. 4)** — siehe DECISIONS.md
2. **Trigger-Pipeline (Spec 0005) — abgeschlossen (Schritte A–G):** ✅
   - `AiRecall.Trigger.dll`, `ITriggerService`/`TriggerService`
   - `WinEventHookDetector` (out-of-context) + `HeartbeatThread` (Fallback)
   - `TriggerWorker` (Pipeline 1–12) + `HwndDedup` + `Throttle<TKey>`
   - CLI: `recall record --headless --trigger-mode=events|polling|both`
   - CLI: `recall status [--json]` (Diagnose + MVP2-IPC-Vorbereitung)
   - Modal-Dialog-Frontmatter (`parentHwnd`/`parentTitle`/`parentProcess`)
3. **UIA-Fallback:** Wenn OCR zu schlecht, Windows UIA als zusätzliche Textquelle (oder als App-Reader-Default)
4. **OCR-Tessdata-Doku:** README-Schritt-für-Schritt-Anleitung zum Download ✅ (PS-One-Liner drin)
5. **MVP2: Tray-Icon-EXE** ✅ **abgeschlossen (Spec 0006 v1.0)**: Vollwertige
   Windows-Anwendung mit Notification-Area-Icon zum Steuern von
   `ITriggerService` (Start/Stop/Status). In-process-Architektur, kein Subprozess.
   Live Logviewer (Spec 0008) + Settings-Dialog (Spec 0009) inkludiert.
   CLI bleibt für Scripts erhalten.
6. **Spec 0007 Folge-Iterationen (nach v1.0)**:
   - PDF-Verschlüsselung-Handling + mehrseitige Dokumente
   - OCR-Preprocessing (Binarization/Deskew) optional
   - ✅ `*.conversion.md` ↔ `*.content.md` Vereinheitlichung (Bug-Bash I-17, 2026-07-06): ConversionWorker schreibt Document/OCR/UIA in-place in die Capture-MD unter `## Content` (ersetzt Pending-Platzhalter). Kein separates `*.conversion.md` mehr. Ein MD pro Capture.
7. **OCR tessdata-Packaging** (nach Bug-Bash I-14): Auto-Download von `deu.traineddata`+`eng.traineddata` beim ersten Start in `%LOCALAPPDATA%\AiRecall\tessdata`, oder tessdata-Files in den Installer bündeln. → Spec 0012 geplant v0.1 (Modal-Dialog beim ersten Start mit Auto-Download-Option).
8. **MVP 3 (Audio Notes) — Spec 0013 v0.3 (Update 8) ✅ ABGESCHLOSSEN + GEPUSHT**:
   Implementation komplett in 11 Commits (Iter. 1-4):
   - **Iter. 1** (`88cf4f7`): Recording-Block. `AudioConfig` + `AudioDeviceProvider`
     (NAudio.Wasapi 2.2.1) + `IAudioRecorder` + `WasapiAudioRecorder` +
     `RecordingSession` (Lifecycle `Created→Recording→Recorded|Failed`,
     `IAsyncDisposable`). Privacy-First Default `Audio.Enabled=false`.
     +23 Tests.
   - **Iter. 2** (`787c151`): **Polling-basierte Meeting-Anwesenheitserkennung**.
     `MeetingPresencePoller` (5-s-`PeriodicTimer`, Edge-Detection,
     Start-Debounce `MinMeetingDurationSeconds=30s`). `MeetingPresenceSnapshot`
     + `MeetingPresenceStateChangedEventArgs`. `TeamsConfig.AutoRecordMeetings
     /MinMeetingDurationSeconds/PresencePollIntervalSeconds`. +14 Tests.
   - **Iter. 3a** (`8d77e7a`): Stereo-Concatenator. Mono+Loopback → `combined-stereo.wav`.
     NAudio 2.2.1 nutzt `ToSampleProvider().Read(float[],…)`. +7 Tests.
   - **Iter. 3b** (`725f352`): Provider-Interface `ITranscriptionProvider` +
     DTOs (`TranscriptionOptions`, `TranscriptionResult`, `TranscriptionSegment`,
     `TranscriptionProgress`, `AudioTranscriptionTask`) +
     `TranscriptionConfig` + `TranscriptionConfigResolver` (Fallback
     `azure-speech`). +12 Tests.
   - **Iter. 3c** (`c278616`): `AzureSpeechTranscriptionProvider` (NuGet
     `Microsoft.CognitiveServices.Speech 1.40.0`, Speaker-Label `C0-S1`).
     +9 Tests.
   - **Iter. 3d** (`b21411a`): `DeepgramTranscriptionProvider` (HttpClient,
     `https://api.deepgram.com/v1/listen?model=nova-2&language=…&diarize=true&
     smart_format=true`). +13 Tests.
   - **Iter. 3e** (`c292b25`): `TranscriptionWorker` (Init, Channel-basiert,
     Concatenate→Provider→MD-Update→Cleanup). `MetadataUpdater` schreibt
     `transcript_status` + `## Transcription`-Block.
   - **Iter. 3e-r** (`56965c6`): `TranscriptionWorker` analog
     **ConversionWorker-Pattern** (Spec 0007) refactored: ctor-auto-start
     Background-Pool, Counter (`PendingCount/CompletedCount/FailedCount`),
     `IDisposable`, `ScanForPendingTranscriptions` für Crash-Recovery.
   - **Iter. 3g** (`2d79f7f`): **Trigger-Wiring**: `MeetingTrigger` (Poller
     → `RecordingSession.Start/Stop` → `TranscriptionWorker.Enqueue`).
     Test-Hook `MeetingPresencePoller.RaisePresenceChangedForTest()`. +6 Tests.
   - **Iter. 3f** (`ff97767`): `TranscriptionConnectionTester` (1-s-Silent-Audio
     → Provider → `ConnectionTestResult`). Deckt Netzwerk/Endpoint,
     API-Key, Audio-Format ab. +8 Tests.
   - **Iter. 4** (`92480e7`, **aktuell letzter Commit**): `TriggerService`
     integriert `MeetingTrigger` (analog `ConversionWorker`-Pattern).
     Neue Factory `MeetingTriggerFactory.TryCreateDefault(config, logger)` mit
     Privacy-First-Gate (3 Gates: `Audio.Enabled` / `Teams.AutoRecordMeetings`
     / `AppReader.Teams.Enabled`). `MeetingTrigger` jetzt
     `IDisposable + IAsyncDisposable`, sync-`Dispose()` ruft `DisposeAsync()`
     synchron. `AppConfig.Transcription` als JSON-Property ergänzt.
     **Counter-Race-Bug-Fix** im `TranscriptionWorker`:
     `_failedCount`-Increment NACH `await MarkFailedAsync()` verschoben
     (40 % Flake-Rate vorher, 0 % nachher in 5/5 Runs). +4 Tests.
   - **Test-Stand:** 673 (Bug-Bash 2026-07-06) → **777/777 grün stabil** nach MVP 3.
     104 neue Tests für MVP 3 + Counter-Bug-Fix.
   - **Production-Wiring**: alles automatisch via `TriggerService`-
     Default-Composition — kein manuelles Setup nötig. `MeetingTrigger` ist
     `null` wenn Privacy-Gate zuschlägt.
   - Spezifikation: `specs/0013-audio-notes-mvp3.md`, Update 1-8 dokumentiert.
9. **MVP 4 (Auto Wiki) — Roadmap-Reshuffle 2026-07-06**: Auto-Wiki-Generierung aus Captures wandert von ehemals MVP 3 nach **MVP 4**. Spec-Detail (`0013-auto-wiki.md` Kandidat) folgt in eigenem Cluster, sobald Anforderungen klar sind (LLM-Auswahl, Index-Struktur, Suche/Filter). **NICHT** Teil von MVP1/MVP2/MVP3 (MVP3 = Audio Notes).
10. **Spec 0013 v0.4 (Outlook-Speaker-Mapping)** — wartet auf v0.3-Abnahme. Speaker-IDs (S0/S1 aus Provider-Diarization) → Outlook-Kontakte → Realnamen in MD. Architektur: `SpeakerRoleAssigner` mit `OutlookContactLookup` (Outlook-COM-Interop oder Microsoft-Graph-Provider, Entscheidung in Spec-Erweiterung).