using System.Runtime.InteropServices;
using System.Threading.Channels;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Windows;

namespace AiRecall.Trigger;

/// <summary>
/// Win32-Event-basierter Detector via <c>SetWinEventHook</c> (Spec 0005).
///
/// Subkribiert die in <see cref="WinEventSubscription"/> aktivierten Events
/// <b>out-of-context</b> (kein DLL-Injection in andere Prozesse, keine
/// Admin-Rechte, keine AV-Warnungen). Läuft auf eigenem Thread mit
/// Message-Loop (message-only window + <c>GetMessage</c>/<c>DispatchMessage</c>).
///
/// Im Callback wird das auslösende HWND in <see cref="TriggerEvent"/> verpackt
/// und in einen <see cref="Channel{T}"/> geschrieben (non-blocking via
/// <c>TryWrite</c>). Auflösung zu <see cref="WindowInfo"/> passiert im
/// Worker-Thread, nicht hier (Callback darf nicht blockieren).
/// </summary>
public sealed class WinEventHookDetector : IDisposable
{
    // Win32 Event-Konstanten
    private const uint EVENT_SYSTEM_FOREGROUND    = 0x0003;
    private const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0005;
    private const uint EVENT_OBJECT_FOCUS         = 0x8005;
    private const uint EVENT_OBJECT_NAMECHANGE    = 0x800C;
    private const uint EVENT_OBJECT_VALUECHANGE   = 0x800E;
    private const uint EVENT_OBJECT_SCROLL        = 0x8015;

    // Hook-Flags
    private const uint WINEVENT_OUTOFCONTEXT    = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS  = 0x0002;

    // Win32 Messages
    private const int  WM_QUIT = 0x0012;

    private readonly WinEventSubscription _config;
    private readonly ChannelWriter<TriggerEvent> _writer;
    private readonly Action<TriggerEvent>? _directEventSink;
    private readonly Action<string>? _logWarn;
    private readonly Action<string>? _logError;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly List<IntPtr> _hooks = new();
    private bool _disposed;

    // P/Invoke-Delegate (muss als Field gehalten werden, sonst GC)
    private readonly WinEventProcDelegate _winEventProcDelegate;

    /// <summary>
    /// Erstellt einen neuen WinEventHookDetector.
    /// </summary>
    /// <param name="config">Welche Win32-Events subskribiert werden sollen.</param>
    /// <param name="writer">Channel-Writer für die produzierten Trigger-Events.</param>
    /// <param name="logWarn">Optional: Callback für Warnungen.</param>
    /// <param name="logError">Optional: Callback für Fehler.</param>
    /// <param name="directEventSink">Optional: zusätzlicher direkter Event-Sink
    /// (für Tests; in Produktion reicht der Channel).</param>
    public WinEventHookDetector(
        WinEventSubscription config,
        ChannelWriter<TriggerEvent> writer,
        Action<string>? logWarn = null,
        Action<string>? logError = null,
        Action<TriggerEvent>? directEventSink = null)
    {
        _config = config;
        _writer = writer;
        _logWarn = logWarn;
        _logError = logError;
        _directEventSink = directEventSink;
        _winEventProcDelegate = WinEventProc;
    }

    /// <summary>Startet den Hook-Thread. Idempotent.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinEventHookDetector));
        if (_thread is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "AiRecall.WinEventHook"
        };
        _thread.Start();
    }

    /// <summary>Stoppt den Hook-Thread und gibt alle Hooks frei.</summary>
    public void Stop()
    {
        if (_disposed) return;
        _cts?.Cancel();
        if (_hwnd != IntPtr.Zero)
        {
            // Message-Loop beenden
            PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
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

    // -----------------------------------------------------------------------
    // Thread-Loop
    // -----------------------------------------------------------------------

    private void Loop(CancellationToken token)
    {
        try
        {
            _hwnd = CreateMessageWindow();
            if (_hwnd == IntPtr.Zero)
            {
                _logError?.Invoke("WinEventHookDetector: CreateMessageWindow failed");
                return;
            }

            // Hooks für aktivierte Events registrieren
            if (_config.Foreground)  RegisterHook(EVENT_SYSTEM_FOREGROUND);
            if (_config.Focus)       RegisterHook(EVENT_OBJECT_FOCUS);
            if (_config.NameChange)  RegisterHook(EVENT_OBJECT_NAMECHANGE);
            if (_config.ValueChange) RegisterHook(EVENT_OBJECT_VALUECHANGE);
            if (_config.Scroll)      RegisterHook(EVENT_OBJECT_SCROLL);
            if (_config.MenuPopup)   RegisterHook(EVENT_SYSTEM_MENUPOPUPSTART);

            // Message-Loop (blockierend)
            while (!token.IsCancellationRequested)
            {
                var result = GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (result <= 0) break; // -1 = Fehler, 0 = WM_QUIT
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"WinEventHookDetector loop crashed: {ex}");
        }
        finally
        {
            // Hooks freigeben (in umgekehrter Reihenfolge der Registrierung ist OK)
            foreach (var h in _hooks)
            {
                try { UnhookWinEvent(h); } catch { /* best effort */ }
            }
            _hooks.Clear();

            if (_hwnd != IntPtr.Zero)
            {
                try { DestroyWindow(_hwnd); } catch { /* best effort */ }
                _hwnd = IntPtr.Zero;
            }
        }
    }

    private void RegisterHook(uint eventConstant)
    {
        var hook = SetWinEventHook(
            eventConstant,
            eventConstant,
            IntPtr.Zero,
            _winEventProcDelegate,
            0, // any process
            0, // any thread
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero)
        {
            _hooks.Add(hook);
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            _logWarn?.Invoke($"SetWinEventHook failed for event 0x{eventConstant:X} (Win32Error {err})");
        }
    }

    // -----------------------------------------------------------------------
    // Win32 Callback (out-of-context: läuft auf unserem Hook-Thread)
    // -----------------------------------------------------------------------

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // TriggerKind mappen
        TriggerKind? kind = eventType switch
        {
            EVENT_SYSTEM_FOREGROUND    => TriggerKind.Foreground,
            EVENT_OBJECT_FOCUS         => TriggerKind.Focus,
            EVENT_OBJECT_NAMECHANGE    => TriggerKind.NameChange,
            EVENT_OBJECT_VALUECHANGE   => TriggerKind.ValueChange,
            EVENT_OBJECT_SCROLL        => TriggerKind.Scroll,
            EVENT_SYSTEM_MENUPOPUPSTART => TriggerKind.MenuPopup,
            _ => null
        };
        if (kind is null || hwnd == IntPtr.Zero) return;

        // HWND==0 ist "global" / Desktop → ignorieren (kein Top-Level-Fenster).
        var ev = new TriggerEvent(hwnd, kind.Value, DateTimeOffset.Now);

        // Direct sink (für Tests)
        _directEventSink?.Invoke(ev);

        // Channel (für Worker) — TryWrite ist non-blocking. Wenn voll → Event verwerfen.
        // Heartbeat-Fallback fängt verlorene Events auf.
        _writer.TryWrite(ev);
    }

    // -----------------------------------------------------------------------
    // Win32 P/Invoke
    // -----------------------------------------------------------------------

    private delegate void WinEventProcDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProcDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private static IntPtr CreateMessageWindow()
    {
        // HWND_MESSAGE = -3 (Message-only Window)
        var HWND_MESSAGE = new IntPtr(-3);
        return CreateWindowExW(
            0, "Static", "AiRecallMessageWindow", 0,
            0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }
}