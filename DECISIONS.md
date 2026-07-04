# DECISIONS

Architektur- und Stack-Entscheidungen mit Datum und Begründung. Wird bei
Bedarf von PROJECT.md oder specs/*.md geladen.

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
- **Trigger-Pipeline als separate Library/DLL**: Bleibt in
  `AiRecall.ScreenCapture` (Haupt-Capture-Pipeline) — keine eigene DLL,
  würde nur Split-Komplexität erzeugen.

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
