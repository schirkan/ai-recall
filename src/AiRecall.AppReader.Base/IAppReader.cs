using AiRecall.Core.Models;

namespace AiRecall.AppReader.Base;

/// <summary>
/// Ein App-Reader liest strukturierten Inhalt aus einem aktiven Fenster
/// (URL + Body im Browser, Mail-Header in Outlook, Dokument-Text in Word,
/// Buffer in Notepad, …). Pro Target-App eine eigene DLL, geladen via
/// Reflection in <see cref="AiRecall.Core.AppReaders.AppReaderRegistry"/>.
/// </summary>
public interface IAppReader
{
    /// <summary>Process-Namen (case-insensitive), die dieser Reader versteht (z. B. "chrome", "msedge").</summary>
    IReadOnlyCollection<string> SupportedProcesses { get; }

    /// <summary>Anzeigename, z. B. "Browser (UIA)" oder "Notepad (Win32)" — für Logs und YAML-Frontmatter.</summary>
    string DisplayName { get; }

    /// <summary>Erkennt dieser Reader das Fenster? (Process-Match + optional Title-Heuristik)</summary>
    bool CanRead(WindowInfo window);

    /// <summary>
    /// Liest den strukturierten Inhalt. Liefert <c>null</c> bei Fehler oder
    /// Nicht-Verfügbarkeit. Sollte Exceptions intern abfangen und in den
    /// Logger schreiben, nicht weiterwerfen.
    /// </summary>
    AppReaderResult? Read(WindowInfo window, AppReaderContext context);

    /// <summary>Soll dieser Reader auch außerhalb eines aktiven Captures laufen? (z. B. Mail-Polling)</summary>
    bool SupportsBackgroundPolling => false;

    /// <summary>Polling-Callback (nur wenn <see cref="SupportsBackgroundPolling"/>).</summary>
    void OnPoll(AppReaderContext context) { /* default no-op */ }
}