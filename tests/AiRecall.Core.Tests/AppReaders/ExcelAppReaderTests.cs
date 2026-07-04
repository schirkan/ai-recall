using AiRecall.AppReader.Base;
using AiRecall.AppReader.Documents;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer ExcelAppReader. Spec 0007 Schritt 7: Reader ist duenn.
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

    [Fact]
    public void IsThinReader_True()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Sheet.xlsx - Excel"), Ctx());
        Assert.NotNull(result);
        Assert.True(result!.IsThinReader);
    }

    [Fact]
    public void ContentMarkdown_IsPlaceholder()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Sheet.xlsx - Excel"), Ctx());
        Assert.NotNull(result);
        Assert.Equal(ExcelAppReader.PLACEHOLDER, result!.ContentMarkdown);
    }

    // ----- ParseTitle -----

    [Fact]
    public void ParseTitle_NormalXlsx_ReturnsFilename()
    {
        var (name, untitled, _) = ExcelAppReader.ParseTitle("Sheet.xlsx - Excel");
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
    public void ParseTitle_ReadOnly_Detected()
    {
        var (name, _, readOnly) = ExcelAppReader.ParseTitle("Sheet.xlsx [Read-Only] - Excel");
        Assert.Equal("Sheet.xlsx", name);
        Assert.True(readOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseTitle_EmptyOrNull_ReturnsUntitled(string? title)
    {
        var (name, untitled, _) = ExcelAppReader.ParseTitle(title);
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    // ----- Read (Smoke) -----

    [Fact]
    public void Read_StubXlsx_ContextLabelAndSource()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Budget.xlsx - Excel"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("spreadsheet", result!.ContextKind);
        Assert.Equal("Budget.xlsx", result.ContextLabel);
        Assert.Equal("title-uia", result.Extra!["source"]);
        Assert.False(result.Extra.ContainsKey("filePath"));
    }

    [Fact]
    public void Read_StubUntitled_UsesUntitledLabel()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Book1 - Excel"), Ctx());
        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
    }

    [Fact]
    public void Read_StubReadOnly_LabelHasOnlyFileName()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Locked.xlsx [Read-Only] - Excel"), Ctx());
        Assert.NotNull(result);
        Assert.Equal("Locked.xlsx", result!.ContextLabel);
    }

    // ----- Integration: COM (nur mit Office) -----

    [Fact]
    [Trait("Integration", "Office")]
    public void Read_ComAvailable_SetsFilePath()
    {
        var reader = new ExcelAppReader();
        var result = reader.Read(Win("Test.xlsx - Excel"), Ctx());

        if (result == null) return;
        if (result.Extra == null) return;

        var hasCom = result.Extra.TryGetValue("source", out var src) && src == "com";
        if (!hasCom) return;

        Assert.True(result.Extra.ContainsKey("filePath"));
        Assert.False(string.IsNullOrEmpty(result.Extra["filePath"]));
    }
}
