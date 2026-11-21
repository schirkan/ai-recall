using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AiRecall.AppReader.Base;
using AiRecall.Conversion;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;
using AiRecall.Core.Util;
using AiRecall.Core.Windows;
using AiRecall.ScreenCapture.Screenshot;
using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// Worker-Thread der Trigger-Pipeline (Spec 0005 §Architektur).
///
/// Liest <see cref="TriggerEvent"/>s aus dem <see cref="Channel{T}"/>, der
/// von <see cref="WinEventHookDetector"/> und <see cref="HeartbeatThread"/>
/// befüllt wird. Wendet die Pipeline-Schritte 1–10 aus Spec 0005 an und
/// ruft am Ende <see cref="CaptureWriter.WritePending"/> auf.
///
/// Pipeline-Schritte (Spec 0005 + Spec 0007 + Bug-Bash 2026-07-05):
///   1. HWND normalisieren via <c>GetAncestor(hwnd, GA_ROOT)</c>
///   2. Self-Capture-Filter (PID == eigene PID)
///   3. Class-Blacklist (<c>trigger.blacklist.windowClasses</c>)
///   4. WindowInfo-Lookup
///   5. Process-Blacklist (<c>trigger.blacklist.processes</c>)
///   5b. Title-Blacklist (<c>trigger.blacklist.windowTitles</c>) —
///       schützt z. B. TrayApp-Fenster (SettingsDialog, LogviewerWindow)
///   6. Per-Hwnd-Throttle (<c>trigger.throttleMs</c>)
///   7. Per-App-Throttle (<c>trigger.throttlePerAppSeconds</c>)
///   8. Screenshot
///   9. Hash-Dedup pro HWND (<c>HwndDedup</c>)
///  10. App-Reader (Spec 0004) — schreibt *.content.md synchron
///  11. <see cref="CaptureWriter.WritePending"/> + enqueue to ConversionWorker
///
/// Spec 0007 Schritt 6: OCR ist in den async <see cref="ConversionWorker"/>
/// gewandert. TriggerWorker schreibt nur noch den Pending-MD und übergibt
/// den Pfad an den ConversionWorker, der OCR + DocumentConverter
/// asynchron ausführt.
/// </summary>
public sealed class TriggerWorker : IDisposable
{
    // GetAncestor-Flags
    private const uint GA_ROOT       = 2;
    private const uint GA_ROOTOWNER  = 3;

    private readonly ChannelReader<TriggerEvent> _reader;
    private readonly AppConfig _config;
    private readonly Serilog.ILogger _logger;
    private readonly Throttle<IntPtr> _hwndThrottle;
    private readonly Throttle<string> _appThrottle;
    private readonly HwndDedup _hwndDedup;
    private readonly AppReaderRegistry _appReaderRegistry;
    private readonly ConversionWorker? _conversionWorker;
    private readonly object _captureCommit = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public long CaptureCount { get; private set; }
    public long SkippedCount { get; private set; }
    public long ThrottleCount { get; private set; }
    public long DuplicateCount { get; private set; }
    public long BlacklistCount { get; private set; }
    public long SelfCaptureCount { get; private set; }
    public long ErrorCount { get; private set; }

    public TriggerWorker(
        ChannelReader<TriggerEvent> reader,
        AppConfig config,
        Serilog.ILogger logger,
        AppReaderRegistry? appReaderRegistry = null,
        ConversionWorker? conversionWorker = null)
    {
        _reader = reader;
        _config = config;
        _logger = logger;
        _hwndThrottle = new Throttle<IntPtr>(TimeSpan.FromMilliseconds(config.Trigger.ThrottleMs));
        _appThrottle = new Throttle<string>(TimeSpan.FromSeconds(config.Trigger.ThrottlePerAppSeconds));

        var stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AiRecall", "hwnd-dedup-state.json");
        _hwndDedup = new HwndDedup(stateFile);

        _appReaderRegistry = appReaderRegistry ?? LoadAppReaderRegistry();
        _conversionWorker = conversionWorker;
    }

    /// <summary>Startet den Worker-Thread. Idempotent.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TriggerWorker));
        if (_thread?.IsAlive == true) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "AiRecall.TriggerWorker"
        };
        _thread.Start();
    }

    /// <summary>Stoppt den Worker-Thread.</summary>
    public void Stop()
    {
        if (_disposed) return;
        _cts?.Cancel();
        // Finish any commit already in progress. A blocked window or reader
        // may return later, but its cancelled event cannot commit anything.
        lock (_captureCommit)
            _hwndDedup.Save();

        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _logger.Warning("Capture worker is still waiting on an external window or reader; capture writes are stopped");
            return;
        }

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
    // Pipeline-Loop
    // -----------------------------------------------------------------------

    private void Loop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_reader.TryRead(out var ev))
                {
                    if (_reader.Completion.IsCompleted) break;
                    // Aktives Warten statt Spin-Loop
                    if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(10)))
                    {
                        break; // Cancellation
                    }
                    continue;
                }

                try
                {
                    ProcessEvent(ev, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "TriggerWorker: ProcessEvent threw");
                    ErrorCount++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "TriggerWorker loop crashed");
        }
    }

    /// <summary>
    /// Verarbeitet ein einzelnes <see cref="TriggerEvent"/> durch die Pipeline.
    /// <c>public</c> für Tests und für externes Replay (z. B. Re-Process
    /// historischer Events).
    /// </summary>
    public void ProcessEvent(TriggerEvent ev)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ProcessEvent(ev, _cts?.Token ?? default);
    }

    private void ProcessEvent(TriggerEvent ev, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 1. HWND normalisieren (Child-HWND → Top-Level)
        var rootHwnd = GetAncestor(ev.Hwnd, GA_ROOT);
        if (rootHwnd == IntPtr.Zero)
        {
            SkippedCount++;
            return;
        }

        // 2. Self-Capture-Filter (PID == eigene PID)
        GetWindowThreadProcessId(rootHwnd, out var pid);
        if (pid == Environment.ProcessId)
        {
            SelfCaptureCount++;
            return;
        }

        // 3. Class-Blacklist
        var className = GetClassName(rootHwnd);
        if (MatchesAny(className, _config.Trigger.Blacklist.WindowClasses))
        {
            BlacklistCount++;
            return;
        }

        // 4. WindowInfo-Lookup (kann null sein wenn HWND weg ist)
        var window = WindowInfoLookup.Get(rootHwnd.ToInt64());
        if (window is null)
        {
            SkippedCount++;
            return;
        }

        // 4b. Modal-Dialog-Detection (Spec 0005 §Modale Dialoge):
        //     Wenn GA_ROOTOWNER != rootHwnd, dann ist rootHwnd ein modaler
        //     Dialog und der Owner (parent) ist der eigentliche Hauptfenster-
        //     Kontext. Wir schreiben NUR den Foreground-Capture, reichern das
        //     Frontmatter aber mit parentHwnd/parentTitle/parentProcess an.
        WindowInfo? parentWindow = null;
        var ownerHwnd = GetAncestor(rootHwnd, GA_ROOTOWNER);
        if (ownerHwnd != IntPtr.Zero && ownerHwnd != rootHwnd)
        {
            parentWindow = WindowInfoLookup.Get(ownerHwnd.ToInt64());
        }

        ProcessWindow(ev, window, parentWindow, WindowScreenshot.CapturePng, token);
    }

    internal void ProcessWindow(TriggerEvent ev, WindowInfo window, WindowInfo? parentWindow,
        Func<WindowInfo, byte[]> capture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rootHwnd = window.Handle;
        // The owner title is persisted too, so apply metadata filters to both.
        if (IsWindowIgnored(window) || (parentWindow is not null && IsWindowIgnored(parentWindow)))
        {
            BlacklistCount++;
            return;
        }

        // 6. Per-Hwnd-Throttle
        if (!_hwndThrottle.Allows(rootHwnd, ev.Timestamp))
        {
            ThrottleCount++;
            return;
        }

        // 7. Per-App-Throttle
        if (!_appThrottle.Allows(window.ProcessName, ev.Timestamp))
        {
            ThrottleCount++;
            return;
        }

        // 8. Screenshot
        byte[] pngBytes;
        try
        {
            pngBytes = capture(window);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Screenshot failed for HWND 0x{Hwnd:X}", rootHwnd.ToInt64());
            ErrorCount++;
            return;
        }

        // 9. Hash-Dedup pro HWND
        var hash = Hashing.Sha256(pngBytes);
        lock (_captureCommit)
        {
            token.ThrowIfCancellationRequested();
            if (_hwndDedup.IsDuplicate(rootHwnd, hash, ev.Timestamp))
            {
                _logger.Debug("HWND-Dedup hit for HWND 0x{Hwnd:X}", rootHwnd.ToInt64());
                DuplicateCount++;
                return;
            }
        }

        // 10. App-Reader (Spec 0004) — wird VOR dem Pending-MD aufgerufen,
        //     damit filePath/uiaContent aus dem Extra-Dict ins Frontmatter
        //     geschrieben werden koennen. Spec 0007 Schritt 7: duenne Reader
        //     (Word/Excel/PowerPoint) liefern nur Metadaten, der Conversion-
        //     Worker assemblet daraus spaeter das *.conversion.md.
        AppReaderResult? readerResult = null;
        if (_config.AppReader.Enabled && _appReaderRegistry.Readers.Count > 0)
        {
            var context = new AppReaderContext
            {
                Config = _config,
                Logger = _logger,
                CancellationToken = token
            };
            readerResult = _appReaderRegistry.TryRead(window, context);
        }

        if (IgnoreMatcher.IsUrlIgnored(readerResult?.ContextUrl, _config.ScreenRecorder))
        {
            BlacklistCount++;
            return;
        }

        lock (_captureCommit)
        {
            token.ThrowIfCancellationRequested();
            CommitCapture(ev, window, parentWindow, pngBytes, hash, readerResult);
        }
    }

    private bool IsWindowIgnored(WindowInfo window) =>
        MatchesAny(window.ProcessName, _config.Trigger.Blacklist.Processes) ||
        MatchesAny(window.Title, _config.Trigger.Blacklist.WindowTitles) ||
        IgnoreMatcher.IsWindowIgnored(window, _config.ScreenRecorder);

    private void CommitCapture(TriggerEvent ev, WindowInfo window, WindowInfo? parentWindow,
        byte[] pngBytes, string hash, AppReaderResult? readerResult)
    {
        var rootHwnd = window.Handle;
        // 11. Pending-MD schreiben (mit App-Reader-Metadaten wenn vorhanden)
        string? appFilePath = null;
        string? appUiaContent = null;
        if (readerResult?.Extra is not null)
        {
            if (readerResult.Extra.TryGetValue("filePath", out var fp) && !string.IsNullOrEmpty(fp))
                appFilePath = fp;
            if (readerResult.Extra.TryGetValue("uiaContent", out var uia) && !string.IsNullOrEmpty(uia))
                appUiaContent = uia;
        }

        var captureRoot = AppPaths.ResolveDataPath(_config.Capture.RootPath);
        var item = CaptureWriter.WritePending(
            window, pngBytes, hash, captureRoot,
            parentWindow: parentWindow,
            filePath: appFilePath,
            uiaContent: appUiaContent);

        // 12. Content-MD synchron (nur fuer NICHT-duenne Reader).
        //     Duenne Reader (Word/Excel/PowerPoint) liefern nur Metadaten;
        //     ihr Content-Markdown ist der Platzhalter — der echte Content
        //     kommt async aus dem ConversionWorker.
        if (readerResult is not null && !readerResult.IsThinReader)
        {
            var coreRecord = readerResult.ToContentRecord();
            CaptureWriter.WriteContent(coreRecord, window, item.Timestamp, captureRoot, readerResult.ContextLabel);
        }

        // 12. Async Conversion-Worker (Spec 0007) — enqueue der MD-Pfad
        if (_conversionWorker is not null && !_conversionWorker.TryEnqueue(item.MarkdownPath))
            _logger.Warning("Conversion worker did not accept {MdPath}; capture remains pending", item.MarkdownPath);

        // 13. State aktualisieren
        _hwndThrottle.Mark(rootHwnd, ev.Timestamp);
        _appThrottle.Mark(window.ProcessName, ev.Timestamp);
        _hwndDedup.Mark(rootHwnd, hash, ev.Timestamp);

        CaptureCount++;
        _logger.Information(
            "Captured {Kind} {Process}/{Title} -> {Path} (hash={Hash})",
            ev.Kind, window.ProcessName, window.Title, item.ScreenshotPath, hash);
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Case-insensitive Substring-Match. Public für Tests.
    /// </summary>
    public static bool MatchesAny(string value, IReadOnlyCollection<string> patterns)
    {
        if (string.IsNullOrEmpty(value) || patterns is null || patterns.Count == 0) return false;
        foreach (var p in patterns)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (value.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        try
        {
            var len = NativeGetClassName(hwnd, sb, sb.Capacity);
            if (len > 0) return sb.ToString();
        }
        catch { /* HWND weg o. ä. */ }
        return string.Empty;
    }

    private AppReaderRegistry LoadAppReaderRegistry()
    {
        var pluginDir = _config.AppReader.PluginPath;
        var resolved = string.IsNullOrWhiteSpace(pluginDir) || pluginDir == "."
            ? AppContext.BaseDirectory
            : Path.IsPathRooted(pluginDir)
                ? pluginDir
                : AppPaths.ResolveDataPath(pluginDir);
        return AppReaderRegistry.LoadFromDirectory(resolved, _logger);
    }

    // -----------------------------------------------------------------------
    // P/Invoke
    // -----------------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NativeGetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}