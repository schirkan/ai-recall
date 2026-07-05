using System.Diagnostics;
using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.AppReader.OneNote;

/// <summary>
/// OneNote-App-Reader (Process <c>OneNote</c>) — Read only (Spec 0010).
///
/// <para>
/// Im Gegensatz zum Outlook-Reader (Dual-Modus mit OnPoll + Folder-Iteration)
/// ist OneNote <b>Page-orientiert</b>: der User arbeitet mit EINER sichtbaren
/// Page. Es gibt keinen konstanten Daten-Stream, daher kein Background-Poll.
/// Capture wird ausschliesslich ueber den Trigger (Foreground/Heartbeat)
/// ausgeloest.
/// </para>
///
/// <para>
/// Pipeline (siehe Spec 0010 \u00a7OneNoteAppReader):
/// </para>
/// <list type="number">
///   <item><see cref="OneNoteComInterop.IsOneNoteRunning"/> \u2014 Pre-Filter</item>
///   <item><see cref="OneNoteComInterop.TryGetActivePage"/> \u2014 4-stufige Strategie</item>
///   <item><see cref="OneNoteComInterop.TryGetPageContentXml"/> \u2014 XML-Content</item>
///   <item><see cref="OneNotePageXmlToMarkdown.ConvertBody"/> \u2014 XML\u2192MD</item>
///   <item>Truncate auf <c>cfg.MaxContentKB</c> \u2014 Cap gegen riesige Pages</item>
///   <item>Header mit Hierarchy (Notebook/Section/LastModified) + MD-Body</item>
/// </list>
///
/// <para>
/// Persistenz-Schema (Spec 0010): <c>capture/yyyy-MM-dd/onenote/HHmmss-{pageIdShort}.md</c>.
/// </para>
/// </summary>
public sealed class OneNoteAppReader : AppReaderBase
{
    /// <summary>OneNote-Prozessname (case-insensitive fuer <see cref="AppReaderBase.MatchesProcess"/>).</summary>
    public const string OneNoteProcessName = "OneNote";

    /// <summary>Subfolder unterhalb des Capture-Root, in den Pages geschrieben werden.</summary>
    public const string PageSubdirectory = "onenote";

    private ILogger? _logger;
    private string _captureRoot = string.Empty;

    /// <summary>Parameterloser Konstruktor (fuer <see cref="System.Activator.CreateInstance(Type)"/> im Plugin-Loader).</summary>
    public OneNoteAppReader() { }

    /// <summary>
    /// Test-Konstruktor (internal, sichtbar fuer <c>AiRecall.Core.Tests</c>):
    /// injiziert Logger + CaptureRoot, sodass Tests ohne installiertes OneNote
    /// und ohne <c>%APPDATA%</c> laufen koennen.
    /// </summary>
    internal OneNoteAppReader(ILogger logger, string captureRoot)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _captureRoot = captureRoot ?? throw new ArgumentNullException(nameof(captureRoot));
    }

    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { OneNoteProcessName };
    public override string DisplayName => "OneNote (COM + Active Page)";
    public override bool SupportsBackgroundPolling => false;

    // ============================================================================
    // Read: aktive Page (4-stage COM-Strategie)
    // ============================================================================

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        var cfg = context.Config.AppReader.OneNote;
        _logger = context.Logger;

        try
        {
            // 1) Process-Check: OneNote ueberhaupt laufend? Wenn nicht, gar nicht erst COM versuchen.
            if (!OneNoteComInterop.IsOneNoteRunning())
            {
                return null;
            }

            // 2) 4-stufige Active-Page-Strategie (Windows API / HierarchyXML / Auto)
            var info = OneNoteComInterop.TryGetActivePage(cfg.ActivePageStrategy);
            if (info == null || !info.HasMinimumInfo)
            {
                // Keine aktive Page gefunden \u2014 Caller faellt auf OCR/UIA zurueck.
                return null;
            }

            // 3) Skip-Notebook-Patterns: wenn Notebook-Titel matcht, kein Capture
            if (!string.IsNullOrEmpty(info.NotebookTitle))
            {
                foreach (var pattern in cfg.SkipNotebookPatterns)
                {
                    if (string.IsNullOrEmpty(pattern)) continue;
                    if (info.NotebookTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Logger.Information(
                            "OneNoteAppReader: skipping {PageId} (notebook '{Notebook}' matches skip-pattern '{Pattern}')",
                            info.PageIdShort, info.NotebookTitle, pattern);
                        return null;
                    }
                }
            }

            // 4) Page-Content-XML holen (xs2013-Schema)
            var xml = OneNoteComInterop.TryGetPageContentXml(info.PageId);
            if (string.IsNullOrEmpty(xml))
            {
                // Page-ID gefunden, aber Content-XML nicht abrufbar
                // (z. B. Page wurde gerade geloescht) \u2014 trotzdem Hierarchy liefern
                // mit Hinweis-Body, damit das Capture nicht komplett leer ist.
                return BuildFallbackResult(info, cfg, "(page content unavailable)");
            }

            // 5) XML \u2192 MD
            var bodyMd = OneNotePageXmlToMarkdown.ConvertBody(xml, cfg);

            // 6) Cap auf cfg.MaxContentKB
            bodyMd = TruncateBody(bodyMd, cfg.MaxContentKB);

            // 7) Header-Composition: Hierarchy + MD-Body
            var md = BuildFullMarkdown(info, bodyMd, xml, cfg);

            // 8) Extra-Felder fuer Frontmatter-Supplement
            var extra = new Dictionary<string, string>
            {
                ["pageId"] = info.PageId,
                ["pageIdShort"] = info.PageIdShort,
                ["pageTitle"] = info.PageTitle ?? string.Empty,
                ["section"] = info.SectionTitle ?? string.Empty,
                ["sectionId"] = info.SectionId ?? string.Empty,
                ["notebook"] = info.NotebookTitle ?? string.Empty,
                ["notebookId"] = info.NotebookId ?? string.Empty,
                ["lastModified"] = info.LastModified == DateTime.MinValue
                    ? string.Empty
                    : info.LastModified.ToString("O"),
                ["strategy"] = cfg.ActivePageStrategy,
                ["includeImages"] = cfg.IncludeImages ? "true" : "false",
                ["includeTags"] = cfg.IncludeTags ? "true" : "false",
                ["source"] = "onenote-com",
            };

            return new AppReaderResult(
                ContentMarkdown: md,
                ContextLabel: string.IsNullOrWhiteSpace(info.PageTitle)
                    ? info.PageIdShort
                    : info.PageTitle,
                ContextKind: "onenote-page",
                ReaderName: DisplayName,
                ReaderVersion: typeof(OneNoteAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: extra);
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "OneNoteAppReader: Read failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    // ============================================================================
    // Capture-Root (fuer OnPoll-Tests + Diagnostics; Read delegiert an TriggerWorker)
    // ============================================================================

    /// <summary>
    /// Default-Capture-Root (<c>%APPDATA%/AiRecall/capture</c>, analog zu Outlook).
    /// </summary>
    public static string DefaultCaptureRoot() => System.IO.Path.Combine(
        ConfigLoader.AppDataSubdirectory,
        "capture");

    // ============================================================================
    // Process-Detection (statisch, wie Outlook)
    // ============================================================================

    /// <summary>
    /// Liefert <c>true</c>, wenn ein OneNote-Prozess laeuft. Nuetzlich fuer
    /// UI-Hinweise (TrayApp) und Diagnostics — der Reader selbst prueft das
    /// intern (Schritt 1 in <see cref="Read"/>).
    /// </summary>
    public static bool IsOneNoteProcessRunning() =>
        Process.GetProcessesByName(OneNoteProcessName).Length > 0;

    /// <summary>
    /// PageId-Short, kompakte Vorschau (8 Zeichen ohne Bindestriche), fuer
    /// Dateinamen-Suffix und Log-Lines. Oeffentlich fuer Test-Fixtures.
    /// </summary>
    public static string ShortId(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return "0";
        var clean = pageId.Replace("-", string.Empty).Replace(" ", string.Empty);
        return clean.Length >= 8 ? clean.Substring(0, 8) : clean;
    }

    // ============================================================================
    // Markdown-Composition (Private Helpers)
    // ============================================================================

    /// <summary>
    /// Baut das vollstaendige Markdown mit YAML-Frontmatter, Hierarchy-Header
    /// und Body. Pattern aus OutlookAppReader.WriteMailCapture uebernommen
    /// (Spec 0010 \u00a7Persistenz-Schema).
    /// </summary>
    internal string BuildFullMarkdown(
        OneNoteHierarchyInfo info,
        string bodyMd,
        string xml,
        OneNoteConfig cfg)
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;
        var version = typeof(OneNoteAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var attachments = OneNotePageXmlToMarkdown.ExtractInsertedFileNames(xml);
        var hasHierarchy = ShouldIncludeSection(cfg) || ShouldIncludeNotebook(cfg);

        // YAML-Frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"timestamp: {now:O}");
        sb.AppendLine($"kind: \"onenote-page\"");
        sb.AppendLine($"pageId: \"{EscapeYaml(info.PageId)}\"");
        sb.AppendLine($"pageTitle: \"{EscapeYaml(info.PageTitle)}\"");

        if (ShouldIncludeSection(cfg))
        {
            sb.AppendLine($"section: \"{EscapeYaml(info.SectionTitle)}\"");
            sb.AppendLine($"sectionId: \"{EscapeYaml(info.SectionId)}\"");
        }
        if (ShouldIncludeNotebook(cfg))
        {
            sb.AppendLine($"notebook: \"{EscapeYaml(info.NotebookTitle)}\"");
            sb.AppendLine($"notebookId: \"{EscapeYaml(info.NotebookId)}\"");
        }

        if (info.LastModified != DateTime.MinValue)
        {
            sb.AppendLine($"lastModified: {info.LastModified:O}");
        }

        sb.AppendLine($"strategy: \"{cfg.ActivePageStrategy}\"");
        sb.AppendLine($"includeImages: {(cfg.IncludeImages ? "true" : "false")}");
        sb.AppendLine($"includeTags: {(cfg.IncludeTags ? "true" : "false")}");
        if (attachments.Count > 0)
        {
            sb.AppendLine($"attachments: \"{string.Join(", ", attachments)}\"");
        }
        sb.AppendLine($"source: \"onenote-com\"");
        sb.AppendLine($"reader: \"AiRecall.AppReader.OneNote\"");
        sb.AppendLine($"readerVersion: \"{version}\"");
        sb.AppendLine("---");

        // Hierarchy-Header (siehe Spec 0010 Output-Schema)
        sb.AppendLine();
        sb.AppendLine($"# {info.PageTitle}");
        sb.AppendLine();
        sb.AppendLine("*Source: OneNote (COM + Active Page)*  ");
        sb.AppendLine($"*Strategy: {cfg.ActivePageStrategy}*  ");

        if (hasHierarchy)
        {
            if (!string.IsNullOrEmpty(info.NotebookTitle))
            {
                sb.Append("*Notebook: ").Append(EscapeYaml(info.NotebookTitle));
                if (!string.IsNullOrEmpty(info.NotebookId))
                {
                    sb.Append(" (").Append(EscapeYaml(info.NotebookId)).Append(')');
                }
                sb.AppendLine("*  ");
            }
            if (!string.IsNullOrEmpty(info.SectionTitle))
            {
                sb.Append("*Section: ").Append(EscapeYaml(info.SectionTitle));
                if (!string.IsNullOrEmpty(info.SectionId))
                {
                    sb.Append(" (").Append(EscapeYaml(info.SectionId)).Append(')');
                }
                sb.AppendLine("*  ");
            }
        }

        if (info.LastModified != DateTime.MinValue)
        {
            sb.Append("*Last-Modified: ").Append(info.LastModified.ToString("yyyy-MM-dd HH:mm:ss zzz")).AppendLine("*  ");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(bodyMd) ? "_(empty page)_" : bodyMd);
        return sb.ToString();
    }

    /// <summary>
    /// Fallback wenn Page-Content-XML nicht lesbar (Page wurde gerade geloescht,
    /// RPC-Fehler nach erfolgreicher ID-Ermittlung). Liefert Hierarchy-Info
    /// mit Hinweis-Body, damit das Capture nicht leer ist.
    /// </summary>
    private AppReaderResult BuildFallbackResult(OneNoteHierarchyInfo info, OneNoteConfig cfg, string hint)
    {
        var md = BuildFullMarkdown(info, $"_{hint}_", string.Empty, cfg);
        var extra = new Dictionary<string, string>
        {
            ["pageId"] = info.PageId,
            ["pageIdShort"] = info.PageIdShort,
            ["pageTitle"] = info.PageTitle ?? string.Empty,
            ["section"] = info.SectionTitle ?? string.Empty,
            ["notebook"] = info.NotebookTitle ?? string.Empty,
            ["source"] = "onenote-com-fallback",
            ["strategy"] = cfg.ActivePageStrategy,
        };
        return new AppReaderResult(
            ContentMarkdown: md,
            ContextLabel: string.IsNullOrWhiteSpace(info.PageTitle) ? info.PageIdShort : info.PageTitle,
            ContextKind: "onenote-page-fallback",
            ReaderName: DisplayName,
            ReaderVersion: typeof(OneNoteAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Extra: extra);
    }

    private static string TruncateBody(string bodyMd, int maxContentKB)
    {
        if (maxContentKB <= 0 || string.IsNullOrEmpty(bodyMd)) return bodyMd ?? string.Empty;
        var maxBytes = maxContentKB * 1024;
        if (bodyMd.Length <= maxBytes) return bodyMd;
        return bodyMd.Substring(0, maxBytes) + "\n\n_(truncated per OneNoteConfig.MaxContentKB)_";
    }

    private static bool ShouldIncludeSection(OneNoteConfig cfg) =>
        cfg.HierarchyDepth is "PageAndSection" or "PageAndSectionAndNotebook";

    private static bool ShouldIncludeNotebook(OneNoteConfig cfg) =>
        cfg.HierarchyDepth is "PageAndSectionAndNotebook";

    private static string EscapeYaml(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s!
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", " ")
            .Replace("\r", " ");
    }
}
