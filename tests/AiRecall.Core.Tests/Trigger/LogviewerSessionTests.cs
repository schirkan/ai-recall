using AiRecall.Trigger;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class LogviewerSessionTests
{
    [Fact]
    public void Ctor_NullSink_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LogviewerSession(null!));
    }

    [Fact]
    public void Ctor_ZeroCapacity_Throws()
    {
        using var sink = new InMemoryLogSink();
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogviewerSession(sink, capacity: 0));
    }

    [Fact]
    public void New_BufferIsEmpty()
    {
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink);
        Assert.Equal(0, session.BufferCount);
        Assert.Empty(session.Snapshot());
    }

    [Fact]
    public void SinkEmit_AppendsToBuffer()
    {
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink);
        sink.Emit(MakeEvent(LogEventLevel.Information, "hello"));
        Assert.Equal(1, session.BufferCount);
        Assert.Equal("hello", session.Snapshot()[0].Message);
    }

    [Fact]
    public void EventAppended_RaisedOnSinkEmit()
    {
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink);
        LogEventEntry? received = null;
        session.EventAppended += (_, e) => received = e;

        sink.Emit(MakeEvent(LogEventLevel.Information, "live"));

        Assert.NotNull(received);
        Assert.Equal("live", received!.Message);
    }

    [Fact]
    public void Capacity_Overflow_DropsOldest()
    {
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink, capacity: 3);
        for (var i = 1; i <= 5; i++) sink.Emit(MakeEvent(LogEventLevel.Information, $"e{i}"));
        var snap = session.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal("e3", snap[0].Message);
        Assert.Equal("e5", snap[2].Message);
    }

    [Fact]
    public void ClearBuffer_RemovesAll()
    {
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink);
        sink.Emit(MakeEvent(LogEventLevel.Information, "a"));
        sink.Emit(MakeEvent(LogEventLevel.Information, "b"));
        Assert.Equal(2, session.BufferCount);

        session.ClearBuffer();
        Assert.Equal(0, session.BufferCount);
        Assert.Empty(session.Snapshot());
    }

    [Fact]
    public void Cleared_EventRaised_OnClearBuffer()
    {
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink);
        var fired = 0;
        session.Cleared += (_, _) => fired++;
        session.ClearBuffer();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void FilteredSnapshot_AppliesFilter()
    {
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink);
        session.Filter.MinLevel = LogEventLevel.Warning;
        sink.Emit(MakeEvent(LogEventLevel.Information, "info-msg"));
        sink.Emit(MakeEvent(LogEventLevel.Warning, "warn-msg"));
        sink.Emit(MakeEvent(LogEventLevel.Error, "err-msg"));

        var filtered = session.FilteredSnapshot();
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, e => Assert.True(e.Level >= LogEventLevel.Warning));
    }

    [Fact]
    public void Dispose_DetachesSinkSubscription()
    {
        using var sink = new InMemoryLogSink();
        var session = new LogviewerSession(sink);
        session.Dispose();
        sink.Emit(MakeEvent(LogEventLevel.Information, "after"));
        Assert.Equal(0, session.BufferCount);
    }

    [Fact]
    public void IsPaused_DoesNotPreventAppending_ToBuffer()
    {
        // IsPaused ist ein UI-Hint; die Session selbst puffert weiter, das UI
        // soll bei Pause keine Notifications feuern. (LogviewerWindow liest
        // IsPaused in seinem OnEventAppended-Handler.)
        using var sink = new InMemoryLogSink();
        using var session = new LogviewerSession(sink);
        session.IsPaused = true;
        sink.Emit(MakeEvent(LogEventLevel.Information, "x"));
        Assert.Equal(1, session.BufferCount);
    }

    [Fact]
    public void MultipleSessions_OverSameSink_AreIsolated()
    {
        using var sink = new InMemoryLogSink();
        using var s1 = new LogviewerSession(sink, capacity: 5);
        using var s2 = new LogviewerSession(sink, capacity: 10);
        for (var i = 1; i <= 7; i++) sink.Emit(MakeEvent(LogEventLevel.Information, $"e{i}"));

        Assert.Equal(5, s1.BufferCount);  // capacity 5
        Assert.Equal(7, s2.BufferCount);  // capacity 10, alle
    }

    private static readonly MessageTemplateParser Parser = new();

    private static LogEvent MakeEvent(LogEventLevel level, string message)
    {
        return new LogEvent(
            DateTimeOffset.Now, level, exception: null,
            Parser.Parse(message),
            Enumerable.Empty<LogEventProperty>());
    }
}