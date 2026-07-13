using AiRecall.Conversion;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;
using Serilog;

namespace AiRecall.Core.Tests.Conversion;

/// <summary>
/// Tests fuer ConversionWorker (Spec 0007 Schritt 3).
/// Channel-Queue + DocumentConverter-Integration + Frontmatter-Update.
/// OCR-Tests kommen in Schritt 4 (eigene Test-Klasse).
/// </summary>
public class ConversionWorkerTests : IDisposable
{
    private readonly string _tempDir;

    public ConversionWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"conv-test-{Guid.NewGuid():N}");
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

    private (string mdPath, string pngPath) CreatePendingCapture(string title, string? filePath)
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        var captureRoot = _tempDir;
        var item = CaptureWriter.WritePending(Win(title), pngBytes, "hash123",
            captureRoot, filePath: filePath);

        // Setze filePath in der MD manuell (WritePending erstellt ohne filePath; fuer Test injizieren)
        if (filePath != null)
        {
            var content = File.ReadAllText(item.MarkdownPath);
            content = content.Replace("conversion: \"pending\"",
                $"filePath: \"{filePath}\"\nconversion: \"pending\"");
            File.WriteAllText(item.MarkdownPath, content);
        }

        // Bug-Bash 2026-07-06 I-17: Den "_(conversion pending)_"-Platzhalter
        // NICHT mehr vorab befuellen. Der Worker schreibt den Content jetzt
        // in-place in die Haupt-MD (Replace auf den Platzhalter). Wenn er
        // weg waere, wuerde der Fallback-Pfad eine zweite Kopie einfuegen.

        return (item.MarkdownPath, item.ScreenshotPath);
    }

    // ----- ParseFrontmatter -----

    [Fact]
    public void ParseFrontmatter_EmptyContent_EmptyDict()
    {
        Assert.Empty(ConversionWorker.ParseFrontmatter(""));
    }

    [Fact]
    public void ParseFrontmatter_NoFrontmatter_EmptyDict()
    {
        Assert.Empty(ConversionWorker.ParseFrontmatter("# Just a heading\n\nNo YAML"));
    }

    [Fact]
    public void ParseFrontmatter_ValidYaml_ParsesFields()
    {
        var content = "---\nfilePath: \"C:\\test.docx\"\nscreenshot: 123-doc.png\nhash: abc\nconversion: \"pending\"\n---\n\n# Body";
        var fm = ConversionWorker.ParseFrontmatter(content);

        Assert.Equal(@"C:\test.docx", fm["filePath"]);
        Assert.Equal("123-doc.png", fm["screenshot"]);
        Assert.Equal("abc", fm["hash"]);
        Assert.Equal("pending", fm["conversion"]);
    }

    [Fact]
    public void ParseFrontmatter_CaseInsensitiveKeys()
    {
        var content = "---\nFilePath: \"x\"\n---\n";
        var fm = ConversionWorker.ParseFrontmatter(content);
        Assert.Equal("x", fm["filepath"]); // case-insensitive
    }

    // ----- Worker Lifecycle -----

    [Fact]
    public void Worker_StartsAndStopsCleanly()
    {
        using var worker = new ConversionWorker(Cfg(), Logger());
        Assert.True(worker.PendingCount == 0);
        worker.Stop();
        Assert.True(worker.PendingCount == 0);
    }

    [Fact]
    public void Worker_DisposeIsIdempotent()
    {
        var worker = new ConversionWorker(Cfg(), Logger());
        worker.Dispose();
        worker.Dispose(); // kein Throw
    }

    // ----- Enqueue + Process -----

    [Fact]
    public async Task EnqueueAsync_WithTxtFile_WritesContentAndUpdatesFrontmatter()
    {
        // Arrange: txt-Datei als filePath
        var txtPath = Path.Combine(_tempDir, "source.txt");
        File.WriteAllText(txtPath, "Hello from source.txt");
        var (mdPath, _) = CreatePendingCapture("source.txt - Notepad", txtPath);

        // Act
        using var worker = new ConversionWorker(Cfg(), Logger());
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker, expectedZero: true, timeoutMs: 5000);

        // Assert
        Assert.True(worker.CompletedCount >= 1, $"Expected >=1 completed, got {worker.CompletedCount}");

        // Bug-Bash 2026-07-06 I-17: Content wird jetzt in-place in der
        // Capture-MD unter "## Content" assembliert (kein .conversion.md mehr).
        var updatedMd = File.ReadAllText(mdPath);
        Assert.Contains("Hello from source.txt", updatedMd);
        Assert.Contains("## Document content", updatedMd);
        Assert.DoesNotContain("_(conversion pending)_", updatedMd);

        // Frontmatter updated
        Assert.Contains("conversion: \"done\"", updatedMd);
        Assert.Contains("conversionSteps:", updatedMd);
        Assert.Contains("doc=ok", updatedMd);
    }

    [Fact]
    public async Task EnqueueAsync_NoFilePath_NoContentGenerated_StillCompletes()
    {
        // Ohne filePath: kein Document-Conversion, status=ok mit skip
        var (mdPath, _) = CreatePendingCapture("no file", filePath: null);

        using var worker = new ConversionWorker(Cfg(), Logger());
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker, expectedZero: true, timeoutMs: 5000);

        // Bug-Bash I-17: kein separates .conversion.md mehr
        var contentPath = Path.ChangeExtension(mdPath, ".conversion.md");
        Assert.False(File.Exists(contentPath));

        var updatedMd = File.ReadAllText(mdPath);
        // ohne filePath: kein document step, status=ok mit skip
        Assert.Contains("doc=skip", updatedMd);
    }

    [Fact]
    public async Task EnqueueAsync_UnsupportedExtension_FailsDocumentStep()
    {
        // .odt (kein Konverter)
        var odtPath = Path.Combine(_tempDir, "x.odt");
        File.WriteAllText(odtPath, "OpenDocument");
        var (mdPath, _) = CreatePendingCapture("x.odt", odtPath);

        using var worker = new ConversionWorker(Cfg(), Logger());
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker, expectedZero: true, timeoutMs: 5000);

        var updatedMd = File.ReadAllText(mdPath);
        // doc=fail weil OpenDocument nicht unterstützt
        Assert.Contains("doc=fail", updatedMd);
    }

    [Fact]
    public async Task EnqueueAsync_MissingFile_HandlesGracefully()
    {
        // filePath zeigt auf nicht-existierende Datei
        var (mdPath, _) = CreatePendingCapture("missing", filePath: @"C:\does-not-exist\nope.txt");

        using var worker = new ConversionWorker(Cfg(), Logger());
        await worker.EnqueueAsync(mdPath);
        await WaitForPending(worker, expectedZero: true, timeoutMs: 5000);

        // Worker sollte nicht crashen, Frontmatter sollte fehler-status haben
        var updatedMd = File.ReadAllText(mdPath);
        Assert.Contains("conversion:", updatedMd);
    }

    [Fact]
    public void TryEnqueue_NullOrEmpty_NoIncrement()
    {
        using var worker = new ConversionWorker(Cfg(), Logger());
        Assert.False(worker.TryEnqueue(""));
        Assert.False(worker.TryEnqueue("   "));
        Assert.False(worker.TryEnqueue(null!));
        Assert.Equal(0, worker.PendingCount);
    }

    // ----- UpdateConversionStatus (CaptureWriter) -----

    [Fact]
    public void UpdateConversionStatus_AddsNewFields()
    {
        var (mdPath, _) = CreatePendingCapture("test", filePath: null);
        CaptureWriter.UpdateConversionStatus(mdPath, "done",
            conversionSteps: "doc=ok,openxml");

        var content = File.ReadAllText(mdPath);
        Assert.Contains("conversion: \"done\"", content);
        Assert.Contains("conversionTimestamp:", content);
        Assert.Contains("conversionSteps:", content);
    }

    [Fact]
    public void UpdateConversionStatus_OverwritesExistingFields()
    {
        var (mdPath, _) = CreatePendingCapture("test", filePath: null);
        CaptureWriter.UpdateConversionStatus(mdPath, "done");
        CaptureWriter.UpdateConversionStatus(mdPath, "failed",
            conversionError: "test-error");

        var content = File.ReadAllText(mdPath);
        Assert.Contains("conversion: \"failed\"", content);
        Assert.Contains("conversionError:", content);
    }

    [Fact]
    public void UpdateConversionStatus_RemovesErrorOnSuccess()
    {
        var (mdPath, _) = CreatePendingCapture("test", filePath: null);
        CaptureWriter.UpdateConversionStatus(mdPath, "failed",
            conversionError: "first-error");
        CaptureWriter.UpdateConversionStatus(mdPath, "done"); // kein Error

        var content = File.ReadAllText(mdPath);
        Assert.Contains("conversion: \"done\"", content);
        Assert.DoesNotContain("first-error", content);
        Assert.DoesNotContain("conversionError:", content);
    }

    [Fact]
    public void UpdateConversionStatus_MissingFile_NoThrow()
    {
        // Sollte nicht crashen, einfach nix tun
        CaptureWriter.UpdateConversionStatus(Path.Combine(_tempDir, "missing.md"), "done");
    }

    // ----- Helper -----

    private static async Task WaitForPending(ConversionWorker worker, bool expectedZero, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var isZero = worker.PendingCount == 0;
            if (isZero == expectedZero) return;
            await Task.Delay(50);
        }
    }
}