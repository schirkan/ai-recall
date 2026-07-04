using AiRecall.AppReader.Base;
using AiRecall.AppReader.Documents;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer PowerPointAppReader. Spec 0007 Schritt 7: Reader ist duenn.
/// </summary>
public class PowerPointAppReaderTests
{
    private static WindowInfo Win(string title) =>
        new(IntPtr.Zero, title, 1234, "POWERPNT", true, new WindowRect(0, 0, 100, 100));

    private static AppReaderContext Ctx() => new()
    {
        Config = new AppConfig(),
        Logger = new LoggerConfiguration().CreateLogger()
    };

    [Fact]
    public void SupportedProcesses_ContainsPowerPoint()
    {
        var reader = new PowerPointAppReader();
        Assert.Contains("POWERPNT", reader.SupportedProcesses);
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var reader = new PowerPointAppReader();
        Assert.False(string.IsNullOrEmpty(reader.DisplayName));
    }

    [Fact]
    public void CanRead_PowerPoint_True()
    {
        var reader = new PowerPointAppReader();
        Assert.True(reader.CanRead(Win("Q3-Review.pptx - PowerPoint")));
    }

    [Fact]
    public void CanRead_Notepad_False()
    {
        var reader = new PowerPointAppReader();
        Assert.False(reader.CanRead(new WindowInfo(IntPtr.Zero, "x", 1, "notepad", true, new WindowRect(0, 0, 100, 100))));
    }

    [Fact]
    public void IsThinReader_True()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Q3-Review.pptx - PowerPoint"), Ctx());
        Assert.NotNull(result);
        Assert.True(result!.IsThinReader);
    }

    [Fact]
    public void ContentMarkdown_IsPlaceholder()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Q3-Review.pptx - PowerPoint"), Ctx());
        Assert.NotNull(result);
        Assert.Equal(PowerPointAppReader.PLACEHOLDER, result!.ContentMarkdown);
    }

    // ----- ParseTitle -----

    [Fact]
    public void ParseTitle_NormalPptx_ReturnsFilename()
    {
        var (name, untitled, _) = PowerPointAppReader.ParseTitle("Q3-Review.pptx - PowerPoint");
        Assert.Equal("Q3-Review.pptx", name);
        Assert.False(untitled);
    }

    [Fact]
    public void ParseTitle_Presentation1_IsUntitled()
    {
        var (name, untitled, _) = PowerPointAppReader.ParseTitle("Presentation1 - PowerPoint");
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_ReadOnly_Detected()
    {
        var (name, _, readOnly) = PowerPointAppReader.ParseTitle("Q3.pptx [Read-Only] - PowerPoint");
        Assert.Equal("Q3.pptx", name);
        Assert.True(readOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseTitle_EmptyOrNull_ReturnsUntitled(string? title)
    {
        var (name, untitled, _) = PowerPointAppReader.ParseTitle(title);
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    // ----- Read (Smoke) -----

    [Fact]
    public void Read_StubPptx_ContextLabelAndSource()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Q3-Review.pptx - PowerPoint"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("presentation", result!.ContextKind);
        Assert.Equal("Q3-Review.pptx", result.ContextLabel);
        Assert.Equal("title-uia", result.Extra!["source"]);
        Assert.False(result.Extra.ContainsKey("filePath"));
    }

    [Fact]
    public void Read_StubUntitled_UsesUntitledLabel()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Presentation1 - PowerPoint"), Ctx());
        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
    }

    [Fact]
    public void Read_StubReadOnly_LabelHasOnlyFileName()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Locked.pptx [Read-Only] - PowerPoint"), Ctx());
        Assert.NotNull(result);
        Assert.Equal("Locked.pptx", result!.ContextLabel);
    }

    // ----- Integration: COM (nur mit Office) -----

    [Fact]
    [Trait("Integration", "Office")]
    public void Read_ComAvailable_SetsFilePath()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Test.pptx - PowerPoint"), Ctx());

        if (result == null) return;
        if (result.Extra == null) return;

        var hasCom = result.Extra.TryGetValue("source", out var src) && src == "com";
        if (!hasCom) return;

        Assert.True(result.Extra.ContainsKey("filePath"));
        Assert.False(string.IsNullOrEmpty(result.Extra["filePath"]));
    }
}
