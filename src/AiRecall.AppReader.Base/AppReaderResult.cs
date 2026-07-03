namespace AiRecall.AppReader.Base;

/// <summary>
/// Ergebnis eines App-Reader-Aufrufs. Wird in <c>*.content.md</c> unter dem
/// Capture-Verzeichnis persistiert (siehe Spec 0004 §"Persistenz").
/// </summary>
/// <param name="ContentMarkdown">Strukturierter Inhalt als Markdown.</param>
/// <param name="ContextLabel">Anzeige-Label (URL, Mail-Subject, Dokument-Pfad, …).</param>
/// <param name="ContextKind">Kategorie: "url", "mail", "document", "buffer", "path", …</param>
/// <param name="ReaderName">Anzeigename des Readers (z. B. "Browser (UIA)").</param>
/// <param name="ReaderVersion">Version der DLL (Assembly-Version).</param>
/// <param name="Extra">Optionale Zusatz-Metadaten (z. B. EntryID bei Mails).</param>
public sealed record AppReaderResult(
    string ContentMarkdown,
    string? ContextLabel,
    string? ContextKind,
    string ReaderName,
    string ReaderVersion,
    IReadOnlyDictionary<string, string>? Extra = null
);