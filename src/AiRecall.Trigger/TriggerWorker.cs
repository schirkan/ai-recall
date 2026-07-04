using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;
using AiRecall.Core.Util;
using AiRecall.Core.Windows;
using AiRecall.ScreenCapture.Screenshot;
using AiRecall.ScreenCapture.Text;
using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// Worker-Thread der Trigger-Pipeline (Spec 0005 §Architektur).
///
/// Liest <see cref="TriggerEvent"/>s aus dem <see cref="Channel{T}"/>, der
/// von <see cref="WinEventHookDetector"/> und <see cref="HeartbeatThread"/>
/// befüllt wird. Wendet die Pipeline-Schritte 1–10 aus Spec 0005 an und
/// ruft am Ende <see cref="CaptureWriter.Write"/> auf.
///
/// Pipeline-Schritte (Spec 0005):
///   1. HWND normalisieren via <c>GetAncestor(hwnd, GA_ROOT)</c>
///   2. Self-Capture-Filter (PID == eigene PID)
///   3. Class-Blacklist (<c>trigger.blacklist.windowClasses</c>)
///   4. WindowInfo-Lookup
///   5. Process-Blacklist (<c>trigger.blacklist.processes</c>)
///   6. Per-Hwnd-Throttle (<c>trigger.throttleMs</c>)
///   7. Per-App-Throttle (<c>trigger.throttlePerAppSeconds</c>)
///   8. Screenshot
///   9. Hash-Dedup pro HWND (<c>HwndDedup</c>)
///  10. OCR (Tesseract)
///  11. App-Reader (Spec 0004)
///  12. <see cref="CaptureWriter.Write"/> + Content-MD
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
        AppReaderRegistry? appReaderRegistry = null)
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
    }

    /// <summary>Startet den Worker-Thread. Idempotent.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TriggerWorker));
        if (_thread is not null) return;
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
        try { _reader.Completion.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _cts?.Dispose();
        _cts = null;
        _hwndDedup.Save();
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
                    ProcessEvent(ev);
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

        // 5. Process-Blacklist
        if (MatchesAny(window.ProcessName, _config.Trigger.Blacklist.Processes))
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
            pngBytes = WindowScreenshot.CapturePng(window);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Screenshot failed for HWND 0x{Hwnd:X}", rootHwnd.ToInt64());
            ErrorCount++;
            return;
        }

        // 9. Hash-Dedup pro HWND
        var hash = Hashing.Sha256(pngBytes);
        if (_hwndDedup.IsDuplicate(rootHwnd, hash, ev.Timestamp))
        {
            _logger.Debug("HWND-Dedup hit for HWND 0x{Hwnd:X}", rootHwnd.ToInt64());
            DuplicateCount++;
            return;
        }

        // 10. OCR (Tesseract) — Bild-Beweis + zusätzliche Textquelle
        string contentText = string.Empty;
        if (_config.Ocr.Engine.Equals("tesseract", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var ocr = new OcrEngine(_config.Ocr);
                contentText = ocr.ExtractText(pngBytes);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OCR failed: {Message}", ex.Message);
            }
        }

        // 11. App-Reader (Spec 0004)
        var item = CaptureWriter.Write(window, pngBytes, contentText, hash, _config.Capture.RootPath, parentWindow: parentWindow);

        if (_config.AppReader.Enabled && _appReaderRegistry.Readers.Count > 0)
        {
            var context = new AppReaderContext
            {
                Config = _config,
                Logger = _logger,
                CancellationToken = default
            };
            var readerResult = _appReaderRegistry.TryRead(window, context);
            if (readerResult is not null)
            {
                var coreRecord = new AppContentRecord(
                    readerResult.ContentMarkdown,
                    readerResult.ContextLabel,
                    readerResult.ContextKind,
                    readerResult.ReaderName,
                    readerResult.ReaderVersion,
                    readerResult.Extra);
                CaptureWriter.WriteContent(coreRecord, window, item.Timestamp, _config.Capture.RootPath, readerResult.ContextLabel);
            }
        }

        // 12. State aktualisieren
        _hwndThrottle.Mark(rootHwnd, ev.Timestamp);
        _appThrottle.Mark(window.ProcessName, ev.Timestamp);
        _hwndDedup.Mark(rootHwnd, hash, ev.Timestamp);

        CaptureCount++;
        _logger.Information(
            "Captured {Kind} {Process}/{Title} -> {Path} (hash={Hash}, chars={Chars})",
            ev.Kind, window.ProcessName, window.Title, item.ScreenshotPath, hash, contentText.Length);
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
                : Path.Combine(AppContext.BaseDirectory, pluginDir);
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