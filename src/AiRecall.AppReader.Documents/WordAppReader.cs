using AiRecall.AppReader.Base;
using AiRecall.Core.Models;

namespace AiRecall.AppReader.Documents;

/// <summary>
/// Word-App-Reader (Process <c>WINWORD</c>).
///
/// Strategie (Spec 0004 Iter. Documents + Spec 0007 Schritt 7):
///   1. <b>Dünn</b>: liefert nur Title + FilePath + ggf. UIA-Rohcontent
///      (kein eigener Content-Markdown-Body).
///   2. COM-Lookup via <see cref="OfficeComInterop"/> → <c>FullName</c>
///      (echter Pfad). COM-Text-Range wird NICHT mehr gelesen, weil
///      <c>ConversionWorker</c> (OpenXml) den vollen Datei-Inhalt
///      zuverlässiger und asynchron liefert (Spec 0007).
///   3. COM-Fehler (kein Office / falsche Instanz / COM-Wrapper leer):
///      Fallback auf Title-Parsing + UIA-Text. Kein <c>filePath</c>, der
///      <c>ConversionWorker</c> bekommt dann nur OCR + UIA-Section.
///   4. <see cref="AppReaderResult.IsThinReader"/> = <c>true</c>: der
///      <c>TriggerWorker</c> schreibt KEIN <c>*.content.md</c> mehr
///      synchron, sondern reicht <c>filePath</c> + <c>uiaContent</c> im
///      Pending-MD-Frontmatter an den <c>ConversionWorker</c> weiter.
///
/// e2e-Smoke-Tests entfallen in der Sandbox (Office nicht installiert).
/// COM-Pfad läuft nur auf Maschinen mit Office, dort ohne weitere
/// Konfiguration.
/// </summary>
public sealed class WordAppReader : AppReaderBase
{
    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { "WINWORD" };
    public override string DisplayName => "Word (file detection + UIA)";

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        try
        {
            var extra = new Dictionary<string, string>();
            string? filePath = null;
            string source;

            // 1) COM-Pfad (bevorzugt fuer den vollstaendigen Pfad)
            var (expectedFileName, _, _, _) = ParseTitle(window.Title);
            var comInfo = OfficeComInterop.TryGetWordInfo(
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

            // 2) UIA-Rohcontent (immer, auch bei COM-Erfolg — Title-Text)
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

            // 4) ContextLabel + ContentMarkdown
            var (fallbackFileName, isUntitled, isReadOnly, isSafeMode) = ParseTitle(window.Title);
            var label = !string.IsNullOrEmpty(filePath)
                ? System.IO.Path.GetFileName(filePath)
                : (isUntitled ? "(untitled)" : fallbackFileName);

            return new AppReaderResult(
                ContentMarkdown: PLACEHOLDER,
                ContextLabel: label,
                ContextKind: "document",
                ReaderName: DisplayName,
                ReaderVersion: typeof(WordAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: extra,
                IsThinReader: true);
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Word reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    /// <summary>Platzhalter fuer duenne Reader (Spec 0007 Schritt 7).</summary>
    internal const string PLACEHOLDER = "_(siehe .content.md)_";

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

    /// <summary>
    /// Heuristik: ist der aus dem Titel geparste Filename ein echter Dateiname
    /// (kein Placeholder wie "(untitled)" oder leer)?
    /// </summary>
    private static bool IsLikelyARealFilename(string fileName) =>
        !string.IsNullOrEmpty(fileName)
        && fileName != "(untitled)"
        && !fileName.Equals("Document1", StringComparison.OrdinalIgnoreCase);
}
