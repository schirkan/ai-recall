using AiRecall.AppReader.Base;
using AiRecall.AppReader.Documents;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer PowerPointAppReader. Office in Sandbox nicht verfuegbar
/// (Martin 2026-07-04) → Title-Parsing und Fallback-Reads.
/// COM-Integration-Tests mit <c>[Trait("Integration", "Office")]</c>.
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
    public void SupportedProcesses_ContainsPowerpnt()
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
    public void CanRead_Powerpnt_True()
    {
        var reader = new PowerPointAppReader();
        Assert.True(reader.CanRead(Win("Slides.pptx - PowerPoint")));
    }

    [Fact]
    public void CanRead_Notepad_False()
    {
        var reader = new PowerPointAppReader();
        Assert.False(reader.CanRead(new WindowInfo(IntPtr.Zero, "x", 1, "notepad", true, new WindowRect(0, 0, 100, 100))));
    }

    // ----- ParseTitle (Fallback-Pfad) -----

    [Fact]
    public void ParseTitle_NormalPptx_ReturnsFilename()
    {
        var (name, untitled, readOnly) = PowerPointAppReader.ParseTitle("Slides.pptx - PowerPoint");
        Assert.Equal("Slides.pptx", name);
        Assert.False(untitled);
        Assert.False(readOnly);
    }

    [Fact]
    public void ParseTitle_UnsavedMarker_Stripped()
    {
        var (name, untitled, _) = PowerPointAppReader.ParseTitle("*Slides.pptx - PowerPoint");
        Assert.Equal("Slides.pptx", name);
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
    public void ParseTitle_EmptyAfterStrip_IsUntitled()
    {
        var (name, untitled, _) = PowerPointAppReader.ParseTitle(" - PowerPoint");
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_ReadOnlyFlag_Detected()
    {
        var (name, untitled, readOnly) = PowerPointAppReader.ParseTitle("Slides.pptx [Read-Only] - PowerPoint");
        Assert.Equal("Slides.pptx", name);
        Assert.True(readOnly);
        Assert.False(untitled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTitle_EmptyOrNull_ReturnsUntitled(string? title)
    {
        var (name, untitled, _) = PowerPointAppReader.ParseTitle(title);
        Assert.True(untitled);
        Assert.Equal("(untitled)", name);
    }

    [Fact]
    public void ParseTitle_NoPowerPointSuffix_ReturnsTitleAsIs()
    {
        var (name, untitled, _) = PowerPointAppReader.ParseTitle("Slides.pptx");
        Assert.Equal("Slides.pptx", name);
        Assert.False(untitled);
    }

    // ----- Read (Smoke, COM liefert null in Sandbox) -----

    [Fact]
    public void Read_StubPptx_NoCrash_IncludesFileName()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Q3-Review.pptx - PowerPoint"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("presentation", result!.ContextKind);
        Assert.Equal("Q3-Review.pptx", result.ContextLabel);
        Assert.Contains("Q3-Review.pptx", result.ContentMarkdown);
        Assert.Equal("title-uia", result.Extra!["source"]);
        Assert.Equal("False", result.Extra["hasContent"]);
        Assert.Equal("False", result.Extra["isUntitled"]);
    }

    [Fact]
    public void Read_StubUntitled_UsesUntitledLabel()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Presentation1 - PowerPoint"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("(untitled)", result!.ContextLabel);
        Assert.Contains("untitled", result.ContentMarkdown);
    }

    [Fact]
    public void Read_StubReadOnly_IncludesModeLine()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Locked.pptx [Read-Only] - PowerPoint"), Ctx());

        Assert.NotNull(result);
        Assert.Contains("Read-Only", result!.ContentMarkdown);
    }

    [Fact]
    public void Read_UiaDisabled_StillReturnsResultWithoutBody()
    {
        var cfg = new AppConfig();
        cfg.AppReader.Documents.EnableUiaExtraction = false;
        var ctx = new AppReaderContext { Config = cfg, Logger = new LoggerConfiguration().CreateLogger() };

        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Slides.pptx - PowerPoint"), ctx);

        Assert.NotNull(result);
        Assert.Equal("False", result!.Extra!["hasContent"]);
    }

    // ----- Integration: COM (nur mit Office) -----

    [Fact]
    [Trait("Integration", "Office")]
    public void Read_ComAvailable_SetsFilePath()
    {
        var reader = new PowerPointAppReader();
        var result = reader.Read(Win("Test.pptx - PowerPoint"), Ctx());

        if (result == null || result.Extra == null) return;
        if (!result.Extra.TryGetValue("source", out var src) || src != "com") return;

        Assert.True(result.Extra.ContainsKey("filePath"));
        Assert.False(string.IsNullOrEmpty(result.Extra["filePath"]));
    }
}