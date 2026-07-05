namespace AiRecall.AppReader.OneNote;

/// <summary>
/// Top-Level Record fuer die OneNote-Hierarchie-Information einer Page.
/// Wird von <see cref="OneNoteComInterop"/> (COM-Reads) erzeugt und vom
/// <see cref="OneNoteAppReader"/> in den Frontmatter geschrieben (Spec 0010).
///
/// Top-Level statt nested in <c>OneNoteComInterop</c> weil nested records
/// in C# 12 nicht im outer-Namespace sichtbar sind (gleiche Begruendung
/// wie <c>MailSnapshotFromCom</c> in Outlook-App-Reader).
/// </summary>
internal sealed record OneNoteHierarchyInfo(
    string PageId,
    string PageTitle,
    string SectionId,
    string SectionTitle,
    string NotebookId,
    string NotebookTitle,
    DateTime LastModified)
{
    /// <summary>
    /// Page-GUID ohne Bindestriche, erste 8 Zeichen (fuer Dateinamen-Suffix,
    /// analog zu Outlook-Mails, die EntryID-Short benutzen).
    /// </summary>
    public string PageIdShort
    {
        get
        {
            var compact = PageId.Replace("-", string.Empty);
            return compact.Length <= 8 ? compact : compact.Substring(0, 8);
        }
    }

    /// <summary>
    /// <c>true</c>, wenn mindestens die PageId gesetzt ist (Mindest-Info fuer ein Capture).
    /// Title/Section/Notebook koennen leer sein, wenn COM nur eine teilweise Antwort liefert.
    /// </summary>
    public bool HasMinimumInfo => !string.IsNullOrEmpty(PageId);
}
