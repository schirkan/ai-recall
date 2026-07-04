namespace AiRecall.Conversion;

/// <summary>
/// OCR-Engine-Interface (Spec 0007 Schritt 4).
///
/// Async-Signature fuer Integration in die Async-Conversion-Pipeline.
/// Implementierungen muessen thread-safe sein (mehrere Captures parallel).
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// Engine-Name (z. B. "tesseract") fuer Frontmatter-Diagnose.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Extrahiert Text aus einem PNG-Byte-Array.
    /// Liefert leeren String wenn kein Text erkannt wurde.
    /// Wirft keine Exceptions (Fehler werden als leerer String + Logger-Warnung gemeldet).
    /// </summary>
    Task<string> ExtractTextAsync(byte[] pngBytes, CancellationToken ct = default);
}