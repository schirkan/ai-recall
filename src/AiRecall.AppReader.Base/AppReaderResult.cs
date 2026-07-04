namespace AiRecall.AppReader.Base;

/// <summary>
/// Ergebnis eines App-Reader-Aufrufs. Wird in <c>*.content.md</c> unter dem
/// Capture-Verzeichnis persistiert (siehe Spec 0004 §"Persistenz").
/// </summary>
/// <param name="ContentMarkdown">Strukturierter Inhalt als Markdown. Bei
///   <see cref="IsThinReader"/>=<c>true</c> nur ein Platzhalter (z. B.
///   "_(siehe .content.md)_"), weil die echte Inhalts-Erzeugung
///   asynchron im <c>ConversionWorker</c> passiert.</param>
/// <param name="ContextLabel">Anzeige-Label (URL, Mail-Subject, Dokument-Pfad, …).</param>
/// <param name="ContextKind">Kategorie: "url", "mail", "document", "buffer", "path", …</param>
/// <param name="ReaderName">Anzeigename des Readers (z. B. "Browser (UIA)").</param>
/// <param name="ReaderVersion">Version der DLL (Assembly-Version).</param>
/// <param name="Extra">Optionale Zusatz-Metadaten (z. B. EntryID bei Mails).
///   Bei <see cref="IsThinReader"/>=<c>true</c> enthaelt es die strukturierten
///   Felder, die der <c>ConversionWorker</c> braucht: <c>filePath</c>,
///   <c>fileName</c>, <c>uiaContent</c>, <c>source</c>.</param>
/// <param name="IsThinReader">Wenn <c>true</c>, liefert der Reader nur
///   strukturierte Metadaten (Title + FilePath + ggf. UIA) und ueberlaesst
///   die Inhalts-Aufbereitung dem async <c>ConversionWorker</c> (Spec 0007
///   Schritt 7). Default <c>false</c> → Reader erzeugt eigenen Content-Markdown
///   und <c>WriteContent</c> wird sofort aufgerufen.</param>
public sealed record AppReaderResult(
    string ContentMarkdown,
    string? ContextLabel,
    string? ContextKind,
    string ReaderName,
    string ReaderVersion,
    IReadOnlyDictionary<string, string>? Extra = null,
    bool IsThinReader = false
);