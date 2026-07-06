using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace AiRecall.Trigger;

/// <summary>
/// Generischer periodischer Foreground-Window-Poll-Thread (Bug-Bash 2026-07-06 I-24).
///
/// Frueher gab es zwei parallel implementierte Klassen — <see cref="HeartbeatThread"/>
/// und <see cref="PeriodicCaptureThread"/> — mit identischer Loop-Logik. Diese
/// Klasse konsolidiert die Implementierung; die oeffentlichen Klassen sind jetzt
/// dünne Wrapper fuer Abwaertskompatibilitaet.
///
/// Verwendungszwecke:
///   - Heartbeat (alle ~30s, Fallback fuer verlorene WinEventHook-Events)
///   - PeriodicCapture (3-10s, erfasst Video/Slideshow ohne Event-Trigger)
///
/// <c>intervalMs = 0</c> deaktiviert den Thread komplett (kein Thread wird gestartet).
/// </summary>
internal sealed class PollThread : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly int _intervalMs;
    private readonly ChannelWriter<TriggerEvent> _writer;
    private readonly TriggerKind _triggerKind;
    private readonly string _threadName;
    private readonly string _logPrefix;
    private readonly Action<string>? _logWarn;
    private readonly Action<string>? _logError;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public int IntervalMs => _intervalMs;
    public TriggerKind Kind => _triggerKind;

    public PollThread(
        int intervalMs,
        ChannelWriter<TriggerEvent> writer,
        TriggerKind triggerKind,
        string threadName,
        string logPrefix,
        Action<string>? logWarn = null,
        Action<string>? logError = null)
    {
        if (string.IsNullOrEmpty(threadName)) throw new ArgumentException("threadName required", nameof(threadName));
        if (string.IsNullOrEmpty(logPrefix)) throw new ArgumentException("logPrefix required", nameof(logPrefix));

        _intervalMs = Math.Max(0, intervalMs);
        _writer = writer;
        _triggerKind = triggerKind;
        _threadName = threadName;
        _logPrefix = logPrefix;
        _logWarn = logWarn;
        _logError = logError;
    }

    /// <summary>Startet den Poll-Thread. Idempotent. Tut nichts, wenn IntervalMs = 0.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PollThread));
        if (_intervalMs <= 0) return; // disabled
        if (_thread is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = _threadName
        };
        _thread.Start();
    }

    /// <summary>Stoppt den Poll-Thread.</summary>
    public void Stop()
    {
        if (_disposed) return;
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    private void Loop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Cancellation-aware Sleep: WaitHandle.WaitOne(timeout) reagiert
                // auf Token-Cancel und blockiert sonst für das Intervall.
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(_intervalMs)))
                {
                    break; // cancellation
                }

                try
                {
                    var hwnd = GetForegroundWindow();
                    if (hwnd == IntPtr.Zero) continue;

                    var ev = new TriggerEvent(hwnd, _triggerKind, DateTimeOffset.Now);
                    _writer.TryWrite(ev);
                }
                catch (Exception ex)
                {
                    _logWarn?.Invoke($"{_logPrefix} poll failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"{_logPrefix} loop crashed: {ex}");
        }
    }
}
