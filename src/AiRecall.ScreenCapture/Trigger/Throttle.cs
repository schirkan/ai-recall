namespace AiRecall.ScreenCapture.Trigger;

/// <summary>
/// Pro-Prozess-Throttle (Spec 0002 TR-4). Hält für jeden Prozessnamen den
/// Zeitpunkt des letzten Captures. Erlaubt ein neues Capture erst nach
/// Ablauf des konfigurierten Intervalls.
/// </summary>
public sealed class Throttle
{
    private readonly Dictionary<string, DateTimeOffset> _lastCapture = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;

    public Throttle(TimeSpan window)
    {
        if (window < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    /// <summary>
    /// True, wenn der Prozess jetzt capturen darf (Intervall abgelaufen oder
    /// noch nie capturiert).
    /// </summary>
    public bool Allows(string processKey, DateTimeOffset now)
    {
        if (!_lastCapture.TryGetValue(processKey, out var last)) return true;
        return now - last >= _window;
    }

    /// <summary>Markiert „jetzt gerade capturiert" für den Prozess.</summary>
    public void Mark(string processKey, DateTimeOffset now)
    {
        _lastCapture[processKey] = now;
    }

    /// <summary>Setzt den State zurück (für Tests).</summary>
    public void Reset() => _lastCapture.Clear();
}