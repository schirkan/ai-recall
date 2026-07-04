using System.Threading.Channels;
using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
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
///   </list>
///
/// Beide Quellen schreiben in denselben <see cref="Channel{T}"/>. Der
/// Channel wird unbounded erzeugt (Events gehen nicht verloren; Worker
/// liest aktiv). Der Worker ruft am Ende
/// <see cref="AiRecall.Core.Persistence.CaptureWriter.Write"/> auf.
///
/// Die beiden Quellen können einzeln deaktiviert werden (z. B. für Tests
/// oder für den MVP2-Tray-EXE, der den Heartbeat evtl. nicht braucht).
/// Standard: beide aktiv, sofern die Config sie erlaubt.
/// </summary>
public sealed class TriggerService : ITriggerService
{
    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly Channel<TriggerEvent> _channel;
    private readonly WinEventHookDetector? _winEventHook;
    private readonly HeartbeatThread? _heartbeat;
    private readonly TriggerWorker _worker;

    private bool _isRunning;
    private bool _disposed;

    public TriggerService(
        AppConfig config,
        ILogger logger,
        AppReaderRegistry? appReaderRegistry = null,
        bool enableWinEventHook = true,
        bool enableHeartbeat = true)
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

        _worker = new TriggerWorker(_channel.Reader, _config, _logger, appReaderRegistry);
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

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TriggerService));
        if (_isRunning) return; // idempotent
        // Reihenfolge: Worker zuerst (liest), dann Quellen (schreiben).
        _worker.Start();
        _winEventHook?.Start();
        _heartbeat?.Start();
        _isRunning = true;
        _logger.Information(
            "TriggerService started (WinEventHook={Hook}, Heartbeat={Hb} @ {HbSec}s)",
            _winEventHook is not null,
            _heartbeat is not null,
            _config.Trigger.HeartbeatIntervalSeconds);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_isRunning) return; // idempotent
        // Reihenfolge: Quellen zuerst (schreiben nicht mehr), dann Worker,
        // dann Channel-Completion.
        try { _winEventHook?.Stop(); } catch (Exception ex) { _logger.Warning(ex, "WinEventHook.Stop failed"); }
        try { _heartbeat?.Stop(); } catch (Exception ex) { _logger.Warning(ex, "Heartbeat.Stop failed"); }
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
        _disposed = true;
    }
}