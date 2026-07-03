using AiRecall.Core.Models;

namespace AiRecall.ScreenCapture.Trigger;

/// <summary>
/// Welche Art von Ereignis den Trigger ausgelöst hat (Spec 0002 TR-1..3, TR-6).
/// </summary>
public enum CaptureEventKind
{
    /// <summary>Fenster im Fokus hat sich geändert.</summary>
    Activate,
    /// <summary>Fenster-Bounds haben sich geändert (Scroll oder Resize).</summary>
    Scroll,
    /// <summary>Cursor hat sich innerhalb des FG-Windows deutlich bewegt.</summary>
    Click,
    /// <summary>Periodischer Capture-Timer (TR-6).</summary>
    Periodic
}

/// <summary>
/// Ein einzelnes Trigger-Event, das ggf. zu einem Capture führt.
/// </summary>
public sealed record CaptureEvent(
    CaptureEventKind Kind,
    DateTimeOffset Timestamp,
    WindowInfo Window
)
{
    public string ProcessKey => Window.ProcessName.ToLowerInvariant();
}