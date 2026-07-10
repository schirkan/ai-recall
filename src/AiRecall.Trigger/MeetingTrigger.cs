using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Audio;
using AiRecall.Core.Configuration;
using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Trigger;

/// <summary>
/// Kontext fuer eine neue Recording-Session. Wird vom
/// <see cref="MeetingTrigger"/> beim <c>PresenceChanged(true)</c>-Event erzeugt
/// und an die Recording-Factory weitergegeben.
/// </summary>
public sealed record MeetingRecordingContext(
    string Topic,
    string ChatIdShort,
    string? WindowTitle,
    DateTimeOffset DetectedAt);

/// <summary>
/// Trigger-Wiring: <see cref="MeetingPresencePoller.PresenceChanged"/> →
/// <see cref="RecordingSession"/> starten/stoppen → <see cref="TranscriptionWorker.EnqueueAsync"/>
/// (Spec 0013 v0.3 §1 + §5.4 Auto-Recording-Trigger).
/// <para>
/// Production-Komposition:
/// <list type="number">
///   <item><see cref="Start"/> subscribt das <c>PresenceChanged</c>-Event des Pollers.</item>
///   <item>Bei <c>IsActive=true</c>: erstellt via <paramref name="recorderFactory"/>
///         eine <see cref="RecordingSession"/>, ruft <see cref="RecordingSession.Start"/>.</item>
///   <item>Bei <c>IsActive=false</c>: findet die aktive Session, ruft
///         <see cref="RecordingSession.StopAsync"/>, enqueued den resultierenden
///         Task im <see cref="TranscriptionWorker"/>.</item>
/// </list>
/// </para>
/// <para>
/// Erweiterung Spec 0014 Iter. 1: implementiert zusaetzlich
/// <see cref="IRecordingControl"/> fuer manuelle Audio-Aufnahmen via
/// <see cref="StartManualAsync"/>. Dafuer wird optional eine zweite Factory
/// (<c>manualRecorderFactory</c>) im Konstruktor injiziert.
/// </para>
/// </summary>
public sealed class MeetingTrigger : IDisposable, IAsyncDisposable, IRecordingControl
{
    private readonly MeetingPresencePoller _poller;
    private readonly TranscriptionWorker _worker;
    private readonly Func<MeetingRecordingContext, RecordingSession> _recorderFactory;
    private readonly Func<RecordingSession>? _manualRecorderFactory;
    private readonly TranscriptionOptions _defaultOptions;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ActiveRecording> _active = new(StringComparer.Ordinal);
    private bool _subscribed;
    private bool _disposed;

    /// <summary>Anzahl aktuell laufender Aufnahmen (fuer Diagnose).</summary>
    public int ActiveCount => _active.Count;

    /// <inheritdoc />
    public bool IsRecording => !_active.IsEmpty;

    /// <summary>
    /// Wird gefeuert, wenn ein Meeting beendet und ein Transkriptions-Task
    /// erfolgreich enqueued wurde.
    /// </summary>
    public event EventHandler<MeetingTranscriptEnqueuedEventArgs>? TranscriptEnqueued;

    /// <inheritdoc />
    public event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;

    public MeetingTrigger(
        MeetingPresencePoller poller,
        TranscriptionWorker worker,
        Func<MeetingRecordingContext, RecordingSession> recorderFactory,
        TranscriptionOptions defaultOptions,
        ILogger? logger = null,
        Func<RecordingSession>? manualRecorderFactory = null)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
        _defaultOptions = defaultOptions;
        _logger = logger;
        _manualRecorderFactory = manualRecorderFactory;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MeetingTrigger));
        if (_subscribed) return;
        _poller.PresenceChanged += OnPresenceChanged;
        _subscribed = true;
        _logger?.Information("MeetingTrigger started");
    }

    public void Stop()
    {
        if (!_subscribed) return;
        _poller.PresenceChanged -= OnPresenceChanged;
        _subscribed = false;
        _logger?.Information("MeetingTrigger stopped");
    }

    private async void OnPresenceChanged(object? sender, MeetingPresenceStateChangedEventArgs e)
    {
        try
        {
            if (e.IsActive)
            {
                StartRecording(e, RecordingSource.MeetingAuto);
            }
            else
            {
                await StopRecordingAsync(e.ChatIdShort, RecordingSource.MeetingAuto).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "MeetingTrigger: unerwarteter Fehler bei {IsActive}", e.IsActive);
        }
    }

    private void StartRecording(MeetingPresenceStateChangedEventArgs e, RecordingSource source)
    {
        var key = e.ChatIdShort ?? Guid.NewGuid().ToString("N")[..8];
        if (!_active.TryAdd(key, null!)) return; // bereits aktiv

        try
        {
            var ctx = new MeetingRecordingContext(
                Topic: e.Topic ?? "(unknown topic)",
                ChatIdShort: key,
                WindowTitle: e.WindowTitle,
                DetectedAt: e.DetectedAt);
            var session = _recorderFactory(ctx);
            session.Start();
            _active[key] = new ActiveRecording(session, ctx, e.DetectedAt, source);
            _logger?.Information("MeetingTrigger: recording started ({Source}) fuer {Topic} (chatId={ChatIdShort})",
                source, ctx.Topic, ctx.ChatIdShort);
            RecordingStateChanged?.Invoke(this, new RecordingStateChangedEventArgs(
                IsRecording: true,
                Source: source,
                Key: key,
                Topic: ctx.Topic,
                At: e.DetectedAt));
        }
        catch (Exception ex)
        {
            _active.TryRemove(key, out _);
            _logger?.Error(ex, "MeetingTrigger: Start fehlgeschlagen fuer {ChatIdShort}", key);
        }
    }

    /// <inheritdoc />
    public Task<string> StartManualAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MeetingTrigger));
        if (_manualRecorderFactory is null)
        {
            throw new NotSupportedException(
                "MeetingTrigger wurde ohne manualRecorderFactory konstruiert. " +
                "Fuer manuelle Aufnahmen muss die Factory im Konstruktor injiziert werden (Spec 0014 Iter. 1).");
        }
        // Single-Active-Recording-Constraint (Martin 2026-07-10 19:11):
        // maximal 1 aktive Aufnahme. Wenn schon eine laeuft (egal ob Auto
        // oder Manual), muss der Aufrufer erst StopAsync() aufrufen.
        if (IsRecording)
        {
            throw new InvalidOperationException(
                "Eine Audio-Aufnahme laeuft bereits (Single-Active-Recording-Constraint, " +
                "Spec 0014 v0.1 Update 2). Bitte zuerst StopAsync() aufrufen.");
        }
        ct.ThrowIfCancellationRequested();

        // Eindeutiger Key: 32 hex aus Guid + Counter (Spec 0014 Iter. 1b).
        // Dictionary bleibt intern trotzdem Dictionary (auch wenn max. 1
        // Eintrag), damit das Pattern zu Auto-Recording symmetrisch bleibt.
        var key = "manual-" + Guid.NewGuid().ToString("N");
        if (!_active.TryAdd(key, null!))
        {
            // Kollision ist extrem unwahrscheinlich (128 bit), aber defensiv.
            _logger?.Warning("MeetingTrigger: manual-key collision fuer {Key}, retry", key);
            return StartManualAsync(ct);
        }

        try
        {
            var session = _manualRecorderFactory();
            session.Start();
            var startedAt = DateTimeOffset.UtcNow;
            var ctx = new MeetingRecordingContext(
                Topic: "(manual recording)",
                ChatIdShort: key,
                WindowTitle: null,
                DetectedAt: startedAt);
            _active[key] = new ActiveRecording(session, ctx, startedAt, RecordingSource.Manual);
            _logger?.Information("MeetingTrigger: manual recording started (key={Key})", key);
            RecordingStateChanged?.Invoke(this, new RecordingStateChangedEventArgs(
                IsRecording: true,
                Source: RecordingSource.Manual,
                Key: key,
                Topic: null,
                At: startedAt));
            return Task.FromResult(key);
        }
        catch (Exception ex)
        {
            _active.TryRemove(key, out _);
            _logger?.Error(ex, "MeetingTrigger: manual Start fehlgeschlagen (key={Key})", key);
            throw;
        }
    }

    private async Task StopRecordingAsync(string? key, RecordingSource source)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!_active.TryGetValue(key, out var active) || active is null) return;
        if (active.Source != source) return; // fremde Source, kein Eingriff
        if (!_active.TryRemove(key, out _)) return;

        await FinalizeStopAsync(key, active).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_disposed) return;
        if (_active.IsEmpty) return;

        // Snapshot der Keys vor dem Stop, damit waehrend des async-Await
        // keine neuen Sessions ueber den Snapshot hinaus gestoppt werden.
        // Single-Active: normalerweise max. 1 Eintrag, aber defensiv fuer
        // Iter. 3+ Multi-Session-Aktivierungen.
        var keys = new List<string>(_active.Keys);
        foreach (var k in keys)
        {
            if (_active.TryRemove(k, out var active) && active is not null)
            {
                await FinalizeStopAsync(k, active).ConfigureAwait(false);
            }
        }
    }

    private async Task FinalizeStopAsync(string key, ActiveRecording active)
    {
        try
        {
            var paths = await active.Session.StopAsync().ConfigureAwait(false);
            var task = new AudioTranscriptionTask(
                Folder: paths.Folder,
                MicPath: paths.MicPath,
                LoopbackPath: paths.LoopbackPath,
                MetadataPath: paths.MetadataPath,
                Options: _defaultOptions,
                EnqueuedAt: DateTimeOffset.UtcNow);
            var enqueued = _worker.TryEnqueue(task);
            if (enqueued)
            {
                _logger?.Information(
                    "MeetingTrigger: recording stopped ({Source}) + transcription enqueued ({Topic} → {Folder})",
                    active.Source, active.Context.Topic, paths.Folder);
                TranscriptEnqueued?.Invoke(this, new MeetingTranscriptEnqueuedEventArgs(
                    ChatIdShort: key, Topic: active.Context.Topic, Folder: paths.Folder));
            }
            RecordingStateChanged?.Invoke(this, new RecordingStateChangedEventArgs(
                IsRecording: !_active.IsEmpty,
                Source: active.Source,
                Key: key,
                Topic: active.Context.Topic,
                At: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "MeetingTrigger: Stop fehlgeschlagen fuer {Key}", key);
            // Auch im Fehlerfall ein RecordingStateChanged feuern, damit
            // Konsumenten (Tray-Icon) nicht in einem haengenden "IsRecording=true"
            // State bleiben.
            RecordingStateChanged?.Invoke(this, new RecordingStateChangedEventArgs(
                IsRecording: !_active.IsEmpty,
                Source: active.Source,
                Key: key,
                Topic: active.Context.Topic,
                At: DateTimeOffset.UtcNow));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        // Auch manuelle + Auto-Aufnahmen sauber stoppen.
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sync-Dispose fuer Composition in <c>TriggerService</c> (analog
    /// <c>ConversionWorker</c>). Stop() loest das Poller-Event,
    /// <c>StopRecordingAsync</c> laeuft fire-and-forget im Hintergrund —
    /// darum reicht die IDiposable-Form fuer Service-Lifecycle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record ActiveRecording(
        RecordingSession Session,
        MeetingRecordingContext Context,
        DateTimeOffset StartedAt,
        RecordingSource Source);
}

/// <summary>
/// EventArgs fuer <see cref="MeetingTrigger.TranscriptEnqueued"/>.
/// </summary>
public sealed record MeetingTranscriptEnqueuedEventArgs(
    string ChatIdShort,
    string Topic,
    string Folder);