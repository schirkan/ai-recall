using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace AiRecall.ScreenCapture.Trigger;

/// <summary>
/// Periodisches Heartbeat-Polling (Spec 0005 §Trigger-Quellen, sekundär).
///
/// Ruft alle <see cref="IntervalSeconds"/> <c>GetForegroundWindow</c> auf und
/// schreibt ein <see cref="TriggerEvent"/> mit <see cref="TriggerKind.Heartbeat"/>
/// in den Channel. Dient als Fallback, falls WinEventHook-Events verloren gehen
/// (Sleep/Resume, schneller User-Session-Wechsel, hohe Systemlast).
///
/// <see cref="IntervalSeconds"/> = 0 deaktiviert den Heartbeat komplett
/// (kein Thread wird gestartet).
/// </summary>
public sealed class HeartbeatThread : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly int _intervalSeconds;
    private readonly ChannelWriter<TriggerEvent> _writer;
    private readonly Action<string>? _logWarn;
    private readonly Action<string>? _logError;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public int IntervalSeconds => _intervalSeconds;

    public HeartbeatThread(
        int intervalSeconds,
        ChannelWriter<TriggerEvent> writer,
        Action<string>? logWarn = null,
        Action<string>? logError = null)
    {
        _intervalSeconds = Math.Max(0, intervalSeconds);
        _writer = writer;
        _logWarn = logWarn;
        _logError = logError;
    }

    /// <summary>Startet den Heartbeat-Thread. Idempotent. Tut nichts, wenn IntervalSeconds = 0.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HeartbeatThread));
        if (_intervalSeconds <= 0) return; // disabled
        if (_thread is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "AiRecall.Heartbeat"
        };
        _thread.Start();
    }

    /// <summary>Stoppt den Heartbeat-Thread.</summary>
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
                if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(_intervalSeconds)))
                {
                    // Wait-Handle signalisiert = CancellationToken wurde gecancelt
                    break;
                }

                try
                {
                    var hwnd = GetForegroundWindow();
                    if (hwnd == IntPtr.Zero) continue;

                    var ev = new TriggerEvent(hwnd, TriggerKind.Heartbeat, DateTimeOffset.Now);
                    _writer.TryWrite(ev);
                }
                catch (Exception ex)
                {
                    _logWarn?.Invoke($"Heartbeat poll failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Heartbeat loop crashed: {ex}");
        }
    }
}