using System.Threading.Channels;

namespace AiRecall.Trigger;

/// <summary>
/// Periodisches Heartbeat-Polling (Spec 0005 §Trigger-Quellen, sekundaer).
///
/// Duenner Wrapper um <see cref="PollThread"/> fuer Backwards-Compat
/// (Bug-Bash 2026-07-06 I-24). Heartbeat laeuft alle ~30s (Default) als
/// Fallback fuer verlorene WinEventHook-Events (Sleep/Resume, schneller
/// Session-Wechsel, hohe Systemlast).
///
/// <see cref="IntervalSeconds"/> = 0 deaktiviert den Heartbeat komplett
/// (kein Thread wird gestartet).
/// </summary>
public sealed class HeartbeatThread : IDisposable
{
    private readonly PollThread _inner;

    public int IntervalSeconds => _inner.IntervalMs / 1000;

    public HeartbeatThread(
        int intervalSeconds,
        ChannelWriter<TriggerEvent> writer,
        Action<string>? logWarn = null,
        Action<string>? logError = null)
    {
        // IntervalSeconds -> IntervalMs; Math.Max(0, ...) sitzt im PollThread.
        _inner = new PollThread(
            intervalMs: intervalSeconds * 1000,
            writer: writer,
            triggerKind: TriggerKind.Heartbeat,
            threadName: "AiRecall.Heartbeat",
            logPrefix: "Heartbeat",
            logWarn: logWarn,
            logError: logError);
    }

    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
    public void Dispose() => _inner.Dispose();
}
