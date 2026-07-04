using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// PowerPoint-App-Reader (Process <c>POWERPNT</c>).
///
/// Strategie (Spec 0004 Iter. Documents):
///   1. Fenster-Titel parsen — Filename + Read-Only-Marker.
///   2. Optional: UIA-Text-Extraktion (best effort; bei Praesentations-
///      Modus liefert UIA den sichtbaren Slide-Inhalt, im Edit-Modus
///      den Outline-Bereich).
///   3. Bei UIA-Fehler: Title-only-Markdown.
///
/// Hinweis: Folie-Nummer / Notizen lassen sich nur via COM-Interop
/// (PowerPoint.Application.ActivePresentation) zuverlaessig lesen.
/// Das ist nicht Teil dieses Readers.
/// </summary>
public sealed class PowerPointAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "POWERPNT" };
    public override string DisplayName => "PowerPoint (Title + UIA)";

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
                md.AppendLine("_Hinweis: UIA liefert sichtbaren Slide- / Outline-Inhalt. Folien-Nummern, Notizen und Layout nur via COM-Interop._");
            }

            var label = isUntitled ? "(untitled)" : fileName;
            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
                ContextLabel: label,
                ContextKind: "presentation",
                ReaderName: DisplayName,
                ReaderVersion: typeof(PowerPointAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
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
            context.Logger.Warning(ex, "PowerPoint reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>
    /// Parst einen PowerPoint-Fenster-Titel.
    /// Internal fuer Unit-Tests.
    /// Reihenfolge: erst App-Suffix, dann [Read-Only]-Flag, dann Unsaved-Marker.
    /// </summary>
    internal static (string FileName, bool IsUntitled, bool IsReadOnly) ParseTitle(string? title)
    {
        const string suffix = " - PowerPoint";
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

        if (string.Equals(name, "Presentation1", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(name))
            return ("(untitled)", true, isReadOnly);

        return (name, false, isReadOnly);
    }

    private static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s.TrimEnd();
        return s[..maxChars].TrimEnd() + "\n… (truncated)";
    }
}