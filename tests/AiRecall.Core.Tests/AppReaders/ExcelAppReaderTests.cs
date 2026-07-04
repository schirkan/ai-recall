using AiRecall.AppReader.Base;
using AiRecall.AppReader.Documents;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer ExcelAppReader. Office in Sandbox nicht verfuegbar
/// (Martin 2026-07-04) → Title-Parsing und Fallback-Reads.
/// COM-Integration-Tests mit <c>[Trait("Integration", "Office")]</c>.
/// </summary>
public class ExcelAppReaderTests
{
    private static WindowInfo Win(string title) =>
        new(IntPtr.Zero, title, 1234, "EXCEL", true, new WindowRect(0, 0, 100, 100));

    private static AppReaderContext Ctx() => new()
    {
        Config = new AppConfig(),
        Logger = new LoggerConfiguration().CreateLogger()
    };

    [Fact]
    public void SupportedProcesses_ContainsExcel()
    {
        var reader = new ExcelAppReader();
        Assert.Contains("EXCEL", reader.SupportedProcesses);
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var reader = new ExcelAppReader();
        Assert.False(string.IsNullOrEmpty(reader.DisplayName));
    }

    [Fact]
    public void CanRead_Excel_True()
    {
        var reader = new ExcelAppReader();
        Assert.True(reader.CanRead(Win("Sheet.xlsx - Excel")));
    }

    [Fact]
    public void CanRead_Notepad_False()
    {
        var reader = new ExcelAppReader();
        Assert.False(reader.CanRead(new WindowInfo(IntPtr.Zero, "x", 1, "notepad", true, new WindowRect(0, 0, 100, 100))));
    }

    // ----- ParseTitle (Fallback-Pfad) -----

    [Fact]
    public void ParseTitle_NormalXlsx_ReturnsFilename()
    {
        var (name, untitled, readOnly) = ExcelAppReader.ParseTitle("Sheet.xlsx - Excel");
        Assert.Equal("Sheet.xlsx", name);
        Assert.False(untitled);
        Assert.False(readOnly);
    }

    [Fact]
    public void ParseTitle_UnsavedMarker_Stripped()
    {
        var (name, untitled, _) = ExcelAppReader.ParseTitle("*Sheet.xlsx - Excel");
        Assert.Equal("Sheet.xlsx", name);
        Assert.False(untitled);
    }

    [Fact]
    public void ParseTitle_Book1_IsUntitled()
    {
        var (name, untitled, _) = ExcelAppReader.ParseTitle("Book1 - Excel");
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_EmptyAfterStrip_IsUntitled()
    {
        var (name, untitled, _) = ExcelAppReader.ParseTitle(" - Excel");
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_ReadOnlyFlag_Detected()
    {
        var (name, untitled, readOnly) = ExcelAppReader.ParseTitle("Sheet.xlsx [Read-Only] - Excel");
        Assert.Equal("Sheet.xlsx", name);
        Assert.False(untitled);
        Assert.True(readOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTitle_EmptyOrNull_ReturnsUntitled(string? title)
    {
        var (name, untitled, _) = ExcelAppReader.ParseTitle(title);
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_NoExcelSuffix_ReturnsTitleAsIs()
    {
        var (name, untitled, _) = ExcelAppReader.ParseTitle("Sheet.xlsx");
        Assert.Equal("Sheet.xlsx", name);
        Assert.False(untitled);
    }

    // ----- Read (Smoke, COM liefert null in Sandbox) -----

    [Fact]
    public void Read_StubXlsx_NoCrash_IncludesFileName()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Budget.xlsx - Excel"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("spreadsheet", result!.ContextKind);
        Assert.Equal("Budget.xlsx", result.ContextLabel);
        Assert.Contains("Budget.xlsx", result.ContentMarkdown);
        Assert.Equal("title-uia", result.Extra!["source"]);
        Assert.Equal("False", result.Extra["hasContent"]);
        Assert.Equal("False", result.Extra["isUntitled"]);
    }

    [Fact]
    public void Read_StubUntitled_UsesUntitledLabel()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Book1 - Excel"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
        Assert.Contains("untitled", result.ContentMarkdown);
    }

    [Fact]
    public void Read_StubReadOnly_IncludesModeLine()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Locked.xlsx [Read-Only] - Excel"), Ctx());

        Assert.NotNull(result);
        Assert.Contains("Read-Only", result!.ContentMarkdown);
    }

    [Fact]
    public void Read_UiaDisabled_StillReturnsResultWithoutBody()
    {
        var cfg = new AppConfig();
        cfg.AppReader.Documents.EnableUiaExtraction = false;
        var ctx = new AppReaderContext { Config = cfg, Logger = new LoggerConfiguration().CreateLogger() };

        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Sheet.xlsx - Excel"), ctx);

        Assert.NotNull(result);
        Assert.Equal("False", result!.Extra!["hasContent"]);
    }

    // ----- Integration: COM (nur mit Office) -----

    [Fact]
    [Trait("Integration", "Office")]
    public void Read_ComAvailable_SetsFilePath()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Test.xlsx - Excel"), Ctx());

        if (result == null || result.Extra == null) return;
        if (!result.Extra.TryGetValue("source", out var src) || src != "com") return;

        Assert.True(result.Extra.ContainsKey("filePath"));
        Assert.False(string.IsNullOrEmpty(result.Extra["filePath"]));
    }
}