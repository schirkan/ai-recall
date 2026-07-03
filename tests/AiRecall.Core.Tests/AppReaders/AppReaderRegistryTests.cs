using AiRecall.AppReader.Base;
using AiRecall.Core.Configuration;
using AiRecall.Core.Models;
using Serilog;

namespace AiRecall.Core.Tests.AppReaders;

public class AppReaderRegistryTests
{
    private sealed class StubReader : AppReaderBase
    {
        private readonly string[] _processes;
        private readonly string _name;
        private readonly Func<WindowInfo, AppReaderContext, AppReaderResult?>? _read;

        public StubReader(string name, string[] processes,
            Func<WindowInfo, AppReaderContext, AppReaderResult?>? read = null)
        {
            _name = name;
            _processes = processes;
            _read = read;
        }

        public override IReadOnlyCollection<string> SupportedProcesses => _processes;
        public override string DisplayName => _name;

        public override AppReaderResult? Read(WindowInfo window, AppReaderContext context)
            => _read?.Invoke(window, context);
    }

    private static AppReaderContext Ctx(AppConfig? cfg = null) => new()
    {
        Config = cfg ?? new AppConfig(),
        Logger = new LoggerConfiguration().CreateLogger()
    };

    private static WindowInfo Win(string process, string title = "x") =>
        new(IntPtr.Zero, title, 1, process, true, new WindowRect(0, 0, 100, 100));

    [Fact]
    public void Empty_HasNoReaders()
    {
        var reg = AppReaderRegistry.Empty(new LoggerConfiguration().CreateLogger());
        Assert.Empty(reg.Readers);
        Assert.Null(reg.FindForWindow(Win("chrome")));
    }

    [Fact]
    public void FindForWindow_MatchesByProcessName_CaseInsensitive()
    {
        var reader = new StubReader("test", new[] { "chrome" });
        var reg = AppReaderRegistry.FromReaders(new IAppReader[] { reader }, new LoggerConfiguration().CreateLogger());
        Assert.Same(reader, reg.FindForWindow(Win("Chrome")));
        Assert.Same(reader, reg.FindForWindow(Win("CHROME")));
    }

    [Fact]
    public void FindForWindow_NoMatch_ReturnsNull()
    {
        var reader = new StubReader("test", new[] { "chrome" });
        var reg = AppReaderRegistry.FromReaders(new IAppReader[] { reader }, new LoggerConfiguration().CreateLogger());
        Assert.Null(reg.FindForWindow(Win("notepad")));
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenNoMatch()
    {
        var reg = AppReaderRegistry.Empty(new LoggerConfiguration().CreateLogger());
        Assert.Null(reg.TryRead(Win("chrome"), Ctx()));
    }

    [Fact]
    public void TryRead_ReturnsReaderResult_WhenReaderMatches()
    {
        var expected = new AppReaderResult("md", "label", "kind", "r", "1.0.0");
        var reader = new StubReader("r", new[] { "chrome" }, (_, _) => expected);
        var reg = AppReaderRegistry.FromReaders(new IAppReader[] { reader }, new LoggerConfiguration().CreateLogger());
        var actual = reg.TryRead(Win("chrome"), Ctx());
        Assert.Same(expected, actual);
    }

    [Fact]
    public void TryRead_SwallowsExceptions_ReturnsNull()
    {
        var reader = new StubReader("r", new[] { "chrome" }, (_, _) => throw new InvalidOperationException("boom"));
        var reg = AppReaderRegistry.FromReaders(new IAppReader[] { reader }, new LoggerConfiguration().CreateLogger());
        Assert.Null(reg.TryRead(Win("chrome"), Ctx()));
    }

    [Fact]
    public void FirstReaderWins_OnDuplicateProcess()
    {
        var first = new StubReader("first", new[] { "chrome" }, (_, _) =>
            new AppReaderResult("first-md", null, null, "first", "1.0"));
        var second = new StubReader("second", new[] { "chrome" }, (_, _) =>
            new AppReaderResult("second-md", null, null, "second", "1.0"));
        var reg = AppReaderRegistry.FromReaders(new IAppReader[] { first, second }, new LoggerConfiguration().CreateLogger());
        var match = reg.FindForWindow(Win("chrome"));
        Assert.Same(first, match);
    }

    [Fact]
    public void LoadFromDirectory_NonExistent_LogsWarning_ReturnsEmpty()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var reg = AppReaderRegistry.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "ai-recall-nonexistent-" + Guid.NewGuid().ToString("N")), logger);
        Assert.Empty(reg.Readers);
    }
}