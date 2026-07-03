using System.Diagnostics;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using AiRecall.Core.Persistence;
using AiRecall.Core.Util;
using AiRecall.ScreenCapture.Screenshot;
using AiRecall.ScreenCapture.Text;
using Serilog;

namespace AiRecall.ScreenCapture.Trigger;

/// <summary>
/// Orchestriert eine vollständige Capture-Sequenz: EventDetector →
/// Throttle → Screenshot → Hash → Dedup → optional OCR → optional
/// App-Reader → Persistenz.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private readonly AppConfig _config;
    private readonly Serilog.ILogger _logger;
    private readonly Throttle _throttle;
    private readonly Dedup _dedup;
    private readonly AppReaderRegistry _appReaderRegistry;

    public EventDetector Detector { get; }

    public long CaptureCount { get; private set; }
    public long SkippedCount { get; private set; }
    public long DuplicateCount { get; private set; }

    public CapturePipeline(
        AppConfig config,
        Serilog.ILogger logger,
        AppReaderRegistry? appReaderRegistry = null)
    {
        _config = config;
        _logger = logger;
        _throttle = new Throttle(TimeSpan.FromMilliseconds(config.ScreenRecorder.ThrottleMs));

        var stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AiRecall", "dedup-state.json");
        _dedup = new Dedup(stateFile);

        _appReaderRegistry = appReaderRegistry ?? LoadAppReaderRegistry();

        Detector = new EventDetector(
            pollIntervalMs: 50,
            periodicCaptureMs: config.ScreenRecorder.PeriodicCaptureMs,
            clickPixelThreshold: 5);
        Detector.OnEvent += HandleEvent;
    }

    public void Start() => Detector.Start();

    public void Stop()
    {
        Detector.Stop();
        _dedup.Save();
    }

    public void Dispose() => Stop();

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

    private void HandleEvent(CaptureEvent ev)
    {
        try
        {
            ProcessEvent(ev);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "CapturePipeline: event handler threw");
        }
    }

    private void ProcessEvent(CaptureEvent ev)
    {
        var key = ev.ProcessKey;

        if (IgnoreMatcher.IsIgnored(ev.Window, null, _config.ScreenRecorder))
        {
            _logger.Debug("Skip {Kind} for {Process}/{Title}: ignore list", ev.Kind, ev.Window.ProcessName, ev.Window.Title);
            SkippedCount++;
            return;
        }

        if (!_throttle.Allows(key, ev.Timestamp))
        {
            _logger.Debug("Throttled {Kind} for {Process}/{Title}", ev.Kind, ev.Window.ProcessName, ev.Window.Title);
            SkippedCount++;
            return;
        }

        var sw = Stopwatch.StartNew();
        byte[] pngBytes;
        try
        {
            pngBytes = WindowScreenshot.CapturePng(ev.Window);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Screenshot failed for HWND 0x{Hwnd:X}", ev.Window.Handle.ToInt64());
            SkippedCount++;
            return;
        }

        var hash = Hashing.Sha256(pngBytes);

        if (_dedup.IsDuplicate(key, hash, ev.Timestamp))
        {
            _logger.Debug("Duplicate capture for {Process}/{Title} (hash {Hash})", ev.Window.ProcessName, ev.Window.Title, hash);
            DuplicateCount++;
            return;
        }

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

        var item = CaptureWriter.Write(
            ev.Window,
            pngBytes,
            contentText,
            hash,
            _config.Capture.RootPath);

        // App-Reader, falls konfiguriert.
        if (_appReaderRegistry.Readers.Count > 0)
        {
            var context = new AppReaderContext
            {
                Config = _config,
                Logger = _logger,
                CancellationToken = default
            };
            var readerResult = _appReaderRegistry.TryRead(ev.Window, context);
            if (readerResult is not null)
            {
                var coreRecord = new AppContentRecord(
                    readerResult.ContentMarkdown,
                    readerResult.ContextLabel,
                    readerResult.ContextKind,
                    readerResult.ReaderName,
                    readerResult.ReaderVersion,
                    readerResult.Extra);
                CaptureWriter.WriteContent(coreRecord, ev.Window, item.Timestamp, _config.Capture.RootPath, readerResult.ContextLabel);
            }
        }

        _throttle.Mark(key, ev.Timestamp);
        _dedup.Mark(key, hash, ev.Timestamp);

        sw.Stop();
        CaptureCount++;
        _logger.Information(
            "Captured {Kind} {Process}/{Title} -> {Path} (hash={Hash}, chars={Chars}, elapsedMs={Elapsed})",
            ev.Kind, ev.Window.ProcessName, ev.Window.Title, item.ScreenshotPath, hash, contentText.Length, sw.ElapsedMilliseconds);
    }
}