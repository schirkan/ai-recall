using System.Text.Json.Serialization;

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
}

public sealed class AppReaderConfig
{
    /// <summary>Master-Switch: wenn <c>false</c>, werden keine App-Reader ausgeführt.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Verzeichnis mit <c>AiRecall.AppReader.*.dll</c>. Relativ =&gt; <c>AppContext.BaseDirectory</c>.</summary>
    [JsonPropertyName("pluginPath")]
    public string PluginPath { get; set; } = ".";

    /// <summary>Maximale Länge des extrahierten Inhalts pro Reader (KB).</summary>
    [JsonPropertyName("maxContentKB")]
    public int MaxContentKB { get; set; } = 64;

    [JsonPropertyName("outlook")]
    public OutlookConfig Outlook { get; set; } = new();

    [JsonPropertyName("browser")]
    public BrowserConfig Browser { get; set; } = new();

    [JsonPropertyName("notepad")]
    public NotepadConfig Notepad { get; set; } = new();
}

public sealed class OutlookConfig
{
    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = new() { "Inbox", "Sent Items" };

    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Wenn true: Mails, die offenbar durch Outlook-Regeln "berührungslos" verarbeitet wurden, werden nicht persistiert.</summary>
    [JsonPropertyName("ignoreAutoRuleMails")]
    public bool IgnoreAutoRuleMails { get; set; } = false;
}

public sealed class BrowserConfig
{
    [JsonPropertyName("maxTextLengthKB")]
    public int MaxTextLengthKB { get; set; } = 50;

    /// <summary>Chrome DevTools Protocol Anbindung (optional, erfordert Browser-Start mit <c>--remote-debugging-port</c>).</summary>
    [JsonPropertyName("cdp")]
    public CdpConfig Cdp { get; set; } = new();

    /// <summary>
    /// ReverseMarkdown-Konfiguration für die HTML→Markdown-Konvertierung.
    /// Wirkt unabhängig vom CDP-Gate; alle Felder sind optional und fallen auf
    /// die <see cref="MarkdownSettings"/>-Defaults zurück.
    /// </summary>
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
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>CDP-HTTP-Endpoint, typisch <c>http://localhost:9222</c>.</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "http://localhost:9222";

    /// <summary>Timeout (ms) für HTTP-Lookup + WebSocket-Roundtrip.</summary>
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
    [JsonPropertyName("unknownTags")]
    public string? UnknownTags { get; set; }

    /// <summary>GitHub-Flavored-Markdown (Tabellen, fenced code, etc.). Default: <c>false</c>.</summary>
    [JsonPropertyName("githubFlavored")]
    public bool? GithubFlavored { get; set; }

    /// <summary>HTML-Kommentare vor Konvertierung entfernen. Default: <c>true</c>.</summary>
    [JsonPropertyName("removeComments")]
    public bool? RemoveComments { get; set; }

    /// <summary>
    /// Erlaubte URI-Schemes für <c>&lt;a href&gt;</c>. Default in der Library:
    /// <c>{"http", "https", "ftp", "ftps", "mailto", "tel"}</c>.
    /// </summary>
    [JsonPropertyName("whitelistUriSchemes")]
    public List<string>? WhitelistUriSchemes { get; set; }

    /// <summary>
    /// Smarte Href-Behandlung (URL-Dekodierung, Whitespaces). Default: <c>false</c>.
    /// </summary>
    [JsonPropertyName("smartHrefHandling")]
    public bool? SmartHrefHandling { get; set; }

    /// <summary>
    /// Tabellen ohne Header-Zeile: <c>"Default"</c> oder <c>"EmptyRow"</c>.
    /// </summary>
    [JsonPropertyName("tableWithoutHeaderRowHandling")]
    public string? TableWithoutHeaderRowHandling { get; set; }

    /// <summary>
    /// Bullet-Character für unsortierte Listen. Als einzelnes Zeichen oder String;
    /// bei leerem Wert fällt die Library auf <c>'*'</c> zurück.
    /// </summary>
    [JsonPropertyName("listBulletChar")]
    public string? ListBulletChar { get; set; }

    /// <summary>Default-Sprache für Code-Blöcke (z. B. <c>"text"</c>, <c>"bash"</c>).</summary>
    [JsonPropertyName("defaultCodeBlockLanguage")]
    public string? DefaultCodeBlockLanguage { get; set; }
}

public sealed class NotepadConfig
{
    [JsonPropertyName("maxBufferKB")]
    public int MaxBufferKB { get; set; } = 256;
}

public sealed class CaptureConfig
{
    [JsonPropertyName("rootPath")]
    public string RootPath { get; set; } = "capture";

    [JsonPropertyName("screenshotFormat")]
    public string ScreenshotFormat { get; set; } = "png";
}

public sealed class ScreenRecorderConfig
{
    [JsonPropertyName("throttleMs")]
    public int ThrottleMs { get; set; } = 1000;

    [JsonPropertyName("periodicCaptureMs")]
    public int PeriodicCaptureMs { get; set; } = 0;

    /// <summary>Process names (case-insensitive substring) to skip.</summary>
    [JsonPropertyName("ignoreApps")]
    public List<string> IgnoreApps { get; set; } = new();

    /// <summary>URL substrings (case-insensitive) to skip when an app context URL is known.</summary>
    [JsonPropertyName("ignoreUrls")]
    public List<string> IgnoreUrls { get; set; } = new();

    /// <summary>Window title substrings (case-insensitive) to skip.</summary>
    [JsonPropertyName("ignoreWindowTitles")]
    public List<string> IgnoreWindowTitles { get; set; } = new();
}

public sealed class OcrConfig
{
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "tesseract";

    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = new() { "deu", "eng" };

    /// <summary>Path to the tessdata directory. Relative paths resolve to AppContext.BaseDirectory.</summary>
    [JsonPropertyName("tessDataPath")]
    public string TessDataPath { get; set; } = "tessdata";
}

public sealed class LoggingConfig
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    /// <summary>Log directory. <c>null</c> disables file logging. Relative paths resolve to AppContext.BaseDirectory.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; } = "logs";
}
