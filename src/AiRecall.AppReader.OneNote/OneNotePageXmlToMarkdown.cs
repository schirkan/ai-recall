using System.Text;
using System.Xml;
using AiRecall.Core.Configuration;

namespace AiRecall.AppReader.OneNote;

/// <summary>
/// Pure-Function-Konverter von OneNote-Page-XML (xs2013) zu Markdown (Spec 0010).
///
/// <para>Diese Klasse ist absichtlich <b>zustandslos und IO-frei</b>: keine
/// COM-Aufrufe, kein Disk-Zugriff, kein Logging. Sie ist vollstaendig
/// deterministisch und daher einfach unit-testbar
/// (siehe <c>OneNotePageXmlToMarkdownTests</c>).</para>
///
/// <para>Mapping-Tabelle (siehe Spec 0010 \u00a7Komponenten):</para>
/// <list type="table">
/// <listheader><term>OneNote-XML</term><description>Markdown</description></listheader>
/// <item><term><c>one:OE</c> (Top-Level)</term><description>Absatz (<c>\n\n</c>)</description></item>
/// <item><term><c>one:T</c> (CDATA)</term><description>Plain-Text, HTML-Entities decodiert</description></item>
/// <item><term><c>one:Image</c></term><description><c>![alt](filePath)</c> wenn <c>IncludeImages=true</c></description></item>
/// <item><term><c>one:Tag</c> (to-do)</term><description><c>[ ]</c> oder <c>[x]</c> wenn <c>IncludeTags=true</c></description></item>
/// <item><term><c>one:Tag</c> (andere)</term><description><c>#tag-name</c> wenn <c>IncludeTags=true</c></description></item>
/// <item><term><c>one:InkContent</c></term><description><c>*(handschriftlich)*</c> Hinweis</description></item>
/// <item><term><c>one:Table</c></term><description>Markdown-Tabelle</description></item>
/// <item><term><c>one:InsertedFile</c></term><description>Filename-Liste (Frontmatter, keine Persistenz)</description></item>
/// </list>
///
/// <para>HTML-Entities werden via <see cref="System.Web.HttpUtility.HtmlDecode(string?)"/>
/// dekodiert (in .NET 8 built-in unter <c>System.Web</c>).</para>
/// </summary>
internal static class OneNotePageXmlToMarkdown
{
    /// <summary>OneNote-XML-Namespace (Schema-Variante 2013, siehe <see cref="OneNoteComInterop"/>).</summary>
    internal const string OneNoteXmlNamespace = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    /// <summary>
    /// Konvertiert den Body (alle Page-Children) zu Markdown.
    /// Der Titel-Header (\"# Title\") wird NICHT hier erzeugt \u2014 das macht der
    /// Reader (Cluster 4), der ausserdem Notebook/Section/LastModified kennt.
    /// </summary>
    /// <returns>Markdown-Body ohne fuehrenden/abschliessenden Trim-Probleme.
    /// Bei null/ungueltigem XML: leerer String.</returns>
    public static string ConvertBody(string xml, OneNoteConfig config)
    {
        if (string.IsNullOrEmpty(xml) || config == null) return string.Empty;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var root = doc.DocumentElement;
            if (root == null) return string.Empty;

            var sb = new StringBuilder();
            AppendPageChildren(root, sb, config);
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Extrahiert nur den Page-Titel aus dem <c>name</c>-Attribut der Root-<c>Page</c>.
    /// Liefert null bei null/ungueltigem XML oder fehlendem Attribut.
    /// </summary>
    public static string? ExtractPageTitle(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var root = doc.DocumentElement;
            return root?.Attributes?["name"]?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extrahiert den <c>lastModifiedTime</c>-String der Page.
    /// Liefert null bei null/ungueltigem XML oder fehlendem Attribut.
    /// </summary>
    public static string? ExtractLastModified(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc.DocumentElement?.Attributes?["lastModifiedTime"]?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sammelt alle <c>one:InsertedFile</c>-Filenames (Pfade truncated auf Filename).
    /// Wird fuer den Frontmatter des Readers verwendet (Cluster 4).
    /// </summary>
    public static IReadOnlyList<string> ExtractInsertedFileNames(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return Array.Empty<string>();
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("one", OneNoteXmlNamespace);

            var nodes = doc.SelectNodes("//one:InsertedFile", ns);
            if (nodes == null || nodes.Count == 0) return Array.Empty<string>();

            var result = new List<string>(nodes.Count);
            foreach (XmlNode node in nodes)
            {
                var path = node.Attributes?["path"]?.Value;
                if (string.IsNullOrEmpty(path)) continue;
                // nur Filename, kein voller Pfad (Datenschutz)
                var idx = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
                var name = idx >= 0 ? path.Substring(idx + 1) : path;
                if (!string.IsNullOrEmpty(name) && !result.Contains(name))
                {
                    result.Add(name);
                }
            }
            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ============================================================================
    // Implementation \u2014 rekursiver Page-Children-Walk
    // ============================================================================

    private static void AppendPageChildren(XmlNode root, StringBuilder sb, OneNoteConfig config)
    {
        var first = true;
        foreach (XmlNode child in root.ChildNodes)
        {
            // Block-Trennung zwischen Page-Children (Outline, Image am Page-Level, InsertedFile, etc.)
            if (!first && NeedsBlockSeparatorBefore(child))
            {
                sb.Append("\n\n");
            }
            first = false;

            AppendNode(child, sb, config, listIndent: 0);
        }
    }

    /// <summary>
    /// Einige Nodes brauchen einen Block-Separator davor (z. B. Image auf Page-Level),
    /// andere nicht (z. B. Inline-Tag innerhalb eines OE).
    /// </summary>
    private static bool NeedsBlockSeparatorBefore(XmlNode node)
    {
        return node.LocalName is "Outline" or "Image" or "Table" or "InkContent" or "InsertedFile" or "Tag";
    }

    private static void AppendNode(XmlNode node, StringBuilder sb, OneNoteConfig config, int listIndent)
    {
        switch (node.LocalName)
        {
            case "Outline":
                AppendOutline(node, sb, config, listIndent);
                break;
            case "OE":
                AppendOE(node, sb, config, listIndent);
                break;
            case "T":
                AppendT(node, sb);
                break;
            case "Image":
                AppendImage(node, sb, config);
                break;
            case "Tag":
                AppendTag(node, sb, config);
                break;
            case "InsertedFile":
                AppendInsertedFile(node, sb, config);
                break;
            case "InkContent":
                AppendInkContent(node, sb);
                break;
            case "Table":
                AppendTable(node, sb, config);
                break;
            default:
                // Unbekannte Tags: rekursiv in Children laufen
                foreach (XmlNode child in node.ChildNodes)
                {
                    AppendNode(child, sb, config, listIndent);
                }
                break;
        }
    }

    // ----------------------------------------------------------------------------
    // Outline / OE \u2014 Absatz- und Listen-Logik
    // ----------------------------------------------------------------------------

    private static void AppendOutline(XmlNode outline, StringBuilder sb, OneNoteConfig config, int listIndent)
    {
        var first = true;
        foreach (XmlNode child in outline.ChildNodes)
        {
            if (child.LocalName == "OE")
            {
                if (!first) sb.Append("\n\n");
                AppendOE(child, sb, config, listIndent);
                first = false;
            }
        }
        sb.Append("\n\n");
    }

    /// <summary>
    /// Outline-Element (<c>one:OE</c>): entweder Top-Level-Paragraph oder Bullet-Item.
    /// Mehrere <c>one:T</c>-Runs werden konkateniert (mit optionalen Inline-Tags/Images).
    /// Verschachtelte <c>one:OE</c> = Sub-Bullet (ein Level tiefer).
    /// </summary>
    private static void AppendOE(XmlNode oe, StringBuilder sb, OneNoteConfig config, int listIndent)
    {
        var inlineContent = new StringBuilder();
        var listChildren = new List<XmlNode>();

        foreach (XmlNode child in oe.ChildNodes)
        {
            switch (child.LocalName)
            {
                case "T":
                    inlineContent.Append(DecodeHtml(child.InnerText));
                    break;
                case "Image":
                    if (config.IncludeImages)
                    {
                        inlineContent.Append(' ');
                        inlineContent.Append(FormatImageInline(child));
                    }
                    break;
                case "Tag":
                    if (config.IncludeTags)
                    {
                        inlineContent.Append(' ');
                        inlineContent.Append(FormatTagInline(child));
                    }
                    break;
                case "InkContent":
                    inlineContent.Append(" *(handschriftlich)*");
                    break;
                case "OE":
                    // Sub-Bullet
                    listChildren.Add(child);
                    break;
                default:
                    // Unbekannt \u2014 ignorieren (rekursiv wuerde falsche Reihenfolge erzeugen)
                    break;
            }
        }

        // Wenn OE ein Bullet ist (enthaelt nested OE oder hat style="list"), Prefix setzen
        var bulletPrefix = IsBulletStyle(oe)
            ? new string(' ', listIndent * 2) + "- "
            : string.Empty;

        // Inline-Content schreiben (falls vorhanden)
        if (inlineContent.Length > 0)
        {
            sb.Append(bulletPrefix);
            sb.Append(inlineContent);
        }

        // Sub-Bullets rekursiv (jeweils mit Block-Separator)
        foreach (var subOe in listChildren)
        {
            sb.Append('\n');
            AppendOE(subOe, sb, config, listIndent + 1);
        }
    }

    /// <summary>
    /// Heuristik: OneNote markiert Bullet-OEs mit style="list".
    /// Falls nicht gesetzt, aber verschachtelte OEs vorhanden, ebenfalls Bullet.
    /// </summary>
    private static bool IsBulletStyle(XmlNode oe)
    {
        var style = oe.Attributes?["style"]?.Value;
        return !string.IsNullOrEmpty(style) && style.Contains("list", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendT(XmlNode t, StringBuilder sb)
    {
        sb.Append(DecodeHtml(t.InnerText));
    }

    // ----------------------------------------------------------------------------
    // Image / Tag / InkContent / InsertedFile / Table
    // ----------------------------------------------------------------------------

    private static void AppendImage(XmlNode image, StringBuilder sb, OneNoteConfig config)
    {
        if (!config.IncludeImages) return;
        sb.AppendLine();
        sb.Append(FormatImageInline(image));
    }

    /// <summary>Inline-Format eines Bildes: <c>![alt](filename)</c>.</summary>
    private static string FormatImageInline(XmlNode image)
    {
        var alt = image.Attributes?["alt"]?.Value ?? "image";
        var src = image.Attributes?["src"]?.Value ?? string.Empty;
        // nur Filename (Datenschutz + Laenge)
        var idx = Math.Max(src.LastIndexOf('\\'), src.LastIndexOf('/'));
        var filename = idx >= 0 ? src.Substring(idx + 1) : src;
        if (string.IsNullOrEmpty(filename)) filename = "(embedded)";
        return $"![{alt}]({filename})";
    }

    private static void AppendTag(XmlNode tag, StringBuilder sb, OneNoteConfig config)
    {
        if (!config.IncludeTags) return;
        sb.AppendLine();
        sb.Append(FormatTagInline(tag));
    }

    /// <summary>
    /// Format eines Tags: to-do-Status als <c>[ ]</c>/<c>[x]</c>, andere als <c>#tag</c>.
    /// </summary>
    private static string FormatTagInline(XmlNode tag)
    {
        var type = tag.Attributes?["type"]?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(type))
        {
            return "*#tag*";
        }
        if (type.StartsWith("to-do", StringComparison.OrdinalIgnoreCase))
        {
            var completed = type.Contains("complete", StringComparison.OrdinalIgnoreCase)
                || type.Contains("done", StringComparison.OrdinalIgnoreCase);
            return completed ? "[x]" : "[ ]";
        }
        // generic tag
        var name = type.StartsWith("#") ? type : "#" + type;
        return "*" + name + "*";
    }

    private static void AppendInsertedFile(XmlNode file, StringBuilder sb, OneNoteConfig config)
    {
        // InsertedFile am Page-Level: kurze Zeile mit Filename + Source
        var path = file.Attributes?["path"]?.Value ?? string.Empty;
        var idx = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        var filename = idx >= 0 ? path.Substring(idx + 1) : path;
        if (string.IsNullOrEmpty(filename)) filename = "(attachment)";
        sb.Append("*Attached File:* `").Append(filename).AppendLine("`");
    }

    private static void AppendInkContent(XmlNode ink, StringBuilder sb)
    {
        // Handschrift kann nicht in MD abgebildet werden.
        sb.AppendLine();
        sb.Append("*(handschriftlich)*");
        // Optional: erkannte Texte aus <one:InkWord>-Kindern extrahieren (experimentell)
        foreach (XmlNode child in ink.ChildNodes)
        {
            if (child.LocalName == "InkWord" || child.LocalName == "RecognizedText")
            {
                var text = DecodeHtml(child.InnerText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(' ').Append(text);
                }
            }
        }
    }

    /// <summary>
    /// Tabellen-Konvertierung zu Markdown-Tabelle.
    /// OneNote-XML: <c>one:Table</c> enthaelt <c>one:Row</c>s, diese <c>one:Cell</c>s,
    /// diese wiederum OEs (Inhalt).
    /// </summary>
    private static void AppendTable(XmlNode table, StringBuilder sb, OneNoteConfig config)
    {
        var rows = new List<List<string>>();
        var headerRow = new List<string>();
        var isFirstRow = true;

        foreach (XmlNode row in table.ChildNodes)
        {
            if (row.LocalName != "Row") continue;

            var cells = new List<string>();
            foreach (XmlNode cell in row.ChildNodes)
            {
                if (cell.LocalName != "Cell") continue;

                // Cell-Inhalt = XML der Zelle. Wir konvertieren mit derselben Logik,
                // kuerzen aber auf eine Zeile (Replace \n mit ' ') fuer Tabellen-Layout.
                var cellXml = cell.OuterXml;
                var cellMd = ConvertBody(cellXml, config).Replace("\n", " ").Replace("\r", " ");
                cellMd = System.Text.RegularExpressions.Regex.Replace(cellMd, @"\s+", " ").Trim();
                cells.Add(cellMd);
            }

            if (cells.Count == 0) continue;

            if (isFirstRow)
            {
                headerRow = cells;
                isFirstRow = false;
            }
            rows.Add(cells);
        }

        if (rows.Count == 0) return;

        // Header
        sb.Append("| ").Append(string.Join(" | ", headerRow)).AppendLine(" |");
        sb.Append('|');
        for (var i = 0; i < headerRow.Count; i++)
        {
            sb.Append(" --- |");
        }
        sb.AppendLine();

        // Body
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append("| ").Append(string.Join(" | ", rows[r])).AppendLine(" |");
        }
        sb.AppendLine();
    }

    // ----------------------------------------------------------------------------
    // Utilities
    // ----------------------------------------------------------------------------

    /// <summary>Decodiert HTML-Entities via System.Web.HttpUtility.HtmlDecode.</summary>
    private static string DecodeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return System.Web.HttpUtility.HtmlDecode(s) ?? string.Empty;
    }
}
