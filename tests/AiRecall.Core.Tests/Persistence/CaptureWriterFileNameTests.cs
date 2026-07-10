using AiRecall.Core.Models;
using AiRecall.Core.Persistence;

namespace AiRecall.Core.Tests.Persistence;

/// <summary>
/// Schema-Lock für CaptureWriter-Dateinamen (Martin-Direktive 2026-07-10 21:27):
/// Der Fenstertitel wird BEWUSST NICHT in den Dateinamen aufgenommen — Titel
/// können beliebig lang sein und führen auf Windows zu Path-Length-Problemen.
/// Titel landen unverändert im YAML-Frontmatter und als H1 im Body, der
/// Dateiname enthält nur <c>{HHmmss-fff}</c>. Diese Tests verhindern eine
/// versehentliche Re-Einführung des Title-Slugs.
/// </summary>
public class CaptureWriterFileNameTests : IDisposable
{
    private readonly string _tempRoot;

    public CaptureWriterFileNameTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ai-recall-filename-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* ignore */ }
    }

    private static WindowInfo MakeWindow(string title, string process = "msedge", int pid = 1) =>
        new WindowInfo(new IntPtr(0x123), title, pid, process, true, new WindowRect(0, 0, 100, 100));

    [Fact]
    public void Write_Filename_HasOnlyTimestamp_NotTitle()
    {
        // Sehr langer Browser-Titel — wenn er im Filename landet, würde der Test fehlschlagen
        const string longTitle = "Willkommen bei Microsoft 365 - Häufig gestellte Fragen zur Verwaltung - Microsoft Edge";
        var window = MakeWindow(longTitle, "msedge");
        var item = CaptureWriter.Write(window, new byte[] { 0x01 }, "x", "h", _tempRoot);

        var filename = Path.GetFileNameWithoutExtension(item.MarkdownPath);
        Assert.Matches(@"^\d{6}-\d{3}$", filename); // HHmmss-fff exakt 6+3 Ziffern mit "-"
        Assert.DoesNotContain("Willkommen", filename);
        Assert.DoesNotContain("Microsoft", filename);
        Assert.DoesNotContain("Edge", filename);
    }

    [Fact]
    public void WritePending_Filename_HasOnlyTimestamp_NotTitle()
    {
        const string longTitle = "Document.docx - Word";
        var window = MakeWindow(longTitle, "WINWORD");
        var item = CaptureWriter.WritePending(window, new byte[] { 0x01 }, "h", _tempRoot);

        var filename = Path.GetFileNameWithoutExtension(item.MarkdownPath);
        Assert.Matches(@"^\d{6}-\d{3}$", filename);
        Assert.DoesNotContain("Document", filename);
        Assert.DoesNotContain("Word", filename);
    }

    [Fact]
    public void Write_Filename_PngMatchesMdBasename()
    {
        // PNG- und MD-Datei müssen denselben Basename haben, sonst bricht die Referenz
        // im Markdown-Frontmatter (`screenshot:`-Link).
        var window = MakeWindow("X - App", "chrome");
        var item = CaptureWriter.Write(window, new byte[] { 0xDE, 0xAD }, "x", "h", _tempRoot);

        var mdBase = Path.GetFileNameWithoutExtension(item.MarkdownPath);
        var pngBase = Path.GetFileNameWithoutExtension(item.ScreenshotPath);
        Assert.Equal(mdBase, pngBase);
    }

    [Fact]
    public void Write_PathIsUnderProcessAndDayFolders()
    {
        // Struktur: {root}/yyyy-MM-dd/{process}/{HHmmss-fff}.md
        var window = MakeWindow("T", "msedge", pid: 42);
        var item = CaptureWriter.Write(window, new byte[] { 0x01 }, "x", "h", _tempRoot);

        var rel = Path.GetRelativePath(_tempRoot, item.MarkdownPath);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(3, segments.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", segments[0]); // yyyy-MM-dd
        Assert.Equal("msedge", segments[1]);
        Assert.Matches(@"^\d{6}-\d{3}\.md$", segments[2]);
    }

    [Fact]
    public void Write_ContentMd_SharesBasenameWithMainMd()
    {
        // Wenn ein App-Reader liefert, muss {base}.content.md denselben Basename haben wie {base}.md,
        // sonst kann das Frontmatter-Link nicht zwischen beiden korrelieren.
        var window = MakeWindow("T", "msedge");
        var item = CaptureWriter.Write(window, new byte[] { 0x01 }, "x", "h", _tempRoot);
        var mainDir = Path.GetDirectoryName(item.MarkdownPath)!;
        var mainMdName = Path.GetFileName(item.MarkdownPath); // "HHmmss-fff.md"

        var record = new AppContentRecord(
            ContentMarkdown: "**URL:** https://example.com",
            ContextLabel: "https://example.com",
            ContextKind: "url",
            ReaderName: "Browser",
            ReaderVersion: "1.0",
            Extra: new Dictionary<string, string> { ["url"] = "https://example.com" });
        var contentPath = CaptureWriter.WriteContent(record, window, item.Timestamp, _tempRoot);
        var contentMdName = Path.GetFileName(contentPath); // "HHmmss-fff.content.md"

        Assert.Equal(mainDir, Path.GetDirectoryName(contentPath));
        Assert.EndsWith(".content.md", contentMdName);
        Assert.Equal(mainMdName.Replace(".md", ""), contentMdName.Replace(".content.md", ""));
    }

    [Fact]
    public void Write_VeryLongTitle_DoesNotBreakWindowsPathLimit()
    {
        // Windows MAX_PATH ist 260 Zeichen. Wenn der Titel im Filename landet,
        // kann dieser Test auf einem realen Windows-System fehlschlagen.
        var monsterTitle = new string('A', 200) + " - Microsoft Edge";
        var window = MakeWindow(monsterTitle, "msedge");
        var item = CaptureWriter.Write(window, new byte[] { 0x01 }, "x", "h", _tempRoot);

        // Voller Pfad muss unter 260 Zeichen bleiben
        var fullPath = item.MarkdownPath;
        Assert.True(fullPath.Length < 260,
            $"Path too long ({fullPath.Length}): {fullPath}");
    }
}