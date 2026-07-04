using System.Runtime.InteropServices;
using AiRecall.Core.Models;
using AiRecall.Core.Windows;

namespace AiRecall.ScreenCapture.Trigger;

/// <summary>
/// Polling-basierter Event-Detector (Spec 0002 TR-1..3, Polling-Implementierung
/// aus Spec 0003 §Implementierungs-Hinweis).
///
/// Erkennt drei Event-Typen:
///   - <b>Activate</b>: FG-Window ändert sich (HWND wechselt).
///   - <b>Scroll</b>: FG-Window-Bounds ändern sich (kann auch Resize/Move sein).
///   - <b>Click</b>: Cursor bewegt sich um &gt;= <see cref="ClickPixelThreshold"/> px
///     seit dem letzten Event (innerhalb des FG-Windows).
///
/// Optional periodischer Event alle <see cref="PeriodicCaptureMs"/> ms (TR-6).
/// </summary>
public sealed class EventDetector : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private readonly int _pollIntervalMs;
    private readonly int _periodicCaptureMs;
    private readonly int _clickPixelThreshold;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private IntPtr _lastHwnd = IntPtr.Zero;
    private WindowRect _lastBounds = new(0, 0, 0, 0);
    private POINT _lastCursor;
    private DateTimeOffset _lastPeriodicEmit = DateTimeOffset.MinValue;

    public event Action<CaptureEvent>? OnEvent;

    public EventDetector(int pollIntervalMs = 50, int periodicCaptureMs = 0, int clickPixelThreshold = 5)
    {
        if (pollIntervalMs < 10) pollIntervalMs = 10;
        _pollIntervalMs = pollIntervalMs;
        _periodicCaptureMs = periodicCaptureMs;
        _clickPixelThreshold = clickPixelThreshold;
    }

    /// <summary>Startet den Polling-Loop im Hintergrund.</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "AiRecall.EventDetector"
        };
        _thread.Start();
    }

    /// <summary>Stoppt den Polling-Loop und wartet auf Thread-Ende.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private void Loop(CancellationToken token)
    {
        // Initiale Snapshot-Werte, damit der erste Activate nicht falsch gefeuert wird.
        _lastHwnd = GetForegroundWindow();
        if (_lastHwnd != IntPtr.Zero && GetWindowRect(_lastHwnd, out var r))
            _lastBounds = new WindowRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        else
            _lastBounds = new WindowRect(0, 0, 0, 0);
        GetCursorPos(out _lastCursor);

        while (!token.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
            catch
            {
                // swallow — Polling darf nicht abbrechen
            }
            Thread.Sleep(_pollIntervalMs);
        }
    }

    private void PollOnce()
    {
        var now = DateTimeOffset.Now;
        var hwnd = GetForegroundWindow();

        // Periodic capture (TR-6)
        if (_periodicCaptureMs > 0 && (now - _lastPeriodicEmit).TotalMilliseconds >= _periodicCaptureMs
            && hwnd != IntPtr.Zero)
        {
            var win = TryResolveWindow(hwnd);
            if (win is not null)
            {
                _lastPeriodicEmit = now;
                OnEvent?.Invoke(new CaptureEvent(CaptureEventKind.Periodic, now, win));
            }
        }

        if (hwnd == IntPtr.Zero) return;

        // Activate: FG-Window hat sich geändert.
        if (hwnd != _lastHwnd)
        {
            _lastHwnd = hwnd;
            var win = TryResolveWindow(hwnd);
            if (win is not null)
            {
                OnEvent?.Invoke(new CaptureEvent(CaptureEventKind.Activate, now, win));
                // Snapshot zurücksetzen, damit nicht sofort noch ein Scroll/Click feuert.
                _lastBounds = win.Bounds;
                GetCursorPos(out _lastCursor);
            }
            return;
        }

        // Scroll/Resize: Bounds haben sich geändert.
        if (GetWindowRect(hwnd, out var r))
        {
            var bounds = new WindowRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            if (bounds != _lastBounds)
            {
                _lastBounds = bounds;
                var win = TryResolveWindow(hwnd);
                if (win is not null)
                {
                    OnEvent?.Invoke(new CaptureEvent(CaptureEventKind.Scroll, now, win));
                }
            }
        }

        // Click: Cursor bewegt sich >= Threshold px.
        if (GetCursorPos(out var cur))
        {
            var dx = cur.X - _lastCursor.X;
            var dy = cur.Y - _lastCursor.Y;
            if (Math.Abs(dx) >= _clickPixelThreshold || Math.Abs(dy) >= _clickPixelThreshold)
            {
                _lastCursor = cur;
                var win = TryResolveWindow(hwnd);
                if (win is not null)
                {
                    OnEvent?.Invoke(new CaptureEvent(CaptureEventKind.Click, now, win));
                }
            }
        }
    }

    private static WindowInfo? TryResolveWindow(IntPtr hWnd)
    {
        try
        {
            // Wir nutzen ActiveWindowDetector, um Process-Name + Titel zu bekommen.
            // Für den FG-Pfad ist der einfachste Weg: WindowInfoLookup.
            return WindowInfoLookup.Get(hWnd.ToInt64());
        }
        catch
        {
            return null;
        }
    }
}