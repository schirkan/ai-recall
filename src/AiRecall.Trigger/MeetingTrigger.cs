using System;
using System.Collections.Concurrent;
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
/// </summary>
public sealed class MeetingTrigger : IAsyncDisposable
{
    private readonly MeetingPresencePoller _poller;
    private readonly TranscriptionWorker _worker;
    private readonly Func<MeetingRecordingContext, RecordingSession> _recorderFactory;
    private readonly TranscriptionOptions _defaultOptions;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ActiveRecording> _active = new(StringComparer.Ordinal);
    private bool _subscribed;
    private bool _disposed;

    /// <summary>Anzahl aktuell laufender Aufnahmen (fuer Diagnose).</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// Wird gefeuert, wenn ein Meeting beendet und ein Transkriptions-Task
    /// erfolgreich enqueued wurde.
    /// </summary>
    public event EventHandler<MeetingTranscriptEnqueuedEventArgs>? TranscriptEnqueued;

    public MeetingTrigger(
        MeetingPresencePoller poller,
        TranscriptionWorker worker,
        Func<MeetingRecordingContext, RecordingSession> recorderFactory,
        TranscriptionOptions defaultOptions,
        ILogger? logger = null)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
        _defaultOptions = defaultOptions;
        _logger = logger;
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
                StartRecording(e);
            }
            else
            {
                await StopRecordingAsync(e).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "MeetingTrigger: unerwarteter Fehler bei {IsActive}", e.IsActive);
        }
    }

    private void StartRecording(MeetingPresenceStateChangedEventArgs e)
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
            _active[key] = new ActiveRecording(session, ctx, e.DetectedAt);
            _logger?.Information("MeetingTrigger: recording started für {Topic} (chatId={ChatIdShort})",
                ctx.Topic, ctx.ChatIdShort);
        }
        catch (Exception ex)
        {
            _active.TryRemove(key, out _);
            _logger?.Error(ex, "MeetingTrigger: Start fehlgeschlagen für {ChatIdShort}", key);
        }
    }

    private async Task StopRecordingAsync(MeetingPresenceStateChangedEventArgs e)
    {
        var key = e.ChatIdShort ?? string.Empty;
        if (string.IsNullOrEmpty(key)) return;
        if (!_active.TryGetValue(key, out var active) || active is null) return;
        if (!_active.TryRemove(key, out _)) return;

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
                    "MeetingTrigger: recording stopped + transcription enqueued ({Topic} → {Folder})",
                    active.Context.Topic, paths.Folder);
                TranscriptEnqueued?.Invoke(this, new MeetingTranscriptEnqueuedEventArgs(
                    ChatIdShort: key, Topic: active.Context.Topic, Folder: paths.Folder));
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "MeetingTrigger: Stop fehlgeschlagen für {ChatIdShort}", key);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Stop();
        return ValueTask.CompletedTask;
    }

    private sealed record ActiveRecording(
        RecordingSession Session,
        MeetingRecordingContext Context,
        DateTimeOffset StartedAt);
}

/// <summary>
/// EventArgs fuer <see cref="MeetingTrigger.TranscriptEnqueued"/>.
/// </summary>
public sealed record MeetingTranscriptEnqueuedEventArgs(
    string ChatIdShort,
    string Topic,
    string Folder);
