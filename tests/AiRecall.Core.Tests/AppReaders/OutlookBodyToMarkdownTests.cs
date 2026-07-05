using AiRecall.AppReader.Outlook;
using AiRecall.Core.Configuration;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="OutlookBodyToMarkdown"/> (Spec 0004 Iter. 3).
/// Konvertierung Plain→MD (passthrough), HTML→MD (Tag-Strip + Links +
/// Line-Breaks + Images + Entities + Whitespace-Normalisierung + Truncate).
/// </summary>
public class OutlookBodyToMarkdownTests
{
    private static HtmlToMarkdownOptions DefaultOpts() => new(); // alle Defaults

    [Fact]
    public void FromPlain_PassesThrough()
    {
        var result = OutlookBodyToMarkdown.FromPlain("Hallo Martin,\n\nGruss, Alice");
        Assert.Equal("Hallo Martin,\n\nGruss, Alice", result);
    }

    [Fact]
    public void FromPlain_DecodesHtmlEntities()
    {
        var result = OutlookBodyToMarkdown.FromPlain("AT&amp;T &lt;3");
        Assert.Equal("AT&T <3", result);
    }

    [Fact]
    public void FromPlain_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OutlookBodyToMarkdown.FromPlain(""));
        Assert.Equal(string.Empty, OutlookBodyToMarkdown.FromPlain(null!));
    }

    [Fact]
    public void FromHtml_StripsStyleAndScript()
    {
        var html = "<style>body { color: red }</style><script>alert(1)</script>Hallo Welt";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Hallo Welt", result);
    }

    [Fact]
    public void FromHtml_StripsConditionalComments()
    {
        var html = "<!--[if gte mso 9]><xml>foo</xml><![endif]-->Sichtbarer Text";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Sichtbarer Text", result);
    }

    [Fact]
    public void FromHtml_StripsRegularComments()
    {
        var html = "Vor<!-- geheim -->Nach";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("VorNach", result);
    }

    [Fact]
    public void FromHtml_PreservesLinks()
    {
        var html = "Klick <a href=\"https://example.com\">hier</a> bitte";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Klick [hier](https://example.com) bitte", result);
    }

    [Fact]
    public void FromHtml_PreservesLinksWithoutHrefKeepsText()
    {
        var html = "<a>nothing</a>";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        // Kein href → Text bleibt, aber Tag wird gestrippt
        Assert.Equal("nothing", result);
    }

    [Fact]
    public void FromHtml_DropsImagesByDefault()
    {
        var html = "Vor <img src=\"https://tracker.example/pixel.gif\" alt=\"x\"> Nach";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Vor Nach", result);
        Assert.DoesNotContain("tracker", result);
    }

    [Fact]
    public void FromHtml_KeepsImagesWhenConfigured()
    {
        var opts = new HtmlToMarkdownOptions { StripImages = false };
        var html = "Vor <img src=\"https://example.com/cat.png\" alt=\"cat\"> Nach";
        var result = OutlookBodyToMarkdown.FromHtml(html, opts);
        Assert.Equal("Vor ![image](https://example.com/cat.png) Nach", result);
    }

    [Fact]
    public void FromHtml_ConvertsLineBreaks()
    {
        var html = "<p>Absatz eins</p><p>Absatz zwei</p>";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Absatz eins\n\nAbsatz zwei", result);
    }

    [Fact]
    public void FromHtml_BrBecomesNewline()
    {
        var html = "Zeile eins<br>Zeile zwei";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Zeile eins\nZeile zwei", result);
    }

    [Fact]
    public void FromHtml_DecodesEntities()
    {
        var html = "AT&amp;T &lt;3&gt; &quot;quoted&quot; &nbsp; end";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        // &nbsp; wird zu Non-Breaking-Space, der im Normalizer zu Space wird.
        // Whitespace-Normalizer kollabiert mehrfache Spaces auf einen.
        Assert.Equal("AT&T <3> \"quoted\" end", result);
    }

    [Fact]
    public void FromHtml_NormalizesWhitespace()
    {
        // Mehrfache Spaces/Tabs/Newlines zusammenfassen
        var html = "<p>Wort1    Wort2\n\n\n\nWort3</p>";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Wort1 Wort2\n\nWort3", result);
    }

    [Fact]
    public void FromHtml_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OutlookBodyToMarkdown.FromHtml("", DefaultOpts()));
        Assert.Equal(string.Empty, OutlookBodyToMarkdown.FromHtml("   ", DefaultOpts()));
    }

    [Fact]
    public void FromHtml_StripsStrongAndEm()
    {
        var html = "<strong>fett</strong> und <em>kursiv</em>";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("fett und kursiv", result);
    }

    [Fact]
    public void FromHtml_OutlookConditionalXml_Removed()
    {
        // Outlook packt Word-HTML in Conditional-Comments
        var html = @"<!--[if gte mso 9]>
<xml>
  <o:OfficeDocumentSettings>
    <o:AllowPNG/>
  </o:OfficeDocumentSettings>
</xml>
<![endif]-->
<p>Echte Mail-Inhalt</p>";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Equal("Echte Mail-Inhalt", result);
    }

    [Fact]
    public void FromHtml_RealWorldOutlookBody()
    {
        // Typischer Outlook-Newsletter. Prueft, dass die Kern-Elemente
        // (Style-Block weg, Image weg, Link konvertiert, Entities decodiert,
        // Block-Tags in Newlines) alle korrekt funktionieren. Exact-Whitespace-
        // Match ist nicht garantiert weil Outlook-Newsletter zwischen Block-
        // Tags oft Whitespace hat, den der Konverter nicht in jedem Fall
        // weg-normalisiert.
        var html = @"
<style>.ExternalClass p { margin: 0 }</style>
<div style='font-family: Arial'>
  <p>Hallo Martin,</p>
  <p>wir haben neue Angebote: <a href='https://shop.example/offer'>Hier klicken</a></p>
  <p><img src='https://tracker.example/p.gif' width='1' height='1'></p>
  <p>Viele Gr&uuml;&szlig;e,<br>Ihr Team</p>
</div>";
        var result = OutlookBodyToMarkdown.FromHtml(html, DefaultOpts());
        Assert.Contains("Hallo Martin,", result);
        Assert.Contains("[Hier klicken](https://shop.example/offer)", result);
        Assert.Contains("Viele Grüße,", result);
        Assert.Contains("Ihr Team", result);
        Assert.DoesNotContain("tracker.example", result); // img entfernt
        Assert.DoesNotContain("font-family", result); // style entfernt
        Assert.DoesNotContain("<", result); // keine Tags mehr
    }

    [Fact]
    public void Truncate_ShortText_NoOp()
    {
        var text = "Kurze Mail";
        var result = OutlookBodyToMarkdown.Truncate(text, maxKB: 256);
        Assert.Equal("Kurze Mail", result);
    }

    [Fact]
    public void Truncate_LongText_AppendsTruncationHint()
    {
        var text = new string('x', 300 * 1024); // 300 KB
        var result = OutlookBodyToMarkdown.Truncate(text, maxKB: 256);
        Assert.EndsWith("_(... truncated, original size: 300 KB)_", result);
        Assert.True(result.Length <= 256 * 1024 + 100); // + Spielraum fuer Hint
    }

    [Fact]
    public void Truncate_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OutlookBodyToMarkdown.Truncate("", 256));
        Assert.Equal(string.Empty, OutlookBodyToMarkdown.Truncate(null!, 256));
    }

    [Fact]
    public void FromHtml_NullOptions_UsesDefaults()
    {
        var html = "<p>Hallo</p>";
        var result = OutlookBodyToMarkdown.FromHtml(html, options: null!);
        Assert.Equal("Hallo", result);
    }

    [Fact]
    public void FromHtml_WithOutlookConfig_UsesConfigOptions()
    {
        var config = new OutlookConfig();
        config.HtmlToMarkdown.StripImages = false;
        config.HtmlToMarkdown.PreserveLinks = true;

        var html = "<p>Bild <img src='x.png'></p>";
        var result = OutlookBodyToMarkdown.FromHtml(html, config);
        Assert.Equal("Bild ![image](x.png)", result);
    }
}