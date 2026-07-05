using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AiRecall.Core.Configuration;

namespace AiRecall.AppReader.Outlook;

/// <summary>
/// Konvertiert Outlook-Mail-Bodies (HTML oder Plain) zu Markdown.
/// Spec 0004 Iter. 3 §„HTML-zu-Markdown-Konvertierung".
///
/// <para>
/// Outlook produziert haeufig verschachteltes HTML mit Conditional-
/// Comments, Word-spezifischen CSS-Klassen und Inline-Styles. Unsere
/// Konvertierung strippt alles, was sie nicht versteht — das Ergebnis ist
/// Plain-Text mit Markdown-Link-Syntax, nicht „schoenes" Markdown.
/// Ausreichend fuer Volltext-Indexierung (Ziel von Recall), nicht fuer
/// Rendering.
/// </para>
///
/// <para>
/// Bewusst <b>NICHT</b> ReverseMarkdown (zu fett, viele Edge-Cases bei
/// Outlook-spezifischem HTML). Eigene simple Tag-Strip-Logik.
/// </para>
/// </summary>
public static class OutlookBodyToMarkdown
{
    // Outlook-spezifische Conditional-Comments: <!--[if gte mso 9]>...<![endif]-->
    private static readonly Regex ConditionalCommentRegex = new(
        @"<!--\[if[^]]*]>.*?<!\[endif\]-->",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // Normale HTML-Kommentare
    private static readonly Regex HtmlCommentRegex = new(
        @"<!--.*?-->",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // <style>...</style> Bloecke (CSS)
    private static readonly Regex StyleBlockRegex = new(
        @"<style\b[^>]*>.*?</style>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // <script>...</script> Bloecke
    private static readonly Regex ScriptBlockRegex = new(
        @"<script\b[^>]*>.*?</script>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // <a href="X">Y</a>
    private static readonly Regex AnchorRegex = new(
        @"<a\b[^>]*?\bhref\s*=\s*[""']?(?<url>[^""'\s>]+)[""']?[^>]*>(?<text>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // <img ...> (self-closing oder nicht)
    private static readonly Regex ImageRegex = new(
        @"<img\b[^>]*?(?:\bsrc\s*=\s*[""']?(?<src>[^""'\s>]+)[""']?[^>]*?)?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Block-Elemente, die Zeilenumbrueche erzwingen
    private static readonly Regex BlockTagRegex = new(
        @"</?(?:p|div|br|h[1-6]|li|tr|blockquote)\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Generisches Tag-Match (fuer Stripping)
    private static readonly Regex AnyTagRegex = new(
        @"<[^>]+?>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Konvertiert Plain-Text zu Markdown (1:1-Passthrough, mit
    /// HTML-Decoding falls noetig).
    /// </summary>
    public static string FromPlain(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        return WebUtility.HtmlDecode(plain).TrimEnd();
    }

    /// <summary>
    /// Konvertiert HTML zu Markdown. Verwendet
    /// <see cref="HtmlToMarkdownOptions"/> aus <see cref="OutlookConfig"/>
    /// fuer Link/LineBreak/Image-Behandlung.
    /// </summary>
    public static string FromHtml(string html, HtmlToMarkdownOptions options)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        if (options is null) options = new HtmlToMarkdownOptions();

        var s = html;

        // 1. Outlook-Conditional-Comments komplett entfernen
        s = ConditionalCommentRegex.Replace(s, " ");

        // 2. Style + Script Bloecke entfernen
        s = StyleBlockRegex.Replace(s, " ");
        s = ScriptBlockRegex.Replace(s, " ");

        // 3. HTML-Kommentare entfernen
        s = HtmlCommentRegex.Replace(s, string.Empty);

        // 4. <img>-Tags entfernen oder konvertieren
        if (options.StripImages)
        {
            s = ImageRegex.Replace(s, " ");
        }
        else
        {
            s = ImageRegex.Replace(s, m =>
            {
                var src = m.Groups["src"].Value;
                return string.IsNullOrEmpty(src) ? " " : $"![image]({src})";
            });
        }

        // 5. <a href>... </a> zu Markdown-Link konvertieren
        if (options.PreserveLinks)
        {
            s = AnchorRegex.Replace(s, m =>
            {
                var url = m.Groups["url"].Value;
                var text = StripTags(m.Groups["text"].Value).Trim();
                text = WebUtility.HtmlDecode(text);
                if (string.IsNullOrEmpty(text)) text = url;
                return $"[{text}]({url})";
            });
        }

        // 6. Block-Tags zu Zeilenumbruechen
        if (options.PreserveLineBreaks)
        {
            s = BlockTagRegex.Replace(s, m =>
            {
                var tag = m.Value.ToLowerInvariant();
                return tag.StartsWith("</") ? "\n\n" : "\n";
            });
        }

        // 7. Alle restlichen Tags entfernen
        s = AnyTagRegex.Replace(s, "");

        // 8. HTML-Entities decoden
        s = WebUtility.HtmlDecode(s);

        // 8b. Non-Breaking-Space (U+00A0) zu normalem Space normalisieren,
        // damit der Whitespace-Normalizer ihn korrekt behandelt.
        s = s.Replace('\u00A0', ' ');

        // 9. Mehrfache Leerzeichen und Zeilenumbrueche normalisieren
        s = NormalizeWhitespace(s);

        return s.Trim();
    }

    /// <summary>
    /// Convenience: <c>FromHtml(html, context.Config.AppReader.Outlook.HtmlToMarkdown)</c>.
    /// </summary>
    public static string FromHtml(string html, OutlookConfig config)
    {
        if (config is null) return FromHtml(html, new HtmlToMarkdownOptions());
        return FromHtml(html, config.HtmlToMarkdown);
    }

    /// <summary>
    /// Schneidet einen Text auf maxBytes Zeichen (nicht Bytes!) ab und
    /// haengt einen Hinweis an, wenn abgeschnitten wurde. Verwendet die
    /// <see cref="OutlookConfig.BodyTruncateKB"/>-Grenze.
    /// </summary>
    public static string Truncate(string text, int maxKB)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var maxChars = maxKB * 1024;
        if (text.Length <= maxChars) return text;

        var truncated = text[..maxChars];
        var originalKB = text.Length / 1024;
        return truncated + $"\n\n_(... truncated, original size: {originalKB} KB)_";
    }

    private static string StripTags(string input)
    {
        return AnyTagRegex.Replace(input, "");
    }

    private static string NormalizeWhitespace(string input)
    {
        // Mehrfache Leerzeichen/Tabs/Newlines zusammenfassen, ABER:
        // Block-Boundaries (zwei aufeinanderfolgende Newlines) bleiben
        // erhalten. Drei oder mehr aufeinanderfolgende Newlines werden
        // auf zwei kollabiert.
        var sb = new StringBuilder(input.Length);
        bool lastWasSpace = false;
        int consecutiveNewlines = 0;
        foreach (var ch in input)
        {
            if (ch == '\r') continue; // CR entfernen (LF bleibt)
            if (ch == '\n')
            {
                consecutiveNewlines++;
                if (consecutiveNewlines <= 2) sb.Append('\n');
                lastWasSpace = false;
                continue;
            }
            if (consecutiveNewlines > 0 || lastWasSpace)
            {
                // Reset Block-Boundary-Tracker, sobald ein Nicht-Newline-Zeichen kommt
                consecutiveNewlines = 0;
            }
            if (ch == ' ' || ch == '\t')
            {
                if (lastWasSpace) continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        // Trailing Newlines nicht abschneiden — Caller macht Trim()
        return sb.ToString();
    }
}