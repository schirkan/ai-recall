using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.AppReader.Browser;

/// <summary>
/// Liest den Tab-Titel + URL + Body aus Edge / Chrome.
///
/// Strategie (Spec 0004):
///   1. Tab-Titel aus dem Fenster-Titel parsen („Page Title — Browser").
///   2. URL + Body via UIA (<c>ValuePattern</c> für Address-Bar,
///      <c>TextPattern</c> für Document-Control). Plain-Text-Body wird
///      als Code-Block in MD eingebettet.
///   3. Optional, opt-in via <c>appReader.browser.cdp.enabled</c>:
///      Chrome DevTools Protocol ersetzt die UIA-Textextraktion.
///      Voraussetzung: Browser mit <c>--remote-debugging-port</c> gestartet.
///      HTML wird via <c>ReverseMarkdown</c> konvertiert (reichhaltiger als UIA).
/// </summary>
public sealed class BrowserAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "msedge", "chrome" };
    public override string DisplayName => "Browser (UIA; CDP opt-in via config)";

    // Bekannte Suffixe, die Browser an den Tab-Titel hängen.
    // WICHTIG: Längste zuerst! Sonst matched " - Microsoft Edge" vor
    // " - InPrivate - Microsoft Edge" und "InPrivate" bleibt im Titel.
    private static readonly string[] BrowserSuffixes =
    {
        " - InPrivate - Microsoft\u00A0Edge",  // non-breaking space
        " - InPrivate - Microsoft Edge",
        " - Microsoft\u00A0Edge",
        " - Microsoft Edge",
        " - Google Chrome",
        " - Chrome",
        " - Mozilla Firefox"            // Firefox fällt hier zwar nicht hin, schadet nicht
    };

    private static readonly Regex UrlRegex = new(
        @"^(https?://[^\s]+|file://[^\s]+|about:[^\s]+|chrome://[^\s]+|edge://[^\s]+|chrome-extension://[^\s]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var (tabTitle, browserSuffix) = ParseTitle(window.Title);

            // 1. CDP-Pfad versuchen (intern: Skip wenn cdp.enabled == false)
            var cdpConfig = context.Config.AppReader.Browser.Cdp;
            ChromeDevToolsProtocolClient.CdpResult? cdp = null;
            if (cdpConfig.Enabled)
            {
                context.Logger.Information(
                    "Browser reader: CDP enabled (endpoint={Endpoint}, timeoutMs={Timeout}), querying…",
                    cdpConfig.Endpoint, cdpConfig.TimeoutMs);
                cdp = ChromeDevToolsProtocolClient.TryReadActivePage(cdpConfig);
                if (cdp is null)
                {
                    context.Logger.Information(
                        "Browser reader: CDP returned no page snapshot (endpoint unreachable or no active tab) — falling back to UIA");
                }
            }
            else
            {
                context.Logger.Debug(
                    "Browser reader: CDP disabled in config, using UIA only");
            }

            // 2. URL: CDP hat Vorrang, sonst UIA
            string? url = cdp?.Url;
            if (string.IsNullOrEmpty(url))
            {
                url = TryReadUrlViaUia(window.Handle, context.Logger);
                if (string.IsNullOrEmpty(url))
                {
                    context.Logger.Debug(
                        "Browser reader: UIA produced no URL for HWND 0x{Hwnd:X} (no Edit/ComboBox match)",
                        window.Handle.ToInt64());
                }
            }

            // 3. Content: CDP-HTML → ReverseMarkdown; sonst UIA-TextPattern
            var maxChars = context.Config.AppReader.Browser.MaxTextLengthKB * 1024;
            string? contentMarkdown = null;
            string contentSource = "none";

            if (!string.IsNullOrEmpty(cdp?.Html))
            {
                contentMarkdown = ConvertHtmlToMarkdown(cdp.Html, maxChars, context.Config.AppReader.Browser.Markdown);
                contentSource = "cdp";
            }
            else
            {
                var text = TryReadBodyViaUia(window.Handle, maxChars, context.Logger);
                if (!string.IsNullOrEmpty(text))
                {
                    contentMarkdown = $"```\n{TruncateForCodeBlock(text, maxChars)}\n```";
                    contentSource = "uia-text";
                }
                else
                {
                    context.Logger.Debug(
                        "Browser reader: UIA produced no body for HWND 0x{Hwnd:X} (no Document/Pane match)",
                        window.Handle.ToInt64());
                }
            }

            var md = new StringBuilder();
            md.AppendLine($"**Tab title:** {tabTitle}");
            md.AppendLine($"**URL:** {(url ?? "_(not exposed)_")}");
            md.AppendLine($"**Browser suffix:** {browserSuffix}");
            md.AppendLine($"**Content source:** {contentSource}");
            if (!string.IsNullOrEmpty(contentMarkdown))
            {
                md.AppendLine();
                md.AppendLine("## Content");
                md.AppendLine();
                md.AppendLine(contentMarkdown);
            }

            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
                ContextLabel: url ?? tabTitle,
                ContextKind: "url",
                ReaderName: DisplayName,
                ReaderVersion: typeof(BrowserAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["tabTitle"] = tabTitle,
                    ["url"] = url ?? "",
                    ["process"] = window.ProcessName,
                    ["contentSource"] = contentSource
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Browser reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    private static (string Title, string Suffix) ParseTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ("", "");

        foreach (var suffix in BrowserSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = title[..^suffix.Length].Trim();
                return (string.IsNullOrEmpty(stripped) ? title : stripped, suffix);
            }
        }
        return (title, "");
    }

    private static readonly Regex NoiseTagsRegex = new(
        @"<(script|style|svg|noscript)\b[^>]*>.*?</\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlCommentsRegex = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Matcht <c>src="data:image/svg+xml;base64,..."</c>-Data-URLs (Lazy-Load-Placeholder,
    /// Inline-Icon-SVGs). Wird durch einen Marker ersetzt, damit das umgebende
    /// <c>&lt;img&gt;</c>-Tag gültig bleibt.
    /// </summary>
    private static readonly Regex InlineSvgSrcRegex = new(
        @"src=""data:image/svg\+xml[^""]*""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ConvertHtmlToMarkdown(string html, int maxChars, MarkdownSettings? settings)
    {
        try
        {
            var cleaned = StripNoise(html);
            var converter = BuildConverter(settings);
            var md = converter.Convert(cleaned);
            return Truncate(md, maxChars);
        }
        catch (Exception ex)
        {
            // ReverseMarkdown kann bei exotischem HTML scheitern — Fallback auf Plain-Text
            return $"```\n{TruncateForCodeBlock(StripHtmlTags(html), maxChars)}\n```\n_(HTML→MD failed: {ex.Message})_";
        }
    }

    /// <summary>
    /// Baut einen frischen <see cref="ReverseMarkdown.Converter"/> mit den aus
    /// <paramref name="settings"/> gemappten <see cref="ReverseMarkdown.Config"/>-Werten.
    /// Felder, die in <paramref name="settings"/> nicht gesetzt sind, bleiben auf den
    /// Library-Defaults. Damit ist die JSON-Konfiguration 1:1 auf die
    /// ReverseMarkdown-Optionen abbildbar.
    /// </summary>
    public static ReverseMarkdown.Converter BuildConverter(MarkdownSettings? settings)
    {
        var converter = new ReverseMarkdown.Converter();
        if (settings is null) return converter;

        var cfg = converter.Config;

        if (settings.UnknownTags is { Length: > 0 } ut &&
            Enum.TryParse<ReverseMarkdown.Config.UnknownTagsOption>(ut, ignoreCase: true, out var u))
        {
            cfg.UnknownTags = u;
        }
        if (settings.GithubFlavored is bool gf) cfg.GithubFlavored = gf;
        if (settings.RemoveComments is bool rc) cfg.RemoveComments = rc;
        if (settings.WhitelistUriSchemes is { } ws) cfg.WhitelistUriSchemes = ws.ToArray();
        if (settings.SmartHrefHandling is bool sh) cfg.SmartHrefHandling = sh;
        if (settings.TableWithoutHeaderRowHandling is { Length: > 0 } twh &&
            Enum.TryParse<ReverseMarkdown.Config.TableWithoutHeaderRowHandlingOption>(twh, ignoreCase: true, out var tt))
        {
            cfg.TableWithoutHeaderRowHandling = tt;
        }
        if (settings.ListBulletChar is { Length: > 0 } lb) cfg.ListBulletChar = lb[0];
        if (settings.DefaultCodeBlockLanguage is { Length: > 0 } dcl) cfg.DefaultCodeBlockLanguage = dcl;

        return converter;
    }

    /// <summary>
    /// Entfernt Content-Blöcke, die für Markdown-Aufzeichnung sinnlos sind:
    /// &lt;script&gt;/&lt;style&gt;/&lt;svg&gt;/&lt;noscript&gt;, HTML-Kommentare
    /// und inline base64-SVG-Data-URLs in <c>src</c>-Attributen.
    /// </summary>
    public static string StripNoise(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        try
        {
            var withoutComments = HtmlCommentsRegex.Replace(html, string.Empty);
            var withoutNoiseTags = NoiseTagsRegex.Replace(withoutComments, string.Empty);
            return InlineSvgSrcRegex.Replace(withoutNoiseTags, "src=\"(inline-svg)\"");
        }
        catch
        {
            return html;
        }
    }

    private static string StripHtmlTags(string html)
    {
        return Regex.Replace(html, "<[^>]+>", string.Empty);
    }

    private static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        return s[..maxChars] + "\n\n_(truncated — original length: " + s.Length + " chars)_";
    }

    private static string TruncateForCodeBlock(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        return s[..maxChars] + "\n// ... (truncated)";
    }

    private static string? TryReadUrlViaUia(IntPtr hWnd, ILogger? logger = null)
    {
        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element is null)
            {
                logger?.Debug("Browser reader UIA: AutomationElement.FromHandle returned null for HWND 0x{Hwnd:X}", hWnd.ToInt64());
                return null;
            }

            // Strategie 1+2: Edits + ComboBoxes mit Address/Search/URL im Name oder in der AutomationId.
            // Moderner Edge hat die Adressleiste oft als ComboBox mit AutomationId "urlBar"
            // oder Name "Address and search bar" (lokalisiert).
            var urlCandidates = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
            var cond = new AndCondition(
                urlCandidates,
                new PropertyCondition(AutomationElement.IsEnabledProperty, true));

            var edits = element.FindAll(TreeScope.Descendants, cond);
            logger?.Debug("Browser reader UIA: found {Count} Edit/ComboBox candidates under HWND 0x{Hwnd:X}", edits.Count, hWnd.ToInt64());

            string? firstPlausibleUrl = null;
            foreach (AutomationElement edit in edits)
            {
                var name = edit.Current.Name ?? string.Empty;
                var automationId = edit.Current.AutomationId ?? string.Empty;
                var isAddressLike =
                    name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("URL", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Url bar", StringComparison.OrdinalIgnoreCase) ||
                    automationId.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                    automationId.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                    automationId.Equals("urlBar", StringComparison.OrdinalIgnoreCase) ||
                    automationId.Equals("addressBar", StringComparison.OrdinalIgnoreCase);

                if (!isAddressLike) continue;

                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) ||
                    patternObj is not ValuePattern vp)
                {
                    continue;
                }
                var value = vp.Current.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (UrlRegex.IsMatch(value)) return value;
                firstPlausibleUrl ??= value;
            }

            // Strategie 3 (Fallback): Wenn genau EIN Edit/ComboBox im Baum vorhanden ist
            // und einen Wert hat, der wie eine URL aussieht, nehmen wir ihn an.
            // Edge hat nur eine kombinierte Adress-/Suchleiste — selbst wenn Name/Id
            // nicht passen, ist das die Address-Bar.
            if (firstPlausibleUrl is null && edits.Count == 1)
            {
                AutomationElement only = edits[0];
                if (only.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) &&
                    patternObj is ValuePattern vp)
                {
                    var value = vp.Current.Value;
                    if (!string.IsNullOrWhiteSpace(value) && UrlRegex.IsMatch(value))
                    {
                        logger?.Debug("Browser reader UIA: using single-Edit/ComboBox fallback for URL");
                        return value;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Browser reader UIA: TryReadUrlViaUia failed for HWND 0x{Hwnd:X}", hWnd.ToInt64());
            return null;
        }
    }

    private static string? TryReadBodyViaUia(IntPtr hWnd, int maxChars, ILogger? logger = null)
    {
        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element is null)
            {
                logger?.Debug("Browser reader UIA: AutomationElement.FromHandle returned null for HWND 0x{Hwnd:X}", hWnd.ToInt64());
                return null;
            }

            // Strategie 1: Document-Control (klassisch, z. B. Firefox/IE).
            var doc = element.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            if (doc is not null)
            {
                var fromDoc = TryReadTextFromElement(doc, maxChars);
                if (fromDoc is not null)
                {
                    logger?.Debug("Browser reader UIA: body found via Document control");
                    return fromDoc;
                }
            }

            // Strategie 2: Pane-Control mit TextPattern. Edge (Chromium) rendert
            // Webseiten-Content oft in einer Custom-Tree-Pane, nicht als Document.
            var pane = element.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
            if (pane is not null)
            {
                var fromPane = TryReadTextFromElement(pane, maxChars);
                if (fromPane is not null)
                {
                    logger?.Debug("Browser reader UIA: body found via Pane control");
                    return fromPane;
                }
            }

            logger?.Debug("Browser reader UIA: no Document or Pane control with TextPattern under HWND 0x{Hwnd:X}", hWnd.ToInt64());
            return null;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Browser reader UIA: TryReadBodyViaUia failed for HWND 0x{Hwnd:X}", hWnd.ToInt64());
            return null;
        }
    }

    private static string? TryReadTextFromElement(AutomationElement element, int maxChars)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
            patternObj is TextPattern tp)
        {
            var range = tp.DocumentRange;
            var text = range.GetText(maxChars);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        return null;
    }
}