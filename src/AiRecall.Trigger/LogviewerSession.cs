using System.Collections.Concurrent;
using Serilog.Events;

namespace AiRecall.Trigger;

/// <summary>
/// In-memory buffer + filter for the live logviewer (Spec 0008 v0.2).
/// Subscribes to an <see cref="InMemoryLogSink"/> on construction,
/// retains events in a bounded list, and exposes filtered snapshots.
///
/// Pure-logic, no WinForms dependency — unit-testable in isolation.
/// </summary>
public sealed class LogviewerSession : IDisposable
{
    private readonly InMemoryLogSink _sink;
    private readonly LinkedList<LogEventEntry> _buffer = new();
    private readonly object _lock = new();
    private bool _disposed;

    public int Capacity { get; }
    public LogFilter Filter { get; }
    public bool IsPaused { get; set; }

    public event EventHandler<LogEventEntry>? EventAppended;
    public event EventHandler? Cleared;

    public LogviewerSession(InMemoryLogSink sink, int capacity = 10_000, LogFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _sink = sink;
        Capacity = capacity;
        Filter = filter ?? new LogFilter();

        _sink.EventEmitted += OnSinkEventEmitted;
    }

    public int BufferCount
    {
        get { lock (_lock) return _buffer.Count; }
    }

    /// <summary>Returns a snapshot of all events currently in the buffer (oldest first).</summary>
    public IReadOnlyList<LogEventEntry> Snapshot()
    {
        lock (_lock)
        {
            return _buffer.ToArray();
        }
    }

    /// <summary>Returns a snapshot of events that pass the current filter (oldest first).</summary>
    public IReadOnlyList<LogEventEntry> FilteredSnapshot()
    {
        lock (_lock)
        {
            return _buffer.Where(Filter.Matches).ToArray();
        }
    }

    public void ClearBuffer()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private void OnSinkEventEmitted(object? sender, LogEventEntry entry)
    {
        // Disposed-Check + Buffer-Mutation UNTER demselben Lock, um Race
        // mit Dispose() zu vermeiden (Bug-Bash 2026-07-05 I-3).
        lock (_lock)
        {
            if (_disposed) return;
            _buffer.AddLast(entry);
            while (_buffer.Count > Capacity)
            {
                _buffer.RemoveFirst();
            }
        }

        EventAppended?.Invoke(this, entry);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sink.EventEmitted -= OnSinkEventEmitted;
    }
}