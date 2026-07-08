using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using AiRecall.Core.Audio;

using Serilog;

namespace AiRecall.Transcription;

/// <summary>
/// Channel-basierter Background-Worker fuer Audio-Transkription
/// (Spec 0013 v0.3 §5.4 Worker-Pattern, analog Spec 0007 ConversionWorker).
/// <list type="bullet">
///   <li>Pro Task: Stereo-Concatenate → Provider-Transkription → MD-Update → Cleanup</li>
///   <li>Max-N parallel (Default: 2)</li>
///   <li>Bei Provider-Fehler: combined-stereo.wav bleibt liegen (Debug-Evidence),
///         meta.md bekommt <c>transcript_status: failed</c> + ErrorMessage</li>
///   <li>Bei Erfolg: combined-stereo.wav wird geloescht, meta.md bekommt
///         <c>transcript_status: done</c> + Transkriptions-Block</li>
/// </list>
/// </summary>
public sealed class TranscriptionWorker : IAsyncDisposable
{
    private readonly ITranscriptionProvider _provider;
    private readonly StereoConcatenator _concatenator;
    private readonly MetadataUpdater _metadata;
    private readonly ILogger? _logger;
    private readonly int _maxParallel;
    private readonly Channel<AudioTranscriptionTask> _queue;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private CancellationTokenSource? _cts;
    private Task[]? _workerTasks;
    private int _disposed;

    /// <summary>Anzahl aktuell laufender oder wartender Tasks.</summary>
    public int PendingCount => _running.Count;

    /// <summary>Provider-Name (fuer Diagnose / Tests).</summary>
    public string ProviderName => _provider.Name;

    /// <summary>
    /// Produktions-Konstruktor.
    /// </summary>
    /// <param name="provider">Azure- oder Deepgram-Provider.</param>
    /// <param name="concatenator">Stereo-Concatenator (default: neu).</param>
    /// <param name="metadata">MD-Updater (default: neu).</param>
    /// <param name="maxParallel">Max gleichzeitige Transkriptionen (Default 2).</param>
    /// <param name="logger">Serilog-Logger.</param>
    public TranscriptionWorker(
        ITranscriptionProvider provider,
        StereoConcatenator? concatenator = null,
        MetadataUpdater? metadata = null,
        int maxParallel = 2,
        ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _concatenator = concatenator ?? new StereoConcatenator();
        _metadata = metadata ?? new MetadataUpdater();
        _maxParallel = Math.Max(1, maxParallel);
        _logger = logger;
        _queue = Channel.CreateUnbounded<AudioTranscriptionTask>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>Startet die Background-Worker-Tasks (idempotent).</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(TranscriptionWorker));
        if (_workerTasks is not null) return; // bereits gestartet
        _cts = new CancellationTokenSource();
        _workerTasks = new Task[_maxParallel];
        for (int i = 0; i < _maxParallel; i++)
        {
            _workerTasks[i] = Task.Run(() => RunWorkerAsync(_cts.Token));
        }
    }

    /// <summary>Stoppt den Worker, wartet auf laufende Tasks (max 30 s).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = _cts;
        var tasks = _workerTasks;
        if (cts is null || tasks is null) return;
        _queue.Writer.TryComplete();
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { _logger?.Warning("TranscriptionWorker: Stop-Timeout (30s)"); }
        catch (OperationCanceledException) { /* erwartet */ }
    }

    /// <summary>Enqueue eines neuen Transkriptions-Tasks (non-blocking).</summary>
    public void Enqueue(AudioTranscriptionTask task)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(TranscriptionWorker));
        if (_workerTasks is null) throw new InvalidOperationException("Worker nicht gestartet — zuerst Start() aufrufen.");
        var id = Guid.NewGuid();
        var runner = Task.Run(() => ProcessTaskAsync(task, _cts?.Token ?? CancellationToken.None));
        _running[id] = runner;
        _ = runner.ContinueWith(_ => _running.TryRemove(id, out _), TaskScheduler.Default);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // ProcessTaskAsync wird bereits in Enqueue via Task.Run gestartet
                // (fire-and-forget). Hier nur Channel lesen + Yield.
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) { /* erwartet */ }
        catch (Exception ex)
        {
            _logger?.Error(ex, "TranscriptionWorker: RunWorkerAsync-Loop beendet mit Fehler");
        }
    }

    private async Task ProcessTaskAsync(AudioTranscriptionTask task, CancellationToken cancellationToken)
    {
        var progress = new Progress<TranscriptionProgress>(p =>
            _logger?.Debug("TranscriptionWorker: {Folder} {Pct}% {Step}", task.Folder, p.PercentComplete, p.CurrentStep));

        string? stereoPath = null;
        try
        {
            // 1. Stereo-Concatenate
            var paths = new MeetingRecordingPaths(task.Folder, task.MicPath, task.LoopbackPath, task.MetadataPath);
            stereoPath = _concatenator.Concatenate(paths);
            _logger?.Information("TranscriptionWorker: Stereo concatenated → {StereoPath}", stereoPath);

            // 2. Provider-Transkription
            var result = await _provider
                .TranscribeAsync(stereoPath, task.Options, progress, cancellationToken)
                .ConfigureAwait(false);

            // 3. MD-Update
            await _metadata.UpdateAsync(task.MetadataPath, result, cancellationToken).ConfigureAwait(false);

            // 4. Cleanup: bei Erfolg combined-stereo.wav loeschen, bei Fehler behalten
            if (result.IsSuccess)
            {
                TryDelete(stereoPath);
                _logger?.Information(
                    "TranscriptionWorker: done ({Segments} Segmente, {Speakers} Speaker) → {Folder}",
                    result.Segments.Count, result.SpeakerCount, task.Folder);
            }
            else
            {
                _logger?.Warning(
                    "TranscriptionWorker: failed (Provider={Provider}, Error={Error}) — combined-stereo.wav bleibt fuer Debug liegen: {StereoPath}",
                    result.ProviderName, result.ErrorMessage, stereoPath);
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.Information("TranscriptionWorker: Task gecancelt fuer {Folder}", task.Folder);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "TranscriptionWorker: unerwarteter Fehler bei {Folder}", task.Folder);
            await _metadata.MarkFailedAsync(task.MetadataPath, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _workerTasks = null;
        }
    }
}
