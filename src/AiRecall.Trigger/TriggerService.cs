using System.Threading.Channels;
using AiRecall.AppReader.Base;
using AiRecall.Conversion;
using AiRecall.Core.Configuration;
using AiRecall.ScreenCapture.Text;
using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// Orchestrator der Trigger-Pipeline (Spec 0005 §Architektur).
///
/// Verdrahtet:
///   <list type="bullet">
///     <item><see cref="WinEventHookDetector"/> (Hauptquelle, optional)</item>
///     <item><see cref="HeartbeatThread"/> (Fallback-Polling, optional)</item>
///     <item><see cref="TriggerWorker"/> (verarbeitet Events aus dem Channel)</item>
///     <item><see cref="ConversionWorker"/> (async OCR + DocumentConverter, Spec 0007)</item>
///   </list>
///
/// Beide Trigger-Quellen schreiben in denselben <see cref="Channel{T}"/>. Der
/// Channel wird unbounded erzeugt (Events gehen nicht verloren; Worker
/// liest aktiv). Der Worker ruft am Ende
/// <see cref="AiRecall.Core.Persistence.CaptureWriter.WritePending"/> auf und
/// enqueued den MD-Pfad an den <see cref="ConversionWorker"/>.
///
/// Die Trigger-Quellen können einzeln deaktiviert werden (z. B. für Tests
/// oder für den MVP2-Tray-EXE, der den Heartbeat evtl. nicht braucht).
/// Standard: beide aktiv, sofern die Config sie erlaubt.
///
/// Conversion-Worker (Spec 0007 Schritt 6):
///   - Wenn <c>conversion.enabled=true</c> und kein externer Worker
///     injiziert wurde, wird intern einer mit
///     <see cref="TesseractOcrEngineAdapter"/> erzeugt.
///   - Wird im <see cref="Dispose"/> sauber beendet (idempotent).
///   - Über <see cref="ConversionWorker"/> öffentlich erreichbar (für
///     Status-Inspektion aus CLI/MVP2-Tray-EXE).
/// </summary>
public sealed class TriggerService : ITriggerService
{
    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly Channel<TriggerEvent> _channel;
    private readonly WinEventHookDetector? _winEventHook;
    private readonly HeartbeatThread? _heartbeat;
    private readonly PeriodicCaptureThread? _periodicCapture;
    private readonly TriggerWorker _worker;
    private readonly ConversionWorker? _conversionWorker;
    private readonly bool _ownsConversionWorker;

    private bool _isRunning;
    private bool _disposed;

    public TriggerService(
        AppConfig config,
        ILogger logger,
        AppReaderRegistry? appReaderRegistry = null,
        bool enableWinEventHook = true,
        bool enableHeartbeat = true,
        ConversionWorker? conversionWorker = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _channel = Channel.CreateUnbounded<TriggerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,   // Worker ist der einzige Reader
            SingleWriter = false   // WinEventHook + Heartbeat schreiben beide
        });

        if (enableWinEventHook)
        {
            _winEventHook = new WinEventHookDetector(
                _config.Trigger.WinEvents,
                _channel.Writer,
                logWarn: msg => _logger.Warning("WinEventHook: {Msg}", msg),
                logError: msg => _logger.Error("WinEventHook: {Msg}", msg));
        }

        if (enableHeartbeat && _config.Trigger.HeartbeatIntervalSeconds > 0)
        {
            _heartbeat = new HeartbeatThread(
                _config.Trigger.HeartbeatIntervalSeconds,
                _channel.Writer,
                logWarn: msg => _logger.Warning("Heartbeat: {Msg}", msg),
                logError: msg => _logger.Error("Heartbeat: {Msg}", msg));
        }

        // Bug-Bash 2026-07-06 I-23: Periodischer Capture-Trigger. 0 = deaktiviert.
        // Sinnvolle Werte 3000-10000 ms fuer Video-Streams / Slideshows.
        if (_config.ScreenRecorder.PeriodicCaptureMs > 0)
        {
            _periodicCapture = new PeriodicCaptureThread(
                _config.ScreenRecorder.PeriodicCaptureMs,
                _channel.Writer,
                logWarn: msg => _logger.Warning("PeriodicCapture: {Msg}", msg),
                logError: msg => _logger.Error("PeriodicCapture: {Msg}", msg));
        }

        // ConversionWorker (Spec 0007 Schritt 6):
        //   - Wenn extern injiziert: verwenden, nicht disposen
        //   - Wenn null + Conversion.Enabled: selbst erzeugen mit
        //     TesseractOcrEngineAdapter, disposen beim Service-Dispose
        //   - Bei OcrEngine-Init-Fehler (z. B. tessdata fehlt) Fallback auf
        //     NullOcrEngine — Conversion laeuft dann ohne OCR weiter
        if (conversionWorker is not null)
        {
            _conversionWorker = conversionWorker;
            _ownsConversionWorker = false;
        }
        else if (_config.Conversion.Enabled)
        {
            IOcrEngine ocrEngine;
            if (_config.Ocr.Engine.Equals("tesseract", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ocrEngine = new TesseractOcrEngineAdapter(new OcrEngine(_config.Ocr), _logger);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "TriggerService: Tesseract init failed, using NullOcrEngine (no OCR)");
                    ocrEngine = new NullOcrEngine();
                }
            }
            else
            {
                ocrEngine = new NullOcrEngine();
            }
            _conversionWorker = new ConversionWorker(_config, _logger, ocrEngine);
            _ownsConversionWorker = true;
        }

        _worker = new TriggerWorker(_channel.Reader, _config, _logger, appReaderRegistry, _conversionWorker);
    }

    /// <inheritdoc />
    public bool IsRunning => _isRunning && !_disposed;

    // -----------------------------------------------------------------------
    // Counter (delegieren an den Worker)
    // -----------------------------------------------------------------------

    public long CaptureCount    => _worker.CaptureCount;
    public long SkippedCount    => _worker.SkippedCount;
    public long ThrottleCount   => _worker.ThrottleCount;
    public long DuplicateCount  => _worker.DuplicateCount;
    public long BlacklistCount  => _worker.BlacklistCount;
    public long SelfCaptureCount => _worker.SelfCaptureCount;
    public long ErrorCount      => _worker.ErrorCount;

    /// <summary>
    /// Direkter Zugriff auf den Channel-Writer (für Tests und für externes
    /// Replay historischer Events).
    /// </summary>
    public ChannelWriter<TriggerEvent> ChannelWriter => _channel.Writer;

    /// <summary>
    /// Async Conversion-Worker (Spec 0007). Kann <c>null</c> sein, wenn
    /// <c>conversion.enabled=false</c> und kein externer Worker injiziert wurde.
    /// </summary>
    public ConversionWorker? ConversionWorker => _conversionWorker;

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TriggerService));
        if (_isRunning) return; // idempotent
        // Reihenfolge: Worker zuerst (liest), dann Quellen (schreiben).
        _worker.Start();
        _winEventHook?.Start();
        _heartbeat?.Start();
        _periodicCapture?.Start();
        _isRunning = true;
        _logger.Information(
            "TriggerService started (WinEventHook={Hook}, Heartbeat={Hb} @ {HbSec}s, PeriodicCapture={Pc} @ {PcMs}ms)",
            _winEventHook is not null,
            _heartbeat is not null,
            _config.Trigger.HeartbeatIntervalSeconds,
            _periodicCapture is not null,
            _config.ScreenRecorder.PeriodicCaptureMs);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_isRunning) return; // idempotent
        // Reihenfolge: Quellen zuerst (schreiben nicht mehr), dann Worker,
        // dann Channel-Completion.
        try { _winEventHook?.Stop(); } catch (Exception ex) { _logger.Warning(ex, "WinEventHook.Stop failed"); }
        try { _heartbeat?.Stop(); } catch (Exception ex) { _logger.Warning(ex, "Heartbeat.Stop failed"); }
        try { _periodicCapture?.Stop(); } catch (Exception ex) { _logger.Warning(ex, "PeriodicCapture.Stop failed"); }
        _channel.Writer.TryComplete();
        try { _worker.Stop(); } catch (Exception ex) { _logger.Warning(ex, "Worker.Stop failed"); }
        _isRunning = false;
        _logger.Information(
            "TriggerService stopped (captures={C}, throttled={T}, dedup={D}, blacklist={B}, self={S}, errors={E})",
            CaptureCount, ThrottleCount, DuplicateCount, BlacklistCount, SelfCaptureCount, ErrorCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _worker.Dispose();
        _winEventHook?.Dispose();
        _heartbeat?.Dispose();
        _periodicCapture?.Dispose();
        if (_ownsConversionWorker)
        {
            _conversionWorker?.Dispose();
        }
        _disposed = true;
    }
}