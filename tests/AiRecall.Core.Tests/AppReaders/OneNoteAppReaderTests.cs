using AiRecall.AppReader.Base;
using AiRecall.AppReader.OneNote;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;
using Xunit;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="OneNoteAppReader"/> (Spec 0010, Cluster 4).
///
/// <para>
/// COM-spezifische Pfade (active page reading) benoetigen installiertes OneNote
/// und werden mit <c>[Trait("Integration", "OneNote")]</c> markiert. Wir testen
/// hier v. a. die COM-unabhaengige Logik: Public-Surface, Static-Helpers und
/// den internen <c>BuildFullMarkdown</c>-Helper (direkt mit Mock-Hierarchy).
/// </para>
/// </summary>
public class OneNoteAppReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger _logger;

    public OneNoteAppReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "onenote-appreader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger = new LoggerConfiguration().WriteTo.Sink(new NullSink()).CreateLogger();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* ignore */ }
    }

    private static AppConfig DefaultConfig() => new();

    private OneNoteAppReader CreateReader(string? captureRoot = null)
        => new(_logger, captureRoot ?? Path.Combine(_tempDir, "capture"));

    private AppReaderContext CreateContext(OneNoteConfig? onenote = null)
    {
        var cfg = DefaultConfig();
        if (onenote != null) cfg.AppReader.OneNote = onenote;
        return new AppReaderContext { Config = cfg, Logger = _logger };
    }

    private static WindowInfo MakeWindow(string processName = "OneNote", string title = "Test - OneNote")
        => new(IntPtr.Zero, title, 0, processName, true, new WindowRect(0, 0, 800, 600));

    // ============================================================================
    // Public Surface
    // ============================================================================

    [Fact]
    public void SupportedProcesses_ContainsOneNote()
    {
        var reader = new OneNoteAppReader();
        Assert.Contains("OneNote", reader.SupportedProcesses);
    }

    [Fact]
    public void DisplayName_ContainsOneNote()
    {
        var reader = new OneNoteAppReader();
        Assert.False(string.IsNullOrWhiteSpace(reader.DisplayName));
        Assert.Contains("OneNote", reader.DisplayName);
    }

    [Fact]
    public void SupportsBackgroundPolling_IsFalse()
    {
        var reader = new OneNoteAppReader();
        Assert.False(reader.SupportsBackgroundPolling);
    }

    [Fact]
    public void CanRead_MatchesOneNoteProcess_CaseInsensitive()
    {
        var reader = new OneNoteAppReader();
        var win = MakeWindow("onenote", "Notebook - OneNote");
        Assert.True(reader.CanRead(win));
    }

    [Fact]
    public void CanRead_RejectsOtherProcess()
    {
        var reader = new OneNoteAppReader();
        var win = MakeWindow("chrome", "Tab");
        Assert.False(reader.CanRead(win));
    }

    // ============================================================================
    // Static Helpers — ShortId
    // ============================================================================

    [Theory]
    [InlineData(null,          "0")]
    [InlineData("",            "0")]
    [InlineData("   ",         "0")]
    [InlineData("AB12CD34EF",  "AB12CD34")]    // genug Zeichen
    [InlineData("AB12CD34",    "AB12CD34")]    // genau 8
    [InlineData("AB12-CD34-EF", "AB12CD34")]    // Bindestriche weg
    [InlineData("AB12 CD34",   "AB12CD34")]    // Leerzeichen weg
    [InlineData("ABC",         "ABC")]         // < 8 → bleibt
    [InlineData("ABCDEFGHIJKLMNOP", "ABCDEFGH")] // > 8 → trunkiert
    public void ShortId_ProducesExpectedResult(string? input, string expected)
    {
        Assert.Equal(expected, OneNoteAppReader.ShortId(input));
    }

    [Fact]
    public void IsOneNoteProcessRunning_NoThrow()
    {
        // Smoke-Test: loest Process.GetProcessesByName aus. Auf einem CI ohne
        // OneNote liefert es false, auf Martins Workstation true. Wichtig ist
        // nur: keine Exception.
        var exception = Record.Exception(() => OneNoteAppReader.IsOneNoteProcessRunning());
        Assert.Null(exception);
    }

    [Fact]
    public void DefaultCaptureRoot_NonEmpty()
    {
        var root = OneNoteAppReader.DefaultCaptureRoot();
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.Contains("capture", root, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================================
    // Internal Builder — BuildFullMarkdown
    // ============================================================================

    [Fact]
    public void BuildFullMarkdown_PageAndSection_IncludesSectionInFrontmatter()
    {
        var reader = CreateReader();
        var info = MakeHierarchy();
        var cfg = new OneNoteConfig { HierarchyDepth = "PageAndSection" };

        var md = reader.BuildFullMarkdown(info, "Body-Inhalt", "<root/>", cfg);

        Assert.Contains("kind: \"onenote-page\"", md);
        Assert.Contains("pageId: \"PG-1\"", md);
        Assert.Contains("section: \"My Section\"", md);
        Assert.Contains("sectionId: \"SEC-1\"", md);
        Assert.DoesNotContain("notebook:", md);   // PageAndSectionAndNotebook = no notebook here
        Assert.Contains("Body-Inhalt", md);
    }

    [Fact]
    public void BuildFullMarkdown_PageOnly_OmitsSectionAndNotebook()
    {
        var reader = CreateReader();
        var info = MakeHierarchy();
        var cfg = new OneNoteConfig { HierarchyDepth = "PageOnly" };

        var md = reader.BuildFullMarkdown(info, "Body", "<root/>", cfg);

        Assert.Contains("pageId: \"PG-1\"", md);
        Assert.DoesNotContain("section:", md);
        Assert.DoesNotContain("notebook:", md);
        Assert.DoesNotContain("notebookId:", md);
    }

    [Fact]
    public void BuildFullMarkdown_PageAndSectionAndNotebook_IncludesAllFields()
    {
        var reader = CreateReader();
        var info = MakeHierarchy();
        var cfg = new OneNoteConfig { HierarchyDepth = "PageAndSectionAndNotebook" };

        var md = reader.BuildFullMarkdown(info, "Body", "<root/>", cfg);

        Assert.Contains("section: \"My Section\"", md);
        Assert.Contains("notebook: \"My Notebook\"", md);
        Assert.Contains("notebookId: \"NB-1\"", md);
    }

    [Fact]
    public void BuildFullMarkdown_EmptyBody_RendersHint()
    {
        var reader = CreateReader();
        var info = MakeHierarchy();
        var cfg = new OneNoteConfig();

        var md = reader.BuildFullMarkdown(info, "", "<root/>", cfg);

        Assert.Contains("_(empty page)_", md);
    }

    [Fact]
    public void BuildFullMarkdown_IncludesAttachmentsFromXml()
    {
        var reader = CreateReader();
        var info = MakeHierarchy();
        var cfg = new OneNoteConfig();

        const string xmlWithFile = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:InsertedFile path="C:/foo/report.pdf"/>
            </one:Page>
            """;

        var md = reader.BuildFullMarkdown(info, "Body", xmlWithFile, cfg);

        Assert.Contains("attachments: \"report.pdf\"", md);
    }

    private static OneNoteHierarchyInfo MakeHierarchy() =>
        new(
            PageId: "PG-1",
            PageTitle: "Test Page",
            SectionId: "SEC-1",
            SectionTitle: "My Section",
            NotebookId: "NB-1",
            NotebookTitle: "My Notebook",
            LastModified: new DateTime(2026, 7, 5, 18, 0, 0, DateTimeKind.Utc));
}
