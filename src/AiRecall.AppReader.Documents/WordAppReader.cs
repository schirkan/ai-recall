using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// Word-App-Reader (Process <c>WINWORD</c>).
///
/// Strategie (Spec 0004 Iter. Documents Schritt 2, Martin 2026-07-04):
///   1. COM-Lookup via <see cref="OfficeComInterop"/> → <c>FullName</c>
///      (echter Pfad) + <c>Range.Text</c> (Plain-Inhalt).
///   2. Bei COM-Erfolg: <c>filePath</c> im Frontmatter + Inhalt unter
///      <c>## Document content (via COM)</c> in <c>content.md</c>.
///   3. COM-Fehler (kein Office installiert, kein ProgID, andere Instanz
///      aktiv): Fallback auf Title-Parsing + optional UIA-Text.
///
/// e2e-Smoke-Tests entfallen in der Sandbox (Martin 2026-07-04, Office
/// nicht installiert). COM-Pfad laeuft nur auf Maschinen mit Office, dort
/// aber ohne weitere Konfiguration.
/// </summary>
public sealed class WordAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "WINWORD" };
    public override string DisplayName => "Word (COM + Title + UIA)";

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            // 1) COM-Pfad (bevorzugt)
            var comInfo = OfficeComInterop.TryGetWordInfo();
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
                    md.AppendLine("```");
                    md.AppendLine(Truncate(comInfo.Text, maxChars));
                    md.AppendLine("```");
                }
                else if (!string.IsNullOrEmpty(comInfo.Error))
                {
                    md.AppendLine();
                    md.AppendLine($"_(COM-Text-Lese-Fehler: {comInfo.Error})_");
                }

                return new AppReaderResult(
                    ContentMarkdown: md.ToString(),
                    ContextLabel: System.IO.Path.GetFileName(comInfo.FullPath),
                    ContextKind: "document",
                    ReaderName: DisplayName,
                    ReaderVersion: typeof(WordAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
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
            var (fileName, isUntitled, isReadOnly, isSafeMode) = ParseTitle(window.Title);
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
            if (isSafeMode) md2.AppendLine("**Mode:** Safe Mode");

            if (!string.IsNullOrEmpty(bodyText))
            {
                md2.AppendLine();
                md2.AppendLine("```");
                md2.AppendLine(Truncate(bodyText, fallbackMaxChars));
                md2.AppendLine("```");
            }

            var label = isUntitled ? "(untitled)" : fileName;
            return new AppReaderResult(
                ContentMarkdown: md2.ToString(),
                ContextLabel: label,
                ContextKind: "document",
                ReaderName: DisplayName,
                ReaderVersion: typeof(WordAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["fileName"] = fileName,
                    ["isUntitled"] = isUntitled.ToString(),
                    ["isReadOnly"] = isReadOnly.ToString(),
                    ["isSafeMode"] = isSafeMode.ToString(),
                    ["source"] = "title-uia",
                    ["hasContent"] = (!string.IsNullOrEmpty(bodyText)).ToString()
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Word reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>
    /// Parst einen Word-Fenster-Titel (Fallback-Pfad).
    /// Reihenfolge: erst App-Suffix, dann Flags in beliebiger Reihenfolge
    /// ([Read-Only], (Safe Mode)), dann Unsaved-Marker, dann Untitled.
    /// </summary>
    internal static (string FileName, bool IsUntitled, bool IsReadOnly, bool IsSafeMode) ParseTitle(string? title)
    {
        const string suffix = " - Word";
        const string readOnlyFlag = " [Read-Only]";
        const string safeFlag = " (Safe Mode)";

        if (string.IsNullOrWhiteSpace(title)) return ("(untitled)", true, false, false);

        var isReadOnly = false;
        var isSafeMode = false;

        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            title = title[..^suffix.Length];

        bool changed;
        do
        {
            changed = false;
            if (title.EndsWith(readOnlyFlag, StringComparison.OrdinalIgnoreCase))
            {
                isReadOnly = true;
                title = title[..^readOnlyFlag.Length];
                changed = true;
            }
            else if (title.EndsWith(safeFlag, StringComparison.OrdinalIgnoreCase))
            {
                isSafeMode = true;
                title = title[..^safeFlag.Length];
                changed = true;
            }
        } while (changed);

        var name = title.Trim();
        if (name.StartsWith("*")) name = name[1..].Trim();

        if (string.Equals(name, "Document1", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(name))
            return ("(untitled)", true, isReadOnly, isSafeMode);

        return (name, false, isReadOnly, isSafeMode);
    }

    private static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s.TrimEnd();
        return s[..maxChars].TrimEnd() + "\n… (truncated)";
    }
}