using AiRecall.ScreenCapture.Text;
using Serilog;

namespace AiRecall.Conversion;

/// <summary>
/// Adapter: wrappt den bestehenden <see cref="OcrEngine"/> (synchron, Tesseract)
/// in eine async-IOcrEngine-Implementation.
/// Tesseract-Calls sind CPU-bound → <see cref="Task.Run"/> entkoppelt den
/// Capture-Loop. Cancellation via <see cref="CancellationToken"/>.
/// </summary>
public sealed class TesseractOcrEngineAdapter : IOcrEngine
{
    private readonly OcrEngine _inner;
    private readonly ILogger _logger;

    public string Name => "tesseract";

    public TesseractOcrEngineAdapter(OcrEngine inner, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? Serilog.Core.Logger.None;
    }

    public async Task<string> ExtractTextAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        if (pngBytes == null || pngBytes.Length == 0)
        {
            _logger.Warning("TesseractOcrEngineAdapter: empty PNG bytes");
            return string.Empty;
        }
        try
        {
            return await Task.Run(() => _inner.ExtractText(pngBytes), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "TesseractOcrEngineAdapter: OCR failed");
            return string.Empty;
        }
    }
}

/// <summary>
/// Null-Object-Pattern: gibt immer leeren String zurueck, ohne Tesseract zu nutzen.
/// Nuetzlich fuer Tests, CI oder Maschinen ohne tessdata.
/// </summary>
public sealed class NullOcrEngine : IOcrEngine
{
    public string Name => "none";
    public Task<string> ExtractTextAsync(byte[] pngBytes, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}