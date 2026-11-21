using System.Collections.Concurrent;

using AiRecall.AppReader.Teams;
using AiRecall.Core.Audio;
using AiRecall.Core.Configuration;
using AiRecall.Transcription;

using Serilog;

namespace AiRecall.Trigger;

/// <summary>Context for a recording started by the Teams presence poller.</summary>
public sealed record MeetingRecordingContext(
    string Topic,
    string ChatIdShort,
    string? WindowTitle,
    DateTimeOffset DetectedAt);

/// <summary>Starts and finalizes Teams and manual recordings, then queues transcription.</summary>
public sealed class MeetingTrigger : IDisposable, IAsyncDisposable, IRecordingControl
{
    private readonly MeetingPresencePoller _poller;
    private readonly TranscriptionWorker? _worker;
    private readonly bool _ownsWorker;
    private readonly Func<MeetingRecordingContext, RecordingSession> _recorderFactory;
    private readonly Func<RecordingSession>? _manualRecorderFactory;
    private readonly TranscriptionOptions _defaultOptions;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ActiveRecording> _active = new(StringComparer.Ordinal);
    private readonly object _recordingsLock = new();
    private readonly List<Task> _finalizing = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _subscribed;
    private bool _acceptManual = true;
    private bool _disposed;

    public int ActiveCount => _active.Count;
    internal bool IsPolling => _poller.IsRunning;
    public bool IsRecording => !_active.IsEmpty;

    public event EventHandler<MeetingTranscriptEnqueuedEventArgs>? TranscriptEnqueued;
    public event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;

    public MeetingTrigger(
        MeetingPresencePoller poller,
        TranscriptionWorker? worker,
        Func<MeetingRecordingContext, RecordingSession> recorderFactory,
        TranscriptionOptions defaultOptions,
        ILogger? logger = null,
        Func<RecordingSession>? manualRecorderFactory = null,
        bool ownsWorker = false)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _worker = worker;
        _ownsWorker = ownsWorker;
        _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
        _defaultOptions = defaultOptions;
        _logger = logger;
        _manualRecorderFactory = manualRecorderFactory;
    }

    public void Start() => Start(new TeamsConfig());

    public void Start(TeamsConfig teamsConfig, bool enablePolling = true)
    {
        _lifecycle.Wait();
        try
        {
            lock (_recordingsLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_subscribed) return;
                _acceptManual = true;
                if (!enablePolling) return;
                _poller.PresenceChanged += OnPresenceChanged;
                _subscribed = true;
                try { _poller.StartAsync(teamsConfig, CancellationToken.None).GetAwaiter().GetResult(); }
                catch
                {
                    _poller.PresenceChanged -= OnPresenceChanged;
                    _subscribed = false;
                    _acceptManual = false;
                    throw;
                }
            }
            _logger?.Information("MeetingTrigger started");
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Stops polling and waits for every capture and WAV finalization.</summary>
    public void Stop() => ShutdownAsync(dispose: false).GetAwaiter().GetResult();

    private void OnPresenceChanged(object? sender, MeetingPresenceStateChangedEventArgs e)
    {
        lock (_recordingsLock)
        {
            // An invocation already queued when the handler was removed must
            // not start a new recording during shutdown.
            if (!_subscribed) return;
            if (!e.IsActive)
            {
                if (e.ChatIdShort is { } key && _active.TryGetValue(key, out var active)
                    && active.Source == RecordingSource.MeetingAuto)
                    QueueFinalization(key, active);
                return;
            }

            var meetingKey = e.ChatIdShort ?? Guid.NewGuid().ToString("N")[..8];
            if (_active.ContainsKey(meetingKey) || _active.Values.Any(r => r.Source == RecordingSource.Manual)) return;

            var ctx = new MeetingRecordingContext(e.Topic ?? "(unknown topic)", meetingKey, e.WindowTitle, e.DetectedAt);
            try
            {
                var session = _recorderFactory(ctx);
                session.Start();
                _active[meetingKey] = new ActiveRecording(session, ctx, RecordingSource.MeetingAuto);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "MeetingTrigger: Start fehlgeschlagen fuer {ChatIdShort}", meetingKey);
                return;
            }
            _logger?.Information("MeetingTrigger: recording started (chatId={ChatIdShort})", meetingKey);
            NotifyRecordingState(new(true, RecordingSource.MeetingAuto, meetingKey, ctx.Topic, e.DetectedAt));
        }
    }

    /// <inheritdoc />
    public Task<string> StartManualAsync(CancellationToken ct)
    {
        lock (_recordingsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_manualRecorderFactory is null)
                throw new NotSupportedException("Manual recording requires a manual recorder factory.");
            if (!_acceptManual)
                throw new InvalidOperationException("MeetingTrigger is stopped.");
            if (IsRecording || _finalizing.Any(task => !task.IsCompleted))
                throw new InvalidOperationException("Eine Audio-Aufnahme laeuft bereits (Single-Active-Recording-Constraint). Bitte zuerst StopAsync() aufrufen.");
            ct.ThrowIfCancellationRequested();

            var key = "manual-" + Guid.NewGuid().ToString("N");
            var session = _manualRecorderFactory();
            session.Start();
            var startedAt = DateTimeOffset.UtcNow;
            var ctx = new MeetingRecordingContext("(manual recording)", key, null, startedAt);
            _active[key] = new ActiveRecording(session, ctx, RecordingSource.Manual);
            _logger?.Information("MeetingTrigger: manual recording started (key={Key})", key);
            NotifyRecordingState(new(true, RecordingSource.Manual, key, null, startedAt));
            return Task.FromResult(key);
        }
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        lock (_recordingsLock)
        {
            foreach (var (key, active) in _active.ToArray())
                QueueFinalization(key, active);
            _finalizing.RemoveAll(task => task.IsCompleted);
            return Task.WhenAll(_finalizing);
        }
    }

    // Caller holds _recordingsLock. Removing the active recording and tracking
    // its task must be atomic so shutdown also joins a concurrent Teams stop.
    private void QueueFinalization(string key, ActiveRecording active)
    {
        _active.TryRemove(key, out _);
        _finalizing.RemoveAll(task => task.IsCompleted);
        _finalizing.Add(Task.Run(() => FinalizeStopAsync(key, active)));
    }

    private async Task FinalizeStopAsync(string key, ActiveRecording active)
    {
        try
        {
            var paths = await active.Session.StopAsync().ConfigureAwait(false);
            var task = new AudioTranscriptionTask(paths.Folder, paths.MicPath, paths.LoopbackPath,
                paths.MetadataPath, _defaultOptions, DateTimeOffset.UtcNow);
            if (_worker?.TryEnqueue(task) == true)
            {
                _logger?.Information("MeetingTrigger: recording stopped ({Source}) + transcription enqueued ({Topic} → {Folder})",
                    active.Source, active.Context.Topic, paths.Folder);
                TranscriptEnqueued?.Invoke(this, new(key, active.Context.Topic, paths.Folder));
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "MeetingTrigger: Stop fehlgeschlagen fuer {Key}", key);
        }
        finally
        {
            await active.Session.DisposeAsync().ConfigureAwait(false);
            NotifyRecordingState(new(IsRecording, active.Source, key, active.Context.Topic, DateTimeOffset.UtcNow));
        }
    }

    private void NotifyRecordingState(RecordingStateChangedEventArgs args)
    {
        try { RecordingStateChanged?.Invoke(this, args); }
        catch (Exception ex) { _logger?.Error(ex, "MeetingTrigger: recording state subscriber failed"); }
    }

    private async Task ShutdownAsync(bool dispose)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_recordingsLock)
            {
                if (_disposed) return;
                _disposed = dispose;
                _acceptManual = false;
                if (_subscribed) _poller.PresenceChanged -= OnPresenceChanged;
                _subscribed = false;
            }

            // Start draining immediately, even while the poller is cancelling.
            var drain = StopAsync();
            try { await _poller.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                try { await drain.ConfigureAwait(false); }
                finally
                {
                    if (dispose)
                    {
                        try { await _poller.DisposeAsync().ConfigureAwait(false); }
                        finally
                        {
                            if (_ownsWorker && _worker is not null)
                            {
                                try { _worker.Stop(); }
                                finally { _worker.Dispose(); }
                            }
                        }
                    }
                }
            }
            _logger?.Information("MeetingTrigger stopped");
        }
        finally { _lifecycle.Release(); }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync(dispose: true));
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed record ActiveRecording(RecordingSession Session, MeetingRecordingContext Context, RecordingSource Source);
}

/// <summary>Event data for <see cref="MeetingTrigger.TranscriptEnqueued"/>.</summary>
public sealed record MeetingTranscriptEnqueuedEventArgs(string ChatIdShort, string Topic, string Folder);
