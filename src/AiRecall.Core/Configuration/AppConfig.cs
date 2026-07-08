using System.ComponentModel;
using System.Text.Json.Serialization;

using AiRecall.Core.Audio;

namespace AiRecall.Core.Configuration;

/// <summary>
/// Root configuration object loaded from JSON (see <see cref="ConfigLoader"/>).
/// All sections have safe defaults; missing properties fall back to those.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("capture")]
    public CaptureConfig Capture { get; set; } = new();

    [JsonPropertyName("screenRecorder")]
    public ScreenRecorderConfig ScreenRecorder { get; set; } = new();

    [JsonPropertyName("ocr")]
    public OcrConfig Ocr { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; set; } = new();

    [JsonPropertyName("appReader")]
    public AppReaderConfig AppReader { get; set; } = new();

    /// <summary>
    /// Trigger-Pipeline-Konfiguration (Spec 0005). Setzt Defaults für
    /// <c>SetWinEventHook</c>-basierte Trigger, Heartbeat-Polling, Throttle,
    /// Dedup-Logik und Class-/Process-Blacklist.
    /// </summary>
    [JsonPropertyName("trigger")]
    public TriggerConfig Trigger { get; set; } = new();

    /// <summary>Async-Conversion-Pipeline (Spec 0007).</summary>
    [JsonPropertyName("conversion")]
    public ConversionConfig Conversion { get; set; } = new();

    /// <summary>
    /// Audio-Recording-Konfiguration (Spec 0013 v0.3, MVP 3 Audio Notes).
    /// Default <c>enabled=false</c> (Privacy-First).
    /// </summary>
    [JsonPropertyName("audio")]
    public AudioConfig Audio { get; set; } = new();
}

public sealed class AppReaderConfig
{
    /// <summary>Master-Switch: wenn <c>false</c>, werden keine App-Reader ausgeführt.</summary>
    [Description("Master-Switch: false = keine App-Reader aktiv (nur Titel-Capture).")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Verzeichnis mit <c>AiRecall.AppReader.*.dll</c>. Relativ =&gt; <c>AppContext.BaseDirectory</c>.</summary>
    [Description("Verzeichnis mit AiRecall.AppReader.*.dll-Plugins. '.' = neben der EXE.")]
    [JsonPropertyName("pluginPath")]
    public string PluginPath { get; set; } = ".";

    /// <summary>Maximale Länge des extrahierten Inhalts pro Reader (KB).</summary>
    // Bug-Bash 2026-07-06 I-21: appReader.maxContentKB entfernt — Dead Code.
    // Kein Reader liest diese Property. Pro-Reader haben eigene Caps:
    //   Outlook.BodyTruncateKB, Browser.MaxTextLengthKB, Notepad.MaxBufferKB,
    //   Documents.MaxTextKB, OneNote.MaxContentKB, Teams.MaxContentKB.

    [JsonPropertyName("outlook")]
    public OutlookConfig Outlook { get; set; } = new();

    [JsonPropertyName("browser")]
    public BrowserConfig Browser { get; set; } = new();

    [JsonPropertyName("notepad")]
    public NotepadConfig Notepad { get; set; } = new();

    [JsonPropertyName("documents")]
    public DocumentsConfig Documents { get; set; } = new();

    [JsonPropertyName("pdf")]
    public PdfConfig Pdf { get; set; } = new();

    /// <summary>OneNote App-Reader (Spec 0010). Read-only, kein Background-Poll.</summary>
    [JsonPropertyName("onenote")]
    public OneNoteConfig OneNote { get; set; } = new();

    /// <summary>Teams App-Reader (Spec 0011). Modern Teams only, UIA + CDP opt-in.</summary>
    [JsonPropertyName("teams")]
    public TeamsConfig Teams { get; set; } = new();
}

public sealed class OutlookConfig
{
    /// <summary>Default-Filename für den EntryID-Dedup-State in <c>%APPDATA%/AiRecall/</c>.</summary>
    public const string DefaultSeenStateFileName = "outlook-seen.json";

    [Description("Outlook-Folder-Namen, die gescannt werden. Default: 'Inbox', 'Sent Items'.")]
    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = new() { "Inbox", "Sent Items" };

    [Description("OnPoll-Intervall in Sekunden fuer Outlook-Folder-Scan. 0 = deaktiviert.")]
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Wenn true: Mails, die offenbar durch Outlook-Regeln "berührungslos" verarbeitet wurden, werden nicht persistiert.</summary>
    [Description("true: Mails ignorieren, die durch Outlook-Regeln automatisch verschoben wurden.")]
    [JsonPropertyName("ignoreAutoRuleMails")]
    public bool IgnoreAutoRuleMails { get; set; } = false;

    /// <summary>Maximale Anzahl Mails, die pro Sweep je Folder geprüft werden (Cap gegen riesige Postfächer).</summary>
    [Description("Maximale Anzahl Mails, die pro Sweep je Folder geprueft werden (Cap gegen riesige Postfaecher).")]
    [JsonPropertyName("maxItemsPerSweep")]
    public int MaxItemsPerSweep { get; set; } = 200;

    /// <summary>Maximale Body-Länge im persistierten MD (KB). Längere Bodies werden abgeschnitten mit Hinweis.</summary>
    [Description("Maximale Body-Laenge im persistierten MD (KB). Laengere Bodies werden abgeschnitten mit Hinweis.")]
    [JsonPropertyName("bodyTruncateKB")]
    public int BodyTruncateKB { get; set; } = 256;

    /// <summary>Konfiguration für die simple HTML→Markdown-Konvertierung der Mail-Bodies.</summary>
    [Description("Sub-Konfiguration fuer HTML->Markdown-Konvertierung in Mail-Bodies.")]
    [JsonPropertyName("htmlToMarkdown")]
    public HtmlToMarkdownOptions HtmlToMarkdown { get; set; } = new();

    /// <summary>
    /// Liefert den Default-Pfad für <c>outlook-seen.json</c>:
    /// <c>%APPDATA%/AiRecall/outlook-seen.json</c> (siehe <see cref="ConfigLoader.AppDataSubdirectory"/>).
    /// </summary>
    public static string DefaultSeenStatePath() => System.IO.Path.Combine(
        ConfigLoader.AppDataSubdirectory,
        DefaultSeenStateFileName);
}

/// <summary>
/// Konfiguration der simplen HTML→Markdown-Konvertierung in OutlookAppReader
/// (Spec 0004 Iter. 3 — kein ReverseMarkdown, eigene simple Strip-Logik).
/// </summary>
public sealed class HtmlToMarkdownOptions
{
    /// <summary><c>&lt;a href="X"&gt;Y&lt;/a&gt;</c> → <c>[Y](X)</c>.</summary>
    [Description("<a href='X'>Y</a> -> [Y](X).")]
    [JsonPropertyName("preserveLinks")]
    public bool PreserveLinks { get; set; } = true;

    /// <summary><c>&lt;br&gt;</c>, <c>&lt;/p&gt;</c>, <c>&lt;/div&gt;</c> → Zeilenumbruch.</summary>
    [Description("<br>, </p>, </div> -> Zeilenumbruch.")]
    [JsonPropertyName("preserveLineBreaks")]
    public bool PreserveLineBreaks { get; set; } = true;

    /// <summary><c>&lt;img&gt;</c>-Tags komplett entfernen (Tracking-Pixel-Schutz).</summary>
    [Description("<img>-Tags komplett entfernen (Tracking-Pixel-Schutz).")]
    [JsonPropertyName("stripImages")]
    public bool StripImages { get; set; } = true;
}

public sealed class BrowserConfig
{
    [Description("Maximaler Browser-Text (KB), der aus dem DOM extrahiert wird.")]
    [JsonPropertyName("maxTextLengthKB")]
    public int MaxTextLengthKB { get; set; } = 50;

    /// <summary>Chrome DevTools Protocol Anbindung (optional, erfordert Browser-Start mit <c>--remote-debugging-port</c>).</summary>
    [Description("Sub-Konfiguration fuer Chrome DevTools Protocol (CDP).")]
    [JsonPropertyName("cdp")]
    public CdpConfig Cdp { get; set; } = new();

    /// <summary>
    /// ReverseMarkdown-Konfiguration für die HTML→Markdown-Konvertierung.
    /// Wirkt unabhängig vom CDP-Gate; alle Felder sind optional und fallen auf
    /// die <see cref="MarkdownSettings"/>-Defaults zurück.
    /// </summary>
    [Description("Sub-Konfiguration fuer ReverseMarkdown (HTML->MD-Konvertierung).")]
    [JsonPropertyName("markdown")]
    public MarkdownSettings Markdown { get; set; } = new();
}

public sealed class CdpConfig
{
    /// <summary>
    /// Wenn <c>false</c> (Default), wird der CDP-Pfad übersprungen und der Browser-Reader
    /// fällt direkt auf UIA zurück. Aktivieren nur, wenn Browser/Edge mit
    /// <c>--remote-debugging-port=9222</c> gestartet wurde.
    /// </summary>
    [Description("CDP aktivieren. Erfordert Browser-Start mit --remote-debugging-port=9222. Default: false.")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>CDP-HTTP-Endpoint, typisch <c>http://localhost:9222</c>.</summary>
    [Description("CDP-HTTP-Endpoint, typisch http://localhost:9222.")]
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "http://localhost:9222";

    /// <summary>Timeout (ms) für HTTP-Lookup + WebSocket-Roundtrip.</summary>
    [Description("Timeout (ms) fuer HTTP-Lookup + WebSocket-Roundtrip.")]
    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 1500;
}

/// <summary>
/// 1:1-Konfiguration für <c>ReverseMarkdown.Config</c> (v3.13). Alle Felder sind optional.
/// Ungesetzte Felder werden nicht in den Converter geschrieben → die Library-Defaults
/// bleiben erhalten. Enums werden als Strings in JSON erwartet (case-insensitive).
/// </summary>
public sealed class MarkdownSettings
{
    /// <summary>
    /// Wie unbekannte HTML-Tags behandelt werden:
    /// <c>"PassThrough"</c> (Default), <c>"Drop"</c>, <c>"Bypass"</c>, <c>"Raise"</c>.
    /// </summary>
    [Description("Wie unbekannte HTML-Tags behandelt werden: 'PassThrough' (Default) / 'Drop' / 'Bypass' / 'Raise'.")]
    [JsonPropertyName("unknownTags")]
    public string? UnknownTags { get; set; }

    /// <summary>GitHub-Flavored-Markdown (Tabellen, fenced code, etc.). Default: <c>false</c>.</summary>
    [Description("GitHub-Flavored-Markdown (Tabellen, fenced code, etc.). null = Library-Default (false).")]
    [JsonPropertyName("githubFlavored")]
    public bool? GithubFlavored { get; set; }

    /// <summary>HTML-Kommentare vor Konvertierung entfernen. Default: <c>true</c>.</summary>
    [Description("HTML-Kommentare vor Konvertierung entfernen. null = Library-Default (true).")]
    [JsonPropertyName("removeComments")]
    public bool? RemoveComments { get; set; }

    /// <summary>
    /// Erlaubte URI-Schemes für <c>&lt;a href&gt;</c>. Default in der Library:
    /// <c>{"http", "https", "ftp", "ftps", "mailto", "tel"}</c>.
    /// </summary>
    [Description("Erlaubte URI-Schemes fuer <a href>. null = Library-Default.")]
    [JsonPropertyName("whitelistUriSchemes")]
    public List<string>? WhitelistUriSchemes { get; set; }

    /// <summary>
    /// Smarte Href-Behandlung (URL-Dekodierung, Whitespaces). Default: <c>false</c>.
    /// </summary>
    [Description("Smarte Href-Behandlung (URL-Dekodierung, Whitespaces).")]
    [JsonPropertyName("smartHrefHandling")]
    public bool? SmartHrefHandling { get; set; }

    /// <summary>
    /// Tabellen ohne Header-Zeile: <c>"Default"</c> oder <c>"EmptyRow"</c>.
    /// </summary>
    [Description("Tabellen ohne Header-Zeile: 'Default' / 'EmptyRow'.")]
    [JsonPropertyName("tableWithoutHeaderRowHandling")]
    public string? TableWithoutHeaderRowHandling { get; set; }

    /// <summary>
    /// Bullet-Character für unsortierte Listen. Als einzelnes Zeichen oder String;
    /// bei leerem Wert fällt die Library auf <c>'*'</c> zurück.
    /// </summary>
    [Description("Bullet-Character fuer unsortierte Listen. Leer = Library-Default ('*').")]
    [JsonPropertyName("listBulletChar")]
    public string? ListBulletChar { get; set; }

    /// <summary>Default-Sprache für Code-Blöcke (z. B. <c>"text"</c>, <c>"bash"</c>).</summary>
    [Description("Default-Sprache fuer Code-Bloecke (z.B. 'text', 'bash').")]
    [JsonPropertyName("defaultCodeBlockLanguage")]
    public string? DefaultCodeBlockLanguage { get; set; }
}

public sealed class NotepadConfig
{
    [Description("Maximaler Notepad-Buffer (KB), der ausgelesen wird.")]
    [JsonPropertyName("maxBufferKB")]
    public int MaxBufferKB { get; set; } = 256;
}

/// <summary>
/// Konfiguration fuer den PDF-Viewer-Reader (Spec 0004 Erweiterung, Martin 2026-07-04).
/// Iter. 1: nur Title-Parsing + Process-Erkennung. Voller PDF-Inhalt via PdfPig in Iter. 2.
/// </summary>
public sealed class PdfConfig
{
    /// <summary>
    /// Liste der PDF-Viewer-Prozesse (case-insensitive). Default enthaelt Adobe Reader,
    /// Acrobat, SumatraPDF, Foxit Reader, PDF-XChange, Edge und Chrome.
    /// </summary>
    [Description("PDF-Viewer-Prozesse (case-insensitive substring). Default: Adobe Reader, Acrobat, SumatraPDF, Foxit, PDF-XChange, Edge, Chrome.")]
    [JsonPropertyName("processes")]
    public List<string> Processes { get; set; } = new()
    {
        "AcroRd32",
        "Acrobat",
        "SumatraPDF",
        "FoxitReader",
        "PDFXEdit",
        "msedge",
        "chrome"
    };
}

/// <summary>
/// Konfiguration fuer den OneNote App-Reader (Spec 0010).
/// Pattern: analog Outlook (Spec 0004 Iter. 3) — Read-only, kein Background-Poll.
/// OneNote ist Page-orientiert (kein Daten-Stream wie Outlook-Mails).
/// </summary>
public sealed class OneNoteConfig
{
    /// <summary>Master-Switch. <c>false</c> liefert der Reader immer <c>null</c>.</summary>
    [Description("Master-Switch. false = OneNote-Reader deaktiviert.")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximale Laenge des Page-Contents im Markdown (KB). Laenger = abgeschnitten
    /// mit Hinweis-Block am Ende.
    /// </summary>
    [Description("Maximale Laenge des OneNote-Page-Contents im Markdown (KB). Laenger = abgeschnitten mit Hinweis-Block.")]
    [JsonPropertyName("maxContentKB")]
    public int MaxContentKB { get; set; } = 256;

    /// <summary>
    /// <c>one:Image</c>-Embeds in Markdown konvertieren. Default <c>false</c> —
    /// Base64-Inflation in MD-Files und Datenschutz-Risiko bei handschriftlichen
    /// Skizzen. Aktivieren nur wenn explizit gewuenscht.
    /// </summary>
    [Description("one:Image-Embeds in Markdown konvertieren. Default false (Base64-Inflation + Datenschutz).")]
    [JsonPropertyName("includeImages")]
    public bool IncludeImages { get; set; } = false;

    /// <summary><c>one:Tag</c> (To-Do-Marker, <c>#tag-name</c>) in Markdown konvertieren.</summary>
    [Description("one:Tag (To-Do-Marker, #tag-name) in Markdown konvertieren.")]
    [JsonPropertyName("includeTags")]
    public bool IncludeTags { get; set; } = true;

    /// <summary>
    /// Hierarchy-Tiefe im Frontmatter:
    /// <c>"PageOnly"</c>, <c>"PageAndSection"</c> (Default), <c>"PageAndSectionAndNotebook"</c>.
    /// </summary>
    [Description("Hierarchy-Tiefe im Frontmatter: 'PageOnly' / 'PageAndSection' (Default) / 'PageAndSectionAndNotebook'.")]
    [JsonPropertyName("hierarchyDepth")]
    public string HierarchyDepth { get; set; } = "PageAndSection";

    /// <summary>
    /// Active-Page-Strategie (Spec 0010 Section "Active-Page-Strategie"):
    /// <c>"WindowsApi"</c> (Default, schnellste via <c>Windows.CurrentWindow.CurrentPageId</c>),
    /// <c>"HierarchyXml"</c> (Fallback via <c>isCurrentlyViewed="true"</c>),
    /// <c>"Auto"</c> (alle 4 Stages probieren bis Erfolg).
    /// </summary>
    [Description("Active-Page-Strategie: 'WindowsApi' (schnellste) / 'HierarchyXml' (Fallback) / 'Auto' (alle Stages).")]
    [JsonPropertyName("activePageStrategy")]
    public string ActivePageStrategy { get; set; } = "WindowsApi";

    /// <summary>
    /// OnPoll-Intervall in Sekunden. <c>0</c> (Default) = Read-only, kein
    /// Background-Poll. OneNote ist Page-orientiert, OnPoll ist i. d. R. nicht
    /// sinnvoll — Capture wird ueber den Trigger (Foreground-Event) ausgeloest.
    /// </summary>
    [Description("OnPoll-Intervall in Sekunden. 0 (Default) = Read-only, kein Background-Poll.")]
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 0;

    /// <summary>
    /// Notebook-Namen-Patterns (case-insensitive substring), die ignoriert werden,
    /// z. B. <c>"*.deleted"</c>, <c>"Archive 2024"</c>.
    /// </summary>
    [Description("Notebook-Namen-Patterns (case-insensitive substring), z.B. '*.deleted', 'Archive 2024'.")]
    [JsonPropertyName("skipNotebookPatterns")]
    public List<string> SkipNotebookPatterns { get; set; } = new();
}

/// <summary>
/// Konfiguration fuer die Office-Dokumente-Reader (Word/Excel/PowerPoint).
/// Spec 0011 — Modern Teams App-Reader (UIA + CDP opt-in).
/// Pattern: analog Outlook (Spec 0004 Iter. 3) und OneNote (Spec 0010),
/// aber ohne COM-Late-Binding (Modern Teams ist Electron-basiert).
/// </summary>
public sealed class TeamsConfig
{
    /// <summary>Master-Switch. <c>false</c> liefert der Reader immer <c>null</c>.</summary>
    [Description("Master-Switch. false = Teams-Reader deaktiviert.")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximale Laenge des Chat-Contents im Markdown (KB). Laenger = abgeschnitten
    /// mit Hinweis-Block am Ende.
    /// </summary>
    [Description("Maximale Laenge des Teams-Chat-Contents im Markdown (KB).")]
    [JsonPropertyName("maxContentKB")]
    public int MaxContentKB { get; set; } = 512;

    /// <summary>
    /// Wenn <c>true</c>: CDP-Pfad bevorzugt wenn Endpoint erreichbar.
    /// Bei <c>false</c>: immer UIA-only (kein WebSocket, schneller, weniger Inhalt).
    /// </summary>
    [Description("true: CDP bevorzugt wenn erreichbar. false: immer UIA-only.")]
    [JsonPropertyName("useCdpIfAvailable")]
    public bool UseCdpIfAvailable { get; set; } = true;

    /// <summary>CDP-HTTP-Endpoint, typisch <c>http://localhost:9222</c>.</summary>
    [Description("CDP-HTTP-Endpoint, typisch http://localhost:9222.")]
    [JsonPropertyName("cdpEndpoint")]
    public string CdpEndpoint { get; set; } = "http://localhost:9222";

    /// <summary>Timeout (ms) fuer CDP-HTTP-Discovery + WebSocket-Roundtrip.</summary>
    [Description("Timeout (ms) fuer CDP-HTTP-Discovery + WebSocket-Roundtrip.")]
    [JsonPropertyName("cdpTimeoutMs")]
    public int CdpTimeoutMs { get; set; } = 1500;

    /// <summary>
    /// Strategy-Preference:
    /// <c>"Cdp"</c> (nur CDP, scheitert wenn nicht erreichbar),
    /// <c>"Uia"</c> (nur UIA),
    /// <c>"Auto"</c> (CDP bevorzugt, UIA-Fallback).
    /// </summary>
    [Description("Strategy-Preference: 'Cdp' (nur CDP) / 'Uia' (nur UIA) / 'Auto' (CDP bevorzugt, UIA-Fallback).")]
    [JsonPropertyName("preferredStrategy")]
    public string PreferredStrategy { get; set; } = "Auto";

    /// <summary>
    /// OnPoll-Intervall in Sekunden. <c>0</c> (Default) = Read-only,
    /// kein Background-Poll. Teams ist Chat-orientiert, Capture reagiert
    /// auf Foreground-Event (User oeffnet neues Chat-Tab).
    /// </summary>
    [Description("OnPoll-Intervall in Sekunden. 0 (Default) = Read-only, kein Background-Poll.")]
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 0;

    /// <summary>
    /// Chat-Title-Patterns (case-insensitive substring), die ignoriert werden.
    /// Nuetzlich fuer Meeting-Chats oder Status-Bots.
    /// </summary>
    [Description("Chat-Title-Patterns (case-insensitive substring), z.B. fuer Meeting-Chats oder Status-Bots.")]
    [JsonPropertyName("skipChatPatterns")]
    public List<string> SkipChatPatterns { get; set; } = new();

    /// <summary>
    /// Sender-Patterns (case-insensitive substring) als Whitelist.
    /// Leer = alle Sender erfasst. Beispiel: <c>["Alice", "Bob"]</c>
    /// erfasst nur Chats mit Alice/Bob.
    /// </summary>
    [Description("Sender-Patterns (case-insensitive substring) als Whitelist. Leer = alle Sender.")]
    [JsonPropertyName("includeSenderPatterns")]
    public List<string> IncludeSenderPatterns { get; set; } = new();

    // ===== Audio-Notes-MVP3 (Spec 0013 v0.3) ====================================
    // Erweiterung TeamsConfig fuer MeetingPresencePoller. Diese Properties steuern
    // die Auto-Recording-Logik (Iter. 2): Polling-Intervall, Mindestdauer,
    // Auto-Record-Toggle. Iter. 1 hat bewusst KEINEN Recording-Start; diese Werte
    // werden erst in Iter. 2 wirklich ausgewertet.

    /// <summary>
    /// true: AiRecall startet Audio-Recording bei erkanntem Teams-Meeting automatisch.
    /// In Iter. 2 wird der Wert vom Poller ausgewertet; ein tatsaechlicher
    /// Recording-Start folgt in Iter. 3 (oder spaeter).
    /// </summary>
    [Description("true: Auto-Recording bei erkanntem Teams-Meeting (Spec 0013 v0.3).")]
    [JsonPropertyName("autoRecordMeetings")]
    public bool AutoRecordMeetings { get; set; } = true;

    /// <summary>
    /// Mindestdauer eines Meetings in Sekunden. Meetings unter diesem Wert
    /// werden verworfen (Default 30 s, verhindert 5-Sekunden-Test-Meetings).
    /// </summary>
    [Description("Mindestdauer (Sekunden), unter der ein Meeting verworfen wird (Default 30).")]
    [JsonPropertyName("minMeetingDurationSeconds")]
    public int MinMeetingDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Polling-Intervall fuer den MeetingPresencePoller in Sekunden (Default 5).
    /// Niedrigere Werte = schnellere Stop-Erkennung, hoehere Werte = weniger CPU-Last.
    /// </summary>
    [Description("Polling-Intervall (Sekunden) fuer den MeetingPresencePoller (Default 5).")]
    [JsonPropertyName("presencePollIntervalSeconds")]
    public int PresencePollIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// Konfiguration fuer die Office-Dokumente-Reader (Word/Excel/PowerPoint).
/// Spec 0004 Iter. Documents.
/// </summary>
public sealed class DocumentsConfig
{
    /// <summary>Maximale Laenge des per UIA extrahierten Textes (KB).</summary>
    [Description("Maximale Laenge des per UIA extrahierten Textes (Word/Excel/PowerPoint).")]
    [JsonPropertyName("maxTextKB")]
    public int MaxTextKB { get; set; } = 64;

    /// <summary>UIA-basierte Text-Extraktion aktivieren. Fallback: Title-only.</summary>
    [Description("UIA-basierte Text-Extraktion. false = nur Titel-Capture (Fallback).")]
    [JsonPropertyName("enableUiaExtraction")]
    public bool EnableUiaExtraction { get; set; } = true;
}

public sealed class CaptureConfig
{
    [Description("Wurzelverzeichnis fuer Capture-Dateien. Relativ => AppContext.BaseDirectory.")]
    [JsonPropertyName("rootPath")]
    public string RootPath { get; set; } = "capture";

    [Description("Screenshot-Format: 'png' (Default) / 'jpg' / 'webp'. Screenshot-Funktion ist inaktiv (Spec 0005 ist capture-frei).")]
    [JsonPropertyName("screenshotFormat")]
    public string ScreenshotFormat { get; set; } = "png";
}

/// <summary>
/// Async-Conversion-Pipeline-Konfiguration (Spec 0007 v0.4 final).
/// Steuert das Verhalten des <c>ConversionWorker</c>.
/// </summary>
public sealed class ConversionConfig
{
    /// <summary>Globaler Toggle. false = keine Async-Conversion.</summary>
    [Description("Globaler Toggle. false = keine Async-Conversion (nur Title-Capture).")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Maximale MD-Laenge (KB) pro Konvertierung.</summary>
    [Description("Maximale Markdown-Laenge pro Konvertierung (KB). Laenger = abgeschnitten mit Hinweis.")]
    [JsonPropertyName("maxTextKB")]
    public int MaxTextKB { get; set; } = 64;

    /// <summary>Max. parallele Conversion-Tasks im Worker-Pool.</summary>
    [Description("Max. parallele Conversion-Tasks im Worker-Pool (Concurrency-Level).")]
    [JsonPropertyName("batchSize")]
    public int BatchSize { get; set; } = 2;

    /// <summary>Pro-Capture-Timeout (Sekunden).</summary>
    [Description("Pro-Capture-Timeout fuer Conversion (Sekunden). 0 = kein Timeout.")]
    [JsonPropertyName("conversionTimeoutSeconds")]
    public int ConversionTimeoutSeconds { get; set; } = 30;
}

// Hinweis: OcrConfig existiert bereits separat (Root-Level "ocr"-Property in AppConfig).
// OCR-spezifische Felder (engine, languages, tessDataPath) bleiben dort.
// Conversion.ocr.* wird in Schritt 4 ergaenzt, falls noetig.

public sealed class ScreenRecorderConfig
{
    [Description("Min-Intervall zwischen Captures fuer dasselbe HWND (Millisekunden).")]
    [JsonPropertyName("throttleMs")]
    public int ThrottleMs { get; set; } = 1000;

    [Description("Periodischer Capture-Trigger in Millisekunden. 0 = deaktiviert. Sinnvoll: 3000-10000 fuer Video/Slideshows.")]
    [JsonPropertyName("periodicCaptureMs")]
    public int PeriodicCaptureMs { get; set; } = 0;

    /// <summary>Process names (case-insensitive substring) to skip.</summary>
    [Description("Prozess-Namen (case-insensitive substring), die periodisch ignoriert werden.")]
    [JsonPropertyName("ignoreApps")]
    public List<string> IgnoreApps { get; set; } = new();

    /// <summary>URL substrings (case-insensitive) to skip when an app context URL is known.</summary>
    [Description("URL-Substrings (case-insensitive) zum Ignorieren bekannter App-URLs. Z.B. 'about:blank'.")]
    [JsonPropertyName("ignoreUrls")]
    public List<string> IgnoreUrls { get; set; } = new();

    /// <summary>Window title substrings (case-insensitive) to skip.</summary>
    [Description("Window-Titel-Substrings (case-insensitive), die periodisch ignoriert werden.")]
    [JsonPropertyName("ignoreWindowTitles")]
    public List<string> IgnoreWindowTitles { get; set; } = new();
}

public sealed class OcrConfig
{
    [Description("OCR-Engine. Default 'tesseract' (die einzige aktuell unterstuetzte Implementierung).")]
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "tesseract";

    [Description("Tesseract-Sprachpakete (z.B. 'deu', 'eng', 'fra'). Reihenfolge = Lese-Prioritaet.")]
    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = new() { "deu", "eng" };

    /// <summary>Path to the tessdata directory. Relative paths resolve to AppContext.BaseDirectory.</summary>
    [Description("Pfad zum tessdata-Verzeichnis. Relativ => AppContext.BaseDirectory. Auch %LOCALAPPDATA%\\AiRecall\\tessdata wird probiert.")]
    [JsonPropertyName("tessDataPath")]
    public string TessDataPath { get; set; } = "tessdata";

    /// <summary>
    /// Wenn <c>true</c> (Default), fragt die TrayApp beim ersten Start
    /// (bzw. wenn tessdata fehlt) nach, ob die Dateien automatisch
    /// heruntergeladen werden sollen. Spec 0012.
    /// </summary>
    [Description("true: TrayApp fragt ob tessdata automatisch heruntergeladen werden soll (Spec 0012).")]
    [JsonPropertyName("autoDownloadTessdata")]
    public bool AutoDownloadTessdata { get; set; } = true;
}

public sealed class LoggingConfig
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    /// <summary>Log directory. <c>null</c> disables file logging. Relative paths resolve to AppContext.BaseDirectory.</summary>
    [Description("Log-Verzeichnis. null = kein File-Logging. Relativ => AppContext.BaseDirectory.")]
    [JsonPropertyName("path")]
    public string? Path { get; set; } = "logs";
}

/// <summary>
/// Trigger-Pipeline-Konfiguration (Spec 0005). Werte, die hier nicht gesetzt
/// sind, fallen auf die dokumentierten Defaults zurück.
/// </summary>
public sealed class TriggerConfig
{
    /// <summary>Master-Switch. <c>false</c> deaktiviert die gesamte Pipeline.</summary>
    [Description("Master-Switch. false = gesamte Trigger-Pipeline deaktiviert.")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Min-Intervall zwischen Captures für dasselbe HWND (ms). Spec 0005 §Konfiguration.</summary>
    [Description("Min-Intervall zwischen Captures fuer dasselbe HWND (Millisekunden).")]
    [JsonPropertyName("throttleMs")]
    public int ThrottleMs { get; set; } = 500;

    /// <summary>Max 1 Capture pro App pro Zeitfenster (Sekunden). Spec 0005 §Konfiguration.</summary>
    [Description("Max 1 Capture pro App pro Zeitfenster (Sekunden).")]
    [JsonPropertyName("throttlePerAppSeconds")]
    public int ThrottlePerAppSeconds { get; set; } = 2;

    /// <summary>Heartbeat-Polling-Fallback (Sekunden). <c>0</c> deaktiviert den Heartbeat.</summary>
    [Description("Heartbeat-Polling-Fallback (Sekunden). 0 = deaktiviert. Sinnvoll: 15-60.")]
    [JsonPropertyName("heartbeatIntervalSeconds")]
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>Granular pro Win32-Event-Typ ein-/ausschalten.</summary>
    [Description("Sub-Konfiguration fuer Win32-Event-Auswahl.")]
    [JsonPropertyName("winEvents")]
    public WinEventSubscription WinEvents { get; set; } = new();

    /// <summary>Blacklist für Window-Klassen und Prozess-Namen.</summary>
    [Description("Sub-Konfiguration fuer Trigger-Blacklist (Window-Klassen, Prozesse, Titel).")]
    [JsonPropertyName("blacklist")]
    public TriggerBlacklist Blacklist { get; set; } = new();
}

/// <summary>
/// Welche Win32-Events subskribiert werden. Spec 0005 §Trigger-Quellen.
/// <c>Selection</c> ist bewusst nicht enthalten (Diskussion 2026-07-04 Punkt 2).
/// </summary>
public sealed class WinEventSubscription
{
    /// <summary><c>EVENT_SYSTEM_FOREGROUND</c> — Haupttrigger für Fensterwechsel.</summary>
    [Description("EVENT_SYSTEM_FOREGROUND - Haupttrigger fuer Fensterwechsel. Sollte immer true sein.")]
    [JsonPropertyName("foreground")]
    public bool Foreground { get; set; } = true;

    /// <summary><c>EVENT_OBJECT_FOCUS</c> — Fokus innerhalb des Fensters.</summary>
    [Description("EVENT_OBJECT_FOCUS - Fokus-Wechsel innerhalb des Fensters.")]
    [JsonPropertyName("focus")]
    public bool Focus { get; set; } = true;

    /// <summary><c>EVENT_OBJECT_NAMECHANGE</c> — Titel/URL geändert.</summary>
    [Description("EVENT_OBJECT_NAMECHANGE - Titel/URL aendert sich.")]
    [JsonPropertyName("nameChange")]
    public bool NameChange { get; set; } = true;

    /// <summary><c>EVENT_OBJECT_VALUECHANGE</c> — Inhalt geändert.</summary>
    [Description("EVENT_OBJECT_VALUECHANGE - Inhalt aendert sich (kann viele Events feuern).")]
    [JsonPropertyName("valueChange")]
    public bool ValueChange { get; set; } = true;

    /// <summary><c>EVENT_OBJECT_SCROLL</c> — Scroll-Bewegung.</summary>
    [Description("EVENT_OBJECT_SCROLL - Scroll-Bewegung (kann viele Events feuern).")]
    [JsonPropertyName("scroll")]
    public bool Scroll { get; set; } = true;

    /// <summary><c>EVENT_SYSTEM_MENUPOPUPSTART</c> — Menü/Kontextmenü geöffnet.</summary>
    [Description("EVENT_SYSTEM_MENUPOPUPSTART - Menue/Kontextmenue wird geoeffnet.")]
    [JsonPropertyName("menuPopup")]
    public bool MenuPopup { get; set; } = true;
}

/// <summary>
/// Blacklist für Win32-Window-Klassen, Prozess-Namen und Window-Titel
/// (Spec 0005 §Sonderfälle + Bug-Bash 2026-07-05 TrayApp-Blacklist).
/// </summary>
public sealed class TriggerBlacklist
{
    /// <summary>Default: Tooltips + Notification-Overflow.</summary>
    [Description("Win32-Window-Klassen (case-sensitive), die ignoriert werden. Default: tooltips_class32 + NotifyIconOverflowWindow.")]
    [JsonPropertyName("windowClasses")]
    public List<string> WindowClasses { get; set; } = new() { "tooltips_class32", "NotifyIconOverflowWindow" };

    /// <summary>Prozess-Namen (case-insensitive substring), die ignoriert werden.</summary>
    [Description("Prozess-Namen (case-insensitive substring), die ignoriert werden. Z.B. 'csrss', 'lsass'.")]
    [JsonPropertyName("processes")]
    public List<string> Processes { get; set; } = new();

    /// <summary>
    /// Window-Titel (case-insensitive substring), die ignoriert werden.
    /// Default: "AiRecall - " — matcht SettingsDialog ("AiRecall - Settings")
    /// und LogviewerWindow ("AiRecall - Live Logviewer") sowie alle
    /// zukünftigen TrayApp-Fenster mit dem Prefix-Kontrakt.
    /// </summary>
    [Description("Window-Titel (case-insensitive substring), die ignoriert werden. Default: 'AiRecall - ' schuetzt eigene TrayApp-Fenster.")]
    [JsonPropertyName("windowTitles")]
    public List<string> WindowTitles { get; set; } = new() { "AiRecall - " };
}



