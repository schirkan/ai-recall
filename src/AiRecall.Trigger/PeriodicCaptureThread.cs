using System.Threading.Channels;

namespace AiRecall.Trigger;

/// <summary>
/// Periodischer Capture-Trigger (Spec 0002 §Periodic Capture, Bug-Bash 2026-07-06 I-23).
///
/// Duenner Wrapper um <see cref="PollThread"/> fuer Backwards-Compat
/// (Bug-Bash 2026-07-06 I-24). Erfasst das Foreground-Window in festen
/// Intervallen (Millisekunden), auch wenn keine WinEventHook-Events feuern —
/// nuetzlich fuer Video-Streams, Slideshows, Live-Daten, etc.
///
/// <see cref="IntervalMs"/> = 0 deaktiviert den Periodic-Capture komplett
/// (kein Thread wird gestartet). Sinnvolle Werte: 3000-10000 ms (3-10 s).
/// </summary>
public sealed class PeriodicCaptureThread : IDisposable
{
    private readonly PollThread _inner;

    public int IntervalMs => _inner.IntervalMs;

    public PeriodicCaptureThread(
        int intervalMs,
        ChannelWriter<TriggerEvent> writer,
        Action<string>? logWarn = null,
        Action<string>? logError = null)
    {
        _inner = new PollThread(
            intervalMs: intervalMs,
            writer: writer,
            triggerKind: TriggerKind.Periodic,
            threadName: "AiRecall.PeriodicCapture",
            logPrefix: "PeriodicCapture",
            logWarn: logWarn,
            logError: logError);
    }

    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
    public void Dispose() => _inner.Dispose();
}
