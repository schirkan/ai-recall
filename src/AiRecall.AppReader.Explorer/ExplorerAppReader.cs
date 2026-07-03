using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Explorer;

/// <summary>
/// Liest den aktuell angezeigten Pfad eines Windows-Explorer-Fensters.
///
/// Strategie (Spec 0004):
///   1. Fenster-Titel parsen („C:\\Users\\Martin\\Downloads — Datei-Explorer"
///      → „C:\\Users\\Martin\\Downloads"). Funktioniert für klassische und
///      Tabbed-Explorer (Win11) gleichermassen.
///   2. Special-Folder-Titel („Dieser PC", „Desktop", „Bibliotheken") werden
///      als „kein Pfad" behandelt und führen zu <c>null</c>.
///   3. Tieferes Shell-COM (IShellBrowser → current folder) wäre robuster,
///      kommt in einer späteren Iteration. Für MVP1 reicht Titel-Parsing
///      für 90% der Fälle.
/// </summary>
public sealed class ExplorerAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "explorer" };
    public override string DisplayName => "Explorer (Title-Parsing)";

    // Suffixe, die Explorer je nach Windows-Version/Sprache anhängt.
    // Enthält sowohl normales ASCII-Hyphen (U+002D), En-Dash (U+2013)
    // als auch Em-Dash (U+2014), da der Titel je nach Build variiert.
    private static readonly string[] Suffixes =
    {
        " - Datei-Explorer",         // Windows 10/11 DE
        " – Datei-Explorer",         // En-Dash
        " — Datei-Explorer",         // Em-Dash
        " - File Explorer",          // Windows 10/11 EN
        " – File Explorer",
        " — File Explorer",
        " - Explorer",               // Fallback (ältere Builds)
        " – Explorer",
        " — Explorer"
    };

    // Special-Folder-Titel ohne konkreten Pfad → null zurückgeben.
    private static readonly string[] SpecialFolders =
    {
        "Dieser PC",
        "This PC",
        "Desktop",
        "Schnellzugriff",
        "Quick Access",
        "Bibliotheken",
        "Libraries",
        "Netzwerk",
        "Network",
        "Home",
        "Startseite",
        "Heimnetzgruppe"
    };

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var path = ParsePath(window.Title);
            if (path is null) return null;

            var md = new StringBuilder();
            md.AppendLine($"**Path:** `{path}`");
            md.AppendLine();
            md.AppendLine($"**Raw title:** {window.Title}");

            return new AppReaderResult(
                ContentMarkdown: md.ToString(),
                ContextLabel: path,
                ContextKind: "path",
                ReaderName: DisplayName,
                ReaderVersion: typeof(ExplorerAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: new Dictionary<string, string>
                {
                    ["path"] = path,
                    ["title"] = window.Title ?? ""
                });
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Explorer reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>
    /// Parst einen Explorer-Fenster-Titel und gibt den Pfad zurück, oder
    /// <c>null</c> wenn kein Pfad erkennbar ist (Special Folder, leerer Titel).
    /// Internal für Unit-Tests.
    /// </summary>
    internal static string? ParsePath(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Suffix abtrennen.
        string? trimmed = null;
        foreach (var suffix in Suffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = title[..^suffix.Length].Trim();
                break;
            }
        }

        // Kein bekannter Suffix — kein normaler Explorer-Tab.
        if (trimmed is null) return null;

        // Special-Folder → kein Pfad.
        foreach (var sf in SpecialFolders)
        {
            if (string.Equals(trimmed, sf, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        // Wenn es wie ein Pfad aussieht (C:\, D:\, \\server), zurückgeben.
        if (LooksLikePath(trimmed)) return trimmed;

        // Sonst: relativer Pfad oder benannter Ordner (Bibliotheken\Bilder,
        // Dokumente, …). Wir geben ihn unverändert zurück — der User kann
        // in der Ignore-Liste oder im MD den genauen Pfad sehen.
        return trimmed;
    }

    internal static bool LooksLikePath(string s)
    {
        // C:\ oder C:/ oder \\server\share
        if (s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
            return true;
        // UNC
        if (s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal))
            return true;
        return false;
    }
}