using AiRecall.Core.Models;

namespace AiRecall.AppReader.Base;

/// <summary>
/// Optionale Basisklasse für <see cref="IAppReader"/>-Implementierungen.
/// Stellt <see cref="MatchesProcess"/> als Helper bereit.
/// </summary>
public abstract class AppReaderBase : IAppReader
{
    public abstract IReadOnlyCollection<string> SupportedProcesses { get; }
    public abstract string DisplayName { get; }

    public virtual bool CanRead(WindowInfo window) =>
        MatchesProcess(window.ProcessName, SupportedProcesses);

    public abstract AppReaderResult? Read(WindowInfo window, AppReaderContext context);

    public virtual bool SupportsBackgroundPolling => false;

    public virtual void OnPoll(AppReaderContext context) { /* default no-op */ }

    /// <summary>Case-insensitive Vergleich gegen eine Liste von Prozessnamen.</summary>
    protected static bool MatchesProcess(string? processName, IReadOnlyCollection<string> supported)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (var p in supported)
        {
            if (string.Equals(processName, p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}