namespace AiRecall.ScreenCapture.Trigger;

/// <summary>
/// Generischer Throttle: pro Key (Prozessname, HWND, …) wird der Zeitpunkt
/// des letzten Captures gespeichert; ein neues Capture ist erst nach
/// Ablauf des Intervalls erlaubt.
///
/// Spec 0002 TR-4 (pro-App) und Spec 0005 §Pipeline-Schritte 2+3
/// (per-HWND + per-App). Generisch, damit derselbe Code für
/// <c>Throttle&lt;string&gt;</c> (Prozessname) und
/// <c>Throttle&lt;IntPtr&gt;</c> (HWND) genutzt werden kann.
/// </summary>
public sealed class Throttle<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, DateTimeOffset> _lastCapture = new();
    private readonly TimeSpan _window;

    public Throttle(TimeSpan window)
    {
        if (window < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    /// <summary>
    /// True, wenn der Key jetzt capturen darf (Intervall abgelaufen oder
    /// noch nie capturiert).
    /// </summary>
    public bool Allows(TKey key, DateTimeOffset now)
    {
        if (!_lastCapture.TryGetValue(key, out var last)) return true;
        return now - last >= _window;
    }

    /// <summary>Markiert „jetzt gerade capturiert" für den Key.</summary>
    public void Mark(TKey key, DateTimeOffset now)
    {
        _lastCapture[key] = now;
    }

    /// <summary>Setzt den State zurück (für Tests).</summary>
    public void Reset() => _lastCapture.Clear();

    /// <summary>Anzahl der getrackten Keys (für Diagnostik/Tests).</summary>
    public int TrackedKeyCount => _lastCapture.Count;
}