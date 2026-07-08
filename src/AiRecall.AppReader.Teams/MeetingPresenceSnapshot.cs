using System;

namespace AiRecall.AppReader.Teams;

/// <summary>
/// Snapshot der aktuellen Teams-Meeting-Anwesenheit (Spec 0013 v0.3 §1).
/// Wird vom <c>MeetingPresencePoller</c> in <c>AiRecall.Trigger</c> zyklisch
/// abgefragt (Polling-API). <see cref="IsActive"/> ist die einzige
/// "harte" Aussage; Topic/WindowTitle/ChatIdShort sind nur befuellt,
/// wenn <see cref="IsActive"/> true ist.
/// </summary>
/// <param name="IsActive">true, wenn ein Teams-Meeting-Fenster erkannt wurde.</param>
/// <param name="Topic">Anzeige-Name des Meetings (best-effort, kann null sein).</param>
/// <param name="WindowTitle">Vollstaendiger MainWindowTitle des Teams-Prozesses.</param>
/// <param name="ChatIdShort">Kurz-ID (8 Zeichen) des Meeting-Chats fuer Datei-Naming.</param>
public sealed record MeetingPresenceSnapshot(
    bool IsActive,
    string? Topic,
    string? WindowTitle,
    string? ChatIdShort);

/// <summary>
/// EventArgs fuer <c>MeetingPresencePoller.PresenceChanged</c>.
/// Wird nur an Edge-Transitions (inactive&lt;-&gt;active) gefeuert,
/// nicht bei jedem Poll-Tick. <see cref="DetectedAt"/> ist UTC.
/// </summary>
/// <param name="IsActive">Neuer Anwesenheits-State.</param>
/// <param name="Topic">Anzeige-Name des Meetings (null bei inactive).</param>
/// <param name="WindowTitle">Vollstaendiger MainWindowTitle (null bei inactive).</param>
/// <param name="ChatIdShort">Kurz-ID des Meeting-Chats (null bei inactive).</param>
/// <param name="DetectedAt">UTC-Zeitpunkt, an dem der Edge erkannt wurde.</param>
public sealed record MeetingPresenceStateChangedEventArgs(
    bool IsActive,
    string? Topic,
    string? WindowTitle,
    string? ChatIdShort,
    DateTimeOffset DetectedAt)
{
    /// <summary>
    /// Convenience-Konstruktor fuer Tests/Debug: nimmt lokale Zeit, konvertiert nach UTC.
    /// </summary>
    public static MeetingPresenceStateChangedEventArgs FromLocal(
        bool isActive,
        string? topic,
        string? windowTitle,
        string? chatIdShort,
        DateTime localDetectedAt)
        => new(isActive, topic, windowTitle, chatIdShort,
               new DateTimeOffset(DateTime.SpecifyKind(localDetectedAt, DateTimeKind.Local)).ToUniversalTime());
}
