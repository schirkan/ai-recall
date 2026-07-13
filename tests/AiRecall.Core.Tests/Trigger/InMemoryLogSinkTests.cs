using AiRecall.Trigger;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class InMemoryLogSinkTests
{
    [Fact]
    public void Ctor_ZeroCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryLogSink(0));
    }

    [Fact]
    public void Ctor_NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryLogSink(-1));
    }

    [Fact]
    public void New_BufferIsEmpty()
    {
        using var sink = new InMemoryLogSink();
        Assert.Equal(0, sink.Count);
        Assert.Equal(10_000, sink.Capacity);
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public void Emit_AddsEventToBuffer()
    {
        using var sink = new InMemoryLogSink();
        sink.Emit(MakeEvent(LogEventLevel.Information, "hello"));

        Assert.Equal(1, sink.Count);
        var snap = sink.Snapshot();
        Assert.Equal("hello", snap[0].Message);
        Assert.Equal(LogEventLevel.Information, snap[0].Level);
    }

    [Fact]
    public void Emit_NullLogEvent_Throws()
    {
        using var sink = new InMemoryLogSink();
        Assert.Throws<ArgumentNullException>(() => sink.Emit(null!));
    }

    [Fact]
    public void Emit_AboveCapacity_DropsOldest_Fifo()
    {
        using var sink = new InMemoryLogSink(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            sink.Emit(MakeEvent(LogEventLevel.Information, $"e{i}"));
        }

        Assert.Equal(3, sink.Count);
        var snap = sink.Snapshot();
        Assert.Equal("e3", snap[0].Message);
        Assert.Equal("e4", snap[1].Message);
        Assert.Equal("e5", snap[2].Message);
    }

    [Fact]
    public void Clear_RemovesAllEvents()
    {
        using var sink = new InMemoryLogSink();
        sink.Emit(MakeEvent(LogEventLevel.Information, "a"));
        sink.Emit(MakeEvent(LogEventLevel.Information, "b"));
        sink.Clear();
        Assert.Equal(0, sink.Count);
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public void EventEmitted_RaisedOnEmit()
    {
        using var sink = new InMemoryLogSink();
        var received = new List<LogEventEntry>();
        sink.EventEmitted += (_, e) => received.Add(e);

        sink.Emit(MakeEvent(LogEventLevel.Warning, "first"));
        sink.Emit(MakeEvent(LogEventLevel.Error, "second"));

        Assert.Equal(2, received.Count);
        Assert.Equal("first", received[0].Message);
        Assert.Equal("second", received[1].Message);
    }

    [Fact]
    public void EventEmitted_MultipleSubscribers_AllNotified()
    {
        using var sink = new InMemoryLogSink();
        var a = 0;
        var b = 0;
        sink.EventEmitted += (_, _) => a++;
        sink.EventEmitted += (_, _) => b++;

        sink.Emit(MakeEvent(LogEventLevel.Information, "x"));

        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void Dispose_DetachesSubscribers_NoMoreEvents()
    {
        var sink = new InMemoryLogSink();
        var received = 0;
        sink.EventEmitted += (_, _) => received++;
        sink.Emit(MakeEvent(LogEventLevel.Information, "before"));
        Assert.Equal(1, received);
        Assert.Equal(1, sink.Count);

        sink.Dispose();
        // Emit nach Dispose wird ignoriert (kein Event, kein Count-Increment)
        sink.Emit(MakeEvent(LogEventLevel.Information, "after"));
        Assert.Equal(1, received); // not incremented
        Assert.Equal(1, sink.Count); // "before" bleibt, "after" wird nicht addiert
    }

    [Fact]
    public void Emit_RendersMessageAndLoggerSourceContext()
    {
        using var sink = new InMemoryLogSink();
        // Build event with SourceContext property "MyApp.Service"
        var props = new List<LogEventProperty>
        {
            new("SourceContext", new ScalarValue("MyApp.Service"))
        };
        var evt = new LogEvent(
            DateTimeOffset.Parse("2026-07-04T22:00:00+02:00"),
            LogEventLevel.Information,
            exception: null,
            Parser.Parse("hello {Name}"),
            props);

        sink.Emit(evt);
        var snap = sink.Snapshot();
        Assert.Single(snap);
        Assert.Equal("MyApp.Service", snap[0].Logger);
        // RenderMessage() rendert das Template mit den Properties; "Name" hat keinen
        // Property-Value im props-Array, daher wird der Placeholder beibehalten
        Assert.Equal("hello {Name}", snap[0].Message);
        Assert.Equal(DateTimeOffset.Parse("2026-07-04T22:00:00+02:00"), snap[0].Timestamp);
    }

    [Fact]
    public void Emit_WithoutSourceContext_UsesFallbackLogger()
    {
        using var sink = new InMemoryLogSink();
        sink.Emit(MakeEvent(LogEventLevel.Debug, "msg"));
        var snap = sink.Snapshot();
        Assert.Equal("AiRecall", snap[0].Logger); // fallback
    }

    [Fact]
    public void Snapshot_IsImmutableCopy()
    {
        using var sink = new InMemoryLogSink();
        sink.Emit(MakeEvent(LogEventLevel.Information, "a"));
        var snap1 = sink.Snapshot();
        sink.Emit(MakeEvent(LogEventLevel.Information, "b"));
        var snap2 = sink.Snapshot();

        Assert.Single(snap1);
        Assert.Equal(2, snap2.Count);
    }

    [Fact]
    public void Emit_ThreadSafe_ConcurrentInvocationsNoThrow()
    {
        using var sink = new InMemoryLogSink(capacity: 1000);
        Parallel.For(0, 500, i =>
        {
            sink.Emit(MakeEvent(LogEventLevel.Information, $"e{i}"));
        });

        Assert.Equal(500, sink.Count);
    }

    /// <summary>
    /// Bug-Bash 2026-07-05 I-3 Regressions-Test: Emit darf NICHT in _events
    /// schreiben, nachdem Dispose() gelaufen ist. Wenn das alte Verhalten
    /// (Disposed-Check außerhalb des Locks) zurueckkommt, kann dieser Test
    /// unter Last ObjectDisposedException oder falsche Count-Werte zeigen.
    /// </summary>
    [Fact]
    public void Emit_AfterDispose_IsSafe_NoThrowNoMutation()
    {
        var sink = new InMemoryLogSink();
        sink.Dispose();

        // Sollte stillschweigend returnen — kein Throw, kein Count-Increment
        sink.Emit(MakeEvent(LogEventLevel.Information, "after-dispose"));
        Assert.Equal(0, sink.Count);
    }

    /// <summary>
    /// Bug-Bash 2026-07-05 I-3 Regressions-Test: Concurrent Emit + Dispose
    /// duerfen nicht zu ObjectDisposedException fuehren.
    /// </summary>
    [Fact]
    public async Task Emit_ConcurrentWithDispose_NoThrow()
    {
        var sink = new InMemoryLogSink(capacity: 10_000);

        var emitTask = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                sink.Emit(MakeEvent(LogEventLevel.Information, $"e{i}"));
            }
        });

        // Dispose mitten im Emit-Lauf
        Thread.Sleep(1);
        sink.Dispose();

        // Emit-Task darf nicht abstuerzen — einzelne Emits nach Dispose
        // werden verworfen, das ist OK.
        await emitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(emitTask.IsCompletedSuccessfully, "Emit-Task sollte nicht abstuerzen");
    }

    // ---------- Test Helpers ----------

    private static readonly MessageTemplateParser Parser = new();

    private static LogEvent MakeEvent(LogEventLevel level, string message, Exception? ex = null)
    {
        return new LogEvent(
            DateTimeOffset.Now,
            level,
            ex,
            Parser.Parse(message),
            Enumerable.Empty<LogEventProperty>());
    }
}