using AiRecall.AppReader.Base;
using AiRecall.AppReader.Pdf;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer PdfViewerAppReader (Spec 0004 Erweiterung, Martin 2026-07-04).
/// Iter. 1: nur Title-Parsing. Voller PDF-Inhalt via PdfPig in Iter. 2.
/// </summary>
public class PdfViewerAppReaderTests
{
    private static WindowInfo Win(string process, string title) =>
        new(IntPtr.Zero, title, 1234, process, true, new WindowRect(0, 0, 100, 100));

    private static AppReaderContext Ctx() => new()
    {
        Config = new AppConfig(),
        Logger = new LoggerConfiguration().CreateLogger()
    };

    // ----- SupportedProcesses / CanRead -----

    [Fact]
    public void SupportedProcesses_ContainsCommonPdfViewers()
    {
        var reader = new PdfViewerAppReader();
        Assert.Contains("AcroRd32", reader.SupportedProcesses);
        Assert.Contains("SumatraPDF", reader.SupportedProcesses);
        Assert.Contains("FoxitReader", reader.SupportedProcesses);
        Assert.Contains("PDFXEdit", reader.SupportedProcesses);
        Assert.Contains("msedge", reader.SupportedProcesses);
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var reader = new PdfViewerAppReader();
        Assert.False(string.IsNullOrEmpty(reader.DisplayName));
    }

    [Fact]
    public void CanRead_SumatraPdf_True()
    {
        var reader = new PdfViewerAppReader();
        Assert.True(reader.CanRead(Win("SumatraPDF", "doc.pdf - SumatraPDF")));
    }

    [Fact]
    public void CanRead_AdobeReader_True()
    {
        var reader = new PdfViewerAppReader();
        Assert.True(reader.CanRead(Win("AcroRd32", "doc.pdf - Adobe Reader")));
    }

    [Fact]
    public void CanRead_Notepad_False()
    {
        var reader = new PdfViewerAppReader();
        Assert.False(reader.CanRead(Win("notepad", "x.txt")));
    }

    // ----- ParseTitle -----

    [Fact]
    public void ParseTitle_FilenameOnly_NoSuffix_ReturnsFilename()
    {
        var (fileName, fullPath, pageInfo) = PdfViewerAppReader.ParseTitle("doc.pdf");
        Assert.Equal("doc.pdf", fileName);
        Assert.Equal(string.Empty, fullPath);
        Assert.Equal(string.Empty, pageInfo);
    }

    [Fact]
    public void ParseTitle_WithSuffix_ReturnsFilename()
    {
        var (fileName, fullPath, pageInfo) = PdfViewerAppReader.ParseTitle("doc.pdf - SumatraPDF");
        Assert.Equal("doc.pdf", fileName);
        Assert.Equal(string.Empty, fullPath);
        Assert.Equal(string.Empty, pageInfo);
    }

    [Fact]
    public void ParseTitle_FullPathSumatra_ReturnsFullPath()
    {
        // SumatraPDF: "C:\path\to\file.pdf - SumatraPDF"
        var (fileName, fullPath, pageInfo) = PdfViewerAppReader.ParseTitle(@"C:\Users\Martin\doc.pdf - SumatraPDF");
        Assert.Equal("doc.pdf", fileName);
        Assert.Equal(@"C:\Users\Martin\doc.pdf", fullPath);
        Assert.Equal(string.Empty, pageInfo);
    }

    [Fact]
    public void ParseTitle_FullPathPdfXChange_ReturnsFullPath()
    {
        var (fileName, fullPath, _) = PdfViewerAppReader.ParseTitle(@"D:\Projects\spec.pdf - PDF-XChange Editor");
        Assert.Equal("spec.pdf", fileName);
        Assert.Equal(@"D:\Projects\spec.pdf", fullPath);
    }

    [Fact]
    public void ParseTitle_PageInfoSumatra_ReturnsPageNumber()
    {
        // SumatraPDF: "doc.pdf - Page 5 of 10 - SumatraPDF"
        var (fileName, fullPath, pageInfo) = PdfViewerAppReader.ParseTitle(@"C:\path\doc.pdf - Page 5 of 10 - SumatraPDF");
        Assert.Equal("doc.pdf", fileName);
        Assert.Equal(@"C:\path\doc.pdf", fullPath);
        Assert.Equal("5", pageInfo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTitle_EmptyOrNull_ReturnsUnknown(string? title)
    {
        var (fileName, fullPath, pageInfo) = PdfViewerAppReader.ParseTitle(title);
        Assert.Equal("(unknown)", fileName);
        Assert.Equal(string.Empty, fullPath);
        Assert.Equal(string.Empty, pageInfo);
    }

    // ----- Read (Smoke) -----

    [Fact]
    public void Read_SumatraPdf_FullPath_ExtractsFilePath()
    {
        var reader = new PdfViewerAppReader();
        var result = reader.Read(Win("SumatraPDF", @"C:\Users\Martin\report.pdf - SumatraPDF"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("pdf", result!.ContextKind);
        Assert.Equal("report.pdf", result.ContextLabel);
        Assert.Contains("report.pdf", result.ContentMarkdown);
        Assert.Equal("True", result.Extra!["hasFullPath"]);
        Assert.Equal(@"C:\Users\Martin\report.pdf", result.Extra["filePath"]);
    }

    [Fact]
    public void Read_AdobeReader_FilenameOnly_NoFullPath()
    {
        var reader = new PdfViewerAppReader();
        var result = reader.Read(Win("AcroRd32", "report.pdf - Adobe Reader"), Ctx());

        Assert.NotNull(result);
        Assert.Equal("report.pdf", result!.ContextLabel);
        Assert.Equal("False", result.Extra!["hasFullPath"]);
        Assert.Equal(string.Empty, result.Extra["filePath"]);
    }

    [Fact]
    public void Read_UnknownProcess_ReturnsNull()
    {
        var reader = new PdfViewerAppReader();
        var result = reader.Read(Win("notepad", "doc.pdf"), Ctx());
        Assert.Null(result);
    }

    [Fact]
    public void Read_CustomProcessFromConfig_Accepted()
    {
        var cfg = new AppConfig();
        cfg.AppReader.Pdf.Processes = new List<string> { "MyCustomPdfReader" };
        var ctx = new AppReaderContext { Config = cfg, Logger = new LoggerConfiguration().CreateLogger() };

        var reader = new PdfViewerAppReader();
        var result = reader.Read(Win("MyCustomPdfReader", "doc.pdf"), ctx);

        Assert.NotNull(result);
        Assert.Equal("doc.pdf", result!.ContextLabel);
    }
}