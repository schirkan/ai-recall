namespace AiRecall.Core.Tessdata;

/// <summary>
/// Repräsentiert eine fehlende tessdata-Datei für eine konfigurierte OCR-Sprache
/// (Spec 0012, Bug-Bash 2026-07-06 I-14 Folge-Iteration).
/// </summary>
public sealed record MissingLanguage(string Code, string FileName)
{
    /// <summary>Tesseract-Sprachcode (z. B. "eng", "deu").</summary>
    public string Code { get; init; } = Code;

    /// <summary>Erwarteter Filename in <c>tessdata/</c> (z. B. "eng.traineddata").</summary>
    public string FileName { get; init; } = FileName;
}