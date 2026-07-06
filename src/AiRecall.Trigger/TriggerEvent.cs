namespace AiRecall.Trigger;

/// <summary>
/// Welche Art von Ereignis den Trigger ausgelöst hat (Spec 0005 §Trigger-Quellen).
///
/// Die Werte korrespondieren mit den gleichnamigen Win32-Event-Konstanten, die
/// in <c>WinEventHookDetector</c> auf TriggerKind gemappt werden.
/// </summary>
public enum TriggerKind
{
    /// <summary><c>EVENT_SYSTEM_FOREGROUND</c> — Fensterwechsel (Haupttrigger).</summary>
    Foreground,
    /// <summary><c>EVENT_OBJECT_FOCUS</c> — Fokus innerhalb des Fensters.</summary>
    Focus,
    /// <summary><c>EVENT_OBJECT_NAMECHANGE</c> — Titel/URL geändert.</summary>
    NameChange,
    /// <summary><c>EVENT_OBJECT_VALUECHANGE</c> — Inhalt geändert.</summary>
    ValueChange,
    /// <summary><c>EVENT_OBJECT_SCROLL</c> — Scroll-Bewegung.</summary>
    Scroll,
    /// <summary><c>EVENT_SYSTEM_MENUPOPUPSTART</c> — Menü/Kontextmenü geöffnet.</summary>
    MenuPopup,
    /// <summary>Heartbeat-Polling-Fallback (nicht aus WinEventHook).</summary>
    Heartbeat,
    /// <summary>
    /// Periodisches Polling (Spec 0002 §Periodic Capture, Bug-Bash 2026-07-06 I-23).
    /// Erfasst das aktuelle Foreground-Window in festen Intervallen
    /// (z. B. 3–10s), auch wenn keine WinEventHook-Events feuern — nuetzlich
    /// fuer Video-Streams, Slideshows oder andere Inhalte, die sich
    /// visuell aendern, ohne dass der Window-Titel oder die Hwnd wechselt.
    /// </summary>
    Periodic
}

/// <summary>
/// Ein einzelnes Trigger-Event, das ggf. zu einem Capture führt (Spec 0005).
///
/// <c>Hwnd</c> ist das HWND aus dem Win32-Event. Es ist <b>nicht</b> zwingend
/// ein Top-Level-Fenster — kann auch ein Child-Element sein. Die Pipeline
/// normalisiert via <c>GetAncestor(hwnd, GA_ROOT)</c>, bevor Throttle/Dedup
/// angewendet werden.
/// </summary>
public sealed record TriggerEvent(
    IntPtr Hwnd,
    TriggerKind Kind,
    DateTimeOffset Timestamp
);