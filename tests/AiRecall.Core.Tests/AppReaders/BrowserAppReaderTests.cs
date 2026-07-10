using AiRecall.AppReader.Base;
using AiRecall.AppReader.Browser;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;
using Serilog.Core;
using Serilog.Events;

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
    public void StripNoise_ReplacesInlineSvgDataUrlsWithMarker()
    {
        var dataUrl = "data:image/svg+xml;base64,PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0idXRmLTgiPz4KPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIzMiIgaGVpZ2h0PSIzMiI+PGNpcmNsZSBjeD0iMTYiIGN5PSIxNiIgcj0iMTYiIGZpbGw9InJlZCIvPjwvc3ZnPg==";
        var html = $"<img src=\"{dataUrl}\" width=\"32\" height=\"32\" alt=\"dot\"> tail";
        var out2 = BrowserAppReader.StripNoise(html);

        Assert.Contains("alt=\"dot\"", out2);
        Assert.Contains("width=\"32\"", out2);
        Assert.Contains("tail", out2);
        Assert.Contains("(inline-svg)", out2);
        Assert.DoesNotContain("PD94bWwg", out2); // base64 data gone
        Assert.DoesNotContain("base64", out2);
    }

    [Fact]
    public void StripNoise_DoesNotTouchNonSvgDataUrls()
    {
        // data:image/png base64 should remain (we only filter SVG)
        var html = "<img src=\"data:image/png;base64,iVBORw0KGgoAAAA=\" alt=\"png\">";
        var out2 = BrowserAppReader.StripNoise(html);
        Assert.Contains("data:image/png;base64", out2);
        Assert.Contains("iVBORw0KGgo", out2);
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

    // ---- MarkdownSettings -> BuildConverter ----

    [Fact]
    public void BuildConverter_NullSettings_ReturnsValidConverter()
    {
        var converter = BrowserAppReader.BuildConverter(null);

        Assert.NotNull(converter);
        Assert.NotNull(converter.Config);
        // Smoke-Test: muss etwas Konvertieren, ohne zu werfen.
        var md = converter.Convert("<p>Hello <strong>World</strong></p>");
        Assert.Contains("Hello", md);
        Assert.Contains("World", md);
    }

    [Fact]
    public void BuildConverter_EmptySettings_PreservesLibraryDefaults()
    {
        // Bei komplett leerem MarkdownSettings darf KEIN Config-Wert überschrieben werden.
        // Defaults der Library v3.13 sind hier dokumentiert (per Reflection verifiziert).
        var converter = BrowserAppReader.BuildConverter(new MarkdownSettings());
        var cfg = converter.Config;

        Assert.False(cfg.RemoveComments);   // Library-Default
        Assert.Equal('-', cfg.ListBulletChar); // Library-Default
        Assert.False(cfg.GithubFlavored);
        Assert.False(cfg.SmartHrefHandling);
        Assert.Equal(ReverseMarkdown.Config.UnknownTagsOption.PassThrough, cfg.UnknownTags);
        Assert.Equal(ReverseMarkdown.Config.TableWithoutHeaderRowHandlingOption.Default, cfg.TableWithoutHeaderRowHandling);
        Assert.Null(cfg.DefaultCodeBlockLanguage);
        Assert.Null(cfg.WhitelistUriSchemes);
    }

    [Fact]
    public void BuildConverter_AllSettings_AppliesAllValues()
    {
        var s = new MarkdownSettings
        {
            UnknownTags = "Drop",
            GithubFlavored = true,
            RemoveComments = false,
            WhitelistUriSchemes = new() { "https", "mailto" },
            SmartHrefHandling = true,
            TableWithoutHeaderRowHandling = "EmptyRow",
            ListBulletChar = "+",
            DefaultCodeBlockLanguage = "bash"
        };
        var cfg = BrowserAppReader.BuildConverter(s).Config;

        Assert.Equal(ReverseMarkdown.Config.UnknownTagsOption.Drop, cfg.UnknownTags);
        Assert.True(cfg.GithubFlavored);
        Assert.False(cfg.RemoveComments);
        Assert.Equal(new[] { "https", "mailto" }, cfg.WhitelistUriSchemes);
        Assert.True(cfg.SmartHrefHandling);
        Assert.Equal(ReverseMarkdown.Config.TableWithoutHeaderRowHandlingOption.EmptyRow, cfg.TableWithoutHeaderRowHandling);
        Assert.Equal('+', cfg.ListBulletChar);
        Assert.Equal("bash", cfg.DefaultCodeBlockLanguage);
    }

    [Fact]
    public void BuildConverter_UnknownTags_IsCaseInsensitive()
    {
        var s = new MarkdownSettings { UnknownTags = "drop" }; // lowercase
        var cfg = BrowserAppReader.BuildConverter(s).Config;
        Assert.Equal(ReverseMarkdown.Config.UnknownTagsOption.Drop, cfg.UnknownTags);
    }

    [Fact]
    public void BuildConverter_UnknownTags_InvalidString_LeavesDefault()
    {
        // Ungültiger String → kein Override → Library-Default bleibt.
        var s = new MarkdownSettings { UnknownTags = "NotAValidOption" };
        var cfg = BrowserAppReader.BuildConverter(s).Config;
        Assert.Equal(ReverseMarkdown.Config.UnknownTagsOption.PassThrough, cfg.UnknownTags);
    }

    [Fact]
    public void BuildConverter_TableWithoutHeaderRow_IsCaseInsensitive()
    {
        var s = new MarkdownSettings { TableWithoutHeaderRowHandling = "emptyrow" };
        var cfg = BrowserAppReader.BuildConverter(s).Config;
        Assert.Equal(ReverseMarkdown.Config.TableWithoutHeaderRowHandlingOption.EmptyRow, cfg.TableWithoutHeaderRowHandling);
    }

    [Fact]
    public void BuildConverter_ListBulletChar_TakesFirstCharOnly()
    {
        // Auch wenn der String mehrere Zeichen enthält, wird nur das erste übernommen.
        var s = new MarkdownSettings { ListBulletChar = "->" };
        var cfg = BrowserAppReader.BuildConverter(s).Config;
        Assert.Equal('-', cfg.ListBulletChar);
    }

    [Fact]
    public void BuildConverter_WhitelistUriSchemes_EmptyList_OverridesDefault()
    {
        // Leere Liste soll die Whitelist komplett leeren (nicht Default lassen).
        var s = new MarkdownSettings { WhitelistUriSchemes = new() };
        var cfg = BrowserAppReader.BuildConverter(s).Config;
        Assert.Empty(cfg.WhitelistUriSchemes);
    }

    [Fact]
    public void ConvertHtmlToMarkdown_EndToEnd_WithGithubFlavored_RendersTable()
    {
        const string html = "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>";

        var s = new MarkdownSettings { GithubFlavored = true };
        var md = BrowserAppReader.BuildConverter(s).Convert(BrowserAppReader.StripNoise(html));

        // GFM table → Pipes + Header-Separator-Zeile
        Assert.Contains("|", md);
        Assert.Contains("---", md);
    }

    [Fact]
    public void ConvertHtmlToMarkdown_WithCustomBulletChar_UsesIt()
    {
        const string html = "<ul><li>one</li><li>two</li></ul>";

        var s = new MarkdownSettings { ListBulletChar = "+" };
        var md = BrowserAppReader.BuildConverter(s).Convert(BrowserAppReader.StripNoise(html));

        Assert.Contains("+ one", md);
        Assert.Contains("+ two", md);
    }

    [Fact]
    public void AppConfig_BrowserConfig_HasMarkdownSettings()
    {
        // Sicherstellen, dass die JSON-Bindung über BrowserConfig.Markdown erreichbar ist
        // und Standardwerte gesetzt sind (kein null).
        var cfg = new AppConfig();
        Assert.NotNull(cfg.AppReader.Browser.Markdown);
        Assert.Null(cfg.AppReader.Browser.Markdown.UnknownTags);
        Assert.Null(cfg.AppReader.Browser.Markdown.GithubFlavored);
        Assert.Null(cfg.AppReader.Browser.Markdown.ListBulletChar);
    }

    // ---- Diagnostic-Logging (Fix A: Logging statt silent catch) ----

    /// <summary>
    /// Minimaler In-Memory-Log-Sink für Test-Assertions. Implementiert
    /// <see cref="ILogEventSink"/> direkt (kein NuGet-Package nötig).
    /// </summary>
    private sealed class ListLogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        public bool Contains(string fragment) =>
            Events.Any(e => e.RenderMessage().Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static (ILogger Logger, ListLogSink Sink) BuildCollectingLogger(LogEventLevel minLevel = LogEventLevel.Verbose)
    {
        var sink = new ListLogSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void Read_CdpEnabled_LogsInformationAboutCdpAttempt()
    {
        // CDP aktiviert, aber Server nicht erreichbar → Information-Log muss kommen.
        var cfg = new AppConfig();
        cfg.AppReader.Browser.Cdp.Enabled = true;
        cfg.AppReader.Browser.Cdp.Endpoint = "http://127.0.0.1:39998";
        cfg.AppReader.Browser.Cdp.TimeoutMs = 100;

        var (logger, sink) = BuildCollectingLogger();
        var ctx = new AppReaderContext { Config = cfg, Logger = logger };

        var reader = new BrowserAppReader();
        var result = reader.Read(Win("msedge", "Sample - Microsoft Edge"), ctx);

        Assert.NotNull(result);
        Assert.True(sink.Contains("CDP enabled"),
            "Expected Information-Log containing 'CDP enabled' when CDP config flag is on");
    }

    [Fact]
    public void Read_CdpEnabled_FailsGracefully_LogsFallbackMessage()
    {
        // CDP an, Server nicht erreichbar → Log muss "falling back to UIA" o. ä. enthalten,
        // damit Diagnose ohne DevTools möglich ist.
        var cfg = new AppConfig();
        cfg.AppReader.Browser.Cdp.Enabled = true;
        cfg.AppReader.Browser.Cdp.Endpoint = "http://127.0.0.1:39997";
        cfg.AppReader.Browser.Cdp.TimeoutMs = 100;

        var (logger, sink) = BuildCollectingLogger();
        var ctx = new AppReaderContext { Config = cfg, Logger = logger };

        var reader = new BrowserAppReader();
        var result = reader.Read(Win("chrome", "Anything - Google Chrome"), ctx);

        Assert.NotNull(result);
        Assert.Equal("none", result!.Extra!["contentSource"]);
        Assert.True(
            sink.Contains("falling back to UIA") || sink.Contains("CDP returned"),
            "Expected a diagnostic log explaining the CDP fallback path");
    }

    [Fact]
    public void Read_CdpDisabled_LogsDebug()
    {
        // CDP aus → Debug-Log muss "CDP disabled" enthalten (für Diagnose bei Tests).
        var cfg = new AppConfig();
        cfg.AppReader.Browser.Cdp.Enabled = false;

        var (logger, sink) = BuildCollectingLogger();
        var ctx = new AppReaderContext { Config = cfg, Logger = logger };

        var reader = new BrowserAppReader();
        var result = reader.Read(Win("msedge", "X - Microsoft Edge"), ctx);

        Assert.NotNull(result);
        Assert.True(sink.Contains("CDP disabled"),
            "Expected Debug-Log containing 'CDP disabled' when CDP config flag is off");
    }

    [Fact]
    public void Read_NoUiaUrlNoUiaBody_LogsBothDebugs()
    {
        // Stub-Hwnd → UIA liefert weder URL noch Body → zwei Debug-Logs erwartet.
        var (logger, sink) = BuildCollectingLogger();
        var ctx = new AppReaderContext { Config = new AppConfig(), Logger = logger };

        var reader = new BrowserAppReader();
        var result = reader.Read(Win("chrome", "Y - Google Chrome"), ctx);

        Assert.NotNull(result);
        Assert.True(sink.Contains("UIA produced no URL"),
            "Expected Debug-Log for missing UIA URL on IntPtr.Zero");
        Assert.True(sink.Contains("UIA produced no body"),
            "Expected Debug-Log for missing UIA body on IntPtr.Zero");
    }
}