using AiRecall.AppReader.Base;
using AiRecall.AppReader.Documents;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer WordAppReader. Da Office in der Sandbox nicht verfuegbar ist
/// (Martin 2026-07-04), werden nur Title-Parsing und Smoke-Reads mit
/// IntPtr.Zero (kein UIA) geprueft.
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

    // ----- ParseTitle -----

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
        // Word-Doc in einem Container ohne " - Word"-Suffix (z. B. PDF-Preview in Outlook)
        var (name, untitled, _, _) = WordAppReader.ParseTitle("Document.docx");
        Assert.Equal("Document.docx", name);
        Assert.False(untitled);
    }

    // ----- Read (Smoke) -----

    [Fact]
    public void Read_StubDocx_NoCrash_IncludesFileName()
    {
        var reader = new WordAppReader();
        var result = reader.Read(Win("ProjectPlan.docx - Word"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("document", result!.ContextKind);
        Assert.Equal("ProjectPlan.docx", result.ContextLabel);
        Assert.Contains("**File:** `ProjectPlan.docx`", result.ContentMarkdown);
        // IntPtr.Zero → UIA liefert nichts → hasUiaText false
        Assert.Equal("False", result.Extra!["hasUiaText"]);
        Assert.Equal("False", result.Extra["isUntitled"]);
        Assert.Equal("False", result.Extra["isReadOnly"]);
        Assert.Equal("False", result.Extra["isSafeMode"]);
    }

    [Fact]
    public void Read_StubUntitled_UsesUntitledLabel()
    {
        var reader = new WordAppReader();
        var result = reader.Read(Win("Document1 - Word"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
        Assert.Contains("**File:** _(untitled)_", result.ContentMarkdown);
        Assert.Equal("True", result.Extra!["isUntitled"]);
    }

    [Fact]
    public void Read_StubReadOnly_IncludesModeLine()
    {
        var reader = new WordAppReader();
        var result = reader.Read(Win("Locked.docx [Read-Only] - Word"), Ctx());

        Assert.NotNull(result);
        Assert.Contains("**Mode:** Read-Only", result!.ContentMarkdown);
        Assert.Equal("True", result.Extra!["isReadOnly"]);
    }

    [Fact]
    public void Read_UiaDisabled_StillReturnsResultWithoutBody()
    {
        var cfg = new AppConfig();
        cfg.AppReader.Documents.EnableUiaExtraction = false;
        var ctx = new AppReaderContext { Config = cfg, Logger = new LoggerConfiguration().CreateLogger() };

        var reader = new WordAppReader();
        var result = reader.Read(Win("Doc.docx - Word"), ctx);

        Assert.NotNull(result);
        // hasUiaText muss false sein, weil UIA deaktiviert ist
        Assert.Equal("False", result!.Extra!["hasUiaText"]);
    }

    [Fact]
    public void Read_StubEmptyTitle_ReturnsUntitled()
    {
        var reader = new WordAppReader();
        var result = reader.Read(new WindowInfo(IntPtr.Zero, "", 1, "WINWORD", true, new WindowRect(0, 0, 100, 100)), Ctx());

        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
    }
}