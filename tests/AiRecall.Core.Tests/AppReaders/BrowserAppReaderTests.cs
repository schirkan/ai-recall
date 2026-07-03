using AiRecall.AppReader.Base;
using AiRecall.AppReader.Browser;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Smoke-Tests für BrowserAppReader. UIA braucht einen echten Window-Handle,
/// diese Tests prüfen nur, dass der Reader nicht crasht und einen sinnvollen
/// Markdown-Output (mit Tab-Titel) liefert, auch wenn UIA selbst fehlschlägt.
/// </summary>
public class BrowserAppReaderTests
{
    private static WindowInfo Win(string process, string title) =>
        new(IntPtr.Zero, title, 1234, process, true, new WindowRect(0, 0, 100, 100));

    private static AppReaderContext Ctx() => new()
    {
        Config = new AppConfig(),
        Logger = new LoggerConfiguration().CreateLogger()
    };

    [Fact]
    public void CanRead_Chrome_True()
    {
        var reader = new BrowserAppReader();
        Assert.True(reader.CanRead(Win("chrome", "Google - Google Chrome")));
    }

    [Fact]
    public void CanRead_MsEdge_True()
    {
        var reader = new BrowserAppReader();
        Assert.True(reader.CanRead(Win("msedge", "Microsoft Edge")));
    }

    [Fact]
    public void CanRead_Notepad_False()
    {
        var reader = new BrowserAppReader();
        Assert.False(reader.CanRead(Win("Notepad", "x")));
    }

    [Fact]
    public void Read_StubChrome_DoesNotCrash_IncludesTitle()
    {
        var reader = new BrowserAppReader();
        var win = Win("chrome", "Test Page - Google Chrome");
        var result = reader.Read(win, Ctx());

        Assert.NotNull(result);
        Assert.Equal("url", result!.ContextKind);
        Assert.Contains("Test Page", result.ContentMarkdown);
        // UIA auf IntPtr.Zero liefert kein Address-Bar → URL bleibt leer
        Assert.True(string.IsNullOrEmpty(result.Extra!["url"]));
    }

    [Fact]
    public void Read_StubEdge_StripsEdgeSuffix()
    {
        var reader = new BrowserAppReader();
        var win = Win("msedge", "Willkommen - Microsoft Edge");
        var result = reader.Read(win, Ctx());

        Assert.NotNull(result);
        Assert.Contains("Willkommen", result!.ContentMarkdown);
    }

    [Fact]
    public void Read_InPrivateEdgeSuffix_IsStripped()
    {
        var reader = new BrowserAppReader();
        var win = Win("msedge", "Privacy - InPrivate - Microsoft Edge");
        var result = reader.Read(win, Ctx());

        Assert.NotNull(result);
        // Tab-Titel ("Privacy") ist extrahiert; Browser-Suffix ist korrekt
        // (- InPrivate - Microsoft Edge). Das Suffix selbst darf im
        // Markdown erwähnt werden (Info-Anzeige), aber der Tab-Titel-Teil
        // darf "InPrivate" nicht mehr enthalten.
        Assert.Contains("Privacy", result!.ContentMarkdown);
        var titleLine = result.ContentMarkdown.Split('\n').First(l => l.Contains("Tab title"));
        Assert.DoesNotContain("InPrivate", titleLine);
    }

    [Fact]
    public void Read_EmptyTitle_DoesNotCrash_ReturnsResult()
    {
        var reader = new BrowserAppReader();
        var win = Win("chrome", "");
        var result = reader.Read(win, Ctx());

        Assert.NotNull(result);
    }

    [Fact]
    public void SupportedProcesses_ContainsChromeAndEdge()
    {
        var reader = new BrowserAppReader();
        Assert.Contains("chrome", reader.SupportedProcesses);
        Assert.Contains("msedge", reader.SupportedProcesses);
    }

    [Fact]
    public void DisplayName_IsNotEmpty()
    {
        var reader = new BrowserAppReader();
        Assert.False(string.IsNullOrEmpty(reader.DisplayName));
    }
}