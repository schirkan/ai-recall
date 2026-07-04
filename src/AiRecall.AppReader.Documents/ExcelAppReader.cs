using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// Excel-App-Reader (Process <c>EXCEL</c>).
///
/// Strategie (Spec 0004 Iter. Documents Schritt 2, Martin 2026-07-04):
///   1. COM-Lookup → <c>FullName</c> + <c>UsedRange</c> als Markdown-Tabelle.
///   2. Bei COM-Erfolg: <c>filePath</c> im Frontmatter + Tabelle in
///      <c>content.md</c> unter <c>## Document content (via COM)</c>.
///   3. Fallback: Title-Parsing + UIA-Text (sichtbare Zellen).
///
/// Hinweis: Sheet-Name (z. B. "Tabelle1") ist im Fenster-Titel NICHT
/// enthalten — nur via COM <c>ActiveSheet.Name</c> lesbar. COM-Pfad
/// liefert diesen mit.
/// </summary>
public sealed class ExcelAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "EXCEL" };
    public override string DisplayName => "Excel (COM + Title + UIA)";

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            // 1) COM-Pfad (bevorzugt)
            var comInfo = OfficeComInterop.TryGetExcelInfo();
            if (comInfo is not null)
            {
                var maxChars = context.Config.AppReader.Documents.MaxTextKB * 1024;
                var md = new StringBuilder();
                md.AppendLine($"**File:** `{comInfo.FullPath}`");

                if (!string.IsNullOrEmpty(comInfo.Text))
                {
                    md.AppendLine();
                    md.AppendLine("## Document content (via COM)");
                    md.AppendLine();
                    md.AppendLine(Truncate(comInfo.Text, maxChars));
                }
                else if (!string.IsNullOrEmpty(comInfo.Error))
                {
                    md.AppendLine();
                    md.AppendLine($"_(COM-Text-Lese-Fehler: {comInfo.Error})_");
                }

                return new AppReaderResult(
                    ContentMarkdown: md.ToString(),
                    ContextLabel: System.IO.Path.GetFileName(comInfo.FullPath),
                    ContextKind: "spreadsheet",
                    ReaderName: DisplayName,
                    ReaderVersion: typeof(ExcelAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    Extra: new Dictionary<string, string>
                    {
                        ["filePath"] = comInfo.FullPath,
                        ["fileName"] = System.IO.Path.GetFileName(comInfo.FullPath),
                        ["source"] = "com",
                        ["hasContent"] = (!string.IsNullOrEmpty(comInfo.Text)).ToString(),
                        ["contentLength"] = (comInfo.Text?.Length ?? 0).ToString()
                    });
            }

            // 2) Fallback: Title + UIA
            var (fileName, isUntitled, isReadOnly) = ParseTitle(window.Title);
            var fallbackMaxChars = context.Config.AppReader.Documents.MaxTextKB * 1024;
            var useUia = context.Config.AppReader.Documents.EnableUiaExtraction;
            string? bodyText = useUia ? UiaTextExtractor.TryExtract(window.Handle, fallbackMaxChars) : null;

            var md2 = new StringBuilder();
            if (isUntitled)
            {
                md2.AppendLine("**File:** _(untitled)_");
            }
            else
            {
                md2.AppendLine($"**File (from title):** `{fileName}`");
            }
            if (isReadOnly) md2.AppendLine("**Mode:** Read-Only");

            if (!string.IsNullOrEmpty(bodyText))
            {
                md2.AppendLine();
                md2.AppendLine("```");
                md2.AppendLine(Truncate(bodyText, fallbackMaxChars));
                md2.AppendLine("```");
                md2.AppendLine();
                md2.AppendLine("_Hinweis: UIA liefert nur sichtbare Zellen. Vollstaendiger Inhalt nur via COM-Interop (Office erforderlich)._");
            }

            var label = isUntitled ? "(untitled)" : fileName;
            return new AppReaderResult(
                ContentMarkdown: md2.ToString(),
                ContextLabel: label,
                ContextKind: "spreadsheet",
                ReaderName: DisplayName,
                ReaderVersion: typeof(ExcelAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["fileName"] = fileName,
                    ["isUntitled"] = isUntitled.ToString(),
                    ["isReadOnly"] = isReadOnly.ToString(),
                    ["source"] = "title-uia",
                    ["hasContent"] = (!string.IsNullOrEmpty(bodyText)).ToString()
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Excel reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

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

    private static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s.TrimEnd();
        return s[..maxChars].TrimEnd() + "\n… (truncated)";
    }
}