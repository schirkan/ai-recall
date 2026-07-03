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
