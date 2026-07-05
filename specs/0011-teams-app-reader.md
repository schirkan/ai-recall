# 0011 — Teams App-Reader

> **Status:** Draft (Cluster 1 in Arbeit)
> **Implements:** TN-1 .. TN-8 (App-Reader-Erweiterung für Microsoft Teams Modern)
> **Pattern:** analog Outlook App-Reader (Spec 0004 Iter. 3) + OneNote App-Reader (Spec 0010)
> **Owner:** Martin + Pia
> **Branch:** `main`
> **Started:** 2026-07-05

## Direktive (Martin 2026-07-05)

Verbindliche Constraints (Spec 0011):

- **NUR Modern Teams** (Electron-basiert, `ms-teams.exe`).
- **KEIN Legacy Teams Classic** (`Microsoft Teams` ProgID) — seit 2023 deprecated.
- **KEIN Graph API** (kein OAuth-Token-Lifecycle, kein async-Polling-Pattern).
- **UIA als Default-Pfad** (immer verfügbar, Plain-Text via `UIAutomationClient`).
- **CDP als opt-in** (wenn Teams mit `--remote-debugging-port=PORT` gestartet,
  HTML→MD via Chromium DevTools Protocol, reichhaltiger als UIA-only).

## Motivation

Microsoft Teams ist Standard für 1:1-Chat, Channel-Kommunikation und Gruppen-
Collaboration. Wir wollen den aktuell aktiven Chat (1:1, Group, oder Channel)
als strukturierten Content loggen — analog Outlook-Mails (Spec 0004 Iter. 3)
und OneNote-Pages (Spec 0010).

Modern Teams ist eine Electron-Anwendung, daher kein COM-Interface:

- **CDP** (analog Browser-Reader Iter. 3, Spec 0004) liefert reichhaltigen Chat-
  Content via Runtime.evaluate auf den Tabs, erfordert aber User-Mitarbeit
  (Teams muss mit `--remote-debugging-port` gestartet werden).
- **UIA** liefert Plain-Text aus dem sichtbaren Chat-Fenster, ist immer
  verfügbar, aber nur oberflächlich (keine HTML-Render-Treue, keine Sender-
  Farben, keine Reply-Threads-Struktur).

Beide Pfade werden im Reader kombiniert: CDP bevorzugt (wenn konfiguriert und
erreichbar), UIA als Standard, Title-Fallback wenn beides scheitert.

## Architektur

Eine neue DLL analog Outlook/OneNote:

```
AiRecall.AppReader.Teams.dll
├ TeamsAppReader           : AppReaderBase  (Read only, kein OnPoll)
├ TeamsUiaReader           : internal static — UIA-Implementation
├ TeamsCdpReader           : internal static — CDP-Implementation (opt-in)
├ TeamsHierarchyInfo       : internal sealed record
│                             (Chat-ID, Chat-Title, Chat-Type, Message-Count)
└ TeamsMessage             : internal sealed record
                              (Sender, Timestamp, Body-MD, IsSelf-Message)
```

`AppReaderRegistry` scannt automatisch via `AiRecall.AppReader.*.dll`-Pattern.

## Active-Chat-Strategie (3-stufig)

1. **CDP-Pfad** (wenn `TeamsConfig.UseCdpIfAvailable=true` UND CDP-Endpoint
   erreichbar via HTTP-Discovery auf `/json/version`):
   - Finde den aktuell aktiven Tab in den CDP-Targets (`Page.list`/`Target.list`)
   - `Runtime.evaluate` auf `document.title` + Chat-Panel-DOM
   - Bessere Message-Extraktion (mit Sendern + Timestamps + Reply-Hierarchie)
   - Liefert reichhaltige Markdown-Section mit Reply-Threads.

2. **UIA-Pfad** (Standard, immer verfügbar):
   - `WindowInfo.Title` parsen (z. B. `"Chat | Alice - Microsoft Teams"`)
   - UIA `TextPattern` auf das Chat-Fenster
   - Plain-Text-Extraktion, einfache Message-Boundary-Erkennung
   - Liefert Plain-Text mit heuristischer Sender/Timestamp-Separation.

3. **Title-Fallback** (wenn COM/CDP/UIA scheitert):
   - Nur Title persistieren, mit `(teams content unavailable)`-Hinweis-Body.

Konfigurierbar via `TeamsConfig.PreferredStrategy` (Cdp | Uia | Auto-Default).

## Komponenten

### TeamsUiaReader (internal static)

Pure UIA-Implementation. Pattern analog `NotepadUiaReader` (Spec 0004):

```csharp
public static class TeamsUiaReader
{
    /// <summary>
    /// Liest via UIA TextPattern aus dem Chat-Fenster.
    /// Liefert null wenn UI nicht bereit (kein Chat offen, UIA nicht verfuegbar).
    /// </summary>
    public static TeamsContent? TryGetActiveChat(WindowInfo window);

    /// <summary>Title-Parser fuer Modern-Teams-Window-Title-Format.</summary>
    public static TeamsTitleInfo ParseWindowTitle(string title);

    /// <summary>Liefert true wenn der uebergebene IntPtr ein aktives Teams-Chat-Window ist.</summary>
    public static bool IsTeamsChatWindow(IntPtr hwnd);
}
```

Window-Title-Format (Modern Teams):
- `"Chat | Alice - Microsoft Teams"` (1:1)
- `"Channel | #general - Microsoft Teams"` (Channel)
- `"Group Chat | Project Alpha - Microsoft Teams"` (Group)
- `"Meeting | Daily Standup - Microsoft Teams"` (Meeting — wird als Chat erfasst, mit Hinweis-Body)

Title-Parser zerlegt nach `|` als Trenner, `- Microsoft Teams` als Suffix.

### TeamsCdpReader (internal static)

CDP-Implementation. Pattern analog Browser-Reader Iter. 3 (Spec 0004):

```csharp
public static class TeamsCdpReader
{
    /// <summary>HTTP-Discovery: GET /json/version auf dem konfigurierten Endpoint.</summary>
    public static bool TryFindEndpoint(string endpoint, TimeSpan timeout, out string wsUrl);

    /// <summary>WebSocket-Connection zu einem CDP-Target, Runtime.evaluate.</summary>
    public static TeamsContent? TryGetActiveChat(string wsUrl, TimeSpan timeout);

    /// <summary>Liefert die aktuell aktive Page (focused Window).</summary>
    public static string? GetFocusedPageId(string wsUrl, TimeSpan timeout);
}
```

Verwendet `Microsoft.Web.WebView2`-LITE-Pattern (kein NuGet-Paket, nur
`System.Net.WebSockets` built-in) — analog Browser-Reader CDP-Integration.

### TeamsConfig (in AppConfig)

```csharp
public sealed class TeamsConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxContentKB { get; set; } = 512;
    public bool UseCdpIfAvailable { get; set; } = true;
    public string CdpEndpoint { get; set; } = "http://localhost:9222";
    public int CdpTimeoutMs { get; set; } = 1500;
    public string PreferredStrategy { get; set; } = "Auto"; // Cdp | Uia | Auto
    public int PollIntervalSeconds { get; set; } = 0;        // 0 = Read-only
    public List<string> SkipChatPatterns { get; set; } = new();
    public List<string> IncludeSenderPatterns { get; set; } = new(); // whitelist (leer = alle)
}
```

Integration in `AppConfig`-Wurzel:

```csharp
[JsonPropertyName("teams")]
public TeamsConfig Teams { get; set; } = new();
```

Default-Section in `default-config.json`:

```json
"appReader": {
  …,
  "teams": {
    "enabled": true,
    "maxContentKB": 512,
    "useCdpIfAvailable": true,
    "cdpEndpoint": "http://localhost:9222",
    "cdpTimeoutMs": 1500,
    "preferredStrategy": "Auto",
    "pollIntervalSeconds": 0,
    "skipChatPatterns": [],
    "includeSenderPatterns": []
  }
}
```

### TeamsAppReader (Hauptklasse)

```csharp
public sealed class TeamsAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses => new[] { "ms-teams" };
    public override string DisplayName => "Microsoft Teams (UIA; CDP opt-in)";
    public override bool SupportsBackgroundPolling => false;

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context);
}
```

**Read-Pipeline** (7 Schritte, analog OneNoteAppReader):

1. `context.Config.AppReader.Teams.Enabled` → bei false: null.
2. Strategy-Auflösung (`Cdp` / `Uia` / `Auto`):
   - `Cdp`: direkt CDP-Pfad
   - `Uia`: direkt UIA-Pfad
   - `Auto`: CDP zuerst (wenn `UseCdpIfAvailable`), UIA als Fallback
3. CDP-Pfad: `TeamsCdpReader.TryFindEndpoint(...)` + `TryGetActiveChat(...)`
4. UIA-Pfad: `TeamsUiaReader.TryGetActiveChat(window, ...)`
5. `SkipChatPatterns`-Filter (case-insensitive Substring gegen Chat-Title)
6. `IncludeSenderPatterns`-Filter (leer = alle Sender; sonst whitelist)
7. `BuildFullMarkdown(info, body, senderList, cfg)` → YAML-Frontmatter + Header + Body

## Persistenz-Schema

```
capture/<yyyy-MM-dd>/ms-teams/<HHmmss>-<chatIdShort>.md
```

`<chatIdShort>` = erste 8 Zeichen einer hash-basierten Chat-ID (deterministisch
aus `Title|Type|SenderSet`, sodass zwei Captures mit identischer Chat-Konstellation
denselben Short-ID erhalten → spätere Deduplikation möglich).

YAML-Frontmatter:

```yaml
kind: teams-chat
chatType: "1:1" | "group" | "channel"
chatTitle: "Chat | Alice"
chatIdShort: "AB12CD34"
source: "teams-cdp" | "teams-uia" | "teams-title-fallback"
strategy: "Cdp" | "Uia" | "Auto"
senderCount: 3
messageCount: 12
isSelfIncluded: true
capturedAt: "2026-07-05T19:34:12Z"
reader: "AiRecall.AppReader.Teams"
readerVersion: "0.1.0"
```

## Tests (~50 neue)

| File | Tests | Inhalt |
|---|---|---|
| `TeamsConfigTests.cs` | 5 | Defaults + JSON-Deserialisierung + Missing-Section-Default |
| `TeamsUiaReaderTests.cs` | 18 | Title-Parser (4 Format-Typen), `IsTeamsChatWindow`, `TryGetActiveChat` mit Mock-UI-Automation |
| `TeamsCdpReaderTests.cs` | 12 | Endpoint-Discovery, WebSocket-Connect, Runtime.evaluate-Parsing, Timeout-Handling |
| `TeamsAppReaderTests.cs` | 15 | Read-Pfad (alle 3 Strategies), IncludeSender-Filter, SkipChat-Filter, BuildFullMarkdown, Fallback-Pfad |
| **Σ** | **50** | analog Outlook/OneNote-Pattern |

Test-Trait-Marker analog Outlook/OneNote:
- `[Trait("Integration", "Teams")]` für Tests, die installiertes Teams + CDP-Endpoint
  voraussetzen. Auf Martins Workstation laufen sie, in CI werden sie geskippt.

## Bekannte Einschränkungen

- **Modern Teams UWP / Teams Web** (Browser-Variante) werden NICHT erfasst
  (UWP hat andere UI-Hierarchie, Browser ist via Browser-Reader abgedeckt).
- **Tab-Wechsel** während Capture: pro Read-Call nur das aktuell aktive
  Tab — Multi-Tab-Chats = Multi-Captures.
- **Reply-Threads** (UIA-Pfad): nur als flache Message-Liste, ohne
  Thread-Hierarchie. CDP-Pfad mit DOM-Analyse kann Threads in Iter. 2.
- **Inline-Media** (Bilder, GIFs): UIA liefert nur Alt-Text, CDP liefert
  DOM-URLs. Beide in Plain-Text/MD, keine Persistierung.
- **Meeting-Chats** (Tab-Typ Meeting): werden mit Hinweis-Body erfasst
  (`(Meeting content extracted via UIA, no audio/video)`).
- **Handschrift/OneNote-Embedded**: nicht via Teams-Reader, wäre über
  OneNote-Reader (Spec 0010) abgedeckt.

## Out-of-Scope (für Iter. 1)

- Legacy Teams Classic (`Microsoft Teams` ProgID COM) — explizit
  ausgeschlossen per Martin-Direktive.
- Graph API mit OAuth-Token — explizit ausgeschlossen per Martin-Direktive.
- Teams Meetings (Audio/Video-Capture).
- Teams Voice Messages (Media-Datei-Persistierung).
- Reactions/Emojis (in Iter. 2 via CDP-DOM-Analyse).
- File-Attachments aus Teams-Chats.

## Referenzen

- Microsoft Teams: <https://www.microsoft.com/microsoft-teams/>
- Chromium DevTools Protocol: <https://chromedevtools.github.io/devtools-protocol/>
- Outlook App-Reader (Spec 0004 Iter. 3): `specs/0004-app-reader.md`
- OneNote App-Reader (Spec 0010): `specs/0010-onenote-app-reader.md`
- Browser-Reader CDP-Integration (Spec 0004 Iter. 3): `src/AiRecall.AppReader.Browser/CdpClient.cs`
- Trigger-Pipeline: `specs/0005-trigger-pipeline.md`
