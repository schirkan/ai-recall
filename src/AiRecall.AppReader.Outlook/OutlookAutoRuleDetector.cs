using System.Text.RegularExpressions;

namespace AiRecall.AppReader.Outlook;

/// <summary>
/// Pure-Function-Heuristik fuer „beruehrungslose" Auto-Regel-Mails
/// (Spec 0004 Iter. 3 §„Auto-Regel-Heuristik"). Wird im Mail-Log-Sweep
/// aufgerufen, wenn <c>OutlookConfig.IgnoreAutoRuleMails == true</c>.
///
/// <para>
/// Operiert auf <see cref="MailSnapshot"/> (Plain-Record, KEIN Outlook COM),
/// damit die Heuristik unit-testbar ist ohne installiertes Outlook.
/// </para>
///
/// <para>
/// Eine Mail ist „Auto-Regel-Suspect", wenn <b>mindestens 2</b> der
/// folgenden Bedingungen zutreffen (siehe <see cref="IsSuspect"/>):
/// </para>
/// <list type="number">
///   <item><c>UnRead == false</c> UND <c>LastModificationTime - ReceivedTime &lt; 5 s</c></item>
///   <item>Folder matcht <c>^(Newsletter|Notifications|Auto|Rule)</c> oder ist
///         <c>Junk E-Mail</c> / <c>Deleted Items</c></item>
///   <item>Sender matcht <c>^(noreply|no-reply|notifications|mailer-daemon)@</c></item>
///   <item>Subject matcht <c>^(WG:|AW:|Fwd:|TR:)</c> UND Body enthaelt
///         <c>Auto-Reply</c> / <c>Automatische Antwort</c> / <c>Out of Office</c></item>
/// </list>
///
/// <para>
/// Die Bedingung „mindestens 2" verhindert False-Positives: eine einzelne
/// Bedingung (z. B. „Mail kommt von noreply@") kann auch legitime
/// Transaktions-Mails betreffen. Erst wenn zwei Indikatoren zusammenkommen,
/// ist es sehr wahrscheinlich eine Regel-Mail.
/// </para>
/// </summary>
public static class OutlookAutoRuleDetector
{
    /// <summary>Zeitfenster (Sekunden) fuer Bedingung 1 (Modification - Received).</summary>
    public const double MarkedReadFastThresholdSeconds = 5.0;

    /// <summary>Folder-Namen, die als „Junk/Trash" gelten (case-insensitive).</summary>
    private static readonly HashSet<string> JunkFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Junk E-Mail",
        "Junk",
        "Spam",
        "Deleted Items",
        "Trash",
    };

    /// <summary>Folder-Prefix-Regex (case-insensitive): Newsletter, Notifications, Auto, Rule.</summary>
    private static readonly Regex FolderPrefixRegex = new(
        @"^(Newsletter|Notifications|Auto|Rule)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Sender-Prefix-Regex: noreply, no-reply, notifications, mailer-daemon.</summary>
    private static readonly Regex NoReplySenderRegex = new(
        @"^(noreply|no-reply|notifications|mailer-daemon)@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Subject-Prefix-Regex: WG:/AW:/Fwd:/TR: (deutsche/englische Reply-Praefixes).</summary>
    private static readonly Regex ReplySubjectRegex = new(
        @"^(WG:|AW:|Fwd:|TR:|RE:|AW\s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Body-Indikator fuer Auto-Reply (mehrere Sprachen).</summary>
    private static readonly Regex AutoReplyBodyRegex = new(
        @"(auto-?reply|automatische antwort|out of office|abwesenheitsnotiz|automatische antwort:|vacation reply)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Hauptmethode: prueft, ob die Mail als Auto-Regel-Suspect eingestuft
    /// werden soll. Liefert true wenn mindestens 2 Bedingungen zutreffen.
    /// </summary>
    public static bool IsSuspect(MailSnapshot mail)
    {
        if (mail is null) return false;

        int hits = 0;
        if (Condition1_MarkedReadImmediately(mail)) hits++;
        if (Condition2_JunkOrAutoFolder(mail)) hits++;
        if (Condition3_NoReplySender(mail)) hits++;
        if (Condition4_AutoReplySubjectAndBody(mail)) hits++;

        return hits >= 2;
    }

    /// <summary>
    /// Bedingung 1: Mail wurde nie geoeffnet, aber als gelesen markiert
    /// (Modification - Received &lt; 5s, UnRead = false).
    /// </summary>
    public static bool Condition1_MarkedReadImmediately(MailSnapshot mail)
    {
        if (mail.UnRead) return false;
        var delta = (mail.LastModificationTime - mail.ReceivedTime).TotalSeconds;
        return delta >= 0 && delta < MarkedReadFastThresholdSeconds;
    }

    /// <summary>
    /// Bedingung 2: Mail ist in einem Junk-/Trash-Folder oder einem
    /// Auto-Folder (Newsletter, Notifications, …).
    /// </summary>
    public static bool Condition2_JunkOrAutoFolder(MailSnapshot mail)
    {
        var folder = mail.FolderName;
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (JunkFolderNames.Contains(folder)) return true;
        return FolderPrefixRegex.IsMatch(folder);
    }

    /// <summary>
    /// Bedingung 3: Sender-Adresse enthaelt einen typischen
    /// NoReply-Prefix (noreply@, no-reply@, notifications@, mailer-daemon@).
    /// </summary>
    public static bool Condition3_NoReplySender(MailSnapshot mail)
    {
        var from = mail.From;
        if (string.IsNullOrWhiteSpace(from)) return false;
        return NoReplySenderRegex.IsMatch(from);
    }

    /// <summary>
    /// Bedingung 4: Subject startet mit Reply-Praefix (WG:/AW:/Fwd:/TR:/RE:)
    /// UND Body enthaelt Auto-Reply-Indikatoren. Beide Sub-Bedingungen
    /// muessen zutreffen.
    /// </summary>
    public static bool Condition4_AutoReplySubjectAndBody(MailSnapshot mail)
    {
        var subject = mail.Subject;
        var body = mail.Body;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body)) return false;

        return ReplySubjectRegex.IsMatch(subject) && AutoReplyBodyRegex.IsMatch(body);
    }

    /// <summary>
    /// Liefert eine menschenlesbare Liste der zutreffenden Bedingungen
    /// (fuer Diagnostics/Logging). Format: "1+3" wenn Bedingung 1 und 3
    /// zutreffen. Leerer String wenn kein Hit.
    /// </summary>
    public static string Explain(MailSnapshot mail)
    {
        if (mail is null) return string.Empty;

        var hits = new List<int>(4);
        if (Condition1_MarkedReadImmediately(mail)) hits.Add(1);
        if (Condition2_JunkOrAutoFolder(mail)) hits.Add(2);
        if (Condition3_NoReplySender(mail)) hits.Add(3);
        if (Condition4_AutoReplySubjectAndBody(mail)) hits.Add(4);

        return string.Join("+", hits);
    }
}

/// <summary>
/// Plain-Record-Snapshot einer Outlook-Mail. Wird von
/// <see cref="OutlookAutoRuleDetector"/> konsumiert, damit die Heuristik
/// ohne Outlook-COM testbar ist. Felder 1:1 von <c>MailItem</c>-Properties
/// (ReceivedTime, LastModificationTime, UnRead, SenderEmailAddress, …).
/// </summary>
public sealed record MailSnapshot(
    string Subject,
    string From,
    string FolderName,
    bool UnRead,
    DateTimeOffset ReceivedTime,
    DateTimeOffset LastModificationTime,
    string Body);