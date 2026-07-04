using Serilog.Core;
using Serilog.Events;

namespace AiRecall.Trigger;

/// <summary>
/// A single, immutable log entry kept by <see cref="InMemoryLogSink"/>. We
/// flatten a <see cref="LogEvent"/> into a typed record so subscribers (e.g.
/// the live logviewer window) don't have to depend on Serilog internals.
/// </summary>
public sealed record LogEventEntry(
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Logger,
    string Message,
    string? Exception)
{
    /// <summary>Builds a <see cref="LogEventEntry"/> from a <see cref="LogEvent"/>.</summary>
    public static LogEventEntry FromLogEvent(LogEvent e)
    {
        // SourceContext property is set by Serilog when using ForContext/LoggerName;
        // the standard "Logger" property is a destructured string of the type name.
        var logger = string.Empty;
        if (e.Properties.TryGetValue("SourceContext", out var sc) && sc is ScalarValue { Value: not null })
        {
            logger = sc.ToString().Trim('"');
        }
        else
        {
            logger = "AiRecall"; // fallback for events without SourceContext
        }

        return new LogEventEntry(
            Timestamp: e.Timestamp,
            Level: e.Level,
            Logger: logger,
            Message: e.RenderMessage(),
            Exception: e.Exception?.ToString());
    }
}

/// <summary>
/// In-process Serilog sink that retains the last N log events in a
/// ring buffer (FIFO) and notifies subscribers on each new event. Used
/// by the MVP2 Tray-EXE to feed the live logviewer window without
/// file I/O or MMF (Spec 0008 v0.2).
///
/// Thread-safety: <see cref="Emit"/>, <see cref="Snapshot"/>, and
/// <see cref="Clear"/> are safe to call from any thread. Subscriber
/// notifications happen on the emitting thread.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink, IDisposable
{
    private readonly object _lock = new();
    private readonly LinkedList<LogEventEntry> _events = new();
    private readonly int _capacity;
    private bool _disposed;

    /// <summary>Creates a sink with the given ring-buffer capacity (default 10.000).</summary>
    public InMemoryLogSink(int capacity = 10_000)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be > 0");
        _capacity = capacity;
    }

    /// <summary>Ring-buffer capacity (max events retained).</summary>
    public int Capacity => _capacity;

    /// <summary>Current number of events in the buffer.</summary>
    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    /// <summary>
    /// Raised on every emitted event. Subscribers must not throw — exceptions
    /// propagate to the Serilog pipeline and can disrupt logging.
    /// </summary>
    public event EventHandler<LogEventEntry>? EventEmitted;

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (_disposed) return;

        var entry = LogEventEntry.FromLogEvent(logEvent);

        lock (_lock)
        {
            _events.AddLast(entry);
            while (_events.Count > _capacity)
            {
                _events.RemoveFirst();
            }
        }

        EventEmitted?.Invoke(this, entry);
    }

    /// <summary>Returns a snapshot of all currently retained events (oldest first).</summary>
    public IReadOnlyList<LogEventEntry> Snapshot()
    {
        lock (_lock)
        {
            return _events.ToArray();
        }
    }

    /// <summary>Empties the ring buffer. Does not affect subscribers.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EventEmitted = null;
    }
}