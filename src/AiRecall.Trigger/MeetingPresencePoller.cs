using System;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Configuration;

using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// Zyklischer Poller fuer Teams-Meeting-Presence (Spec 0013 v0.3 §1).
/// <list type="bullet">
///   <item>Ruft alle <see cref="TeamsConfig.PresencePollIntervalSeconds"/> Sekunden
///         <see cref="IMeetingPresenceProbe.GetSnapshotAsync"/> auf.</item>
///   <item>Feuert <see cref="PresenceChanged"/> nur an Edge-Transitions
///         (inactive→active, active→inactive), <b>nicht</b> bei jedem Tick.</item>
///   <item>Start-Debounce = <see cref="TeamsConfig.MinMeetingDurationSeconds"/>:
///         ein Meeting, das kuerzer sichtbar ist, loest KEIN
///         <c>PresenceChanged(true)</c> aus.</item>
///   <item>Stop-Erkennung erfolgt beim naechsten Tick (= max ein
///         PresencePollInterval Delay).</item>
///   <item>Wenn <see cref="TeamsConfig.AutoRecordMeetings"/> = false:
///         Polling laeuft, <see cref="Current"/> wird aktualisiert, aber
///         <see cref="PresenceChanged"/> wird <b>nie</b> gefeuert.</item>
/// </list>
/// Thread-Safety: <see cref="PresenceChanged"/> wird auf dem Loop-Task gefeuert.
/// Subscriber muessen selbst thread-safe marshallen, falls noetig.
/// </summary>
public sealed class MeetingPresencePoller : IAsyncDisposable
{
    private readonly IMeetingPresenceProbe _probe;
    private readonly IPresenceTicker _ticker;
    private readonly IPresenceClock _clock;
    private readonly ILogger? _logger;

    private TeamsConfig? _cfg;
    private DateTimeOffset? _pendingStartAt;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _disposed;

    /// <summary>
    /// Test-Hook (internal): wird nach <see cref="Evaluate"/> auf dem Loop-Thread
    /// awaited. Tests nutzen das als deterministisches Sync-Signal — direkt
    /// nach dem Hook-Aufruf ist der interne State (IsActive, Events) konsistent.
    /// Wird auf <c>null</c> gesetzt, um den Hook wieder abzubestellen.
    /// Exceptions aus dem Hook werden geloggt und geschluckt, damit der Loop
    /// nicht durch Test-Bugs gekillt wird.
    /// </summary>
    internal Func<MeetingPresenceSnapshot, Task>? OnSnapshotEvaluated
    {
        get => _onSnapshotEvaluated;
        set => _onSnapshotEvaluated = value;
    }
    private Func<MeetingPresenceSnapshot, Task>? _onSnapshotEvaluated;

    /// <summary>Wird an Edge-Transitions gefeuert (inactive↔active).</summary>
    public event EventHandler<MeetingPresenceStateChangedEventArgs>? PresenceChanged;

    /// <summary>Test-Hook (internal): manuell PresenceChanged-Event feuern.</summary>
    internal void RaisePresenceChangedForTest(MeetingPresenceStateChangedEventArgs args)
    {
        PresenceChanged?.Invoke(this, args);
    }

    /// <summary>Letzter gesehener Snapshot (null vor erstem Tick).</summary>
    public MeetingPresenceSnapshot? Current { get; private set; }

    /// <summary>true, solange ein bestaetigtes aktives Meeting anliegt.</summary>
    public bool IsActive { get; private set; }

    /// <summary>true, solange der Loop laeuft.</summary>
    public bool IsRunning => _loopTask is { IsCompleted: false };

    // Produktions-Konstruktor
    public MeetingPresencePoller(IMeetingPresenceProbe probe, ILogger? logger = null)
        : this(probe,
               new PeriodicPresenceTicker(TimeSpan.FromSeconds(5)),
               new SystemPresenceClock(),
               logger)
    {
    }

    // Test-Konstruktor (mit allen Deps)
    public MeetingPresencePoller(
        IMeetingPresenceProbe probe,
        IPresenceTicker ticker,
        IPresenceClock clock,
        ILogger? logger = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger;
    }

    /// <summary>
    /// Startet den Polling-Loop. Liefert sofort zurueck, sobald der Loop
    /// eingerichtet ist; Exceptions aus dem Loop werden ueber das
    /// <see cref="_loopTask"/>-Field sichtbar (typischerweise via
    /// <see cref="StopAsync"/> oder bei naechstem <see cref="StartAsync"/>).
    /// </summary>
    public Task StartAsync(TeamsConfig cfg, CancellationToken ct)
    {
        if (cfg is null) throw new ArgumentNullException(nameof(cfg));
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(MeetingPresencePoller));
        if (IsRunning) throw new InvalidOperationException("Poller laeuft bereits.");

        _cfg = cfg;
        _pendingStartAt = null;
        IsActive = false;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stoppt den Polling-Loop und wartet auf sein Ende (max 2 s).
    /// Idempotent: Aufruf ohne laufenden Loop ist ein No-op.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        var cts = _cts;
        var task = _loopTask;
        if (cts is null || task is null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { return; }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* erwartet */ }
        catch (TimeoutException) { _logger?.Warning("MeetingPresencePoller: Stop-Timeout"); }
    }

    private async Task RunLoopAsync(CancellationToken stoppingCt)
    {
        try
        {
            while (await _ticker.WaitForNextTickAsync(stoppingCt).ConfigureAwait(false))
            {
                stoppingCt.ThrowIfCancellationRequested();

                MeetingPresenceSnapshot snap;
                try
                {
                    snap = await _probe.GetSnapshotAsync(_cfg!, stoppingCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Warning(ex, "MeetingPresencePoller: probe fehlgeschlagen, behalte letzten State");
                    continue;
                }

                Current = snap;
                Evaluate(snap);

                // Test-Hook: nach Evaluate, damit Tests synchronisieren koennen.
                // await (nicht nur Invoke) — falls Hook async ist.
                var hook = _onSnapshotEvaluated;
                if (hook is not null)
                {
                    try
                    {
                        await hook(snap).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(ex, "MeetingPresencePoller: OnSnapshotEvaluated hat geworfen");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* erwartet beim Stop */ }
        catch (Exception ex)
        {
            _logger?.Error(ex, "MeetingPresencePoller: Loop beendet mit Fehler");
        }
        finally
        {
            IsActive = false;
        }
    }

    private void Evaluate(MeetingPresenceSnapshot snap)
    {
        if (_cfg is null) return;

        if (!snap.IsActive)
        {
            // inactive Tick: pending-Debounce verwerfen, oder Edge-Stop feuern
            if (_pendingStartAt is not null) _pendingStartAt = null;
            else if (IsActive) FireChanged(snap, false);
            return;
        }

        // active Tick
        if (!_cfg.AutoRecordMeetings) return;
        if (IsActive) return;

        if (_pendingStartAt is null)
        {
            // Erster aktiver Tick — Debounce starten
            _pendingStartAt = _clock.UtcNow;
            return;
        }

        if (_clock.UtcNow - _pendingStartAt.Value >= TimeSpan.FromSeconds(_cfg.MinMeetingDurationSeconds))
        {
            _pendingStartAt = null;
            IsActive = true;
            FireChanged(snap, true);
        }
    }

    private void FireChanged(MeetingPresenceSnapshot snap, bool isActive)
    {
        if (!isActive) IsActive = false;
        var args = new MeetingPresenceStateChangedEventArgs(
            IsActive: isActive,
            Topic: isActive ? snap.Topic : null,
            WindowTitle: isActive ? snap.WindowTitle : null,
            ChatIdShort: isActive ? snap.ChatIdShort : null,
            DetectedAt: _clock.UtcNow);
        _logger?.Information(
            "MeetingPresencePoller: PresenceChanged isActive={IsActive} topic={Topic} chatIdShort={ChatIdShort}",
            isActive, args.Topic, args.ChatIdShort);
        try
        {
            PresenceChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "MeetingPresencePoller: Subscriber hat geworfen");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (IsRunning) await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
            _cfg = null;
        }
    }
}
