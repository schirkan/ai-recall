namespace AiRecall.Trigger;

/// <summary>
/// Öffentliche Schnittstelle der Trigger-Pipeline (Spec 0005 §Architektur).
///
/// Wird sowohl vom CLI als auch von der geplanten MVP2-Tray-Icon-EXE
/// genutzt. Lifecycle: <see cref="Start"/> → <see cref="Stop"/> →
/// <see cref="IDisposable.Dispose"/>.
///
/// Konfiguration erfolgt über <see cref="AiRecall.Core.Configuration.AppConfig"/>
/// (Block <c>trigger.*</c>); der Service kapselt Channel + WinEventHook +
/// Heartbeat + Worker hinter einer einzigen API.
/// </summary>
public interface ITriggerService : IDisposable
{
    /// <summary>True, wenn <see cref="Start"/> aufgerufen wurde und noch nicht <see cref="Stop"/>.</summary>
    bool IsRunning { get; }

    /// <summary>Anzahl erfolgreich persistierter Captures.</summary>
    long CaptureCount { get; }

    /// <summary>Aggregierte Skips (Window weg, Throttle, Dedup-Hit, Blacklist, Self-Capture).</summary>
    long SkippedCount { get; }

    /// <summary>Anzahl Throttle-Skips (per-Hwnd oder per-App).</summary>
    long ThrottleCount { get; }

    /// <summary>Anzahl Dedup-Skips (gleicher Hash für gleiches HWND).</summary>
    long DuplicateCount { get; }

    /// <summary>Anzahl Blacklist-Skips (Window-Class oder Prozess).</summary>
    long BlacklistCount { get; }

    /// <summary>Anzahl Self-Capture-Skips (eigenes Fenster).</summary>
    long SelfCaptureCount { get; }

    /// <summary>Anzahl Pipeline-Fehler.</summary>
    long ErrorCount { get; }

    /// <summary>Startet alle konfigurierten Quellen und den Worker-Thread.</summary>
    void Start();

    /// <summary>Stoppt alle Quellen und den Worker-Thread.</summary>
    void Stop();
}