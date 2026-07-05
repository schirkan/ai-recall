using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace AiRecall.AppReader.Teams;

/// <summary>
/// UIA-Implementation fuer Teams App-Reader (Spec 0011).
/// Laedt Plain-Text-Chat-Content aus dem sichtbaren Modern-Teams-Window via
/// <see cref="AutomationElement"/> + <see cref="TextPattern"/>.
///
/// <para>Diese Klasse ist die UIA-Komponente der zweistufigen Strategie:
/// <list type="bullet">
///   <item>UIA (dies hier) — Standard, immer verfuegbar, Plain-Text</item>
///   <item>CDP (<see cref="TeamsCdpReader"/>) — opt-in, wenn Teams mit --remote-debugging-port gestartet</item>
/// </list>
/// </para>
///
/// <para>Bei Fehlern (Teams nicht installiert, kein Chat offen, UIA nicht verfuegbar)
/// wird <c>null</c> zurueckgegeben — Caller fallen auf Title-Fallback zurueck.
/// <b>Niemals crashen</b>.</para>
/// </summary>
internal static class TeamsUiaReader
{
    /// <summary>Modern-Teams-Prozessname (case-insensitive fuer Process.GetProcessesByName).</summary>
    private const string TeamsProcessName = "ms-teams";

    /// <summary>
    /// Liefert <c>true</c>, wenn der uebergebene HWND zu einem Modern-Teams-Prozess gehoert.
    /// </summary>
    public static bool IsTeamsChatWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            // ProcessId via HWND aufloesen (System.Windows.Forms)
            uint pid;
            _ = GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return false;

            var proc = Process.GetProcessById((int)pid);
            return string.Equals(proc.ProcessName, TeamsProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parst Modern-Teams-Window-Title.
    /// Format:
    /// <list type="bullet">
    ///   <item><c>"Chat | Alice - Microsoft Teams"</c> (1:1)</item>
    ///   <item><c>"Channel | #general - Microsoft Teams"</c> (Channel — Title beginnt mit #)</item>
    ///   <item><c>"Group Chat | Project Alpha - Microsoft Teams"</c> (Group)</item>
    ///   <item><c>"Meeting | Daily Standup - Microsoft Teams"</c> (Meeting)</item>
    /// </list>
    /// </summary>
    public static TeamsTitleInfo ParseWindowTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
            return new TeamsTitleInfo("(unknown)", TeamsChatKind.Unknown, false);

        // Strip " - Microsoft Teams" suffix
        const string suffix = " - Microsoft Teams";
        var stripped = title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? title[..^suffix.Length]
            : title;

        // Split on " | " (Teams-Konvention)
        string namePart;
        string chatName;
        TeamsChatKind kind;

        var sepIdx = stripped.IndexOf(" | ", StringComparison.Ordinal);
        if (sepIdx > 0)
        {
            namePart = stripped[..sepIdx].Trim();
            chatName = stripped[(sepIdx + 3)..].Trim();
        }
        else
        {
            namePart = stripped.Trim();
            chatName = stripped.Trim();
        }

        kind = namePart.ToLowerInvariant() switch
        {
            "chat"        => TeamsChatKind.OneOnOne,
            "channel"     => TeamsChatKind.Channel,
            "group chat"  => TeamsChatKind.Group,
            "meeting"     => TeamsChatKind.Meeting,
            _             => TeamsChatKind.Unknown,
        };

        return new TeamsTitleInfo($"{namePart} | {chatName}", kind, kind == TeamsChatKind.Meeting);
    }

    /// <summary>
    /// Versucht, den sichtbaren Chat-Content via UIA zu lesen.
    /// Liefert <c>null</c> bei Nicht-Verfuegbarkeit.
    /// </summary>
    /// <remarks>
    /// <para>Strategie:
    /// <list type="number">
    ///   <item>AutomationElement.FromHandle(window.Handle) — Window-Wurzel</item>
    ///   <item>Walk durch Descendants mit <see cref="ControlType.Document"/> oder
    ///         <see cref="ControlType.Text"/> als Chat-Panel-Kandidaten</item>
    ///   <item>TextPattern.DocumentRange.GetText() extrahieren</item>
    ///   <item>Heuristische Message-Separation (Zeilen mit Timestamp-Prefix oder
    ///         Sender-Namen mit Doppelpunkt)</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static TeamsContent? TryGetActiveChat(IntPtr hwnd, int maxContentKB = 256)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            // Suche nach Text/Document-Control im Window-Tree
            var candidates = root.FindAll(TreeScope.Descendants,
                new AndCondition(
                    new Condition[] {
                        new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)),
                    }));

            var sb = new StringBuilder();
            var senderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (AutomationElement el in candidates)
            {
                if (!el.TryGetCurrentPattern(TextPattern.Pattern, out object? rawPattern))
                    continue;

                var pattern = (TextPattern)rawPattern;
                var text = pattern.DocumentRange.GetText(-1);
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Sehr kurzer Text (Header, Buttons) ignorieren
                if (text.Length < 3) continue;

                sb.AppendLine(text.TrimEnd());
                sb.AppendLine();

                // Heuristik: Sender-Namen extrahieren (Lines "Name HH:MM" oder "Name:")
                foreach (var line in text.Split('\n'))
                {
                    var maybeSender = ExtractSenderGuess(line);
                    if (!string.IsNullOrEmpty(maybeSender))
                        senderSet.Add(maybeSender);
                }
            }

            if (sb.Length == 0) return null;

            // Truncate wenn zu lang
            if (maxContentKB > 0 && sb.Length > maxContentKB * 1024)
            {
                sb.Length = maxContentKB * 1024;
                sb.AppendLine();
                sb.AppendLine("_(truncated per TeamsConfig.MaxContentKB)_");
            }

            return new TeamsContent(
                BodyMarkdown: sb.ToString().TrimEnd(),
                SenderSet: senderSet,
                Source: "teams-uia");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Heuristik: extrahiert einen wahrscheinlichen Sender-Namen aus einer Textzeile.
    /// Pattern: "Alice 14:23" oder "Alice:" oder "Alice — Hallo" am Zeilenanfang.
    /// </summary>
    private static string? ExtractSenderGuess(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.Length > 80) return null;
        // Kein Sender wenn Zeile mit Kleinbuchstaben startet (typ. Message-Text)
        if (char.IsLower(trimmed[0])) return null;

        // Pattern "Name HH:MM" oder "Name HH:MM:SS"
        var timeMatch = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"^([A-Za-z0-9 ._\-]+?)\s+\d{1,2}:\d{2}(?::\d{2})?$");
        if (timeMatch.Success) return timeMatch.Groups[1].Value.Trim();

        // Pattern "Name:" (am Zeilenanfang)
        var colonMatch = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"^([A-Za-z0-9 ._\-]+?):");
        if (colonMatch.Success && colonMatch.Groups[1].Value.Trim().Length >= 2)
            return colonMatch.Groups[1].Value.Trim();

        // Pattern "Name \u2014 ..." (em-dash separator)
        var emDashMatch = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"^([A-Za-z0-9 ._\-]+?)\s+\u2014\s+");
        if (emDashMatch.Success) return emDashMatch.Groups[1].Value.Trim();

        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

/// <summary>Geparstes Window-Title-Resultat.</summary>
internal sealed record TeamsTitleInfo(string FormattedTitle, TeamsChatKind Kind, bool IsMeeting)
{
    public string ChatTypeLabel => Kind switch
    {
        TeamsChatKind.OneOnOne => "1:1",
        TeamsChatKind.Channel => "channel",
        TeamsChatKind.Group => "group",
        TeamsChatKind.Meeting => "meeting",
        _ => "unknown",
    };
}

internal enum TeamsChatKind
{
    Unknown,
    OneOnOne,
    Channel,
    Group,
    Meeting,
}

/// <summary>Resultat eines UIA- oder CDP-Capture-Versuchs fuer Teams-Chat.</summary>
internal sealed record TeamsContent(
    string BodyMarkdown,
    IReadOnlyCollection<string> SenderSet,
    string Source);
