namespace AiRecall.Core.Models;

/// <summary>
/// Strukturierter Inhalt aus einem App-Reader (Browser-URL, Mail-Body, Dokument-Text, …).
/// Diese Core-Variante hält CaptureWriter unabhängig von <c>AiRecall.AppReader.Base</c>.
/// Der CLI-Layer konvertiert zwischen <c>AppReaderResult</c> (Plugin-API) und diesem Typ.
/// </summary>
public sealed record AppContentRecord(
    string ContentMarkdown,
    string? ContextLabel,
    string? ContextKind,
    string ReaderName,
    string ReaderVersion,
    IReadOnlyDictionary<string, string>? Extra = null
);