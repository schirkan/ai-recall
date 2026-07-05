using AiRecall.Core.Configuration;
using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// High-level state of the trigger pipeline as seen by the supervisor
/// (Spec 0006 Schritt 2, Tray-EXE).
/// </summary>
public enum TriggerState
{
    /// <summary>No service instance, ready to <see cref="TriggerSupervisor.Start"/>.</summary>
    Stopped,
    /// <summary>Start in progress, sources spinning up.</summary>
    Starting,
    /// <summary>Service running, events flowing.</summary>
    Running,
    /// <summary>Stop in progress, sources being torn down.</summary>
    Stopping,
    /// <summary>Start failed; manual restart needed.</summary>
    Crashed
}

/// <summary>
/// Event args for <see cref="TriggerSupervisor.StateChanged"/>.
/// </summary>
public sealed record TriggerStateChangedEventArgs(TriggerState OldState, TriggerState NewState);

/// <summary>
/// In-process supervisor that wraps <see cref="ITriggerService"/> with
/// lifecycle management, hot-reload via config swap, crash tracking and
/// state-change notifications. Used by the MVP2 Tray-EXE (Spec 0006).
///
/// Replaces the earlier <c>ProcessSupervisor</c> design (subprocess + IPC).
/// By running in-process with the TrayApp, settings hot-reload becomes a
/// simple <see cref="IDisposable.Dispose"/> + new instance, and logs flow
/// through standard Serilog without MMF plumbing.
///
/// Thread-safety: Start/Stop/Restart are intended to be called from the
/// UI thread (or coordinated via a dispatcher). They are not safe to call
/// from multiple threads concurrently.
/// </summary>
public sealed class TriggerSupervisor : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<AppConfig, ILogger, ITriggerService> _serviceFactory;
    private ITriggerService? _service;
    private AppConfig? _currentConfig;
    private bool _disposed;

    /// <summary>
    /// Creates a supervisor with the default factory
    /// (<c>(c, l) => new TriggerService(c, l)</c>).
    /// </summary>
    public TriggerSupervisor(ILogger logger)
        : this(logger, serviceFactory: null)
    {
    }

    /// <summary>
    /// Creates a supervisor with a custom service factory. Used by tests to
    /// inject a fake <see cref="ITriggerService"/>; production code uses the
    /// default factory.
    /// </summary>
    public TriggerSupervisor(
        ILogger logger,
        Func<AppConfig, ILogger, ITriggerService>? serviceFactory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _serviceFactory = serviceFactory ?? DefaultFactory;
    }

    /// <summary>Default factory used when no custom factory is supplied.</summary>
    public static ITriggerService DefaultFactory(AppConfig config, ILogger logger)
        => new TriggerService(config, logger);

    /// <summary>Current supervisor state.</summary>
    public TriggerState State { get; private set; } = TriggerState.Stopped;

    /// <summary>The currently managed trigger service, or <c>null</c> when stopped.</summary>
    public ITriggerService? Service => _service;

    /// <summary>The most recent configuration passed to Start or Restart.</summary>
    public AppConfig? CurrentConfig => _currentConfig;

    /// <summary>How many times Start has crashed since the supervisor was created.</summary>
    public int CrashCount { get; private set; }

    /// <summary>UTC timestamp of the most recent crash, or <c>null</c>.</summary>
    public DateTime? LastCrashAt { get; private set; }

    /// <summary>Raised on every state transition.</summary>
    public event EventHandler<TriggerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Starts the trigger pipeline with the given configuration. No-op if
    /// already running or starting. Throws if the underlying service fails
    /// to start (and leaves <see cref="State"/> in <see cref="TriggerState.Crashed"/>).
    /// </summary>
    public void Start(AppConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);

        if (State is TriggerState.Running or TriggerState.Starting)
        {
            _logger.Debug("TriggerSupervisor.Start: already {State}, no-op", State);
            return;
        }

        SetState(TriggerState.Starting);
        // Bug-Bash 2026-07-05 I-4: Lokale Variable, damit wir im Catch-Block
        // den (halb-initialisierten) Service disposen koennen, auch wenn
        // service.Start() wirft und _service noch nicht zugewiesen ist.
        ITriggerService? service = null;
        try
        {
            service = _serviceFactory(config, _logger);
            service.Start();
            _service = service;
            _currentConfig = config;
            SetState(TriggerState.Running);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "TriggerSupervisor.Start failed");
            CrashCount++;
            LastCrashAt = DateTime.UtcNow;
            try { service?.Dispose(); }
            catch (Exception disposeEx) { _logger.Warning(disposeEx, "TriggerSupervisor.Start: cleanup dispose threw"); }
            _service = null;
            // _currentConfig bewusst NICHT aendern — bleibt auf letztem
            // bekannten guten Wert (oder null bei Erststart).
            SetState(TriggerState.Crashed);
            throw;
        }
    }

    /// <summary>
    /// Stops the trigger pipeline. No-op if not running. Does not throw.
    /// </summary>
    public void Stop()
    {
        // Idempotent via State-Check (kein _disposed-Check, damit Dispose sauber aufräumen kann)
        if (State is TriggerState.Stopped or TriggerState.Stopping) return;

        SetState(TriggerState.Stopping);
        try
        {
            _service?.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "TriggerSupervisor.Stop: service stop threw");
        }
        SetState(TriggerState.Stopped);
    }

    /// <summary>
    /// Stops the current service (if any), disposes it, and starts a new
    /// one with the given configuration. Used for settings hot-reload
    /// (Spec 0009 §Hot-Reload).
    /// </summary>
    public void Restart(AppConfig newConfig)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        _logger.Information("TriggerSupervisor.Restart requested");

        Stop();
        if (_service is not null)
        {
            try { _service.Dispose(); }
            catch (Exception ex) { _logger.Warning(ex, "TriggerSupervisor.Restart: dispose old service threw"); }
            _service = null;
        }
        Start(newConfig);
    }

    private void SetState(TriggerState newState)
    {
        if (State == newState) return;
        var oldState = State;
        State = newState;
        _logger.Information("TriggerSupervisor state {Old} -> {New}", oldState, newState);

        // Bug-Bash 2026-07-05 I-5: Jeden Handler EINZELN aufrufen, weil
        // Multicast-Delegates beim ersten Throw abbrechen und nachfolgende
        // Handler nicht mehr aufgerufen werden. Handler-Snapshot vermeidet
        // NullRef, wenn ein Subscriber sich waehrend des Invoke abmeldet.
        var handler = StateChanged;
        if (handler is not null)
        {
            var args = new TriggerStateChangedEventArgs(oldState, newState);
            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<TriggerStateChangedEventArgs>)d).Invoke(this, args);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "TriggerSupervisor.StateChanged handler threw; state still {New}", newState);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_service is not null)
        {
            try { _service.Dispose(); }
            catch (Exception ex) { _logger.Warning(ex, "TriggerSupervisor.Dispose: service dispose threw"); }
            _service = null;
        }
        _logger.Information("TriggerSupervisor disposed");
    }
}