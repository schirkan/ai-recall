using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// Word-App-Reader (Process <c>WINWORD</c>).
///
/// Strategie (Spec 0004 Iter. Documents):
///   1. Fenster-Titel parsen — Filename + Untitled-Marker.
///   2. Optional: UIA-Text-Extraktion fuer den Dokumentinhalt (best effort).
///   3. Bei UIA-Fehler oder Deaktivierung: Title-only-Markdown.
///
/// Real-Office-Smoke-Tests entfallen in der Sandbox (Martin 2026-07-04).
/// </summary>
public sealed class WordAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "WINWORD" };
    public override string DisplayName => "Word (Title + UIA)";

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var (fileName, isUntitled, isReadOnly, isSafeMode) = ParseTitle(window.Title);
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
            if (isSafeMode) md.AppendLine("**Mode:** Safe Mode");

            if (!string.IsNullOrEmpty(bodyText))
            {
                md.AppendLine();
                md.AppendLine("```");
                md.AppendLine(Truncate(bodyText, maxChars));
                md.AppendLine("```");
            }

            var label = isUntitled ? "(untitled)" : fileName;
            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
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
                    ["hasUiaText"] = (!string.IsNullOrEmpty(bodyText)).ToString()
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Word reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>
    /// Parst einen Word-Fenster-Titel.
    /// Internal fuer Unit-Tests.
    /// Reihenfolge: erst App-Suffix strippen, dann Flags ([Read-Only], (Safe Mode)),
    /// dann Unsaved-Marker. So ist die Reihenfolge der Flags im Titel egal.
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

        // Flags koennen in beliebiger Reihenfolge vor dem App-Suffix stehen
        // (z. B. "Doc.docx [Read-Only] (Safe Mode) - Word"), deshalb Schleife.
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