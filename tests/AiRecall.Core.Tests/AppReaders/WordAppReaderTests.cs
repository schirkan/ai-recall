using AiRecall.AppReader.Base;
using AiRecall.AppReader.Documents;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer WordAppReader. Office in Sandbox nicht verfuegbar
/// (Martin 2026-07-04) → Title-Parsing und Fallback-Reads (COM-Pfad
/// liefert null, Reader faellt auf Title+UIA zurueck).
/// COM-spezifische Tests sind als <c>[Trait("Integration", "Office")]</c>
/// markiert und laufen nur, wenn Office installiert ist.
///
/// Spec 0007 Schritt 7: Reader ist <b>duenn</b> (IsThinReader=true) —
/// liefert nur Title + FileName + ggf. UIA-Rohcontent, kein
/// Content-Markdown-Body. Body kommt async aus dem ConversionWorker.
/// </summary>
public class WordAppReaderTests
{
    private static WindowInfo Win(string title) =>
        new(IntPtr.Zero, title, 1234, "WINWORD", true, new WindowRect(0, 0, 100, 100));

    private static AppReaderContext Ctx() => new()
    {
        Config = new AppConfig(),
        Logger = new LoggerConfiguration().CreateLogger()
    };

    [Fact]
    public void SupportedProcesses_ContainsWinword()
    {
        var reader = new WordAppReader();
        Assert.Contains("WINWORD", reader.SupportedProcesses);
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var reader = new WordAppReader();
        Assert.False(string.IsNullOrEmpty(reader.DisplayName));
    }

    [Fact]
    public void CanRead_Winword_True()
    {
        var reader = new WordAppReader();
        Assert.True(reader.CanRead(Win("Doc.docx - Word")));
    }

    [Fact]
    public void CanRead_Notepad_False()
    {
        var reader = new WordAppReader();
        Assert.False(reader.CanRead(new WindowInfo(IntPtr.Zero, "x", 1, "notepad", true, new WindowRect(0, 0, 100, 100))));
    }

    [Fact]
    public void IsThinReader_True()
    {
        // Spec 0007 Schritt 7: Word-Reader ist duenn
        var reader = new WordAppReader();
        var result = reader.Read(Win("Doc.docx - Word"), Ctx());
        Assert.NotNull(result);
        Assert.True(result!.IsThinReader);
    }

    [Fact]
    public void ContentMarkdown_IsPlaceholder()
    {
        // Duenne Reader: ContentMarkdown ist immer der Platzhalter
        var reader = new WordAppReader();
        var result = reader.Read(Win("Doc.docx - Word"), Ctx());
        Assert.NotNull(result);
        Assert.Equal(WordAppReader.PLACEHOLDER, result!.ContentMarkdown);
    }

    // ----- ParseTitle (Fallback-Pfad, unveraendert) -----

    [Fact]
    public void ParseTitle_NormalDocx_ReturnsFilename()
    {
        var (name, untitled, readOnly, safeMode) = WordAppReader.ParseTitle("Doc.docx - Word");
        Assert.Equal("Doc.docx", name);
        Assert.False(untitled);
        Assert.False(readOnly);
        Assert.False(safeMode);
    }

    [Fact]
    public void ParseTitle_UnsavedMarker_Stripped()
    {
        var (name, untitled, _, _) = WordAppReader.ParseTitle("*Document.docx - Word");
        Assert.Equal("Document.docx", name);
        Assert.False(untitled);
    }

    [Fact]
    public void ParseTitle_Document1_IsUntitled()
    {
        var (name, untitled, _, _) = WordAppReader.ParseTitle("Document1 - Word");
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_EmptyAfterStrip_IsUntitled()
    {
        var (name, untitled, _, _) = WordAppReader.ParseTitle(" - Word");
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_ReadOnlyFlag_Detected()
    {
        var (name, untitled, readOnly, safeMode) = WordAppReader.ParseTitle("Doc.docx [Read-Only] - Word");
        Assert.Equal("Doc.docx", name);
        Assert.False(untitled);
        Assert.True(readOnly);
        Assert.False(safeMode);
    }

    [Fact]
    public void ParseTitle_SafeModeFlag_Detected()
    {
        var (name, untitled, readOnly, safeMode) = WordAppReader.ParseTitle("Doc.docx (Safe Mode) - Word");
        Assert.Equal("Doc.docx", name);
        Assert.False(untitled);
        Assert.False(readOnly);
        Assert.True(safeMode);
    }

    [Fact]
    public void ParseTitle_ReadOnlyAndSafeMode_BothDetected()
    {
        var (name, untitled, readOnly, safeMode) = WordAppReader.ParseTitle("Doc.docx [Read-Only] (Safe Mode) - Word");
        Assert.Equal("Doc.docx", name);
        Assert.False(untitled);
        Assert.True(readOnly);
        Assert.True(safeMode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTitle_EmptyOrNull_ReturnsUntitled(string? title)
    {
        var (name, untitled, _, _) = WordAppReader.ParseTitle(title);
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_NoWordSuffix_ReturnsTitleAsIs()
    {
        var (name, untitled, _, _) = WordAppReader.ParseTitle("Document.docx");
        Assert.Equal("Document.docx", name);
        Assert.False(untitled);
    }

    // ----- Read (Smoke, COM-Pfad liefert null in Sandbox) -----

    [Fact]
    public void Read_StubDocx_NoCrash_ContextLabelAndSource()
    {
        var reader = new WordAppReader();
        var result = reader.Read(Win("ProjectPlan.docx - Word"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("document", result!.ContextKind);
        Assert.Equal("ProjectPlan.docx", result.ContextLabel);
        // Fallback-Pfad in Sandbox: source = "title-uia", kein filePath
        Assert.Equal("title-uia", result.Extra!["source"]);
        Assert.False(result.Extra.ContainsKey("filePath"));
        // UIA liefert in Sandbox null (IntPtr.Zero) → kein uiaContent
        Assert.False(result.Extra.ContainsKey("uiaContent"));
    }

    [Fact]
    public void Read_StubUntitled_UsesUntitledLabel()
    {
        var reader = new WordAppReader();
        var result = reader.Read(Win("Document1 - Word"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
    }

    [Fact]
    public void Read_StubReadOnly_LabelHasOnlyFileName()
    {
        // Duenne Reader: kein Read-Only-Marker mehr im Body, nur ContextLabel
        var reader = new WordAppReader();
        var result = reader.Read(Win("Locked.docx [Read-Only] - Word"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("Locked.docx", result!.ContextLabel);
    }

    [Fact]
    public void Read_UiaDisabled_NoUiaContent()
    {
        var cfg = new AppConfig();
        cfg.AppReader.Documents.EnableUiaExtraction = false;
        var ctx = new AppReaderContext { Config = cfg, Logger = new LoggerConfiguration().CreateLogger() };

        var reader = new WordAppReader();
        var result = reader.Read(Win("Doc.docx - Word"), ctx);

        Assert.NotNull(result);
        Assert.False(result!.Extra!.ContainsKey("uiaContent"));
    }

    [Fact]
    public void Read_StubEmptyTitle_ReturnsUntitled()
    {
        var reader = new WordAppReader();
        var result = reader.Read(new WindowInfo(IntPtr.Zero, "", 1, "WINWORD", true, new WindowRect(0, 0, 100, 100)), Ctx());

        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
    }

    // ----- Integration: COM (nur mit Office) -----

    [Fact]
    [Trait("Integration", "Office")]
    public void Read_ComAvailable_SetsFilePath()
    {
        var reader = new WordAppReader();
        var result = reader.Read(Win("Test.docx - Word"), Ctx());

        if (result == null) return;
        if (result.Extra == null) return;

        var hasCom = result.Extra.TryGetValue("source", out var src) && src == "com";
        if (!hasCom) return;

        Assert.True(result.Extra.ContainsKey("filePath"));
        Assert.False(string.IsNullOrEmpty(result.Extra["filePath"]));
    }
}
