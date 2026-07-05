namespace AiRecall.AppReader.Outlook;

/// <summary>
/// Snapshot-Daten einer einzelnen Mail, geliefert von
/// <see cref="OutlookComInterop"/> bei aktivem Inspector, Explorer-Selektion
/// oder Folder-Iteration.
///
/// <para>
/// Top-level internal record (nicht nested in OutlookComInterop), damit
/// externe Caller innerhalb der Assembly und Tests via
/// [InternalsVisibleTo] ihn ohne den voll-qualifizierten Klassennamen
/// importieren koennen.
/// </para>
///
/// <para>
/// Felder 1:1 von <c>MailItem</c>-Properties (ReceivedTime,
/// LastModificationTime, UnRead, SenderEmailAddress, Body, HTMLBody).
/// </para>
/// </summary>
internal sealed record MailSnapshotFromCom(
    string EntryId,
    string Subject,
    string From,
    string FolderName,
    bool UnRead,
    DateTimeOffset ReceivedTime,
    DateTimeOffset LastModificationTime,
    string Body,
    string HtmlBody);
