using System.Diagnostics;
using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.AppReader.Teams;

/// <summary>
/// Microsoft Teams App-Reader (Process <c>ms-teams</c>) — Modern Teams (Spec 0011).
///
/// <para>
/// Im Gegensatz zu Outlook (Dual-Mode mit OnPoll + Folder-Iteration) und
/// OneNote (Page-orientiert, Read only) ist Teams <b>Chat-orientiert</b>:
/// der User hat EIN aktives Chat-Tab. Es gibt keinen konstanter Stream
/// (anders als Outlook-Mails), aber auch keine einzelne Page (anders als
/// OneNote). Capture wird ausschliesslich ueber den Trigger (Foreground-Event)
/// ausgeloest.
/// </para>
///
/// <para>
/// Pipeline (siehe Spec 0011 §Active-Chat-Strategie):
/// </para>
/// <list type="number">
///   <item><see cref="TeamsCdpReader"/> (opt-in, CDP wenn verfuegbar)</item>
///   <item><see cref="TeamsUiaReader"/> (Standard, UIA-TextPattern)</item>
///   <item>Title-Fallback (nur Title + Hinweis-Body)</item>
/// </list>
///
/// <para>
/// Persistenz-Schema (Spec 0011): <c>capture/yyyy-MM-dd/ms-teams/HHmmss-{chatIdShort}.md</c>.
/// </para>
/// </summary>
public sealed class TeamsAppReader : AppReaderBase
{
    /// <summary>Modern-Teams-Prozessname (case-insensitive fuer <see cref="AppReaderBase.MatchesProcess"/>).</summary>
    public const string TeamsProcessName = "ms-teams";

    /// <summary>Subfolder unterhalb des Capture-Root, in den Chat-Captures geschrieben werden.</summary>
    public const string ChatSubdirectory = "ms-teams";

    private ILogger? _logger;

    /// <summary>Parameterloser Konstruktor (fuer <see cref="System.Activator.CreateInstance(Type)"/> im Plugin-Loader).</summary>
    public TeamsAppReader() { }

    /// <summary>
    /// Test-Konstruktor (internal, sichtbar fuer <c>AiRecall.Core.Tests</c>):
    /// injiziert Logger, sodass Tests ohne Serilog-Setup laufen koennen.
    /// </summary>
    internal TeamsAppReader(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { TeamsProcessName };
    public override string DisplayName => "Microsoft Teams (UIA; CDP opt-in)";
    public override bool SupportsBackgroundPolling => false;

    // ============================================================================
    // Read: aktiver Teams-Chat
    // ============================================================================

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        var cfg = context.Config.AppReader.Teams;
        _logger = context.Logger;

        if (!cfg.Enabled)
        {
            return null;
        }

        try
        {
            // 1) Title-Parsing als Common-Path: liefert Chat-Title, Chat-Type
            var titleInfo = TeamsUiaReader.ParseWindowTitle(window.Title);
            if (titleInfo.Kind == TeamsChatKind.Unknown && string.IsNullOrEmpty(titleInfo.FormattedTitle))
            {
                return null;
            }

            // 2) Strategy-Aufloesung
            var strategy = cfg.PreferredStrategy?.ToLowerInvariant() ?? "auto";
            var preferCdp = strategy != "uia" && cfg.UseCdpIfAvailable;
            var cdpOnly = strategy == "cdp";
            var uiaOnly = strategy == "uia";

            TeamsContent? captured = null;

            // 3) CDP-Pfad (async; Read ist sync — wir blockieren kurz)
            if (preferCdp || cdpOnly)
            {
                try
                {
                    captured = TryGetActiveChatCdp(cfg).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    context.Logger.Warning(ex, "TeamsAppReader: CDP-Pfad failed");
                }
            }

            // 4) UIA-Pfad (Standard-Fallback, sync)
            if (captured == null && !cdpOnly)
            {
                if (TeamsUiaReader.IsTeamsChatWindow(window.Handle))
                {
                    captured = TeamsUiaReader.TryGetActiveChat(window.Handle, cfg.MaxContentKB);
                }
            }

            // 5) Title-Fallback (wenn weder CDP noch UIA Content liefern)
            if (captured == null)
            {
                captured = BuildTitleFallbackContent(titleInfo);
            }

            // 6) SkipChatPatterns-Filter
            foreach (var pattern in cfg.SkipChatPatterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;
                if (titleInfo.FormattedTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    context.Logger.Information(
                        "TeamsAppReader: skipping chat (title '{Title}' matches skip-pattern '{Pattern}')",
                        titleInfo.FormattedTitle, pattern);
                    return null;
                }
            }

            // 7) IncludeSenderPatterns-Filter (Whitelist, leer = alle)
            var senderSet = captured.SenderSet;
            var senderList = senderSet.Count == 0
                ? Array.Empty<string>()
                : FilterAndSortSenders(senderSet, cfg.IncludeSenderPatterns).ToArray();

            // 8) Hierarchy + Chat-ID deterministisch aus Title|Type|SenderSet
            var chatId = TeamsHierarchyInfo.ComputeChatId(
                titleInfo.FormattedTitle,
                titleInfo.ChatTypeLabel,
                senderSet);
            var hierarchy = new TeamsHierarchyInfo(
                ChatTitle: titleInfo.FormattedTitle,
                ChatType: titleInfo.ChatTypeLabel,
                ChatId: chatId,
                IsMeeting: titleInfo.IsMeeting);

            // 9) Markdown-Composition
            var md = BuildFullMarkdown(
                hierarchy, captured.BodyMarkdown, captured.Source,
                senderList, cfg);

            // 10) AppReaderResult
            var extra = new Dictionary<string, string>
            {
                ["chatId"] = chatId,
                ["chatIdShort"] = hierarchy.ChatIdShort,
                ["chatTitle"] = titleInfo.FormattedTitle,
                ["chatType"] = titleInfo.ChatTypeLabel,
                ["isMeeting"] = titleInfo.IsMeeting ? "true" : "false",
                ["senderCount"] = senderList.Length.ToString(),
                ["source"] = captured.Source,
                ["strategy"] = cfg.PreferredStrategy,
            };

            return new AppReaderResult(
                ContentMarkdown: md,
                ContextLabel: string.IsNullOrWhiteSpace(titleInfo.FormattedTitle)
                    ? hierarchy.ChatIdShort
                    : titleInfo.FormattedTitle,
                ContextKind: "teams-chat",
                ReaderName: DisplayName,
                ReaderVersion: typeof(TeamsAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Extra: extra);
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "TeamsAppReader: Read failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    // ============================================================================
    // Process-Detection (statisch)
    // ============================================================================

    /// <summary>
    /// Liefert <c>true</c>, wenn ein Modern-Teams-Prozess laeuft. Nuetzlich fuer
    /// UI-Hinweise (TrayApp) und Diagnostics.
    /// </summary>
    public static bool IsTeamsProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName(TeamsProcessName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ChatId-Short (8 Zeichen) aus Eingabe (Chat-Title oder Hierarchy).
    /// Oeffentlich fuer Test-Fixtures und Debugging.
    /// </summary>
    public static string ShortId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "0";
        var clean = input.Replace("-", string.Empty).Replace(" ", string.Empty);
        return clean.Length <= 8 ? clean : clean.Substring(0, 8);
    }

    // ============================================================================
    // Helper-Pfad: CDP-Async-bridge (Read ist sync, CDP ist async)
    // ============================================================================

    private async Task<TeamsContent?> TryGetActiveChatCdp(TeamsConfig cfg)
    {
        var reachable = await TeamsCdpReader.TryFindEndpointAsync(cfg.CdpEndpoint, cfg.CdpTimeoutMs).ConfigureAwait(false);
        if (!reachable) return null;

        return await TeamsCdpReader.TryGetActiveChatAsync(cfg.CdpEndpoint, cfg.CdpTimeoutMs).ConfigureAwait(false);
    }

    // ============================================================================
    // Title-Fallback-Pfad
    // ============================================================================

    private static TeamsContent BuildTitleFallbackContent(TeamsTitleInfo titleInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Teams — {titleInfo.ChatTypeLabel}");
        sb.AppendLine();
        sb.AppendLine("*UIA und CDP-Pfad nicht verfuegbar — nur Title-Fallback.*  ");
        sb.AppendLine();
        sb.AppendLine($"**Chat-Title:** {titleInfo.FormattedTitle}  ");
        sb.AppendLine($"**Chat-Type:** {titleInfo.ChatTypeLabel}  ");
        if (titleInfo.IsMeeting)
        {
            sb.AppendLine("**Hinweis:** Meeting-Chat erkannt, Content via UIA/CDP nicht extrahiert.  ");
        }
        return new TeamsContent(
            BodyMarkdown: sb.ToString(),
            SenderSet: Array.Empty<string>(),
            Source: "teams-title-fallback");
    }

    // ============================================================================
    // Sender-Whitelist-Filter
    // ============================================================================

    private static IEnumerable<string> FilterAndSortSenders(
        IReadOnlyCollection<string> senders,
        IReadOnlyCollection<string> includePatterns)
    {
        if (includePatterns.Count == 0) return senders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        var filtered = new List<string>();
        foreach (var s in senders)
        {
            foreach (var pattern in includePatterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;
                if (s.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(s);
                    break;
                }
            }
        }
        return filtered.OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
    }

    // ============================================================================
    // Markdown-Composition (Full-Pattern mit Frontmatter)
    // ============================================================================

    internal string BuildFullMarkdown(
        TeamsHierarchyInfo hierarchy,
        string bodyMd,
        string source,
        IReadOnlyList<string> senderList,
        TeamsConfig cfg)
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;
        var version = typeof(TeamsAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        // YAML-Frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"timestamp: {now:O}");
        sb.AppendLine($"kind: \"teams-chat\"");
        sb.AppendLine($"chatId: \"{EscapeYaml(hierarchy.ChatId)}\"");
        sb.AppendLine($"chatTitle: \"{EscapeYaml(hierarchy.ChatTitle)}\"");
        sb.AppendLine($"chatType: \"{hierarchy.ChatType}\"");
        sb.AppendLine($"isMeeting: {(hierarchy.IsMeeting ? "true" : "false")}");
        sb.AppendLine($"strategy: \"{cfg.PreferredStrategy}\"");
        sb.AppendLine($"senderCount: {senderList.Count}");
        if (senderList.Count > 0)
        {
            sb.AppendLine($"senders: \"{string.Join(", ", senderList)}\"");
        }
        sb.AppendLine($"source: \"{source}\"");
        sb.AppendLine($"reader: \"AiRecall.AppReader.Teams\"");
        sb.AppendLine($"readerVersion: \"{version}\"");
        sb.AppendLine("---");

        // Header
        sb.AppendLine();
        sb.AppendLine($"# {hierarchy.ChatTitle}");
        sb.AppendLine();
        sb.AppendLine($"*Source: Teams ({source})*  ");
        sb.AppendLine($"*Chat-Type: {hierarchy.ChatType}*  ");
        if (hierarchy.IsMeeting)
        {
            sb.AppendLine("*Hinweis: Meeting-Chat erkannt.*  ");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Body
        if (string.IsNullOrWhiteSpace(bodyMd))
        {
            sb.AppendLine("_(empty chat)_");
        }
        else
        {
            sb.AppendLine(bodyMd);
        }
        return sb.ToString();
    }

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
