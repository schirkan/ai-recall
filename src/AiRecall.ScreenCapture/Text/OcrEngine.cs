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

        // Bug-Bash 2026-07-06 I-14: Mehrere Suchpfade probieren statt nur dem
        // konfigurierten. Reihenfolge: explizit konfigurierter Pfad (absolut
        // oder relativ zu BaseDirectory) > %LOCALAPPDATA%\AiRecall\tessdata
        // (Standard-Drop-In fuer manuelles Setup) > BaseDirectory\tessdata.
        // So findet Tesseract tessdata auch wenn es nicht neben der EXE liegt.
        var candidatePaths = new List<string>();
        if (Path.IsPathRooted(config.TessDataPath))
        {
            candidatePaths.Add(config.TessDataPath);
        }
        else
        {
            candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, config.TessDataPath));
            candidatePaths.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiRecall", config.TessDataPath));
        }

        var tessDataPath = candidatePaths.FirstOrDefault(Directory.Exists);
        if (tessDataPath is null)
        {
            throw new DirectoryNotFoundException(
                $"Tessdata directory not found. Searched: {string.Join(", ", candidatePaths)}. " +
                "Download language files (e.g. deu.traineddata, eng.traineddata) from " +
                "https://github.com/tesseract-ocr/tessdata_fast and place them in " +
                $"one of these folders, or set ocr.tessDataPath in config.json. " +
                "Or set ocr.engine=\"none\" in config.json to skip OCR entirely.");
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
