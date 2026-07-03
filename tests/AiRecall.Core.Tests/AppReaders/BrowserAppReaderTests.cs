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
        // Weder CDP (kein Browser auf Port 9222) noch UIA-Data (Stub-Hwnd) → "none"
        Assert.Equal("none", result.Extra["contentSource"]);
    }

    [Fact]
    public void Read_NoCdpNoUia_GracefullyReportsContentSource()
    {
        // CDP läuft auf localhost:9222 in der Sandbox nicht (kein Browser gestartet).
        // UIA auf IntPtr.Zero liefert nichts. → contentSource "none", kein Crash.
        var reader = new BrowserAppReader();
        var win = Win("msedge", "Any Page - Microsoft Edge");
        var result = reader.Read(win, Ctx());

        Assert.NotNull(result);
        Assert.Equal("none", result!.Extra!["contentSource"]);
        // Markdown enthält Source-Hinweis
        Assert.Contains("**Content source:** none", result.ContentMarkdown);
    }

    [Fact]
    public void Read_ContentMarkdown_IncludesUrlTitleSuffix()
    {
        var reader = new BrowserAppReader();
        var result = reader.Read(Win("chrome", "Foo - Google Chrome"), Ctx());
        Assert.NotNull(result);
        var md = result!.ContentMarkdown;
        Assert.Contains("**Tab title:** Foo", md);
        Assert.Contains("**URL:**", md);
        Assert.Contains("**Browser suffix:**", md);
        Assert.Contains("**Content source:**", md);
    }

    [Fact]
    public void StripNoise_RemovesScriptsStylesSvgsAndComments()
    {
        const string html = "<div><p>Hello</p><script>alert('x')</script><style>body { color:red }</style><svg width=\"100\"><path d=\"M0 0\"/></svg><noscript>nope</noscript><!-- a comment --><p>World</p></div>";

        var out2 = BrowserAppReader.StripNoise(html);

        Assert.Contains("Hello", out2);
        Assert.Contains("World", out2);
        Assert.DoesNotContain("alert", out2);
        Assert.DoesNotContain("body {", out2);
        Assert.DoesNotContain("<path", out2);
        Assert.DoesNotContain("nope", out2);
        Assert.DoesNotContain("a comment", out2);
    }

    [Fact]
    public void StripNoise_HandlesEmptyAndMalformedGracefully()
    {
        Assert.Equal("", BrowserAppReader.StripNoise(""));
        var partial = "<div>kept<script>oops<span>also kept</span>";
        var out2 = BrowserAppReader.StripNoise(partial);
        Assert.Contains("kept", out2);
    }

    [Fact]
    public void Read_CdpEnabledButNoServer_FallsBackGracefully()
    {
        // cdp.enabled = true, aber kein Browser lauscht auf localhost:9222
        // (Sandbox). CDP-Aufruf wirft → TryReadActivePage liefert null.
        // UIA auf IntPtr.Zero liefert ebenfalls nichts → contentSource "none",
        // kein Crash.
        var cfg = new AppConfig();
        cfg.AppReader.Browser.Cdp.Enabled = true;
        cfg.AppReader.Browser.Cdp.Endpoint = "http://127.0.0.1:39999"; // sicher nicht belegt
        cfg.AppReader.Browser.Cdp.TimeoutMs = 200;

        var ctx = new AppReaderContext { Config = cfg, Logger = new LoggerConfiguration().CreateLogger() };

        var reader = new BrowserAppReader();
        var result = reader.Read(Win("msedge", "Sample - Microsoft Edge"), ctx);

        Assert.NotNull(result);
        Assert.Equal("none", result!.Extra!["contentSource"]);
    }

    [Fact]
    public void Read_CdpEnabledWithShortTimeout_DoesNotBlockLong()
    {
        // Sicherstellen, dass ein sehr kurzer Timeout tatsächlich greift (kein 1500 ms-Default).
        var cfg = new AppConfig();
        cfg.AppReader.Browser.Cdp.Enabled = true;
        cfg.AppReader.Browser.Cdp.Endpoint = "http://127.0.0.1:1"; // Port 1 sollte immer failen
        cfg.AppReader.Browser.Cdp.TimeoutMs = 100;

        var ctx = new AppReaderContext { Config = cfg, Logger = new LoggerConfiguration().CreateLogger() };

        var reader = new BrowserAppReader();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = reader.Read(Win("chrome", "Anything - Google Chrome"), ctx);
        sw.Stop();

        Assert.NotNull(result);
        Assert.Equal("none", result!.Extra!["contentSource"]);
        // Bei 100 ms Timeout kann der Aufruf minimal länger sein (Connect-Retry),
        // aber sicher unter 2 s (Sandbox-Test).
        Assert.True(sw.ElapsedMilliseconds < 2000, $"CDP call took too long: {sw.ElapsedMilliseconds} ms");
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