using System.Text;
using AiRecall.Core.Persistence;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Serilog;
using UglyToad.PdfPig;

namespace AiRecall.Conversion;

/// <summary>
/// Konvertiert eine Datei (beliebige Extension) in einen Markdown-String.
/// Zentrale Konverter-Stelle fuer Spec 0007 (Async Document Conversion Pipeline).
///
/// Strategie (rein .NET, keine externen Tools):
///   .txt/.md/.log/.csv  -> Plain-Read (File.ReadAllText)
///   .docx/.doc          -> DocumentFormat.OpenXml (Wordprocessing)
///   .xlsx/.xls          -> DocumentFormat.OpenXml (Spreadsheet) als Markdown-Tabelle
///   .pptx/.ppt          -> DocumentFormat.OpenXml (Presentation) als "### Slide N"-Liste
///   .pdf                -> UglyToad.PdfPig
///   .html/.htm          -> ReverseMarkdown
///   sonst               -> null + Log-Warnung
///
/// Nie crashen. Fehler -> null + Log.
///
/// Spec 0007 v0.4 final (Martin 2026-07-04 20:01). NuGet-Packages bestaetigt:
///   DocumentFormat.OpenXml (MIT, Microsoft), UglyToad.PdfPig (Apache 2.0), ReverseMarkdown (MIT).
/// </summary>
public static class DocumentConverter
{
    /// <summary>
    /// Konvertiert eine Datei nach Markdown. Liefert null bei Fehler oder unbekanntem Format.
    /// </summary>
    /// <param name="filePath">Absoluter Pfad zur Quelldatei.</param>
    /// <param name="maxChars">Maximale Laenge des MD-Strings (Default 64 KB).</param>
    /// <param name="logger">Optional Serilog-Logger fuer Diagnose.</param>
    /// <returns>MD-String oder null.</returns>
    public static string? Convert(string filePath, int maxChars = 64 * 1024, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (!File.Exists(filePath))
        {
            logger?.Warning("DocumentConverter: file not found: {FilePath}", filePath);
            return null;
        }
        if (maxChars <= 0) maxChars = 64 * 1024;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            return ext switch
            {
                ".txt" or ".md" or ".log" or ".csv" => Truncate(File.ReadAllText(filePath), maxChars),
                ".docx" or ".doc" => ConvertDocx(filePath, maxChars, logger),
                ".xlsx" or ".xls" => ConvertXlsx(filePath, maxChars, logger),
                ".pptx" or ".ppt" => ConvertPptx(filePath, maxChars, logger),
                ".pdf" => ConvertPdf(filePath, maxChars, logger),
                ".html" or ".htm" => ConvertHtml(filePath, maxChars, logger),
                _ => UnknownExtension(filePath, ext, logger)
            };
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "DocumentConverter: failed to convert {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Liefert den Konverter-Namen, der fuer die Datei benutzt werden wuerde
    /// (fuer Frontmatter `converterUsed:` und Diagnose-Logs).
    /// </summary>
    public static string GetConverterForFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return "none";
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".md" or ".log" or ".csv" => "textfile",
            ".docx" or ".doc" => "openxml-word",
            ".xlsx" or ".xls" => "openxml-excel",
            ".pptx" or ".ppt" => "openxml-powerpoint",
            ".pdf" => "pdfpig",
            ".html" or ".htm" => "reversemarkdown",
            _ => "none"
        };
    }

    /// <summary>Prueft, ob fuer die Extension ein Konverter verfuegbar ist.</summary>
    public static bool HasConverter(string filePath)
    {
        return GetConverterForFile(filePath) != "none";
    }

    // ----- Konverter pro Format -----

    private static string? ConvertDocx(string filePath, int maxChars, ILogger? logger)
    {
        var sb = new StringBuilder();
        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return null;

        foreach (var para in body.Elements<Paragraph>())
        {
            var text = para.InnerText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
                if (sb.Length >= maxChars) break;
            }
        }
        return Truncate(sb.ToString(), maxChars);
    }

    private static string? ConvertXlsx(string filePath, int maxChars, ILogger? logger)
    {
        var sb = new StringBuilder();
        using var doc = SpreadsheetDocument.Open(filePath, false);
        var workbookPart = doc.WorkbookPart;
        if (workbookPart == null) return null;

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        foreach (var sheet in workbookPart!.Workbook.Sheets!.OfType<Sheet>())
        {
            sb.AppendLine($"## {sheet.Name?.Value ?? "(unnamed)"}");
            sb.AppendLine();

            if (sheet.Id?.Value == null) continue;
            var sheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var sheetData = sheetPart!.Worksheet!.GetFirstChild<SheetData>();
            if (sheetData == null) continue;

            int colCount = 0;
            foreach (var row in sheetData.Elements<Row>())
            {
                if (sb.Length >= maxChars) break;

                var cells = row.Elements<Cell>().Select(c => GetCellValue(c, sharedStrings)).ToList();
                if (cells.Count > colCount) colCount = cells.Count;
                sb.Append("| ");
                foreach (var cell in cells) sb.Append(EscapeCell(cell)).Append(" | ");
                sb.AppendLine();
            }

            if (colCount > 0 && sheetData.Elements<Row>().Any())
            {
                // Header-Separator-Zeile (GFM)
                sb.Length -= sb.ToString().EndsWith(Environment.NewLine) ? Environment.NewLine.Length : 0;
                sb.Append("|");
                for (int i = 0; i < colCount; i++) sb.Append(" --- |");
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        return Truncate(sb.ToString(), maxChars);
    }

    private static string GetCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings != null
            && int.TryParse(value, out var idx)
            && idx >= 0 && idx < sharedStrings.ChildElements.Count)
        {
            var sst = sharedStrings.ChildElements[idx] as SharedStringItem;
            return sst?.InnerText ?? string.Empty;
        }
        return value;
    }

    private static string EscapeCell(string cell)
    {
        if (string.IsNullOrEmpty(cell)) return string.Empty;
        var escaped = cell.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ").Trim();
        if (escaped.Length > 60) escaped = escaped[..60] + "…";
        return escaped;
    }

    private static string? ConvertPptx(string filePath, int maxChars, ILogger? logger)
    {
        var sb = new StringBuilder();
        using var doc = PresentationDocument.Open(filePath, false);
        var slideParts = doc.PresentationPart?.SlideParts?.ToList();
        if (slideParts == null || slideParts.Count == 0) return null;

        for (int i = 0; i < slideParts.Count; i++)
        {
            if (sb.Length >= maxChars) break;
            var slide = slideParts[i].Slide;
            sb.AppendLine($"### Slide {i + 1}");
            sb.AppendLine();

            bool anyText = false;
            foreach (var shape in slide!.CommonSlideData?.ShapeTree?.Elements<DocumentFormat.OpenXml.Presentation.Shape>() ?? Enumerable.Empty<DocumentFormat.OpenXml.Presentation.Shape>())
            {
                if (sb.Length >= maxChars) break;
                var text = shape.TextBody?.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text.TrimEnd());
                    sb.AppendLine();
                    anyText = true;
                }
            }
            if (!anyText)
            {
                sb.AppendLine("_(keine Text-Inhalte)_");
                sb.AppendLine();
            }
        }
        return Truncate(sb.ToString(), maxChars);
    }

    private static string ConvertPdf(string filePath, int maxChars, ILogger? logger)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(filePath);
        foreach (var page in pdf.GetPages())
        {
            if (sb.Length >= maxChars) break;
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }
        return Truncate(sb.ToString(), maxChars);
    }

    private static string ConvertHtml(string filePath, int maxChars, ILogger? logger)
    {
        var html = File.ReadAllText(filePath);
        var converter = new ReverseMarkdown.Converter();
        var md = converter.Convert(html);
        return Truncate(md, maxChars);
    }

    private static string? UnknownExtension(string filePath, string ext, ILogger? logger)
    {
        logger?.Warning("DocumentConverter: no converter for extension {Ext} ({FilePath})", ext, filePath);
        return null;
    }

    private static string Truncate(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Length <= maxChars) return s.TrimEnd();
        return s[..maxChars].TrimEnd() + "\n… (truncated)";
    }
}