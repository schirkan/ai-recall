using AiRecall.AppReader.Explorer;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

public class ExplorerAppReaderTests
{
    private static WindowInfo Win(string title) =>
        new(IntPtr.Zero, title, 1, "explorer", true, new WindowRect(0, 0, 100, 100));

    private static AppReaderContext Ctx() => new()
    {
        Config = new AppConfig(),
        Logger = new LoggerConfiguration().CreateLogger()
    };

    // ----- ParsePath (internal) -----

    [Theory]
    [InlineData(@"C:\Users\Martin\Downloads - Datei-Explorer", @"C:\Users\Martin\Downloads")]
    [InlineData(@"C:\Users\Martin\Downloads – Datei-Explorer", @"C:\Users\Martin\Downloads")]  // En-Dash
    [InlineData(@"C:\Users\Martin\Downloads — Datei-Explorer", @"C:\Users\Martin\Downloads")]  // Em-Dash
    [InlineData(@"D:\Projects\ai-recall - File Explorer", @"D:\Projects\ai-recall")]
    [InlineData(@"C:\Users\Martin - Explorer", @"C:\Users\Martin")]
    [InlineData(@"\\server\share\folder - File Explorer", @"\\server\share\folder")]
    public void ParsePath_StripsSuffix_KeepsPath(string title, string expected)
    {
        Assert.Equal(expected, ExplorerAppReader.ParsePath(title));
    }

    [Theory]
    [InlineData("Dieser PC - Datei-Explorer")]
    [InlineData("This PC - File Explorer")]
    [InlineData("Desktop")]
    [InlineData("Schnellzugriff - Datei-Explorer")]
    [InlineData("Bibliotheken - Datei-Explorer")]
    [InlineData("Quick Access")]
    [InlineData("Netzwerk - Datei-Explorer")]
    public void ParsePath_SpecialFolders_ReturnsNull(string title)
    {
        Assert.Null(ExplorerAppReader.ParsePath(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParsePath_EmptyOrNull_ReturnsNull(string? title)
    {
        Assert.Null(ExplorerAppReader.ParsePath(title));
    }

    [Theory]
    [InlineData("Random Window Title")]
    [InlineData("File Explorer")]
    [InlineData("Dokumente")]
    public void ParsePath_NoSuffix_ReturnsNull(string title)
    {
        // Ohne bekannten Suffix ist es kein Explorer-Tab → null
        Assert.Null(ExplorerAppReader.ParsePath(title));
    }

    [Theory]
    [InlineData(@"Bibliotheken\Bilder - Datei-Explorer", @"Bibliotheken\Bilder")]
    [InlineData(@"Dokumente - Datei-Explorer", "Dokumente")]
    public void ParsePath_NonAbsolutePath_ReturnedVerbatim(string title, string expected)
    {
        // Relativer Pfad / benannter Ordner → unverändert zurückgeben (kein Drive-Letter).
        Assert.Equal(expected, ExplorerAppReader.ParsePath(title));
    }

    // ----- Read (Interface) -----

    [Fact]
    public void Read_AbsolutePath_ReturnsResult()
    {
        var reader = new ExplorerAppReader();
        var win = Win(@"C:\Users\Martin\Downloads - Datei-Explorer");
        var result = reader.Read(win, Ctx());

        Assert.NotNull(result);
        Assert.Equal("path", result!.ContextKind);
        Assert.Equal(@"C:\Users\Martin\Downloads", result.ContextLabel);
        Assert.Contains("C:\\Users\\Martin\\Downloads", result.ContentMarkdown);
        Assert.Contains("path", result.Extra!.Keys);
        Assert.Equal(@"C:\Users\Martin\Downloads", result.Extra["path"]);
    }

    [Fact]
    public void Read_SpecialFolder_ReturnsNull()
    {
        var reader = new ExplorerAppReader();
        var win = Win("Dieser PC - Datei-Explorer");
        var result = reader.Read(win, Ctx());
        Assert.Null(result);
    }

    [Fact]
    public void Read_EmptyTitle_ReturnsNull()
    {
        var reader = new ExplorerAppReader();
        var result = reader.Read(Win(""), Ctx());
        Assert.Null(result);
    }

    [Fact]
    public void CanRead_ExplorerProcess_True()
    {
        var reader = new ExplorerAppReader();
        Assert.True(reader.CanRead(Win("anything")));
    }

    [Fact]
    public void CanRead_NonExplorerProcess_False()
    {
        var reader = new ExplorerAppReader();
        var win = new WindowInfo(IntPtr.Zero, "title", 1, "chrome", true, new WindowRect(0,0,100,100));
        Assert.False(reader.CanRead(win));
    }
}