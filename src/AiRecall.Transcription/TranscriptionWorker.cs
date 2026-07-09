using System;
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
/// (Spec 0013 v0.3 §5.4 Worker-Pattern, analog Spec 0007 <c>ConversionWorker</c>).
/// <list type="bullet">
///   <li>Background-Task-Pool startet im Konstruktor (auto-start, max-N parallel)</item>
///   <li>Pro Task: Stereo-Concatenate → Provider-Transkription → MD-Update → Cleanup</item>
///   <li>Bei Provider-Fehler: combined-stereo.wav bleibt liegen (Debug-Evidence),
///         meta.md bekommt <c>transcript_status: failed</c> + ErrorMessage</item>
///   <item>Bei Erfolg: combined-stereo.wav wird geloescht, meta.md bekommt
///         <c>transcript_status: done</c> + Transkriptions-Block</item>
///   <item>Counter: <see cref="PendingCount"/>, <see cref="CompletedCount"/>, <see cref="FailedCount"/></item>
/// </list>
/// </summary>
public sealed class TranscriptionWorker : IDisposable
{
    private readonly ITranscriptionProvider _provider;
    private readonly StereoConcatenator _concatenator;
    private readonly MetadataUpdater _metadata;
    private readonly ILogger? _logger;
    private readonly int _maxParallel;
    private readonly Channel<AudioTranscriptionTask> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task[] _workerTasks;
    private bool _disposed;

    private int _pendingCount;
    private int _completedCount;
    private int _failedCount;

    /// <summary>Anzahl Captures in der Channel-Queue (noch nicht verarbeitet).</summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>Anzahl erfolgreich transkribierter Meetings.</summary>
    public int CompletedCount => Volatile.Read(ref _completedCount);

    /// <summary>Anzahl komplett fehlgeschlagener Transkriptionen.</summary>
    public int FailedCount => Volatile.Read(ref _failedCount);

    /// <summary>Provider-Name (fuer Diagnose / Tests).</summary>
    public string ProviderName => _provider.Name;

    /// <summary>
    /// Produktions-Konstruktor. Startet automatisch <c>maxParallel</c> Background-Worker.
    /// </summary>
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

        _channel = Channel.CreateUnbounded<AudioTranscriptionTask>(new UnboundedChannelOptions
        {
            SingleReader = false,  // Multi-Worker
            SingleWriter = false,
        });
        _cts = new CancellationTokenSource();
        _workerTasks = new Task[_maxParallel];
        for (int i = 0; i < _maxParallel; i++)
        {
            _workerTasks[i] = Task.Run(() => RunWorkerAsync(_cts.Token));
        }
        _logger?.Information("TranscriptionWorker started (maxParallel={MaxParallel}, provider={Provider})",
            _maxParallel, _provider.Name);
    }

    /// <summary>
    /// Enqueue eines Transkriptions-Tasks (fire-and-forget).
    /// </summary>
    public bool TryEnqueue(AudioTranscriptionTask task)
    {
        if (_disposed) return false;
        if (task is null) throw new ArgumentNullException(nameof(task));
        Interlocked.Increment(ref _pendingCount);
        return _channel.Writer.TryWrite(task);
    }

    /// <summary>Async-Variante von <see cref="TryEnqueue"/>.</summary>
    public ValueTask EnqueueAsync(AudioTranscriptionTask task, CancellationToken ct = default)
    {
        if (_disposed) return ValueTask.CompletedTask;
        if (task is null) throw new ArgumentNullException(nameof(task));
        Interlocked.Increment(ref _pendingCount);
        return _channel.Writer.WriteAsync(task, ct);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var task in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(task, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _completedCount);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "TranscriptionWorker: failed to process {Folder}", task.Folder);
                    // Counter NACH dem Metadata-Update incrementieren: Tests warten auf
                    // FailedCount == 1 + meta.md mit 'transcript_status: failed'; bei zu frühem
                    // Increment ist meta.md noch nicht geschrieben (Race mit MarkFailedAsync).
                    try
                    {
                        await _metadata.MarkFailedAsync(task.MetadataPath, ex.Message, ct).ConfigureAwait(false);
                    }
                    catch (Exception markEx)
                    {
                        _logger?.Warning(markEx, "TranscriptionWorker: MarkFailed fehlgeschlagen ({Meta})", task.MetadataPath);
                    }
                    Interlocked.Increment(ref _failedCount);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }
        catch (OperationCanceledException) { /* sauberer Stop */ }
        _logger?.Information("TranscriptionWorker worker-task stopped");
    }

    private async Task ProcessAsync(AudioTranscriptionTask task, CancellationToken cancellationToken)
    {
        var progress = new Progress<TranscriptionProgress>(p =>
            _logger?.Debug("TranscriptionWorker: {Folder} {Pct}% {Step}",
                task.Folder, p.PercentComplete, p.CurrentStep));

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

            // 4. Cleanup
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
                // Throw, damit der aeussere Catch als Failed gezaehlt wird
                throw new InvalidOperationException(
                    $"Provider-Transkription fehlgeschlagen: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.Information("TranscriptionWorker: Task gecancelt fuer {Folder}", task.Folder);
            throw;
        }
    }

    /// <summary>
    /// Recovery-Scan: durchsucht <paramref name="storageRoot"/> nach Meeting-Ordnern
    /// mit <c>transcript_status: pending</c> und enqueued sie. Fuer App-Start nach Crash.
    /// </summary>
    public int ScanForPendingTranscriptions(string storageRoot, TranscriptionOptions defaultOptions)
    {
        if (string.IsNullOrEmpty(storageRoot)) throw new ArgumentException("Pfad fehlt", nameof(storageRoot));
        if (!Directory.Exists(storageRoot)) return 0;

        int enqueued = 0;
        foreach (var metaPath in Directory.EnumerateFiles(storageRoot, "meta.md", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(metaPath);
                if (!content.Contains("transcript_status: pending")) continue;
                var folder = Path.GetDirectoryName(metaPath)!;
                var mic = Path.Combine(folder, "mic.wav");
                var loop = Path.Combine(folder, "loopback.wav");
                if (!File.Exists(mic) || !File.Exists(loop)) continue;
                if (TryEnqueue(new AudioTranscriptionTask(
                    Folder: folder,
                    MicPath: mic,
                    LoopbackPath: loop,
                    MetadataPath: metaPath,
                    Options: defaultOptions,
                    EnqueuedAt: DateTimeOffset.UtcNow)))
                {
                    enqueued++;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "TranscriptionWorker.ScanForPending: skip {Path}", metaPath);
            }
        }
        if (enqueued > 0)
        {
            _logger?.Information("TranscriptionWorker.ScanForPending: enqueued {Count} tasks aus {Root}", enqueued, storageRoot);
        }
        return enqueued;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>Stoppt den Worker sauber. Idempotent.</summary>
    public void Stop()
    {
        if (_disposed) return;
        _channel.Writer.TryComplete();
        try
        {
            Task.WhenAll(_workerTasks).Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException) { /* Tasks koennen gecancelt sein */ }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _logger?.Information("TranscriptionWorker stopped by user");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}
