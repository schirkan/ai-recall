using AiRecall.Core.Configuration;
using Tesseract;

namespace AiRecall.ScreenCapture.Text;

/// <summary>
/// OCR engine wrapper. Currently only the Tesseract backend is supported;
/// configured via <c>ocr.engine = "tesseract"</c>.
/// Tessdata language files must live under <see cref="OcrConfig.TessDataPath"/>
/// (relative paths resolve to <c>AppContext.BaseDirectory</c>).
/// </summary>
public sealed class OcrEngine : IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly OcrConfig _config;

    public OcrEngine(OcrConfig config)
    {
        _config = config;

        if (!string.Equals(config.Engine, "tesseract", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"OCR engine '{config.Engine}' is not supported. Only 'tesseract' is available in MVP1.");
        }

        var tessDataPath = Path.IsPathRooted(config.TessDataPath)
            ? config.TessDataPath
            : Path.Combine(AppContext.BaseDirectory, config.TessDataPath);

        if (!Directory.Exists(tessDataPath))
        {
            throw new DirectoryNotFoundException(
                $"Tessdata directory not found: {tessDataPath}. " +
                "Download language files (e.g. deu.traineddata, eng.traineddata) from " +
                "https://github.com/tesseract-ocr/tessdata_fast and place them in that folder. " +
                "Or run 'recall active-window --no-ocr' to skip OCR.");
        }

        var langs = string.Join("+", config.Languages);
        _engine = new TesseractEngine(tessDataPath, langs, EngineMode.Default);
    }

    /// <summary>Extract text from a PNG byte array. Returns the recognized text (may be empty).</summary>
    public string ExtractText(byte[] pngBytes)
    {
        using var img = Pix.LoadFromMemory(pngBytes);
        using var page = _engine.Process(img);
        return page.GetText() ?? string.Empty;
    }

    public void Dispose() => _engine.Dispose();
}
