using AiRecall.Conversion;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;
using Serilog;

namespace AiRecall.Core.Tests.Conversion;

/// <summary>
/// Tests fuer ConversionWorker mit OCR-Integration (Spec 0007 Schritt 4).
/// Verwendet <see cref="FakeOcrEngine"/> statt Tesseract (kein tessdata noetig).
/// </summary>
public class ConversionWorkerOcrTests : IDisposable
{
    private readonly string _tempDir;

    public ConversionWorkerOcrTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"conv-ocr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static AppConfig Cfg() => new()
    {
        Conversion = new ConversionConfig { MaxTextKB = 64 }
    };

    private static ILogger Logger() => new LoggerConfiguration().CreateLogger();

    private static WindowInfo Win(string title) =>
        new(IntPtr.Zero, title, 1234, "WINWORD", true, new WindowRect(0, 0, 100, 100));

    private (string mdPath, string pngPath) CreatePendingCapture(
        string title,
        string? filePath,
        bool writeScreenshot = true)
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        var item = CaptureWriter.WritePending(Win(title), pngBytes, "hash123", _tempDir, filePath: filePath);

        if (!writeScreenshot)
        {
            // Screenshot loeschen + Frontmatter-Eintrag entfernen → OCR muss skippen
            File.Delete(item.ScreenshotPath);
            var content = File.ReadAllText(item.MarkdownPath);
            content = content.Replace("screenshot: " + Path.GetFileName(item.ScreenshotPath) + "\r\n", "");
            content = content.Replace("screenshot: " + Path.GetFileName(item.ScreenshotPath) + "\n", "");
            File.WriteAllText(item.MarkdownPath, content);
        }
        return (item.MarkdownPath, item.ScreenshotPath);
    }

    private static async Task WaitForPending(ConversionWorker worker, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && worker.PendingCount > 0)
        {
            await Task.Delay(50);
        }
    }

    // ----- OCR mit FakeOcrEngine -----

    [Fact]
    public async Task EnqueueAsync_WithOcrAndFilePath_BothSectionsInContent()
    {
        var txtPath = Path.Combine(_tempDir, "doc.txt");
        File.WriteAllText(txtPath, "Document content here");

        var (mdPath, _) = CreatePendingCapture("doc.txt - Notepad", txtPath);
        var ocrEngine = new FakeOcrEngine("Hello from OCR");

        using var worker = new ConversionWorker(Cfg(), Logger(), ocrEngine);
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker);

        var contentPath = Path.ChangeExtension(mdPath, ".conversion.md");
        var content = File.ReadAllText(contentPath);
        Assert.Contains("## Document content", content);
        Assert.Contains("Document content here", content);
        Assert.Contains("## OCR Content", content);
        Assert.Contains("Hello from OCR", content);

        var md = File.ReadAllText(mdPath);
        Assert.Contains("conversion: \"done\"", md);
        Assert.Contains("ocr=ok,fake", md); // conversionSteps steht im Haupt-MD-Frontmatter
    }

    [Fact]
    public async Task EnqueueAsync_OcrEmpty_NoOcrSectionInContent()
    {
        var txtPath = Path.Combine(_tempDir, "doc.txt");
        File.WriteAllText(txtPath, "Just document");

        var (mdPath, _) = CreatePendingCapture("doc.txt - Notepad", txtPath);
        var ocrEngine = new FakeOcrEngine(""); // empty OCR

        using var worker = new ConversionWorker(Cfg(), Logger(), ocrEngine);
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker);

        var contentPath = Path.ChangeExtension(mdPath, ".conversion.md");
        var content = File.ReadAllText(contentPath);
        Assert.DoesNotContain("## OCR Content", content); // keine Section bei leerem OCR
        Assert.Contains("## Document content", content);

        var md = File.ReadAllText(mdPath);
        Assert.Contains("ocr=ok,empty,fake", md);
    }

    [Fact]
    public async Task EnqueueAsync_OcrFails_PartialStatus()
    {
        var txtPath = Path.Combine(_tempDir, "doc.txt");
        File.WriteAllText(txtPath, "Document content");

        var (mdPath, _) = CreatePendingCapture("doc.txt - Notepad", txtPath);
        var ocrEngine = new FailingOcrEngine();

        using var worker = new ConversionWorker(Cfg(), Logger(), ocrEngine);
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker);

        var md = File.ReadAllText(mdPath);
        // OCR failed, doc ok → partial
        Assert.Contains("conversion: \"partial\"", md);
        Assert.Contains("ocr=fail,InvalidOperationException", md);
        Assert.Contains("doc=ok", md);
        Assert.True(worker.PartialCount >= 1);
    }

    [Fact]
    public async Task EnqueueAsync_NoScreenshot_OcrSkipped()
    {
        // writeScreenshot=false: PNG-Datei loeschen + Frontmatter-Eintrag entfernen
        var (mdPath, _) = CreatePendingCapture("no-screenshot", filePath: null, writeScreenshot: false);
        var ocrEngine = new FakeOcrEngine("text");

        using var worker = new ConversionWorker(Cfg(), Logger(), ocrEngine);
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker);

        var md = File.ReadAllText(mdPath);
        // kein screenshot im Frontmatter → ocr skip
        Assert.Contains("ocr=skip,no-screenshot", md);
        Assert.False(File.Exists(Path.ChangeExtension(mdPath, ".conversion.md")));
    }

    [Fact]
    public async Task NullOcrEngine_UsedAsDefault_WorksGracefully()
    {
        var txtPath = Path.Combine(_tempDir, "doc.txt");
        File.WriteAllText(txtPath, "Document");

        var (mdPath, _) = CreatePendingCapture("doc.txt", txtPath);

        using var worker = new ConversionWorker(Cfg(), Logger()); // kein ocrEngine → NullOcrEngine
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker);

        var contentPath = Path.ChangeExtension(mdPath, ".conversion.md");
        var content = File.ReadAllText(contentPath);
        Assert.DoesNotContain("## OCR Content", content);
        Assert.Contains("## Document content", content);
    }
}

/// <summary>Fake-OCR-Engine fuer Tests. Liefert festen Text oder leeren String.</summary>
internal sealed class FakeOcrEngine : IOcrEngine
{
    public string Name => "fake";
    private readonly string _text;
    public FakeOcrEngine(string text) => _text = text;
    public Task<string> ExtractTextAsync(byte[] pngBytes, CancellationToken ct = default)
        => Task.FromResult(_text);
}

/// <summary>OCR-Engine, die immer eine Exception wirft.</summary>
internal sealed class FailingOcrEngine : IOcrEngine
{
    public string Name => "failing";
    public Task<string> ExtractTextAsync(byte[] pngBytes, CancellationToken ct = default)
        => throw new InvalidOperationException("simulated OCR failure");
}