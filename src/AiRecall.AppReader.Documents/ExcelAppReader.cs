using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// Excel-App-Reader (Process <c>EXCEL</c>).
///
/// Strategie (Spec 0004 Iter. Documents + Spec 0007 Schritt 7):
///   1. <b>Dünn</b>: liefert nur Title + FilePath + ggf. UIA-Rohcontent
///      (kein eigener Content-Markdown-Body, keine Tabellen).
///   2. COM-Lookup → <c>FullName</c> (Sheet-Name + Tabellen werden NICHT
///      mehr gelesen, weil <c>ConversionWorker</c> via OpenXml die ganze
///      Tabelle zuverlässig und asynchron extrahiert).
///   3. COM-Fehler: Fallback auf Title-Parsing + UIA.
///   4. <see cref="AppReaderResult.IsThinReader"/> = <c>true</c>.
///
/// Hinweis: Sheet-Name (z. B. "Tabelle1") ist im Fenster-Titel NICHT
/// enthalten — nur via COM <c>ActiveSheet.Name</c> lesbar. Der
/// <c>ConversionWorker</c> kann den Sheet-Namen aus OpenXml
/// rekonstruieren, sofern die Datei lesbar ist.
/// </summary>
public sealed class ExcelAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "EXCEL" };
    public override string DisplayName => "Excel (file detection + UIA)";

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var extra = new Dictionary<string, string>();
            string? filePath = null;
            string source;

            // 1) COM-Pfad
            var (expectedFileName, _, _) = ParseTitle(window.Title);
            var comInfo = OfficeComInterop.TryGetExcelInfo(
                expectedFilename: IsLikelyARealFilename(expectedFileName) ? expectedFileName : null);
            if (comInfo is not null)
            {
                filePath = comInfo.FullPath;
                source = "com";
            }
            else
            {
                source = "title-uia";
            }

            // 2) UIA-Rohcontent
            var maxChars = context.Config.AppReader.Documents.MaxTextKB * 1024;
            var useUia = context.Config.AppReader.Documents.EnableUiaExtraction;
            string? uiaContent = useUia ? UiaTextExtractor.TryExtract(window.Handle, maxChars) : null;

            // 3) Extra aufbauen
            if (!string.IsNullOrEmpty(filePath))
            {
                extra["filePath"] = filePath;
                extra["fileName"] = System.IO.Path.GetFileName(filePath);
            }
            if (!string.IsNullOrEmpty(uiaContent))
            {
                extra["uiaContent"] = uiaContent;
            }
            extra["source"] = source;
            extra["hasContent"] = (!string.IsNullOrEmpty(uiaContent) || !string.IsNullOrEmpty(filePath)).ToString();

            // 4) ContextLabel
            var (fallbackFileName, isUntitled, isReadOnly) = ParseTitle(window.Title);
            var label = !string.IsNullOrEmpty(filePath)
                ? System.IO.Path.GetFileName(filePath)
                : (isUntitled ? "(untitled)" : fallbackFileName);

            return new AppReaderResult(
                ContentMarkdown: PLACEHOLDER,
                ContextLabel: label,
                ContextKind: "spreadsheet",
                ReaderName: DisplayName,
                ReaderVersion: typeof(ExcelAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: extra,
                IsThinReader: true);
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Excel reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>Platzhalter fuer duenne Reader (Spec 0007 Schritt 7).</summary>
    internal const string PLACEHOLDER = "_(siehe .content.md)_";

    /// <summary>
    /// Parst einen Excel-Fenster-Titel (Fallback-Pfad).
    /// Reihenfolge: erst App-Suffix, dann [Read-Only]-Flag, dann Unsaved-Marker.
    /// </summary>
    internal static (string FileName, bool IsUntitled, bool IsReadOnly) ParseTitle(string? title)
    {
        const string suffix = " - Excel";
        const string readOnlyFlag = " [Read-Only]";

        if (string.IsNullOrWhiteSpace(title)) return ("(untitled)", true, false);

        var isReadOnly = false;

        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            title = title[..^suffix.Length];

        if (title.EndsWith(readOnlyFlag, StringComparison.OrdinalIgnoreCase))
        {
            isReadOnly = true;
            title = title[..^readOnlyFlag.Length];
        }

        var name = title.Trim();
        if (name.StartsWith("*")) name = name[1..].Trim();

        if (string.Equals(name, "Book1", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(name))
            return ("(untitled)", true, isReadOnly);

        return (name, false, isReadOnly);
    }

    /// <summary>
    /// Heuristik: ist der aus dem Titel geparste Filename ein echter Dateiname
    /// (kein Placeholder wie "(untitled)" oder leer)?
    /// </summary>
    private static bool IsLikelyARealFilename(string fileName) =>
        !string.IsNullOrEmpty(fileName)
        && fileName != "(untitled)"
        && !fileName.Equals("Book1", StringComparison.OrdinalIgnoreCase);
}
