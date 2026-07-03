using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

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

    private static readonly ReverseMarkdown.Converter MarkdownConverter = new();

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var (tabTitle, browserSuffix) = ParseTitle(window.Title);

            // 1. CDP-Pfad versuchen (intern: Skip wenn cdp.enabled == false)
            var cdp = ChromeDevToolsProtocolClient.TryReadActivePage(context.Config.AppReader.Browser.Cdp);

            // 2. URL: CDP hat Vorrang, sonst UIA
            string? url = cdp?.Url;
            if (string.IsNullOrEmpty(url))
            {
                url = TryReadUrlViaUia(window.Handle);
            }

            // 3. Content: CDP-HTML → ReverseMarkdown; sonst UIA-TextPattern
            var maxChars = context.Config.AppReader.Browser.MaxTextLengthKB * 1024;
            string? contentMarkdown = null;
            string contentSource = "none";

            if (!string.IsNullOrEmpty(cdp?.Html))
            {
                contentMarkdown = ConvertHtmlToMarkdown(cdp.Html, maxChars);
                contentSource = "cdp";
            }
            else
            {
                var text = TryReadBodyViaUia(window.Handle, maxChars);
                if (!string.IsNullOrEmpty(text))
                {
                    contentMarkdown = $"```\n{TruncateForCodeBlock(text, maxChars)}\n```";
                    contentSource = "uia-text";
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

    private static string ConvertHtmlToMarkdown(string html, int maxChars)
    {
        try
        {
            var cleaned = StripNoise(html);
            var md = MarkdownConverter.Convert(cleaned);
            return Truncate(md, maxChars);
        }
        catch (Exception ex)
        {
            // ReverseMarkdown kann bei exotischem HTML scheitern — Fallback auf Plain-Text
            return $"```\n{TruncateForCodeBlock(StripHtmlTags(html), maxChars)}\n```\n_(HTML→MD failed: {ex.Message})_";
        }
    }

    /// <summary>
    /// Entfernt Content-Blöcke, die für Markdown-Aufzeichnung sinnlos sind:
    /// &lt;script&gt;/&lt;style&gt;/&lt;svg&gt;/&lt;noscript&gt; und HTML-Kommentare.
    /// </summary>
    public static string StripNoise(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        try
        {
            var withoutComments = HtmlCommentsRegex.Replace(html, string.Empty);
            return NoiseTagsRegex.Replace(withoutComments, string.Empty);
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

    private static string? TryReadUrlViaUia(IntPtr hWnd)
    {
        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element is null) return null;

            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true));

            var edits = element.FindAll(TreeScope.Descendants, cond);
            foreach (AutomationElement edit in edits)
            {
                var name = edit.Current.Name ?? string.Empty;
                if (name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("URL", StringComparison.OrdinalIgnoreCase))
                {
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) &&
                        patternObj is ValuePattern vp)
                    {
                        var value = vp.Current.Value;
                        if (!string.IsNullOrWhiteSpace(value) && UrlRegex.IsMatch(value))
                        {
                            return value;
                        }
                    }
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadBodyViaUia(IntPtr hWnd, int maxChars)
    {
        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element is null) return null;

            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
            var doc = element.FindFirst(TreeScope.Descendants, cond);
            if (doc is null) return null;

            if (doc.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                patternObj is TextPattern tp)
            {
                var range = tp.DocumentRange;
                var text = range.GetText(maxChars);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}