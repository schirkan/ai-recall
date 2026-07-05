using System.Diagnostics;
using System.Text;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.AppReader.Outlook;

/// <summary>
/// Outlook-App-Reader (Process <c>OUTLOOK</c>).
///
/// <para>
/// Dual-Modus-Reader (Spec 0004 Iter. 3):
/// </para>
/// <list type="bullet">
///   <item><b>Aktiv</b> (<see cref="Read"/>): Inspector-Fenster oder Explorer-Selektion
///         via <see cref="OutlookComInterop"/> (late binding).</item>
///   <item><b>Background-Poll</b> (<see cref="OnPoll"/>): iteriert konfigurierte Folders
///         (Default: Inbox, Sent Items), dedupliziert ueber
///         <see cref="OutlookEntryStore"/> (EntryID-basiert), filtert Auto-Regel-Mails
///         ueber <see cref="OutlookAutoRuleDetector"/>, persistiert neue Mails als
///         eigene MDs unter <c>capture/yyyy-MM-dd/outlook-mail/</c>.</item>
/// </list>
///
/// <para>
/// Throttle: <see cref="OnPoll"/> sweep't nur, wenn <c>OutlookConfig.PollIntervalSeconds</c>
/// seit dem letzten Sweep vergangen sind. Andernfalls No-Op. Damit kann der Worker
/// <see cref="IAppReader.OnPoll"/> beliebig oft aufrufen, ohne dass das Polling
/// Amok laeuft.
/// </para>
///
/// <para>
/// Capture-Root: <c>%APPDATA%/AiRecall/capture</c> (siehe <see cref="DefaultCaptureRoot"/>).
/// Konfigurierbarkeit ueber <see cref="OutlookAppReader(OutlookEntryStore, ILogger, string)"/>
/// (internal Test-Konstruktor).
/// </para>
///
/// <para>
/// Persistenz-Schema (Spec 0004 §"Outlook-Spezial: Mail-Log"):
/// <c>capture/yyyy-MM-dd/outlook-mail/HHmmss-{direction}-{entryIdShort}.md</c>
/// </para>
/// </summary>
public sealed class OutlookAppReader : AppReaderBase
{
    /// <summary>Outlook-Prozessname (case-insensitive fuer <see cref="MatchesProcess"/>).</summary>
    public const string OutlookProcessName = "OUTLOOK";

    /// <summary>Subfolder unterhalb des Capture-Root, in den Mails geschrieben werden.</summary>
    public const string MailSubdirectory = "outlook-mail";

    /// <summary>Default-Capture-Root (gleicher Anchor wie <see cref="OutlookConfig.DefaultSeenStatePath"/>).</summary>
    public static string DefaultCaptureRoot() => System.IO.Path.Combine(
        ConfigLoader.AppDataSubdirectory,
        "capture");

    private readonly object _gate = new();
    private OutlookEntryStore? _store;
    private ILogger? _logger;
    private string _captureRoot = string.Empty;
    private bool _captureRootInitialized;
    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;

    /// <summary>Parameterloser Konstruktor (fuer <see cref="System.Activator.CreateInstance(Type)"/> im Plugin-Loader).</summary>
    public OutlookAppReader() { }

    /// <summary>
    /// Test-Konstruktor (internal, sichtbar fuer <c>AiRecall.Core.Tests</c>):
    /// injiziert Store + Logger + CaptureRoot, sodass OnPoll/WriteMailCapture
    /// ohne installiertes Outlook und ohne %APPDATA% testbar sind.
    /// </summary>
    internal OutlookAppReader(OutlookEntryStore store, ILogger logger, string captureRoot)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _captureRoot = captureRoot ?? throw new ArgumentNullException(nameof(captureRoot));
        _captureRootInitialized = true;
    }

    public override IReadOnlyCollection<string> SupportedProcesses { get; } = new[] { OutlookProcessName };
    public override string DisplayName => "Outlook (COM + Mail-Log)";
    public override bool SupportsBackgroundPolling => true;

    // ============================================================
    // Read: aktives Fenster (Inspector / Explorer Selection)
    // ============================================================

    public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
    {
        var cfg = context.Config.AppReader.Outlook;
        try
        {
            // 1) Aktiver Inspector-Modus (Mail gerade offen)
            var inspectorMail = OutlookComInterop.TryGetActiveInspectorMail();
            if (inspectorMail != null)
            {
                return BuildInspectorResult(inspectorMail, cfg);
            }

            // 2) Explorer-Selektion (Mails in der Liste markiert)
            var selection = OutlookComInterop.TryGetExplorerSelection(cfg.MaxItemsPerSweep);
            if (selection.Count > 0)
            {
                return BuildExplorerSelectionResult(selection, cfg);
            }

            // 3) Fallback: Title-Parsing
            var title = OutlookTitleParser.Parse(window.Title);
            return BuildTitleFallbackResult(window, title);
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "Outlook reader failed for HWND 0x{Hwnd:X}", window.Handle.ToInt64());
            return null;
        }
    }

    // ============================================================
    // OnPoll: Background-Sweep (EntryID-Dedup + Auto-Rule-Filter)
    // ============================================================

    public override void OnPoll(AppReaderContext context)
    {
        var cfg = context.Config.AppReader.Outlook;

        // Throttle (internes Lock, nie blockierend)
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastPollAt != DateTimeOffset.MinValue &&
                (now - _lastPollAt).TotalSeconds < cfg.PollIntervalSeconds)
            {
                return;
            }
            _lastPollAt = now;
        }

        // Lazy Init Store
        if (_store == null)
        {
            try
            {
                _store = OutlookEntryStore.CreateDefault(context.Logger);
            }
            catch (Exception ex)
            {
                context.Logger.Warning(ex, "OutlookAppReader: OutlookEntryStore.CreateDefault failed");
                return;
            }
        }

        _logger = context.Logger;
        EnsureCaptureRoot(context);

        int totalProcessed = 0;
        int totalSuspect = 0;
        int totalDuplicates = 0;

        try
        {
            foreach (var folder in cfg.Folders)
            {
                if (context.CancellationToken.IsCancellationRequested) break;

                IReadOnlyList<MailSnapshotFromCom> mails;
                try
                {
                    mails = OutlookComInterop.TryGetRecentMails(folder, cfg.MaxItemsPerSweep);
                }
                catch (Exception ex)
                {
                    context.Logger.Warning(ex, "OutlookAppReader: TryGetRecentMails failed for {Folder}", folder);
                    continue;
                }

                foreach (var mail in mails)
                {
                    if (context.CancellationToken.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(mail.EntryId)) continue;

                    if (_store.IsSeen(mail.EntryId))
                    {
                        totalDuplicates++;
                        continue;
                    }

                    var verdict = ProcessMail(mail, folder, cfg, context);
                    if (verdict == MailVerdict.Persisted) totalProcessed++;
                    else if (verdict == MailVerdict.SkippedSuspect) totalSuspect++;
                }
            }

            _store.MarkSweepCompleted(DateTimeOffset.UtcNow);

            if (totalProcessed > 0 || totalSuspect > 0 || totalDuplicates > 0)
            {
                context.Logger.Information(
                    "OutlookAppReader poll: {Processed} persisted, {Suspect} skipped, {Dups} duplicates from {Folders}",
                    totalProcessed, totalSuspect, totalDuplicates, string.Join(", ", cfg.Folders));
            }
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "OutlookAppReader: poll iteration failed");
        }
    }

    private enum MailVerdict { Persisted, SkippedSuspect, Failed }

    private MailVerdict ProcessMail(MailSnapshotFromCom mail, string folder, OutlookConfig cfg, AppReaderContext context)
    {
        if (_store == null || _logger == null) return MailVerdict.Failed;

        // 1) AutoRule-Detection (nur wenn aktiviert)
        var snapshot = new MailSnapshot(
            Subject: mail.Subject,
            From: mail.From,
            FolderName: mail.FolderName ?? folder,
            UnRead: mail.UnRead,
            ReceivedTime: mail.ReceivedTime,
            LastModificationTime: mail.LastModificationTime,
            Body: mail.Body);

        if (cfg.IgnoreAutoRuleMails && OutlookAutoRuleDetector.IsSuspect(snapshot))
        {
            _logger.Information(
                "OutlookAppReader: skipping suspect {EntryIdShort} from {Folder} ({Reason})",
                ShortId(mail.EntryId), folder, OutlookAutoRuleDetector.Explain(snapshot));
            _store.MarkSeen(mail.EntryId);
            return MailVerdict.SkippedSuspect;
        }

        // 2) Body-Konvertierung
        string bodyMd = !string.IsNullOrEmpty(mail.HtmlBody)
            ? OutlookBodyToMarkdown.FromHtml(mail.HtmlBody, cfg)
            : OutlookBodyToMarkdown.FromPlain(mail.Body);
        bodyMd = OutlookBodyToMarkdown.Truncate(bodyMd, cfg.BodyTruncateKB);

        // 3) Persist
        try
        {
            EnsureCaptureRoot(context);
            WriteMailCapture(mail, bodyMd, folder, cfg, context);
        }
        catch (Exception ex)
        {
            context.Logger.Warning(ex, "OutlookAppReader: failed to persist {EntryIdShort}", ShortId(mail.EntryId));
            return MailVerdict.Failed;
        }

        // 4) Mark as seen (auch bei Failed nicht — damit Retry moeglich)
        _store.MarkSeen(mail.EntryId);
        return MailVerdict.Persisted;
    }

    private void EnsureCaptureRoot(AppReaderContext context)
    {
        if (_captureRootInitialized && !string.IsNullOrEmpty(_captureRoot)) return;

        lock (_gate)
        {
            if (_captureRootInitialized && !string.IsNullOrEmpty(_captureRoot)) return;
            // Default: %APPDATA%/AiRecall/capture
            var fullPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ConfigLoader.AppDataSubdirectory,
                "capture");
            _captureRoot = fullPath;
            _captureRootInitialized = true;
        }
    }

    private void WriteMailCapture(
        MailSnapshotFromCom mail,
        string bodyMd,
        string folder,
        OutlookConfig cfg,
        AppReaderContext context)
    {
        if (string.IsNullOrEmpty(_captureRoot))
        {
            EnsureCaptureRoot(context);
            if (string.IsNullOrEmpty(_captureRoot))
            {
                throw new InvalidOperationException("OutlookAppReader: captureRoot not initialized");
            }
        }

        var direction = InferDirection(folder);
        var now = DateTimeOffset.Now;
        var dayDir = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(_captureRoot),
            now.ToString("yyyy-MM-dd"),
            MailSubdirectory);
        Directory.CreateDirectory(dayDir);

        var stamp = now.ToString("HHmmss");
        var fileName = $"{stamp}-{direction}-{ShortId(mail.EntryId)}.md";
        var fullPath = System.IO.Path.Combine(dayDir, fileName);

        var version = typeof(OutlookAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var md = new StringBuilder();
        md.AppendLine("---");
        md.AppendLine($"timestamp: {now:O}");
        md.AppendLine($"kind: \"mail\"");
        md.AppendLine($"direction: \"{direction}\"");
        md.AppendLine($"entryId: \"{EscapeYaml(mail.EntryId)}\"");
        md.AppendLine($"subject: \"{EscapeYaml(mail.Subject)}\"");
        md.AppendLine($"from: \"{EscapeYaml(mail.From)}\"");
        md.AppendLine($"folder: \"{EscapeYaml(folder)}\"");
        md.AppendLine($"date: {mail.ReceivedTime:O}");
        md.AppendLine($"lastModificationTime: {mail.LastModificationTime:O}");
        md.AppendLine($"unread: {(mail.UnRead ? "true" : "false")}");
        md.AppendLine($"hasAttachments: false");
        md.AppendLine($"autoRuleSuspect: false");
        md.AppendLine($"source: \"outlook-com\"");
        md.AppendLine($"reader: \"AiRecall.AppReader.Outlook\"");
        md.AppendLine($"readerVersion: \"{version}\"");
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine($"# {mail.Subject}");
        md.AppendLine();
        md.AppendLine($"**From:** {mail.From}  ");
        md.AppendLine($"**Received:** {mail.ReceivedTime:yyyy-MM-dd HH:mm:ss zzz}  ");
        md.AppendLine($"**Folder:** {folder}  ");
        md.AppendLine($"**Direction:** {direction}  ");
        md.AppendLine($"**Source:** Outlook COM ({(string.IsNullOrEmpty(mail.HtmlBody) ? "Plain" : "HTML")})  ");
        md.AppendLine();
        md.AppendLine("## Body");
        md.AppendLine();
        md.AppendLine(string.IsNullOrWhiteSpace(bodyMd) ? "_(empty)_" : bodyMd);

        File.WriteAllText(fullPath, md.ToString(), new UTF8Encoding(false));

        if (_logger != null)
        {
            _logger.Information("OutlookAppReader: persisted {File} ({EntryIdShort}, {Folder})",
                Path.GetFileName(fullPath), ShortId(mail.EntryId), folder);
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static string InferDirection(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return "—";
        var lower = folder.ToLowerInvariant();
        if (lower == "inbox" || lower.Contains("inbox")) return "in";
        if (lower.Contains("sent") || lower.Contains("outbox")) return "out";
        return "—";
    }

    /// <summary>Kompakte EntryID-Vorschau (8 Zeichen, ohne Bindestriche) fuer Dateinamen + Logs.</summary>
    public static string ShortId(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return "0";
        var clean = entryId.Replace("-", "").Replace(" ", "");
        return clean.Length >= 8 ? clean[..8] : clean;
    }

    private static string EscapeYaml(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", " ")
            .Replace("\r", " ");
    }

    // ============================================================
    // Result-Builders
    // ============================================================

    private AppReaderResult BuildInspectorResult(MailSnapshotFromCom mail, OutlookConfig cfg)
    {
        var bodyMd = !string.IsNullOrEmpty(mail.HtmlBody)
            ? OutlookBodyToMarkdown.FromHtml(mail.HtmlBody, cfg)
            : OutlookBodyToMarkdown.FromPlain(mail.Body);
        bodyMd = OutlookBodyToMarkdown.Truncate(bodyMd, cfg.BodyTruncateKB);

        var sb = new StringBuilder();
        sb.AppendLine($"# {mail.Subject}");
        sb.AppendLine();
        sb.AppendLine($"**From:** {mail.From}  ");
        sb.AppendLine($"**Folder:** {mail.FolderName ?? "(unknown)"}  ");
        sb.AppendLine($"**Received:** {mail.ReceivedTime:yyyy-MM-dd HH:mm:ss zzz}  ");
        sb.AppendLine($"**Unread:** {(mail.UnRead ? "yes" : "no")}  ");
        sb.AppendLine($"**Source:** Outlook COM (Inspector)  ");
        sb.AppendLine();
        sb.AppendLine("## Body");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(bodyMd) ? "_(empty)_" : bodyMd);

        var extra = new Dictionary<string, string>
        {
            ["entryId"] = mail.EntryId,
            ["subject"] = mail.Subject,
            ["from"] = mail.From,
            ["folder"] = mail.FolderName ?? "(unknown)",
            ["receivedTime"] = mail.ReceivedTime.ToString("O"),
            ["unread"] = mail.UnRead ? "true" : "false",
            ["source"] = "com-inspector",
        };

        return new AppReaderResult(
            ContentMarkdown: sb.ToString(),
            ContextLabel: string.IsNullOrWhiteSpace(mail.Subject) ? mail.From : mail.Subject,
            ContextKind: "mail",
            ReaderName: DisplayName,
            ReaderVersion: typeof(OutlookAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Extra: extra);
    }

    private AppReaderResult BuildExplorerSelectionResult(IReadOnlyList<MailSnapshotFromCom> mails, OutlookConfig cfg)
    {
        var first = mails[0];
        var sb = new StringBuilder();
        sb.AppendLine($"# Selection ({mails.Count} mail(s))");
        sb.AppendLine();
        sb.AppendLine($"**First selected:** {first.Subject}  ");
        sb.AppendLine($"**From:** {first.From}  ");
        sb.AppendLine();
        sb.AppendLine($"## All selected ({mails.Count})");
        sb.AppendLine();

        foreach (var m in mails)
        {
            var dir = InferDirection(m.FolderName ?? string.Empty);
            var singleBody = !string.IsNullOrEmpty(m.HtmlBody)
                ? OutlookBodyToMarkdown.FromHtml(m.HtmlBody, cfg)
                : OutlookBodyToMarkdown.FromPlain(m.Body);
            singleBody = OutlookBodyToMarkdown.Truncate(singleBody, cfg.BodyTruncateKB);

            sb.AppendLine($"### {m.Subject}");
            sb.AppendLine();
            sb.AppendLine($"- **From:** {m.From}");
            sb.AppendLine($"- **Folder:** {m.FolderName ?? "?"} ({dir})");
            sb.AppendLine($"- **Received:** {m.ReceivedTime:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(singleBody) ? "_(empty)_" : singleBody);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        var extra = new Dictionary<string, string>
        {
            ["entryId"] = first.EntryId,
            ["subject"] = first.Subject,
            ["from"] = first.From,
            ["folder"] = first.FolderName ?? "(unknown)",
            ["receivedTime"] = first.ReceivedTime.ToString("O"),
            ["source"] = "com-explorer-selection",
            ["selectionCount"] = mails.Count.ToString(),
        };

        var label = mails.Count == 1
            ? (string.IsNullOrWhiteSpace(first.Subject) ? first.From : first.Subject)
            : $"{first.Subject} (+{mails.Count - 1})";

        return new AppReaderResult(
            ContentMarkdown: sb.ToString(),
            ContextLabel: label,
            ContextKind: "mail-selection",
            ReaderName: DisplayName,
            ReaderVersion: typeof(OutlookAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Extra: extra);
    }

    private AppReaderResult BuildTitleFallbackResult(WindowInfo window, OutlookTitleInfo title)
    {
        string kindLabel = title.Kind switch
        {
            OutlookTitleKind.FolderView => $"Folder: {title.FolderName}",
            OutlookTitleKind.InspectorSubject => $"Subject: {title.Subject}",
            _ => "Outlook (no COM data)",
        };

        var sb = new StringBuilder();
        sb.AppendLine("# Outlook (title fallback)");
        sb.AppendLine();
        sb.AppendLine(kindLabel);
        if (title.IsReadOnly) sb.AppendLine();
        sb.AppendLine($"**Read-Only:** {(title.IsReadOnly ? "yes" : "no")}  ");
        sb.AppendLine($"**Unsaved-Marker:** {(title.HasUnsavedMarker ? "yes" : "no")}  ");
        sb.AppendLine($"**Unread-Counter:** {(title.HasUnreadCounter ? "yes" : "no")}  ");

        var extra = new Dictionary<string, string>
        {
            ["titleKind"] = title.Kind.ToString(),
            ["folder"] = title.FolderName ?? string.Empty,
            ["subject"] = title.Subject,
            ["isReadOnly"] = title.IsReadOnly ? "true" : "false",
            ["hasUnsavedMarker"] = title.HasUnsavedMarker ? "true" : "false",
            ["hasUnreadCounter"] = title.HasUnreadCounter ? "true" : "false",
            ["source"] = "title-fallback",
        };

        return new AppReaderResult(
            ContentMarkdown: sb.ToString(),
            ContextLabel: title.Subject,
            ContextKind: "mail-fallback",
            ReaderName: DisplayName + " (title)",
            ReaderVersion: typeof(OutlookAppReader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Extra: extra);
    }

    /// <summary>
    /// Statischer Helper: laeuft gerade ein Outlook-Prozess? Nuetzlich fuer
    /// UI-Hinweise (TrayApp) und Diagnostics — der Reader selbst prueft das
    /// nicht (COM liefert null bei Bedarf).
    /// </summary>
    public static bool IsOutlookProcessRunning() =>
        Process.GetProcessesByName(OutlookProcessName).Length > 0;
}


