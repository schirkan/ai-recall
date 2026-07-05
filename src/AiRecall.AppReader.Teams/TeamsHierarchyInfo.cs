using System.Text;

namespace AiRecall.AppReader.Teams;

/// <summary>
/// Snapshot-Daten eines aktiven Teams-Chats.
/// Wird von <see cref="TeamsUiaReader"/> und <see cref="TeamsCdpReader"/>
/// erzeugt und vom <see cref="TeamsAppReader"/> zum BuildFullMarkdown verwendet.
/// </summary>
/// <param name="ChatTitle">Voller Title wie aus dem Window (z. B. "Chat | Alice").</param>
/// <param name="ChatType">Typ des Chats: "1:1", "group" oder "channel".</param>
/// <param name="ChatId">Eindeutige Chat-ID. Bei UIA: deterministische Hash aus Title+Type+SenderSet.</param>
/// <param name="IsMeeting">True wenn es ein Meeting-Chat ist (eingeschraenkter Content).</param>
internal sealed record TeamsHierarchyInfo(
    string ChatTitle,
    string ChatType,
    string ChatId,
    bool IsMeeting)
{
    /// <summary>
    /// Erste 8 Zeichen der Chat-ID ohne Bindestriche (fuer Dateinamen-Suffix),
    /// analog <see cref="AiRecall.AppReader.OneNote.OneNoteHierarchyInfo.PageIdShort"/>.
    /// </summary>
    public string ChatIdShort
    {
        get
        {
            var compact = ChatId.Replace("-", string.Empty);
            return compact.Length <= 8 ? compact : compact.Substring(0, 8);
        }
    }

    /// <summary>Hash-funktion: deterministische Chat-ID aus Title|Type|SenderSet.</summary>
    public static string ComputeChatId(string title, string type, IEnumerable<string> senderSet)
    {
        var sb = new StringBuilder();
        sb.Append(title).Append('|').Append(type).Append('|');
        foreach (var s in senderSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(s).Append(',');
        }
        var input = sb.ToString();
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Format as GUID-like with dashes (36 chars)
        var hex = Convert.ToHexString(hash)[..32].ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}

/// <summary>Eine einzelne Chat-Message (CDP-Pfad).</summary>
internal sealed record TeamsMessage(
    string Sender,
    DateTimeOffset Timestamp,
    string BodyMarkdown,
    bool IsSelfMessage)
{
    /// <summary>Kompakte Vorschau des Sendernamens (16 Zeichen) fuer Header-Zeile.</summary>
    public string SenderShort => Sender.Length <= 16 ? Sender : Sender[..16] + "\u2026";
}
