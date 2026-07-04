using AiRecall.Trigger;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class LogFilterTests
{
    [Fact]
    public void Matches_NoFilter_AlwaysTrue()
    {
        var f = new LogFilter();
        Assert.True(f.Matches(Make(LogEventLevel.Information, "x")));
        Assert.True(f.Matches(Make(LogEventLevel.Error, "y")));
    }

    [Fact]
    public void Matches_LevelFilter_ExcludesLowerLevels()
    {
        var f = new LogFilter { MinLevel = LogEventLevel.Warning };
        Assert.False(f.Matches(Make(LogEventLevel.Information, "x")));
        Assert.False(f.Matches(Make(LogEventLevel.Debug, "x")));
        Assert.True(f.Matches(Make(LogEventLevel.Warning, "x")));
        Assert.True(f.Matches(Make(LogEventLevel.Error, "x")));
        Assert.True(f.Matches(Make(LogEventLevel.Fatal, "x")));
    }

    [Fact]
    public void Matches_SearchText_FiltersByMessage()
    {
        var f = new LogFilter { SearchText = "OCR" };
        Assert.True(f.Matches(Make(LogEventLevel.Information, "OCR started")));
        Assert.False(f.Matches(Make(LogEventLevel.Information, "trigger fired")));
    }

    [Fact]
    public void Matches_SearchText_CaseInsensitive()
    {
        var f = new LogFilter { SearchText = "OCR" };
        Assert.True(f.Matches(Make(LogEventLevel.Information, "ocr started")));
        Assert.True(f.Matches(Make(LogEventLevel.Information, "Ocr started")));
    }

    [Fact]
    public void Matches_EmptySearchText_TreatedAsNoFilter()
    {
        var f = new LogFilter { SearchText = "" };
        Assert.True(f.Matches(Make(LogEventLevel.Information, "anything")));
    }

    [Fact]
    public void Matches_CombinedFilter_BothApply()
    {
        var f = new LogFilter { MinLevel = LogEventLevel.Warning, SearchText = "fail" };
        Assert.True(f.Matches(Make(LogEventLevel.Error, "operation failed")));
        Assert.False(f.Matches(Make(LogEventLevel.Error, "operation succeeded")));
        Assert.False(f.Matches(Make(LogEventLevel.Information, "operation failed")));
    }

    [Fact]
    public void Matches_NullEntry_Throws()
    {
        var f = new LogFilter();
        Assert.Throws<ArgumentNullException>(() => f.Matches(null!));
    }

    [Fact]
    public void Clone_ProducesIndependentFilter()
    {
        var f1 = new LogFilter { MinLevel = LogEventLevel.Warning, SearchText = "x" };
        var f2 = f1.Clone();
        Assert.Equal(f1.MinLevel, f2.MinLevel);
        Assert.Equal(f1.SearchText, f2.SearchText);

        f2.MinLevel = LogEventLevel.Error;
        f2.SearchText = "y";
        Assert.Equal(LogEventLevel.Warning, f1.MinLevel);
        Assert.Equal("x", f1.SearchText);
    }

    private static readonly MessageTemplateParser Parser = new();

    private static LogEventEntry Make(LogEventLevel level, string message)
    {
        var evt = new LogEvent(
            DateTimeOffset.Now, level, exception: null,
            Parser.Parse(message),
            Enumerable.Empty<LogEventProperty>());
        return LogEventEntry.FromLogEvent(evt);
    }
}