using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// Excel-App-Reader (Process <c>EXCEL</c>).
///
/// Strategie (Spec 0004 Iter. Documents):
///   1. Fenster-Titel parsen — Filename + evtl. Read-Only-Marker.
///   2. Optional: UIA-Text-Extraktion (best effort; bei grossen Sheets
///      liefert UIA nur die aktuell sichtbaren Zellen, kein vollstaendiger
///      Inhalt — Begrenzung wird im MD dokumentiert).
///   3. Bei UIA-Fehler: Title-only-Markdown.
///
/// Hinweis Sheet-Name: Excel-Fenster-Titel enthaelt KEINEN Sheet-Namen
/// (im Gegensatz zu z. B. "Tabelle1 - Excel"). Sheet-Namen muessten via
/// COM-Interop (IWorkbook.Worksheets) gelesen werden — das wuerde Office
/// voraussetzen und ist daher nicht Teil dieses Readers.
/// </summary>
public sealed class ExcelAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "EXCEL" };
    public override string DisplayName => "Excel (Title + UIA)";

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var (fileName, isUntitled, isReadOnly) = ParseTitle(window.Title);
            var maxChars = context.Config.AppReader.Documents.MaxTextKB * 1024;
            var useUia = context.Config.AppReader.Documents.EnableUiaExtraction;

            string? bodyText = useUia ? UiaTextExtractor.TryExtract(window.Handle, maxChars) : null;

            var md = new StringBuilder();
            if (isUntitled)
            {
                md.AppendLine("**File:** _(untitled)_");
            }
            else
            {
                md.AppendLine($"**File:** `{fileName}`");
            }
            if (isReadOnly) md.AppendLine("**Mode:** Read-Only");

            if (!string.IsNullOrEmpty(bodyText))
            {
                md.AppendLine();
                md.AppendLine("```");
                md.AppendLine(Truncate(bodyText, maxChars));
                md.AppendLine("```");
                md.AppendLine();
                md.AppendLine("_Hinweis: UIA liefert nur sichtbare Zellen. Vollstaendiger Inhalt nur via COM-Interop (Office erforderlich)._");
            }

            var label = isUntitled ? "(untitled)" : fileName;
            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
                ContextLabel: label,
                ContextKind: "spreadsheet",
                ReaderName: DisplayName,
                ReaderVersion: typeof(ExcelAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["fileName"] = fileName,
                    ["isUntitled"] = isUntitled.ToString(),
                    ["isReadOnly"] = isReadOnly.ToString(),
                    ["hasUiaText"] = (!string.IsNullOrEmpty(bodyText)).ToString()
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Excel reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>
    /// Parst einen Excel-Fenster-Titel.
    /// Internal fuer Unit-Tests.
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

    private static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s.TrimEnd();
        return s[..maxChars].TrimEnd() + "\n… (truncated)";
    }
}