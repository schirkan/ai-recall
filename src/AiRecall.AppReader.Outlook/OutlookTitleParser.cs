namespace AiRecall.AppReader.Outlook;

/// <summary>
/// Parst Outlook-Fenster-Titel (Fallback-Pfad wenn COM nicht verfuegbar).
///
/// <para>
/// Outlook-Fenster-Titel haben typischerweise diese Formen:
/// </para>
/// <list type="bullet">
///   <item><c>Inbox - alice@example.com - Outlook</c> (Hauptfenster, Folder-Ansicht)</item>
///   <item><c>Sent Items - Outlook</c></item>
///   <item><c>Subject here - Outlook</c> (Inspector, geoeffnete Mail)</item>
///   <item><c>Subject here (Read-Only) - Outlook</c> (Inspector mit Read-Only-Markierung)</item>
///   <item><c>Inbox - alice@example.com (2) - Outlook</c> (Unread-Counter in Klammern)</item>
///   <item><c>*Subject - Outlook</c> (ungespeicherte/ungesendete Mail mit Stern-Marker)</item>
/// </list>
///
/// <para>
/// Ziel: <see cref="OutlookTitleInfo"/> mit <c>Kind</c> (Folder oder
/// Inspector-Subject), <c>FolderName</c>, <c>Subject</c> und Flags.
/// </para>
///
/// <para>
/// Pure Function — keine Outlook-COM-Abhaengigkeit, voll unit-testbar.
/// </para>
/// </summary>
public static class OutlookTitleParser
{
    /// <summary>Konstantes Suffix fuer Outlook-Fenster-Titel.</summary>
    public const string OutlookSuffix = " - Outlook";

    /// <summary>Outlook-Folder-Namen, die als Standard-Folder gelten (case-insensitive).</summary>
    private static readonly HashSet<string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Inbox", "Sent Items", "Drafts", "Outbox", "Deleted Items",
        "Junk E-Mail", "Junk", "Spam", "Notes", "Tasks", "Calendar",
        "Contacts", "Archive", "RSS Feeds", "Conversation History",
    };

    /// <summary>
    /// Parst einen Outlook-Fenster-Titel. Liefert immer ein
    /// <see cref="OutlookTitleInfo"/> (nie null). Bei nicht-erkennbarem
    /// Format wird der ganze Titel als Subject genommen und Kind=Folder
    /// mit FolderName=null geliefert.
    /// </summary>
    public static OutlookTitleInfo Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new OutlookTitleInfo(
                Kind: OutlookTitleKind.Unknown,
                FolderName: null,
                Subject: "(leer)",
                HasUnreadCounter: false,
                IsReadOnly: false,
                HasUnsavedMarker: false);
        }

        var working = title.Trim();

        // Outlook-Suffix abschneiden
        if (working.EndsWith(OutlookSuffix, StringComparison.OrdinalIgnoreCase))
        {
            working = working[..^OutlookSuffix.Length].TrimEnd();
        }

        // Read-Only-Markierung entfernen
        bool isReadOnly = false;
        const string readOnlySuffix = " (Read-Only)";
        if (working.EndsWith(readOnlySuffix, StringComparison.OrdinalIgnoreCase))
        {
            isReadOnly = true;
            working = working[..^readOnlySuffix.Length].TrimEnd();
        }

        // Unsaved-Marker (Stern am Anfang)
        bool hasUnsavedMarker = false;
        if (working.StartsWith('*'))
        {
            hasUnsavedMarker = true;
            working = working[1..].TrimStart();
        }

        // Erste Aufteilung am " - " Separator
        // Form: "LeftPart - RightPart - ... - Outlook"
        var parts = working.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return new OutlookTitleInfo(
                Kind: OutlookTitleKind.Unknown,
                FolderName: null,
                Subject: working,
                HasUnreadCounter: false,
                IsReadOnly: isReadOnly,
                HasUnsavedMarker: hasUnsavedMarker);
        }

        // Variante A: "Folder - Account - Outlook" (Hauptfenster)
        // Heuristik: erstes Part ist bekannter Folder-Name → Folder-Ansicht
        // Akzeptiert auch 1-Part-Titel wie "Sent Items - Outlook" (nur Folder-Name)
        if (parts.Length >= 1 && KnownFolders.Contains(parts[0]))
        {
            // Letztes Part koennte Unread-Counter haben: "alice@example.com (2)"
            var lastPart = parts[^1];
            var (cleanLast, hasCounter) = StripUnreadCounter(lastPart);

            // Subject: Account-Name wenn >1 Parts, sonst Folder-Name selbst
            var subject = parts.Length >= 2 ? cleanLast : parts[0];

            return new OutlookTitleInfo(
                Kind: OutlookTitleKind.FolderView,
                FolderName: parts[0],
                Subject: subject,
                HasUnreadCounter: hasCounter,
                IsReadOnly: isReadOnly,
                HasUnsavedMarker: hasUnsavedMarker);
        }

        // Variante B: "Subject - Outlook" (Inspector, offene Mail)
        // Subject kann "-" enthalten, aber NICHT am Ende "- Outlook" (das ist schon entfernt)
        // → nimm den ganzen Rest als Subject
        return new OutlookTitleInfo(
            Kind: OutlookTitleKind.InspectorSubject,
            FolderName: null,
            Subject: working,
            HasUnreadCounter: false,
            IsReadOnly: isReadOnly,
            HasUnsavedMarker: hasUnsavedMarker);
    }

    /// <summary>
    /// Extrahiert einen Unread-Counter aus einem String wie
    /// "alice@example.com (2)" → ("alice@example.com", true).
    /// Plain-Strings ohne Counter werden unveraendert zurueckgegeben.
    /// </summary>
    private static (string Cleaned, bool HasCounter) StripUnreadCounter(string input)
    {
        var trimmed = input.TrimEnd();
        if (!trimmed.EndsWith(')')) return (input, false);

        var openParen = trimmed.LastIndexOf('(');
        if (openParen < 0) return (input, false);

        var between = trimmed[(openParen + 1)..^1].Trim();
        if (between.Length == 0) return (input, false);

        // Counter muss rein numerisch sein
        if (!long.TryParse(between, out _)) return (input, false);

        var cleaned = trimmed[..openParen].TrimEnd();
        return (cleaned, true);
    }
}

/// <summary>Art des Outlook-Fensters (siehe <see cref="OutlookTitleParser"/>).</summary>
public enum OutlookTitleKind
{
    /// <summary>Format unbekannt / Titel leer.</summary>
    Unknown,
    /// <summary>Hauptfenster mit Folder-Ansicht (z. B. "Inbox - alice@… - Outlook").</summary>
    FolderView,
    /// <summary>Inspector mit offener Mail (Subject sichtbar).</summary>
    InspectorSubject,
}

/// <summary>Ergebnis von <see cref="OutlookTitleParser.Parse"/>.</summary>
/// <param name="Kind">Welche Titel-Variante erkannt wurde.</param>
/// <param name="FolderName">Erkannter Folder-Name (nur bei <see cref="OutlookTitleKind.FolderView"/>).</param>
/// <param name="Subject">Subject (bei Inspector) oder Account-Name (bei FolderView) oder Fallback-Text.</param>
/// <param name="HasUnreadCounter">True wenn "(N)" Unread-Counter aus dem Titel extrahiert wurde.</param>
/// <param name="IsReadOnly">True wenn Read-Only-Markierung im Titel war.</param>
/// <param name="HasUnsavedMarker">True wenn "*"-Prefix (ungespeicherte Mail) im Titel war.</param>
public sealed record OutlookTitleInfo(
    OutlookTitleKind Kind,
    string? FolderName,
    string Subject,
    bool HasUnreadCounter,
    bool IsReadOnly,
    bool HasUnsavedMarker);