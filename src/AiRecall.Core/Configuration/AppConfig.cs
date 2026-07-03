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
